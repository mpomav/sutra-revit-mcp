using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Scio.Abstractions;

namespace Scio.Server
{
    /// <summary>
    /// Distills repeated MCP sessions into deterministic pyRevit button wrappers (scio_distill)
    /// and computes local usage/polish insights from recorded traces + telemetry.
    /// Operations: sessions, insights, plan, generate_pyrevit.
    /// </summary>
    public static class DistillTool
    {
        public const string ToolName = "scio_distill";

        // D4 rule: engineering parameters must NEVER be frozen as constants.
        private static readonly string[] EngineeringParamPrefixes =
        {
            "cover", "bar_type", "count", "spacing", "diameter", "thickness", "length", "width", "height", "slope"
        };

        // Tools whose arguments typically require human / plan judgment (Step C).
        private static readonly HashSet<string> JudgmentTools = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "scio_cad_inspect", "scio_capture_view", "scio_execute_python", "scio_structural_pdf_schedule"
        };

        public static string DefaultTracesDir() => Path.Combine(ScioRuntimePaths.DataRoot, "traces");

        public static JObject Handle(JObject args, CancellationToken ct, string? tracesDirOverride = null)
        {
            string op = (args?.Value<string>("operation") ?? "sessions").Trim().ToLowerInvariant();
            string tracesDir = string.IsNullOrWhiteSpace(tracesDirOverride) ? DefaultTracesDir() : tracesDirOverride!;

            switch (op)
            {
                case "sessions": return HandleSessions(args, tracesDir);
                case "insights": return HandleInsights(args, tracesDir);
                case "plan": return HandlePlan(args, tracesDir);
                case "generate_pyrevit": return HandleGeneratePyRevit(args);
                default:
                    throw new ToolRefusalException(
                        $"Unsupported operation '{op}'. Expected: sessions, insights, plan, generate_pyrevit.");
            }
        }

        private static JObject HandleSessions(JObject? args, string tracesDir)
        {
            int limit = Math.Max(1, Math.Min(200, args?.Value<int?>("limit") ?? 30));
            var ids = SessionTraceRecorder.ListSessionIds(tracesDir, limit);
            var arr = new JArray();
            foreach (string id in ids)
            {
                var steps = SessionTraceRecorder.ReadSession(tracesDir, id);
                arr.Add(new JObject
                {
                    ["session_id"] = id,
                    ["step_count"] = steps.Count,
                    ["tools"] = new JArray(steps.Select(s => s.Value<string>("tool")).Where(t => !string.IsNullOrEmpty(t)).Distinct())
                });
            }
            return new JObject { ["operation"] = "sessions", ["count"] = arr.Count, ["sessions"] = arr };
        }

        private static JObject HandleInsights(JObject? args, string tracesDir)
        {
            int maxSessions = Math.Max(1, Math.Min(500, args?.Value<int?>("limit") ?? 100));
            var sessionIds = SessionTraceRecorder.ListSessionIds(tracesDir, maxSessions);

            var toolStats = new Dictionary<string, (int Calls, int Failures, long TotalMs)>(StringComparer.Ordinal);
            var sequenceCounts = new Dictionary<string, int>(StringComparer.Ordinal);

            foreach (string sid in sessionIds)
            {
                var steps = SessionTraceRecorder.ReadSession(tracesDir, sid);
                var seqTools = new List<string>();
                foreach (var s in steps)
                {
                    string tool = s.Value<string>("tool") ?? string.Empty;
                    if (string.IsNullOrWhiteSpace(tool)) continue;
                    seqTools.Add(tool);

                    bool ok = s.Value<bool?>("ok") ?? true;
                    long ms = s.Value<long?>("duration_ms") ?? 0;
                    toolStats.TryGetValue(tool, out var cur);
                    toolStats[tool] = (cur.Calls + 1, cur.Failures + (ok ? 0 : 1), cur.TotalMs + ms);
                }

                if (seqTools.Count >= 2)
                {
                    string sig = string.Join(" -> ", seqTools);
                    sequenceCounts.TryGetValue(sig, out int c);
                    sequenceCounts[sig] = c + 1;
                }
            }

            var topTools = new JArray(toolStats
                .OrderByDescending(kv => kv.Value.Calls)
                .Take(25)
                .Select(kv => new JObject
                {
                    ["tool"] = kv.Key,
                    ["calls"] = kv.Value.Calls,
                    ["failures"] = kv.Value.Failures,
                    ["failure_rate_pct"] = kv.Value.Calls > 0 ? Math.Round(100.0 * kv.Value.Failures / kv.Value.Calls, 1) : 0.0,
                    ["avg_ms"] = kv.Value.Calls > 0 ? Math.Round((double)kv.Value.TotalMs / kv.Value.Calls, 1) : 0.0
                }));

            var polishPriority = new JArray(toolStats
                .Where(kv => kv.Value.Failures > 0 || (kv.Value.Calls > 0 && kv.Value.TotalMs / kv.Value.Calls >= 2000))
                .OrderByDescending(kv => kv.Value.Failures)
                .ThenByDescending(kv => kv.Value.TotalMs / Math.Max(1, kv.Value.Calls))
                .Take(10)
                .Select(kv => new JObject
                {
                    ["tool"] = kv.Key,
                    ["calls"] = kv.Value.Calls,
                    ["failures"] = kv.Value.Failures,
                    ["avg_ms"] = Math.Round((double)kv.Value.TotalMs / Math.Max(1, kv.Value.Calls), 1)
                }));

            var distillCandidates = new JArray(sequenceCounts
                .Where(kv => kv.Value >= 2)
                .OrderByDescending(kv => kv.Value)
                .Take(10)
                .Select(kv => new JObject
                {
                    ["sequence"] = kv.Key,
                    ["repetitions"] = kv.Value,
                    ["ready_for_auto_distill"] = kv.Value >= 3
                }));

            return new JObject
            {
                ["operation"] = "insights",
                ["sessions_analyzed"] = sessionIds.Count,
                ["top_tools"] = topTools,
                ["polish_priority"] = polishPriority,
                ["distill_candidates"] = distillCandidates
            };
        }

