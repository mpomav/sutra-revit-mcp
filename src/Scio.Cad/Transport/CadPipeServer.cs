using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using System.Threading;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Scio.Cad.Core;
using Scio.Contracts;
using Scio.Revit.Core;

namespace Scio.Cad.Transport
{
    /// <summary>
    /// Named-pipe transport for AutoCAD / Civil 3D speaking the exact Sutra wire protocol.
    /// Publishes %USERPROFILE%\.scio\discovery\cad-&lt;year&gt;-&lt;pid&gt;.json on start and removes it on stop.
    /// </summary>
    internal sealed class CadPipeServer
    {
        private const int MaxConcurrentClients = 32;
        private readonly SemaphoreSlim _clientSlots = new SemaphoreSlim(MaxConcurrentClients, MaxConcurrentClients);
        private readonly CadDispatcher _dispatcher;
        private readonly string _cadYear;
        private readonly string _hostKind; // "civil3d" or "autocad"
        private readonly string _addinVersion;
        private readonly string _token;
        private readonly string _instanceId;
        private readonly string _startedUtc;
        private readonly string _pipeName;
        private readonly int _commandTimeoutMs;
        private string _discoveryFile;
        private Thread _thread;
        private volatile bool _running;

        public string PipeName => _pipeName;
        public string HostKind => _hostKind;
        public string CadYear => _cadYear;
        public string DiscoveryFile => _discoveryFile;
        public bool IsRunning => _running;

        public CadPipeServer(CadDispatcher dispatcher, string cadYear, string hostKind, string addinVersion, int commandTimeoutMs = 600000)
        {
            _dispatcher = dispatcher;
            _cadYear = cadYear;
            _hostKind = hostKind;
            _addinVersion = addinVersion;
            _commandTimeoutMs = commandTimeoutMs;
            _token = GenerateToken();
            _instanceId = Guid.NewGuid().ToString("N");
            _startedUtc = DateTime.UtcNow.ToString("o");
            _pipeName = "ScioCad-" + Process.GetCurrentProcess().Id;
        }

        public void Start()
        {
            _running = true;
            PublishDiscovery();
            _thread = new Thread(AcceptLoop) { IsBackground = true, Name = "ScioCad.PipeServer" };
            _thread.Start();
            CadLog.Info("Pipe server listening on " + _pipeName + " (" + _hostKind + " " + _cadYear + ")");
        }

        public void Stop()
        {
            _running = false;
            try
            {
                using (var c = new NamedPipeClientStream(".", _pipeName, PipeDirection.InOut))
                    c.Connect(200);
            }
            catch { }

            DeleteDiscovery();
            CadLog.Info("Pipe server stopped.");
        }

        private void PublishDiscovery()
        {
            try
            {
                string dir = ScioPaths.DiscoveryDir();
                Directory.CreateDirectory(dir);
                int pid = Process.GetCurrentProcess().Id;

                // Sweep orphaned cad-*.json files using Sutra's shared DiscoverySweep rule.
                try
                {
                    List<string> stale = DiscoverySweep.StaleFiles(
                        Directory.GetFiles(dir, "cad-*.json"), IsPidAlive, pid);
                    foreach (string f in stale)
                    {
                        try { File.Delete(f); } catch { }
                    }
                }
                catch { }

                _discoveryFile = Path.Combine(dir, "cad-" + _cadYear + "-" + pid + ".json");
                var payload = new JObject
                {
                    ["schema"] = 3,
                    ["host"] = _hostKind,
                    ["year"] = _cadYear,
                    ["cad_year"] = _cadYear,
                    ["pipe_name"] = _pipeName,
                    ["auth_token"] = _token,
                    ["pid"] = pid,
                    ["addin_version"] = _addinVersion,
                    ["contract_hash"] = Contract.Hash,
                    ["protocol_version"] = Contract.ProtocolVersion,
                    ["commands"] = new JArray(_dispatcher.CommandNames()),
                    ["instance_id"] = _instanceId,
                    ["started_utc"] = _startedUtc
                };

                AtomicFile.WriteAllText(_discoveryFile, payload.ToString(Formatting.Indented));
            }
            catch (Exception ex)
            {
                CadLog.Error("Failed to publish CAD discovery file", ex);
            }
        }

        private void DeleteDiscovery()
        {
            if (string.IsNullOrEmpty(_discoveryFile)) return;
            try
            {
                if (File.Exists(_discoveryFile)) File.Delete(_discoveryFile);
            }
            catch { }
        }

        private static bool IsPidAlive(int pid)
        {
            if (pid <= 0) return false;
            try { using (var p = Process.GetProcessById(pid)) { } return true; }
            catch (ArgumentException) { return false; }
            catch { return true; }
        }

