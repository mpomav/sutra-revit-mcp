using System;
using System.IO;
using System.Linq;
using System.Threading;
using Newtonsoft.Json.Linq;
using Scio.Abstractions;
using Scio.Server;
using Xunit;

namespace Scio.Core.Tests
{
    public sealed class Ola0DistillTests
    {
        [Fact]
        public void F0_RebarBarTypeMatcher_RejectsSubstring6When6mmAnd16mmLoaded()
        {
            var loaded = new[] { "6mm", "16mm", "5/8\"" };

            var sub = RebarBarTypeMatcher.MatchExact("6", loaded);
            Assert.False(sub.Ok);
            Assert.Equal("unknown_bar_type", sub.ErrorCode);
            Assert.Contains("6mm", sub.AvailableTypes);
            Assert.Contains("16mm", sub.AvailableTypes);

            var empty = RebarBarTypeMatcher.MatchExact("", loaded);
            Assert.False(empty.Ok);
            Assert.Equal("missing_bar_type", empty.ErrorCode);

            var exact = RebarBarTypeMatcher.MatchExact("6mm", loaded);
            Assert.True(exact.Ok);
            Assert.Equal("6mm", exact.MatchedName);
        }

        [Fact]
        public void ArgumentValidator_ReportsMissingAndDidYouMean()
        {
            var desc = new ToolDescriptor
            {
                Name = "scio_test_tool",
                InputSchema = new JObject
                {
                    ["type"] = "object",
                    ["properties"] = new JObject
                    {
                        ["bar_type"] = new JObject { ["type"] = "string" },
                        ["spacing_mm"] = new JObject { ["type"] = "number" }
                    },
                    ["required"] = new JArray("bar_type"),
                    ["additionalProperties"] = false
                }
            };

            var res = ArgumentValidator.Validate(desc, new JObject { ["spacing_m"] = 150 });
            Assert.False(res.Ok);
            Assert.Contains("bar_type", res.Missing);
            Assert.Single(res.Unknown);
            Assert.Equal("spacing_mm", res.Unknown[0].DidYouMean);
        }

        [Fact]
        public void TraceRedactor_RedactsPathsAndFiles_PreservesEngineeringNumbers()
        {
            var input = new JObject
            {
                ["target_document"] = "C:\\Proyectos\\TorreA\\Estructura_v3.rvt",
                ["note"] = "Exported to C:\\Users\\marco\\Desktop\\plan.pdf by marco@scio.pe",
                ["count"] = 6,
                ["spacing_mm"] = 150.0,
                ["bar_type"] = "16mm"
            };

            var redacted = (JObject)TraceRedactor.RedactToken(input);
            Assert.DoesNotContain("TorreA", redacted.ToString());
            Assert.DoesNotContain("marco@scio.pe", redacted.ToString());
            Assert.Equal(6, redacted.Value<int>("count"));
            Assert.Equal(150.0, redacted.Value<double>("spacing_mm"));
            Assert.Equal("16mm", redacted.Value<string>("bar_type"));
        }

        [Fact]
        public void RepairMemory_ObserveRemedyAdviceAndQuarantine()
        {
            string tmp = Path.Combine(Path.GetTempPath(), "sutra-rm-" + Guid.NewGuid().ToString("N"));
            string store = Path.Combine(tmp, "memory.json");
            try
            {
                var obs = RepairMemoryTool.Handle(new JObject
                {
                    ["operation"] = "observe",
                    ["tool"] = "scio_rebar_create",
                    ["error_code"] = "unknown_bar_type",
                    ["symptom"] = "No exact RebarBarType named '6' in C:\\Proj\\Modelo.rvt"
                }, CancellationToken.None, store);

                string id = obs["entry"]!.Value<string>("id")!;
                Assert.DoesNotContain("Modelo.rvt", obs["entry"]!.Value<string>("symptom")!);

                RepairMemoryTool.Handle(new JObject
                {
                    ["operation"] = "remedy",
                    ["id"] = id,
                    ["remedy"] = "Use exact RebarBarType name such as '6mm' from scio_rebar_inspect."
                }, CancellationToken.None, store);

                var advice1 = RepairMemoryTool.Handle(new JObject
                {
                    ["operation"] = "advice",
                    ["tool"] = "scio_rebar_create",
                    ["error_code"] = "unknown_bar_type"
                }, CancellationToken.None, store);
                Assert.Equal(1, advice1.Value<int>("match_count"));

                RepairMemoryTool.Handle(new JObject
                {
                    ["operation"] = "quarantine",
                    ["id"] = id
                }, CancellationToken.None, store);

                var advice2 = RepairMemoryTool.Handle(new JObject
                {
                    ["operation"] = "advice",
                    ["tool"] = "scio_rebar_create",
                    ["error_code"] = "unknown_bar_type"
                }, CancellationToken.None, store);
                Assert.Equal(0, advice2.Value<int>("match_count"));
            }
            finally
            {
                if (Directory.Exists(tmp)) Directory.Delete(tmp, true);
            }
        }

