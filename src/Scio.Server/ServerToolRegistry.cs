using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using Scio.Abstractions;
using Scio.Ipc;

namespace Scio.Server
{
    public delegate Task<ToolResponse> ServerToolDelegate(ToolRequest request, CancellationToken ct);

    public sealed class ServerToolRegistry
    {
        private readonly Dictionary<string, ToolDescriptor> _descriptors = new Dictionary<string, ToolDescriptor>(StringComparer.Ordinal);
        private readonly Dictionary<string, ServerToolDelegate> _serverHandlers = new Dictionary<string, ServerToolDelegate>(StringComparer.Ordinal);
        private string? _lastHydratedPipe;
        private string? _lastHydratedHash;
        private string? _lastFailedPipe;
        private DateTime _lastFailedHydrationUtc = DateTime.MinValue;
        private static readonly TimeSpan FailedHydrationCooldown = TimeSpan.FromSeconds(3);

        public string? PinnedPipeName { get; set; }
        public int? PreferredRevitYear { get; set; }
        public string? PinnedHost { get; set; }
        public int? PinnedCadPid { get; set; }
        public PermissionProfile PermissionProfile { get; set; } = PermissionProfile.SafeWrite;
        public HashSet<string> ActivePacks { get; set; } = ToolPackResolver.ResolveActivePacks(null);
        public string SchemaMode { get; set; } = "codemode";
        public bool MockMode { get; set; }
        public SessionTraceRecorder TraceRecorder { get; } = new SessionTraceRecorder();
        public AsyncJobManager Jobs { get; } = new AsyncJobManager();

        /// <summary>Optional test hook overriding IPC _scio_catalog fetch.</summary>
        public Func<string, CancellationToken, Task<ToolResponse>>? RemoteCatalogFetcher { get; set; }

        public async Task<int> HydrateRemoteCatalogAsync(CancellationToken ct, bool force = false)
        {
            if (MockMode && RemoteCatalogFetcher == null) return 0;

            string? pipeName = ResolveTargetPipe();
            if (string.IsNullOrWhiteSpace(pipeName)) return 0;

            if (!force && string.Equals(pipeName, _lastHydratedPipe, StringComparison.Ordinal) && !string.IsNullOrEmpty(_lastHydratedHash))
                return 0;

            if (!force &&
                string.Equals(pipeName, _lastFailedPipe, StringComparison.Ordinal) &&
                (DateTime.UtcNow - _lastFailedHydrationUtc) < FailedHydrationCooldown)
                return 0;

            try
            {
                ToolResponse reply;
                if (RemoteCatalogFetcher != null)
                {
                    reply = await RemoteCatalogFetcher(pipeName!, ct).ConfigureAwait(false);
                }
                else
                {
                    var client = new ScioPipeClient(pipeName!);
                    reply = await client.SendAsync(new ToolRequest { Tool = "_scio_catalog" }, connectTimeoutMs: 3000, ct: ct).ConfigureAwait(false);
                }

                if (!reply.Ok || reply.Data is not JObject data)
                {
                    _lastFailedPipe = pipeName;
                    _lastFailedHydrationUtc = DateTime.UtcNow;
                    return 0;
                }

                string? hash = data.Value<string>("contract_hash");
                if (data["tools"] is not JArray arr)
                {
                    _lastFailedPipe = pipeName;
                    _lastFailedHydrationUtc = DateTime.UtcNow;
                    return 0;
                }

                // Clear stale remote Revit descriptors only when switching from a previous live Revit pipe to a different one
                if (!string.IsNullOrEmpty(_lastHydratedPipe) &&
                    !string.Equals(pipeName, _lastHydratedPipe, StringComparison.Ordinal))
                {
                    var staleRemote = _descriptors.Values
                        .Where(d => !d.ServerSide &&
                                    !string.Equals(d.TargetHost, "cad", StringComparison.OrdinalIgnoreCase) &&
                                    !string.Equals(d.Name, "scio_health", StringComparison.Ordinal))
                        .Select(d => d.Name)
                        .ToList();
                    foreach (string name in staleRemote)
                        _descriptors.Remove(name);
                }

                int registered = 0;
                foreach (var item in arr)
                {
                    var desc = item.ToObject<ToolDescriptor>();
                    if (desc != null && !string.IsNullOrWhiteSpace(desc.Name) && !_serverHandlers.ContainsKey(desc.Name))
                    {
                        RegisterRemoteTool(desc);
                        registered++;
                    }
                }

                _lastHydratedPipe = pipeName;
                _lastHydratedHash = !string.IsNullOrEmpty(hash) ? hash : "hydrated";
                _lastFailedPipe = null;
                _lastFailedHydrationUtc = DateTime.MinValue;
                return registered;
            }
            catch
            {
                _lastFailedPipe = pipeName;
                _lastFailedHydrationUtc = DateTime.UtcNow;
                return 0;
            }
        }