        private static JObject HandlePlan(JObject? args, string tracesDir)
        {
            var sidArr = args?["session_ids"] as JArray;
            var sessionIds = sidArr?.Select(t => t?.ToString() ?? string.Empty).Where(s => !string.IsNullOrWhiteSpace(s)).ToList()
                             ?? new List<string>();
            if (sessionIds.Count == 0)
                throw new ToolRefusalException("'session_ids' (array of 1+ recorded session IDs) is required for plan.");

            var sessions = sessionIds.Select(id => SessionTraceRecorder.ReadSession(tracesDir, id)).Where(s => s.Count > 0).ToList();
            if (sessions.Count == 0)
                throw new ToolRefusalException("None of the requested session_ids contained trace entries.");

            string flowName = (args?.Value<string>("flow_name") ?? "distilled_flow").Trim();
            string criteriaSource = (args?.Value<string>("criteria_source") ?? "ask").Trim().ToLowerInvariant();
            if (criteriaSource != "cad_pdf" && criteriaSource != "conversation" && criteriaSource != "both" && criteriaSource != "ask")
                criteriaSource = "ask";

            int minSteps = sessions.Min(s => s.Count);
            var plannedSteps = new JArray();
            var inputs = new JArray();
            var constants = new JArray();
            var seenInputs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            int cCount = 0;

            for (int i = 0; i < minSteps; i++)
            {
                var aligned = sessions.Select(s => s[i]).ToList();
                string tool = aligned[0].Value<string>("tool") ?? string.Empty;
                bool allSameTool = aligned.All(a => string.Equals(a.Value<string>("tool"), tool, StringComparison.Ordinal));
                bool isJudgment = !allSameTool || JudgmentTools.Contains(tool);
                string kind = isJudgment ? "C" : "D";
                if (isJudgment) cCount++;

                var stepInputs = new JArray();
                var stepConstants = new JObject();
                var allArgKeys = aligned
                    .SelectMany(a => (a["arguments"] as JObject)?.Properties().Select(p => p.Name) ?? Enumerable.Empty<string>())
                    .Distinct(StringComparer.Ordinal)
                    .OrderBy(k => k, StringComparer.Ordinal);

                foreach (string key in allArgKeys)
                {
                    if (string.Equals(key, "dry_run", StringComparison.OrdinalIgnoreCase)) continue;

                    var values = aligned
                        .Select(a => (a["arguments"] as JObject)?[key]?.ToString(Formatting.None) ?? "null")
                        .Distinct(StringComparer.Ordinal)
                        .ToList();

                    bool isElementId = key.EndsWith("_id", StringComparison.OrdinalIgnoreCase) ||
                                       key.EndsWith("_ids", StringComparison.OrdinalIgnoreCase) ||
                                       string.Equals(key, "element_ids", StringComparison.OrdinalIgnoreCase);
                    bool isEngineering = IsEngineeringParameter(key);
                    bool varies = values.Count > 1;

                    if (isElementId || isEngineering || varies || isJudgment)
                    {
                        string sourceKind = isElementId ? "revit_selection" : (isJudgment ? "prompt_judgment" : "prompt_input");
                        stepInputs.Add(new JObject
                        {
                            ["name"] = key,
                            ["source"] = sourceKind,
                            ["engineering_locked_input"] = isEngineering,
                            ["sample_value"] = (aligned[0]["arguments"] as JObject)?[key]
                        });

                        if (seenInputs.Add(key))
                        {
                            inputs.Add(new JObject
                            {
                                ["name"] = key,
                                ["source"] = sourceKind,
                                ["engineering_locked_input"] = isEngineering,
                                ["required"] = true
                            });
                        }
                    }
                    else
                    {
                        JToken? sample = (aligned[0]["arguments"] as JObject)?[key];
                        stepConstants[key] = sample;
                        constants.Add(new JObject
                        {
                            ["step"] = i + 1,
                            ["tool"] = tool,
                            ["name"] = key,
                            ["value"] = sample
                        });
                    }
                }

                plannedSteps.Add(new JObject
                {
                    ["step"] = i + 1,
                    ["tool"] = tool,
                    ["classification"] = kind,
                    ["inputs"] = stepInputs,
                    ["constants"] = stepConstants
                });
            }

            double cRatio = minSteps > 0 ? Math.Round(100.0 * cCount / minSteps, 1) : 0.0;
            return new JObject
            {
                ["operation"] = "plan",
                ["flow_name"] = flowName,
                ["sessions_used"] = sessions.Count,
                ["criteria_source"] = criteriaSource,
                ["ask_criteria_source_if_ambiguous"] = criteriaSource == "ask" || criteriaSource == "both",
                ["step_count"] = minSteps,
                ["judgment_steps_c"] = cCount,
                ["judgment_ratio_pct"] = cRatio,
                ["viable_for_distill"] = cRatio <= 50.0,
                ["inputs"] = inputs,
                ["constants"] = constants,
                ["steps"] = plannedSteps,
                ["contract"] = new JObject
                {
                    ["inputs"] = inputs.DeepClone(),
                    ["output"] = new JObject { ["verification"] = "Verified", ["summary"] = "string" }
                }
            };
        }