        [Fact]
        public void F6_PromoteScript_RefusesExportUntilActive_ThenExportsPyRevitButton()
        {
            string tmp = Path.Combine(Path.GetTempPath(), "sutra-ps-" + Guid.NewGuid().ToString("N"));
            string store = Path.Combine(tmp, "registry.json");
            string extRoot = Path.Combine(tmp, "SCIO-Local.extension");
            try
            {
                var prop = PromoteScriptTool.Handle(new JObject
                {
                    ["operation"] = "propose",
                    ["name"] = "Aceros_Columnas",
                    ["source"] = "print('hello rebar')\n",
                    ["min_permission_profile"] = "SafeWrite"
                }, CancellationToken.None, store);
                string id = prop["generation"]!.Value<string>("id")!;

                // Must refuse export when only proposed
                Assert.Throws<ToolRefusalException>(() => PromoteScriptTool.Handle(new JObject
                {
                    ["operation"] = "export_pyrevit",
                    ["id"] = id,
                    ["extension_root"] = extRoot
                }, CancellationToken.None, store));

                PromoteScriptTool.Handle(new JObject { ["operation"] = "review", ["id"] = id }, CancellationToken.None, store);
                PromoteScriptTool.Handle(new JObject { ["operation"] = "approve", ["id"] = id }, CancellationToken.None, store);
                PromoteScriptTool.Handle(new JObject { ["operation"] = "activate", ["id"] = id }, CancellationToken.None, store);

                var exported = PromoteScriptTool.Handle(new JObject
                {
                    ["operation"] = "export_pyrevit",
                    ["id"] = id,
                    ["extension_root"] = extRoot
                }, CancellationToken.None, store);

                Assert.True(File.Exists(exported.Value<string>("script_path")!));
                Assert.True(File.Exists(exported.Value<string>("bundle_path")!));
            }
            finally
            {
                if (Directory.Exists(tmp)) Directory.Delete(tmp, true);
            }
        }

        [Fact]
        public void F3_F4_Distill_LocksEngineeringParamsAsInputs_GeneratesPyRevitUnder80Lines_AndProducesInsights()
        {
            string tmp = Path.Combine(Path.GetTempPath(), "sutra-distill-" + Guid.NewGuid().ToString("N"));
            try
            {
                // Record 3 steel sessions with same engineering params (still must NOT freeze as constants per D4)
                for (int s = 1; s <= 3; s++)
                {
                    var rec = new SessionTraceRecorder(tmp, $"steel-session-{s}");
                    rec.Record(
                        new ToolRequest { Tool = "scio_rebar_inspect", Arguments = new JObject { ["host_id"] = 1000 + s } },
                        ToolResponse.Success("r1", new JObject { ["total_sets"] = 2 }, "ok", VerificationStatus.ReadOnly),
                        45);
                    rec.Record(
                        new ToolRequest
                        {
                            Tool = "scio_rebar_create",
                            Arguments = new JObject
                            {
                                ["host_id"] = 1000 + s,
                                ["bar_type"] = "16mm",
                                ["count"] = 4,
                                ["spacing_mm"] = 150,
                                ["dry_run"] = false
                            }
                        },
                        ToolResponse.Success("r2", new JObject { ["rebar_id"] = 2000 + s, ["quantity"] = 4 }, "ok", VerificationStatus.Verified),
                        120);
                }

                var plan = DistillTool.Handle(new JObject
                {
                    ["operation"] = "plan",
                    ["flow_name"] = "Aceros_Vigas",
                    ["session_ids"] = new JArray("steel-session-1", "steel-session-2", "steel-session-3")
                }, CancellationToken.None, tmp);

                var inputNames = ((JArray)plan["inputs"]!).Select(x => x.Value<string>("name")).ToList();
                var constNames = ((JArray)plan["constants"]!).Select(x => x.Value<string>("name")).ToList();
                Assert.Contains("host_id", inputNames);
                Assert.Contains("bar_type", inputNames);
                Assert.Contains("count", inputNames);
                Assert.Contains("spacing_mm", inputNames);
                Assert.DoesNotContain("bar_type", constNames);
                Assert.DoesNotContain("count", constNames);
                Assert.DoesNotContain("spacing_mm", constNames);

                var gen = DistillTool.Handle(new JObject
                {
                    ["operation"] = "generate_pyrevit",
                    ["plan"] = plan
                }, CancellationToken.None, tmp);

                Assert.True(gen.Value<bool>("within_80_line_gate"));
                Assert.True(gen.Value<bool>("auto_detects_revit_year_locally"));
                Assert.Contains("REVIT_YEAR = int(__revit__.Application.VersionNumber)", gen.Value<string>("source")!);

                var insights = DistillTool.Handle(new JObject { ["operation"] = "insights" }, CancellationToken.None, tmp);
                Assert.Equal(3, insights.Value<int>("sessions_analyzed"));
                Assert.Contains((JArray)insights["distill_candidates"]!, c => c.Value<bool>("ready_for_auto_distill"));
            }
            finally
            {
                if (Directory.Exists(tmp)) Directory.Delete(tmp, true);
            }
        }
    }
}
