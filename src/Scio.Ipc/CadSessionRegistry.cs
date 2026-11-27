using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Scio.Abstractions;

namespace Scio.Ipc
{
    /// <summary>
    /// Discovery beacon published by Scio.Cad (AutoCAD / Civil 3D) under %USERPROFILE%\.scio\discovery\cad-*.json.
    /// </summary>
    public sealed class CadSessionBeacon
    {
        [JsonProperty("host")]
        public string Host { get; set; } = "autocad";

        [JsonProperty("cad_year")]
        public string CadYear { get; set; } = string.Empty;

        [JsonProperty("year")]
        public string Year { get; set; } = string.Empty;

        [JsonProperty("pipe_name")]
        public string PipeName { get; set; } = string.Empty;

        [JsonProperty("auth_token")]
        public string AuthToken { get; set; } = string.Empty;

        [JsonProperty("pid")]
        public int Pid { get; set; }

        [JsonProperty("addin_version")]
        public string AddinVersion { get; set; } = string.Empty;

        [JsonProperty("contract_hash")]
        public string ContractHash { get; set; } = string.Empty;

        [JsonProperty("commands")]
        public List<string> Commands { get; set; } = new List<string>();

        [JsonProperty("started_utc")]
        public string StartedUtc { get; set; } = string.Empty;

        [JsonIgnore]
        public string EffectiveYear => !string.IsNullOrWhiteSpace(CadYear) ? CadYear : Year;

        [JsonIgnore]
        public string DisplayHost =>
            string.Equals(Host, "civil3d", StringComparison.OrdinalIgnoreCase) ? "Civil 3D" : "AutoCAD";

