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

        public string? PinnedPipeName { get; set; }
        public int? PreferredRevitYear { get; set; }
        public PermissionProfile PermissionProfile { get; set; } = PermissionProfile.SafeWrite;
        public HashSet<string> ActivePacks { get; set; } = ToolPackResolver.ResolveActivePacks(null);
        public string SchemaMode { get; set; } = "codemode";
        public SessionTraceRecorder TraceRecorder { get; } = new SessionTraceRecorder();

        public async Task<int> HydrateRemoteCatalogAsync(CancellationToken ct, bool force = false)
        {
            string? pipeName = ResolveTargetPipe();
            if (string.IsNullOrWhiteSpace(pipeName)) return 0;

            if (!force && string.Equals(pipeName, _lastHydratedPipe, StringComparison.Ordinal) && !string.IsNullOrEmpty(_lastHydratedHash))
                return 0;

            try
            {
                var client = new ScioPipeClient(pipeName!);
                var reply = await client.SendAsync(new ToolRequest { Tool = "_scio_catalog" }, connectTimeoutMs: 1500, ct: ct).ConfigureAwait(false);
                if (!reply.Ok || reply.Data is not JObject data) return 0;

                string? hash = data.Value<string>("contract_hash");
                if (data["tools"] is not JArray arr) return 0;

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
                _lastHydratedHash = hash;
                return registered;
            }
            catch
            {
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
            sw.Stop();
            TraceRecorder.Record(request, response, sw.ElapsedMilliseconds);
            TelemetryTool.RecordToolCall(request.Tool, response.Ok, sw.ElapsedMilliseconds, SchemaMode);
            return response;
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