        public void RegisterServerTool(ToolDescriptor descriptor, ServerToolDelegate handler)
        {
            descriptor.ServerSide = true;
            _descriptors[descriptor.Name] = descriptor;
            _serverHandlers[descriptor.Name] = handler;
        }

        public void RegisterRemoteTool(ToolDescriptor descriptor)
        {
            descriptor.ServerSide = false;
            _descriptors[descriptor.Name] = descriptor;
        }

        public IReadOnlyList<ToolDescriptor> ListVisibleTools()
        {
            return _descriptors.Values
                .Where(d => ToolPackResolver.IsToolVisible(d, ActivePacks, PermissionProfile))
                .OrderBy(d => d.Name, StringComparer.Ordinal)
                .ToList();
        }

        public ToolDescriptor? FindDescriptor(string name)
        {
            return _descriptors.TryGetValue(name, out var desc) ? desc : null;
        }

        public async Task<ToolResponse> DispatchAsync(ToolRequest request, CancellationToken ct)
        {
            if (string.IsNullOrWhiteSpace(request.Tool))
                return ToolResponse.Refusal(request.RequestId, "missing_tool", "Tool name is required.");

            await HydrateRemoteCatalogAsync(ct).ConfigureAwait(false);

            var sw = System.Diagnostics.Stopwatch.StartNew();
            ToolResponse response;

            if (_descriptors.TryGetValue(request.Tool, out var knownDesc))
            {
                var validation = ArgumentValidator.Validate(knownDesc, request.Arguments);
                if (!validation.Ok)
                {
                    response = ToolResponse.Refusal(
                        request.RequestId,
                        "invalid_argument",
                        ArgumentValidator.BuildErrorMessage(request.Tool, validation),
                        details: ArgumentValidator.BuildErrorDetails(validation));
                    sw.Stop();
                    TraceRecorder.Record(request, response, sw.ElapsedMilliseconds);
                    TelemetryTool.RecordToolCall(request.Tool, false, sw.ElapsedMilliseconds, SchemaMode);
                    return response;
                }
            }

            if (_serverHandlers.TryGetValue(request.Tool, out var localHandler))
            {
                response = await localHandler(request, ct).ConfigureAwait(false);
                sw.Stop();
                TraceRecorder.Record(request, response, sw.ElapsedMilliseconds);
                TelemetryTool.RecordToolCall(request.Tool, response.Ok, sw.ElapsedMilliseconds, SchemaMode);
                return response;
            }

            if (_descriptors.TryGetValue(request.Tool, out var desc) &&
                !ToolPackResolver.IsToolVisible(desc, ActivePacks, PermissionProfile))
            {
                response = ToolResponse.Refusal(
                    request.RequestId,
                    "tool_not_permitted",
                    $"Tool '{request.Tool}' is not enabled under the active packs or permission profile ({PermissionProfile}).");
                sw.Stop();
                TraceRecorder.Record(request, response, sw.ElapsedMilliseconds);
                TelemetryTool.RecordToolCall(request.Tool, false, sw.ElapsedMilliseconds, SchemaMode);
                return response;
            }

            if (MockMode)
            {
                response = ToolResponse.Success(
                    request.RequestId,
                    new JObject
                    {
                        ["mock"] = true,
                        ["tool"] = request.Tool,
                        ["arguments"] = request.Arguments ?? new JObject()
                    },
                    $"[MOCK] Simulated '{request.Tool}' (no live Revit session required).",
                    VerificationStatus.ReadOnly,
                    hostVerified: false);
                sw.Stop();
                TraceRecorder.Record(request, response, sw.ElapsedMilliseconds);
                return response;
            }

            bool routeToCad =
                (_descriptors.TryGetValue(request.Tool, out var targetDesc) &&
                 string.Equals(targetDesc.TargetHost, "cad", StringComparison.OrdinalIgnoreCase)) ||
                (string.Equals(request.Tool, "scio_health", StringComparison.Ordinal) &&
                 (PinnedCadPid.HasValue || IsCadHostFilter(PinnedHost)));

            if (routeToCad)
            {
                var cadBeacon = ResolveCadTarget(out string? cadRefusal);
                if (cadRefusal != null)
                {
                    response = ToolResponse.Refusal(request.RequestId, "ambiguous_cad_session", cadRefusal);
                    sw.Stop();
                    TraceRecorder.Record(request, response, sw.ElapsedMilliseconds);
                    TelemetryTool.RecordToolCall(request.Tool, false, sw.ElapsedMilliseconds, SchemaMode);
                    return response;
                }
                if (cadBeacon == null)
                {
                    response = ToolResponse.Failure(
                        request.RequestId,
                        "no_cad_session",
                        "No running AutoCAD / Civil 3D session with Sutra CAD loaded was found. Open AutoCAD or Civil 3D first.");
                    sw.Stop();
                    TraceRecorder.Record(request, response, sw.ElapsedMilliseconds);
                    TelemetryTool.RecordToolCall(request.Tool, false, sw.ElapsedMilliseconds, SchemaMode);
                    return response;
                }

                string wireCmd = _descriptors.TryGetValue(request.Tool, out var cd) && !string.IsNullOrWhiteSpace(cd.RemoteCommand)
                    ? cd.RemoteCommand!
                    : request.Tool;
                var cadClient = new CadPipeClient(cadBeacon);
                response = await cadClient.SendAsync(wireCmd, request, connectTimeoutMs: 5000, ct: ct).ConfigureAwait(false);
                TryAutoIngest(request.Tool, response);
                sw.Stop();
                TraceRecorder.Record(request, response, sw.ElapsedMilliseconds);
                TelemetryTool.RecordToolCall(request.Tool, response.Ok, sw.ElapsedMilliseconds, SchemaMode);
                return response;
            }

            string? pipeName = ResolveTargetPipe();
            if (string.IsNullOrWhiteSpace(pipeName))
            {
                response = ToolResponse.Failure(
                    request.RequestId,
                    "no_revit_session",
                    "No running Autodesk Revit session with SCIO Revit MCP v4 was found. Open Revit first.");
                sw.Stop();
                TraceRecorder.Record(request, response, sw.ElapsedMilliseconds);
                TelemetryTool.RecordToolCall(request.Tool, false, sw.ElapsedMilliseconds, SchemaMode);
                return response;
            }

            var client = new ScioPipeClient(pipeName);
            response = await client.SendAsync(request, connectTimeoutMs: 5000, ct: ct).ConfigureAwait(false);
            TryAutoIngest(request.Tool, response);
            sw.Stop();
            TraceRecorder.Record(request, response, sw.ElapsedMilliseconds);
            TelemetryTool.RecordToolCall(request.Tool, response.Ok, sw.ElapsedMilliseconds, SchemaMode);
            return response;
        }