        private NamedPipeServerStream CreateServerStream()
        {
            var rules = new PipeSecurity();
            var me = WindowsIdentity.GetCurrent().User;
            rules.AddAccessRule(new PipeAccessRule(me, PipeAccessRights.FullControl, AccessControlType.Allow));

#if NET
            return NamedPipeServerStreamAcl.Create(
                _pipeName, PipeDirection.InOut, NamedPipeServerStream.MaxAllowedServerInstances,
                PipeTransmissionMode.Byte, PipeOptions.Asynchronous, 0, 0, rules);
#else
            return new NamedPipeServerStream(
                _pipeName, PipeDirection.InOut, NamedPipeServerStream.MaxAllowedServerInstances,
                PipeTransmissionMode.Byte, PipeOptions.Asynchronous, 0, 0, rules);
#endif
        }

        private void AcceptLoop()
        {
            while (_running)
            {
                NamedPipeServerStream server = null;
                try
                {
                    server = CreateServerStream();
                    server.WaitForConnection();
                    if (!_running)
                    {
                        SafeClose(server);
                        return;
                    }

                    NamedPipeServerStream conn = server;
                    server = null;
                    if (!_clientSlots.Wait(0))
                    {
                        SafeClose(conn);
                        continue;
                    }
                    new Thread(() =>
                    {
                        try { HandleConnection(conn); }
                        finally { _clientSlots.Release(); }
                    })
                    {
                        IsBackground = true,
                        Name = "ScioCad.PipeWorker"
                    }.Start();
                }
                catch (Exception ex)
                {
                    SafeClose(server);
                    if (_running)
                    {
                        CadLog.Warn("Pipe accept error: " + ex.Message);
                        Thread.Sleep(200);
                    }
                }
            }
        }

        private void HandleConnection(NamedPipeServerStream pipe)
        {
            try
            {
                string line = ReadBoundedLine(pipe, Contract.MaxRequestBytes);
                if (line == null)
                {
                    WriteReply(pipe, new JObject
                    {
                        ["id"] = null,
                        ["success"] = false,
                        ["data"] = null,
                        ["error"] = "Request was empty or exceeded the maximum size (" + Contract.MaxRequestBytes + " bytes)."
                    });
                    return;
                }

                JObject req;
                try { req = JObject.Parse(line); }
                catch (Exception ex)
                {
                    WriteReply(pipe, new JObject
                    {
                        ["id"] = null,
                        ["success"] = false,
                        ["data"] = null,
                        ["error"] = "Malformed JSON request: " + ex.Message
                    });
                    return;
                }

                string id = (string)req["id"];
                string token = (string)req["token"];
                string command = (string)req["command"];
                var args = req["params"] as JObject ?? new JObject();

                if (!FixedTimeEquals(token, _token))
                {
                    WriteReply(pipe, new JObject
                    {
                        ["id"] = id,
                        ["success"] = false,
                        ["data"] = null,
                        ["error"] = "Unauthorized: invalid bridge token."
                    });
                    return;
                }

                JObject reply = _dispatcher.Execute(id, command, args, _commandTimeoutMs);
                WriteReply(pipe, reply);
            }
            catch (Exception ex)
            {
                CadLog.Error("Pipe worker error", ex);
            }
            finally
            {
                SafeClose(pipe);
            }
        }

        private static string ReadBoundedLine(Stream stream, int maxBytes)
        {
            var ms = new MemoryStream();
            int b;
            while ((b = stream.ReadByte()) != -1)
            {
                if (b == '\n') break;
                if (ms.Length >= maxBytes) return null;
                ms.WriteByte((byte)b);
            }
            if (ms.Length == 0 && b == -1) return null;
            return Encoding.UTF8.GetString(ms.ToArray()).TrimEnd('\r');
        }

        private static void WriteReply(Stream stream, JObject reply)
        {
            byte[] bytes = new UTF8Encoding(false).GetBytes(reply.ToString(Formatting.None) + "\n");
            if (bytes.Length - 1 > Contract.MaxReplyBytes)
            {
                var fallback = new JObject
                {
                    ["id"] = reply["id"],
                    ["success"] = false,
                    ["data"] = null,
                    ["error"] = "CAD reply exceeded max reply bytes (" + Contract.MaxReplyBytes + "). Narrow your query."
                };
                bytes = new UTF8Encoding(false).GetBytes(fallback.ToString(Formatting.None) + "\n");
            }
            stream.Write(bytes, 0, bytes.Length);
            stream.Flush();
        }

        private static bool FixedTimeEquals(string a, string b)
        {
            if (a == null || b == null || a.Length != b.Length) return false;
            int diff = 0;
            for (int i = 0; i < a.Length; i++) diff |= a[i] ^ b[i];
            return diff == 0;
        }

        private static string GenerateToken()
        {
            byte[] bytes = new byte[24];
            using (var rng = RandomNumberGenerator.Create()) rng.GetBytes(bytes);
            return BitConverter.ToString(bytes).Replace("-", "").ToLowerInvariant();
        }

        private static void SafeClose(NamedPipeServerStream s)
        {
            if (s == null) return;
            try { s.Dispose(); } catch { }
        }
    }
}
