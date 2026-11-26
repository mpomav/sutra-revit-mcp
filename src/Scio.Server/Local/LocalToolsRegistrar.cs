using System;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using Scio.Abstractions;

namespace Scio.Server
{
    /// <summary>
    /// Registers SCIO's local-first SQLite, reporting, batch-audit, and progress-takeoff tools on Scio.Server.
    /// </summary>
    internal static class LocalToolsRegistrar
    {
        public static void RegisterAll(ServerToolRegistry registry)
        {
            Register(
                registry,
                name: "scio_query_local",
                pack: "local",
                description: "Query local SQLite runs ingested from Revit (list_runs, sql, aggregate, get_ids) with zero Revit UI-thread cost.",
                readOnly: true,
                openWorld: false,
                executor: (args, ct) => QueryLocalTool.Handle(args, ct));

            Register(
                registry,
                name: "scio_report",
                pack: "local",
                description: "Generate executive HTML or Markdown reports from local SCIO SQLite runs.",
                readOnly: false,
                openWorld: true,
                executor: (args, ct) => ReportTool.Handle(args, ct));

            Register(
                registry,
                name: "scio_batch_audit",
                pack: "local",
                description: "Cross-model audit comparing multiple local SQLite runs across projects/disciplines.",
                readOnly: false,
                openWorld: true,
                executor: (args, ct) => BatchAuditTool.Handle(args, ct));

            Register(
                registry,
                name: "scio_progress_takeoff",
                pack: "local",
                description: "Compare baseline vs current quantity takeoff runs in local SQLite and compute volumetric/area/count deltas.",
                readOnly: false,
                openWorld: true,
                executor: (args, ct) => ProgressTakeoffTool.Handle(args, ct));

            Register(
                registry,
                name: "scio_suggest_workflow",
                pack: "core",
                description: "Recommend Sutra v1.0.0 tools and workflow steps for a natural-language BIM goal (ES/EN).",
                readOnly: true,
                openWorld: false,
                executor: (args, ct) => SuggestWorkflowTool.Handle(registry, args));

            Register(
                registry,
                name: "scio_telemetry",
                pack: "local",
                description: "Strictly opt-in anonymous usage telemetry (OFF by default) plus local usage insights: status, preview, insights, flush (refused when OFF), or clear.",
                readOnly: false,
                openWorld: true,
                executor: (args, ct) => TelemetryTool.Handle(args, ct));

            Register(
                registry,
                name: "scio_repair_memory",
                pack: "local",
                description: "Local redacted failure-and-remedy memory (list, advice, observe, remedy, quarantine, release).",
                readOnly: false,
                openWorld: false,
                executor: (args, ct) => RepairMemoryTool.Handle(args, ct));

            Register(
                registry,
                name: "scio_promote_script",
                pack: "local",
                description: "Governed script promotion ladder (list, show, propose, review, approve, activate, deactivate, source, resolve, invocation, export_pyrevit).",
                readOnly: false,
                openWorld: true,
                executor: (args, ct) => PromoteScriptTool.Handle(args, ct));

            Register(
                registry,
                name: "scio_distill",
                pack: "local",
                description: "Distill repeated MCP sessions into zero-LLM pyRevit buttons and inspect tool usage/polish insights (sessions, insights, plan, generate_pyrevit).",
                readOnly: true,
                openWorld: false,
                executor: (args, ct) => DistillTool.Handle(args, ct));
        }

        private static void Register(
            ServerToolRegistry registry,
            string name,
            string pack,
            string description,
            bool readOnly,
            bool openWorld,
            Func<JObject, CancellationToken, JObject> executor)
        {
            registry.RegisterServerTool(
                new ToolDescriptor
                {
                    Name = name,
                    Pack = pack,
                    Description = description,
                    ReadOnly = readOnly,
                    OpenWorld = openWorld,
                    ServerSide = true,
                    InputSchema = new JObject
                    {
                        ["type"] = "object",
                        ["additionalProperties"] = true
                    }
                },
                (req, ct) =>
                {
                    try
                    {
                        JObject data = executor(req.Arguments ?? new JObject(), ct);
                        return Task.FromResult(ToolResponse.Success(
                            req.RequestId,
                            data,
                            $"{name} completed.",
                            readOnly ? VerificationStatus.ReadOnly : VerificationStatus.Verified,
                            hostVerified: true));
                    }
                    catch (ToolRefusalException tre)
                    {
                        return Task.FromResult(ToolResponse.Refusal(req.RequestId, "refused", tre.Message, details: tre.Details));
                    }
                    catch (Exception ex)
                    {
                        return Task.FromResult(ToolResponse.Failure(req.RequestId, "local_tool_error", ex.Message));
                    }
                });
        }
    }
}
