using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using Scio.Abstractions;
using Scio.Ipc;
using Scio.Server;
using Xunit;

namespace Scio.Core.Tests
{
    [Collection("ProcessState")]
    public sealed class MultiHostCadRoutingTests : IDisposable
    {
        private readonly string _revitDir;
        private readonly string _cadDir;
        private readonly HashSet<int> _alive = new HashSet<int>();

        public MultiHostCadRoutingTests()
        {
            string root = Path.Combine(Path.GetTempPath(), "sutra-multihost-" + Guid.NewGuid().ToString("N"));
            _revitDir = Path.Combine(root, "revit");
            _cadDir = Path.Combine(root, "cad");
            Directory.CreateDirectory(_revitDir);
            Directory.CreateDirectory(_cadDir);

            SessionRegistry.DirectoryOverride = _revitDir;
            SessionRegistry.LivenessProbe = pid => { lock (_alive) return _alive.Contains(pid); };

            CadSessionRegistry.DirectoryOverride = _cadDir;
            CadSessionRegistry.LivenessProbe = pid => { lock (_alive) return _alive.Contains(pid); };
        }

        public void Dispose()
        {
            SessionRegistry.DirectoryOverride = null;
            SessionRegistry.LivenessProbe = null;
            CadSessionRegistry.DirectoryOverride = null;
            CadSessionRegistry.LivenessProbe = null;
            try
            {
                string? parent = Directory.GetParent(_revitDir)?.FullName;
                if (!string.IsNullOrEmpty(parent) && Directory.Exists(parent))
                    Directory.Delete(parent, true);
            }
            catch { }
        }

        private void PublishRevit(int year, int pid, bool alive = true)
        {
            var o = new JObject
            {
                ["pipe_name"] = $"scio-revit-v4-test-{year}-{pid}",
                ["pid"] = pid,
                ["revit_year"] = year,
                ["version"] = "1.1.0",
                ["contract_hash"] = "test-hash"
            };
            File.WriteAllText(Path.Combine(_revitDir, $"revit-{year}-{pid}.json"), o.ToString());
            if (alive) lock (_alive) _alive.Add(pid);
        }

        private void PublishCad(string host, string year, int pid, bool alive = true)
        {
            var o = new JObject
            {
                ["schema"] = 3,
                ["host"] = host,
                ["year"] = year,
                ["cad_year"] = year,
                ["pipe_name"] = "ScioCad-" + pid,
                ["auth_token"] = "t",
                ["pid"] = pid,
                ["addin_version"] = "1.1.0",
                ["commands"] = new JArray("scio_health", "cad_context", "cad_censo", "c3d_inspect", "cad_write")
            };
            File.WriteAllText(Path.Combine(_cadDir, $"cad-{year}-{pid}.json"), o.ToString());
            if (alive) lock (_alive) _alive.Add(pid);
        }

        [Fact]
        public void Revit_and_Civil3D_coexist_and_resolve_independently()
        {
            PublishRevit(2026, 1001, alive: true);
            PublishCad("civil3d", "2025", 2002, alive: true);

            var registry = new ServerToolRegistry();
            string? revitPipe = registry.ResolveTargetPipe();
            Assert.Equal("scio-revit-v4-test-2026-1001", revitPipe);

            CadSessionBeacon? cad = registry.ResolveCadTarget(out string? refusal);
            Assert.Null(refusal);
            Assert.NotNull(cad);
            Assert.Equal("civil3d", cad!.Host);
            Assert.Equal(2002, cad.Pid);
        }

        [Fact]
        public async Task Target_tool_pins_civil3d_and_clears_with_auto()
        {
            PublishRevit(2026, 1001, alive: true);
            PublishCad("civil3d", "2025", 2002, alive: true);

            var registry = new ServerToolRegistry { MockMode = true };
            _ = new McpStdioHost(registry, "full");

            var pinReply = await registry.DispatchAsync(new ToolRequest
            {
                Tool = "scio_target",
                Arguments = new JObject { ["host"] = "civil3d" }
            }, CancellationToken.None);

            Assert.True(pinReply.Ok);
            var pinData = Assert.IsType<JObject>(pinReply.Data);
            Assert.Equal("civil3d", pinData.Value<string>("pinned_host"));
            Assert.Equal(2002, pinData.Value<int>("pinned_cad_pid"));
            Assert.Equal(2, pinData.Value<int>("targets_found"));

            var clearReply = await registry.DispatchAsync(new ToolRequest
            {
                Tool = "scio_target",
                Arguments = new JObject { ["host"] = "auto" }
            }, CancellationToken.None);

            Assert.True(clearReply.Ok);
            Assert.Null(registry.PinnedHost);
            Assert.Null(registry.PinnedCadPid);
        }

        [Fact]
        public void Stale_cad_discovery_files_are_swept()
        {
            PublishCad("autocad", "2025", 9999, alive: false);
            int swept = CadSessionRegistry.SweepStaleCadSessions();
            Assert.Equal(1, swept);
            Assert.Empty(Directory.GetFiles(_cadDir, "cad-*.json"));
        }

        [Fact]
        public void Cad_tools_are_registered_with_cad_target_host()
        {
            var registry = new ServerToolRegistry { MockMode = true };
            _ = new McpStdioHost(registry, "full");

            var desc = registry.FindDescriptor("scio_cad_context");
            Assert.NotNull(desc);
            Assert.Equal("cad", desc!.TargetHost);
            Assert.Equal("cad_context", desc.RemoteCommand);

            Assert.NotNull(registry.FindDescriptor("scio_c3d_inspect"));
            Assert.NotNull(registry.FindDescriptor("scio_cad_point_cloud"));

            var draftDesc = registry.FindDescriptor("scio_cad_draft");
            Assert.NotNull(draftDesc);
            Assert.Equal("cad", draftDesc!.TargetHost);
            Assert.Equal("cad_draft", draftDesc.RemoteCommand);
            Assert.Equal("cad", draftDesc.Pack);

            var archDesc = registry.FindDescriptor("scio_cad_arch_plan");
            Assert.NotNull(archDesc);
            Assert.Equal("cad", archDesc!.TargetHost);
            Assert.Equal("cad_arch_plan", archDesc.RemoteCommand);

            var detailDesc = registry.FindDescriptor("scio_cad_detail");
            Assert.NotNull(detailDesc);
            Assert.Equal("cad", detailDesc!.TargetHost);
            Assert.Equal("cad_detail", detailDesc.RemoteCommand);

            var mepDesc = registry.FindDescriptor("scio_cad_mep");
            Assert.NotNull(mepDesc);
            Assert.Equal("cad", mepDesc!.TargetHost);
            Assert.Equal("cad_mep", mepDesc.RemoteCommand);

            var critDesc = registry.FindDescriptor("scio_cad_criteria");
            Assert.NotNull(critDesc);
            Assert.Equal("cad", critDesc!.TargetHost);
            Assert.Equal("cad_criteria", critDesc.RemoteCommand);

            var structDesc = registry.FindDescriptor("scio_cad_struct");
            Assert.NotNull(structDesc);
            Assert.Equal("cad", structDesc!.TargetHost);
            Assert.Equal("cad_struct", structDesc.RemoteCommand);

            var civilDesc = registry.FindDescriptor("scio_cad_civil_draft");
            Assert.NotNull(civilDesc);
            Assert.Equal("cad", civilDesc!.TargetHost);
            Assert.Equal("cad_civil_draft", civilDesc.RemoteCommand);
        }
    }
}
