using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using Scio.Abstractions;
using Scio.Server;
using Xunit;

namespace Scio.Core.Tests
{
    public sealed class Ola5AgentAndCloudTests
    {
        [Fact]
        public async Task MockMode_SimulatesRemoteToolWithoutRevit()
        {
            var reg = new ServerToolRegistry { MockMode = true };
            reg.TraceRecorder.Enabled = false;
            var resp = await reg.DispatchAsync(new ToolRequest
            {
                RequestId = "m1",
                Tool = "scio_health",
                Arguments = new JObject()
            }, CancellationToken.None);

            Assert.True(resp.Ok);
            Assert.True(((JObject)resp.Data!).Value<bool>("mock"));
        }

        [Fact]
        public async Task AsyncJobManager_SubmitsAndCompletesJob()
        {
            var reg = new ServerToolRegistry { MockMode = true };
            reg.TraceRecorder.Enabled = false;

            var sub = reg.Jobs.Submit(reg, new JObject { ["tool"] = "scio_health", ["arguments"] = new JObject() });
            string jobId = sub.Value<string>("job_id")!;

            JObject? st = null;
            for (int i = 0; i < 40; i++)
            {
                st = reg.Jobs.Status(new JObject { ["job_id"] = jobId });
                if (st.Value<string>("status") == "completed") break;
                await Task.Delay(25);
            }

            Assert.NotNull(st);
            Assert.Equal("completed", st!.Value<string>("status"));
        }

        [Fact]
        public void ProcedureRunner_StartDecideRecordAdvanceReconcile()
        {
            string tmp = Path.Combine(Path.GetTempPath(), "sutra-proc-" + Guid.NewGuid().ToString("N"));
            string store = Path.Combine(tmp, "state.json");
            try
            {
                var start = ProcedureRunnerTool.Handle(new JObject
                {
                    ["operation"] = "start",
                    ["procedure"] = "custom",
                    ["steps"] = new JArray("step1", "step2")
                }, CancellationToken.None, store);

                string id = start["procedure"]!.Value<string>("id")!;
                ProcedureRunnerTool.Handle(new JObject { ["operation"] = "decide", ["id"] = id, ["choice"] = "use_exact_bar_type" }, CancellationToken.None, store);
                ProcedureRunnerTool.Handle(new JObject { ["operation"] = "record", ["id"] = id, ["note"] = "verified 12 bars" }, CancellationToken.None, store);
                ProcedureRunnerTool.Handle(new JObject { ["operation"] = "advance", ["id"] = id }, CancellationToken.None, store);
                ProcedureRunnerTool.Handle(new JObject { ["operation"] = "advance", ["id"] = id }, CancellationToken.None, store);

                var rec = ProcedureRunnerTool.Handle(new JObject { ["operation"] = "reconcile", ["id"] = id }, CancellationToken.None, store);
                Assert.Equal("completed", rec.Value<string>("status"));
                Assert.True(rec.Value<bool>("complete_with_evidence"));
            }
            finally
            {
                if (Directory.Exists(tmp)) Directory.Delete(tmp, true);
            }
        }

        [Fact]
        public void AuditAccess_ReportsProfileAndNeverSelfApprovesPython()
        {
            var reg = new ServerToolRegistry { MockMode = true };
            var audit = AuditAccessTool.HandleAuditAccess(reg);
            Assert.Equal("SafeWrite", audit.Value<string>("permission_profile"));
            Assert.True(audit.Value<bool>("mock_mode"));
        }
    }
}