        private static JObject HandleGeneratePyRevit(JObject? args)
        {
            var plan = args?["plan"] as JObject;
            if (plan == null)
                throw new ToolRefusalException("'plan' (output of scio_distill operation='plan') is required.");

            string flowName = plan.Value<string>("flow_name") ?? "distilled_flow";
            var steps = plan["steps"] as JArray ?? new JArray();
            var firstTool = steps.FirstOrDefault()?.Value<string>("tool") ?? "scio_rebar_create";
            string moduleDll = InferModuleDll(firstTool);

            // Clean <= 80-line pyRevit wrapper: auto-detects Revit year locally (0 LLM tokens),
            // previews with dry_run=True, requires explicit Yes/No confirmation (default No),
            // attaches a per-run UUID, and alerts if verification != Verified.
            var sb = new StringBuilder();
            sb.AppendLine("# -*- coding: utf-8 -*-");
            sb.AppendLine($"\"\"\"Distilled Sutra flow: {flowName} (runs locally without LLM).\"\"\"");
            sb.AppendLine("import os, uuid, clr");
            sb.AppendLine("from pyrevit import revit, forms, script");
            sb.AppendLine("from Newtonsoft.Json.Linq import JObject");
            sb.AppendLine();
            sb.AppendLine("REVIT_YEAR = int(__revit__.Application.VersionNumber)");
            sb.AppendLine("ADDIN_DIR = os.path.join(os.environ['APPDATA'], 'Autodesk', 'Revit', 'Addins', str(REVIT_YEAR), 'Scio')");
            sb.AppendLine("for dll in ('Scio.Abstractions.dll', 'Scio.Revit.Sdk.dll', '" + moduleDll + "'):");
            sb.AppendLine("    clr.AddReferenceToFileAndPath(os.path.join(ADDIN_DIR, dll))");
            sb.AppendLine();
            sb.AppendLine("from Scio.Abstractions import PermissionProfile, ToolRequest");
            sb.AppendLine("from Scio.Revit.Sdk import RevitContext");
            sb.AppendLine("from Scio.Module.Structure import RebarCreateCommand");
            sb.AppendLine();
            sb.AppendLine("doc = revit.doc");
            sb.AppendLine("hosts = revit.pick_elements(message='Select concrete host elements')");
            sb.AppendLine("if not hosts:");
            sb.AppendLine("    script.exit()");
            sb.AppendLine();
            sb.AppendLine("bar_types = sorted([b.Name for b in revit.DB.FilteredElementCollector(doc).OfClass(revit.DB.Structure.RebarBarType)])");
            sb.AppendLine("bar_type = forms.SelectFromList.show(bar_types, title='Select exact RebarBarType')");
            sb.AppendLine("if not bar_type:");
            sb.AppendLine("    script.exit()");
            sb.AppendLine("count = int(forms.ask_for_string(default='4', prompt='Bar count (integer):') or '0')");
            sb.AppendLine("spacing_mm = float(forms.ask_for_string(default='150', prompt='Spacing (mm):') or '0')");
            sb.AppendLine("if count < 1 or spacing_mm < 25:");
            sb.AppendLine("    forms.alert('Invalid count or spacing_mm.', exitscript=True)");
            sb.AppendLine();
            sb.AppendLine("ctx = RevitContext(__revit__, PermissionProfile.SafeWrite, REVIT_YEAR)");
            sb.AppendLine("cmd = RebarCreateCommand()");
            sb.AppendLine("run_id = str(uuid.uuid4())");
            sb.AppendLine();
            sb.AppendLine("def build_req(host, dry_run):");
            sb.AppendLine("    a = JObject()");
            sb.AppendLine("    a['host_id'] = host.Id.Value if hasattr(host.Id, 'Value') else host.Id.IntegerValue");
            sb.AppendLine("    a['bar_type'] = bar_type");
            sb.AppendLine("    a['count'] = count");
            sb.AppendLine("    a['spacing_mm'] = spacing_mm");
            sb.AppendLine("    a['dry_run'] = dry_run");
            sb.AppendLine("    a['idempotency_key'] = run_id");
            sb.AppendLine("    r = ToolRequest()");
            sb.AppendLine($"    r.Tool = '{firstTool}'");
            sb.AppendLine("    r.Arguments = a");
            sb.AppendLine("    r.DryRun = dry_run");
            sb.AppendLine("    return r");
            sb.AppendLine();
            sb.AppendLine("previews = [cmd.Execute(ctx, build_req(h, True)) for h in hosts]");
            sb.AppendLine("ok_prev = sum(1 for p in previews if p.Ok)");
            sb.AppendLine("if not forms.alert('Preview: {}/{} host(s) ready. Apply changes?'.format(ok_prev, len(hosts)), yes=True, no=True, ok=False):");
            sb.AppendLine("    script.exit()");
            sb.AppendLine();
            sb.AppendLine("results = [cmd.Execute(ctx, build_req(h, False)) for h in hosts]");
            sb.AppendLine("unverified = [r for r in results if (not r.Ok) or str(r.Verification).lower() != 'verified']");
            sb.AppendLine("if unverified:");
            sb.AppendLine("    forms.alert('WARNING: {} operation(s) NOT verified. Inspect model.'.format(len(unverified)), warn_icon=True)");
            sb.AppendLine("else:");
            sb.AppendLine("    forms.alert('Verified: {} element(s) updated (run {}).'.format(len(results), run_id[:8]))");

            string scriptText = sb.ToString();
            int lineCount = scriptText.Split('\n').Length;

            return new JObject
            {
                ["operation"] = "generate_pyrevit",
                ["flow_name"] = flowName,
                ["line_count"] = lineCount,
                ["within_80_line_gate"] = lineCount <= 80,
                ["auto_detects_revit_year_locally"] = true,
                ["module_dll"] = moduleDll,
                ["source"] = scriptText
            };
        }

        public static bool IsEngineeringParameter(string paramName)
        {
            if (string.IsNullOrWhiteSpace(paramName)) return false;
            string lower = paramName.Trim().ToLowerInvariant();
            return EngineeringParamPrefixes.Any(p => lower.StartsWith(p, StringComparison.Ordinal) || lower.Contains("_" + p));
        }

        private static string InferModuleDll(string toolName)
        {
            if (toolName.Contains("rebar") || toolName.Contains("structure") || toolName.Contains("framing"))
                return "Scio.Module.Structure.dll";
            if (toolName.Contains("mep"))
                return "Scio.Module.Mep.dll";
            if (toolName.Contains("view") || toolName.Contains("sheet") || toolName.Contains("tag") || toolName.Contains("dimension"))
                return "Scio.Module.Documentation.dll";
            return "Scio.Module.Modeling.dll";
        }
    }
}