        private static void TryAutoIngest(string toolName, ToolResponse response)
        {
            if (response == null || !response.Ok || response.Data == null) return;
            if (!SqliteIngestor.ShouldIngest(toolName)) return;
            try
            {
                string? modelName = (response.Data as JObject)?.Value<string>("drawing")
                    ?? (response.Data as JObject)?.Value<string>("document")
                    ?? (response.Data as JObject)?.Value<string>("model");
                string? runId = SqliteIngestor.TryIngestResultSync(toolName, response.Data, modelName);
                if (!string.IsNullOrWhiteSpace(runId) && response.Data is JObject obj)
                    obj["sqlite_run_id"] = runId;
            }
            catch { }
        }

        public static bool IsCadHostFilter(string? host) =>
            string.Equals(host, "cad", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(host, "autocad", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(host, "civil3d", StringComparison.OrdinalIgnoreCase);

        public CadSessionBeacon? ResolveCadTarget(out string? refusal, string? explicitHostFilter = null)
        {
            refusal = null;
            var sessions = CadSessionRegistry.DiscoverLiveCadSessions();
            if (sessions.Count == 0) return null;

            string? filter = !string.IsNullOrWhiteSpace(explicitHostFilter)
                ? explicitHostFilter
                : (IsCadHostFilter(PinnedHost) ? PinnedHost : "cad");

            if (PinnedCadPid.HasValue)
            {
                var byPid = sessions.FirstOrDefault(s => s.Pid == PinnedCadPid.Value);
                if (byPid == null)
                {
                    refusal = $"Pinned CAD process ID {PinnedCadPid.Value} is no longer running. Call scio_target with clear=true or host='auto'.";
                    return null;
                }
                if (!byPid.MatchesHost(filter))
                {
                    refusal = $"Pinned CAD process ID {PinnedCadPid.Value} is {byPid.DisplayHost}, which does not match host filter '{filter}'.";
                    return null;
                }
                return byPid;
            }

            var matching = sessions.Where(s => s.MatchesHost(filter)).ToList();
            if (matching.Count == 0) return null;
            if (matching.Count == 1) return matching[0];

            string names = string.Join(", ", matching.Select(m => $"{m.DisplayHost} {m.EffectiveYear} (pid {m.Pid})"));
            refusal = $"{matching.Count} CAD sessions are running ({names}). Call scio_target with a specific pid or host ('autocad' / 'civil3d') to choose one.";
            return null;
        }

        public string? ResolveTargetPipe()
        {
            if (!string.IsNullOrWhiteSpace(PinnedPipeName))
                return PinnedPipeName;

            var sessions = SessionRegistry.DiscoverLiveSessions();
            if (sessions.Count == 0) return null;

            if (PreferredRevitYear.HasValue)
            {
                var match = sessions.FirstOrDefault(s => s.RevitYear == PreferredRevitYear.Value);
                if (match != null) return match.PipeName;
            }

            return sessions[0].PipeName;
        }
    }
}