        public bool MatchesHost(string? filter)
        {
            if (string.IsNullOrWhiteSpace(filter) ||
                string.Equals(filter, "any", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(filter, "cad", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
            return string.Equals(Host, filter, StringComparison.OrdinalIgnoreCase);
        }
    }

    /// <summary>
    /// Discovers live AutoCAD / Civil 3D sessions and cleans up orphaned cad-*.json beacons.
    /// </summary>
    public static class CadSessionRegistry
    {
        public static string DiscoveryDirectory =>
            Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                ".scio", "discovery");

        /// <summary>Optional test hook overriding the discovery directory.</summary>
        public static string? DirectoryOverride { get; set; }

        /// <summary>Optional test hook overriding PID liveness checks.</summary>
        public static Func<int, bool>? LivenessProbe { get; set; }

        private static string EffectiveDirectory =>
            !string.IsNullOrWhiteSpace(DirectoryOverride) ? DirectoryOverride! : DiscoveryDirectory;

        public static List<CadSessionBeacon> DiscoverLiveCadSessions()
        {
            var live = new List<CadSessionBeacon>();
            string dir = EffectiveDirectory;
            if (!Directory.Exists(dir)) return live;

            foreach (string file in Directory.GetFiles(dir, "cad-*.json"))
            {
                try
                {
                    var beacon = JsonConvert.DeserializeObject<CadSessionBeacon>(File.ReadAllText(file, Encoding.UTF8));
                    if (beacon == null || string.IsNullOrWhiteSpace(beacon.PipeName)) continue;

                    if (IsProcessAlive(beacon.Pid))
                    {
                        if (string.IsNullOrWhiteSpace(beacon.Host)) beacon.Host = "autocad";
                        live.Add(beacon);
                    }
                    else
                    {
                        try { File.Delete(file); } catch { }
                    }
                }
                catch
                {
                    // Ignore malformed or transient files
                }
            }

            return live
                .OrderByDescending(b => b.EffectiveYear, StringComparer.Ordinal)
                .ThenByDescending(b => b.StartedUtc, StringComparer.Ordinal)
                .ToList();
        }

        public static int SweepStaleCadSessions()
        {
            string dir = EffectiveDirectory;
            if (!Directory.Exists(dir)) return 0;

            int swept = 0;
            foreach (string file in Directory.GetFiles(dir, "cad-*.json"))
            {
                try
                {
                    var beacon = JsonConvert.DeserializeObject<CadSessionBeacon>(File.ReadAllText(file, Encoding.UTF8));
                    if (beacon == null || !IsProcessAlive(beacon.Pid))
                    {
                        File.Delete(file);
                        swept++;
                    }
                }
                catch
                {
                    // Ignore transient I/O errors
                }
            }
            return swept;
        }

        private static bool IsProcessAlive(int pid)
        {
            if (LivenessProbe != null) return LivenessProbe(pid);
            if (pid <= 0) return false;
            try
            {
                using (var proc = Process.GetProcessById(pid))
                {
                    return !proc.HasExited;
                }
            }
            catch
            {
                return false;
            }
        }
    }

    /// <summary>
    /// Named-pipe client speaking Scio.Cad's newline-delimited JSON wire protocol.
    /// </summary>
    public sealed class CadPipeClient
    {
        private readonly CadSessionBeacon _beacon;

        public CadPipeClient(CadSessionBeacon beacon)
        {
            _beacon = beacon ?? throw new ArgumentNullException(nameof(beacon));
        }

        public async Task<ToolResponse> SendAsync(string wireCommand, ToolRequest request, int connectTimeoutMs = 5000, CancellationToken ct = default)
        {
            using (var client = CreateSecuredClientStream(_beacon.PipeName))
            using (var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct))
            {
                timeoutCts.CancelAfter(Math.Max(request.TimeoutMs, connectTimeoutMs));

                try
                {
                    await client.ConnectAsync(connectTimeoutMs, timeoutCts.Token).ConfigureAwait(false);
                }
                catch (TimeoutException)
                {
                    return ToolResponse.Failure(
                        request.RequestId,
                        "cad_unreachable",
                        $"Timed out connecting to {_beacon.DisplayHost} IPC pipe '{_beacon.PipeName}'. Ensure AutoCAD / Civil 3D is open with Sutra CAD loaded.");
                }
                catch (Exception ex)
                {
                    return ToolResponse.Failure(
                        request.RequestId,
                        "cad_connect_failed",
                        $"Failed to connect to {_beacon.DisplayHost} IPC pipe '{_beacon.PipeName}': {ex.Message}");
                }

                var wireReq = new JObject
                {
                    ["id"] = request.RequestId,
                    ["token"] = _beacon.AuthToken,
                    ["command"] = wireCommand,
                    ["params"] = request.Arguments ?? new JObject()
                };

                byte[] payload = new UTF8Encoding(false).GetBytes(wireReq.ToString(Formatting.None) + "\n");
                await client.WriteAsync(payload, 0, payload.Length, timeoutCts.Token).ConfigureAwait(false);
                await client.FlushAsync(timeoutCts.Token).ConfigureAwait(false);

                string? replyLine = await ReadLineAsync(client, timeoutCts.Token).ConfigureAwait(false);
                if (string.IsNullOrWhiteSpace(replyLine))
                {
                    return ToolResponse.Failure(
                        request.RequestId,
                        "cad_empty_response",
                        $"{_beacon.DisplayHost} closed the IPC pipe without returning a reply.");
                }

                JObject reply;
                try { reply = JObject.Parse(replyLine!); }
                catch (Exception ex)
                {
                    return ToolResponse.Failure(request.RequestId, "cad_malformed_response", $"Malformed JSON from {_beacon.DisplayHost}: {ex.Message}");
                }

                bool ok = reply.Value<bool?>("success") ?? false;
                if (!ok)
                {
                    string errMsg = reply.Value<string>("error") ?? $"{_beacon.DisplayHost} command '{wireCommand}' failed.";
                    return ToolResponse.Failure(request.RequestId, "cad_command_failed", errMsg, reply["data"]);
                }

                bool isDryRun = request.Arguments?.Value<bool?>("dry_run") == true;
                var status = isDryRun ? VerificationStatus.DryRunPreview : VerificationStatus.Verified;
                return ToolResponse.Success(
                    request.RequestId,
                    reply["data"],
                    $"{_beacon.DisplayHost} '{wireCommand}' completed.",
                    status,
                    hostVerified: true);
            }
        }

        private static async Task<string?> ReadLineAsync(Stream stream, CancellationToken ct)
        {
            var ms = new MemoryStream();
            byte[] buf = new byte[4096];
            while (true)
            {
                int read = await stream.ReadAsync(buf, 0, buf.Length, ct).ConfigureAwait(false);
                if (read <= 0) break;
                for (int i = 0; i < read; i++)
                {
                    if (buf[i] == (byte)'\n')
                    {
                        return Encoding.UTF8.GetString(ms.ToArray()).TrimEnd('\r');
                    }
                    ms.WriteByte(buf[i]);
                }
            }
            return ms.Length > 0 ? Encoding.UTF8.GetString(ms.ToArray()).TrimEnd('\r') : null;
        }

        private static NamedPipeClientStream CreateSecuredClientStream(string pipeName)
        {
#if NET8_0_OR_GREATER
            return new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
#else
            return new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
#endif
        }
    }
}
