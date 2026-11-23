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

        public string? PinnedPipeName { get; set; }
        public int? PreferredRevitYear { get; set; }
        public PermissionProfile PermissionProfile { get; set; } = PermissionProfile.SafeWrite;
        public HashSet<string> ActivePacks { get; set; } = ToolPackResolver.ResolveActivePacks(null);

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

            if (_serverHandlers.TryGetValue(request.Tool, out var localHandler))
            {
                return await localHandler(request, ct).ConfigureAwait(false);
            }

            if (_descriptors.TryGetValue(request.Tool, out var desc) &&
                !ToolPackResolver.IsToolVisible(desc, ActivePacks, PermissionProfile))
            {
                return ToolResponse.Refusal(
                    request.RequestId,
                    "tool_not_permitted",
                    $"Tool '{request.Tool}' is not enabled under the active packs or permission profile ({PermissionProfile}).");
            }

            string? pipeName = ResolveTargetPipe();
            if (string.IsNullOrWhiteSpace(pipeName))
            {
                return ToolResponse.Failure(
                    request.RequestId,
                    "no_revit_session",
                    "No running Autodesk Revit session with SCIO Revit MCP v4 was found. Open Revit first.");
            }

            var client = new ScioPipeClient(pipeName);
            return await client.SendAsync(request, connectTimeoutMs: 5000, ct: ct).ConfigureAwait(false);
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
