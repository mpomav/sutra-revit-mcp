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
                description: "Strictly opt-in anonymous usage telemetry (OFF by default) with automatic periodic/shutdown sending plus local usage insights: status, preview, insights, auto_flush, flush (refused when OFF), or clear.",
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

            Register(
                registry,
                name: "scio_submit_job",
                pack: "core",
                description: "Submit a long-running tool call asynchronously and receive a job_id to poll with scio_job_status.",
                readOnly: false,
                openWorld: false,
                executor: (args, ct) => registry.Jobs.Submit(registry, args));

            Register(
                registry,
                name: "scio_job_status",
                pack: "core",
                description: "Poll status/result of an asynchronous job submitted via scio_submit_job (or list recent jobs).",
                readOnly: true,
                openWorld: false,
                executor: (args, ct) => registry.Jobs.Status(args));

            Register(
                registry,
                name: "scio_run_procedure",
                pack: "local",
                description: "Stateful guided BIM procedure runner (start, advance, decide, record, reconcile, status, abandon) with built-in QA playbooks.",
                readOnly: false,
                openWorld: false,
                executor: (args, ct) => ProcedureRunnerTool.Handle(args, ct));

            Register(
                registry,
                name: "scio_audit_access",
                pack: "core",
                description: "Inspect active permission profile, tool packs, schema mode, mock mode, Python authorization, and telemetry state.",
                readOnly: true,
                openWorld: false,
                executor: (args, ct) => AuditAccessTool.HandleAuditAccess(registry));

            Register(
                registry,
                name: "scio_request_python_access",
                pack: "core",
                description: "Check Python execution authorization status or log an owner-approval request (never self-approves).",
                readOnly: false,
                openWorld: false,
                executor: (args, ct) => AuditAccessTool.HandleRequestPythonAccess(args, ct));

            registry.RegisterServerTool(
                new ToolDescriptor
                {
                    Name = "scio_plan_from_image",
                    Pack = "local",
                    Description = "Host-resident read-only raster-to-vector planner (Otsu + Zhang-Suen thinning + Douglas-Peucker) with explicit scale calibration producing segments for scio_create_detail_lines or wall candidates for scio_create_elements.",
                    ReadOnly = true,
                    OpenWorld = false,
                    InputSchema = new JObject
                    {
                        ["type"] = "object",
                        ["properties"] = new JObject
                        {
                            ["image_path"] = new JObject { ["type"] = "string", ["description"] = "Absolute path to PNG/JPG/BMP/TIFF image file." },
                            ["mm_per_pixel"] = new JObject { ["type"] = "number", ["description"] = "Explicit scale calibration in millimeters per pixel." },
                            ["calibration_point_1_px"] = new JObject { ["type"] = "array", ["description"] = "First calibration point [x, y] in pixels." },
                            ["calibration_point_2_px"] = new JObject { ["type"] = "array", ["description"] = "Second calibration point [x, y] in pixels." },
                            ["known_distance_mm"] = new JObject { ["type"] = "number", ["description"] = "Known real-world distance in mm between the two calibration points." },
                            ["origin_x_mm"] = new JObject { ["type"] = "number", ["description"] = "Optional model X origin in mm (default 0)." },
                            ["origin_y_mm"] = new JObject { ["type"] = "number", ["description"] = "Optional model Y origin in mm (default 0)." },
                            ["threshold_mode"] = new JObject { ["type"] = "string", ["enum"] = new JArray("otsu", "manual"), ["description"] = "Binarization mode (default otsu)." },
                            ["manual_threshold"] = new JObject { ["type"] = "integer", ["description"] = "Manual grayscale threshold 0..255 (default 128)." },
                            ["dark_foreground"] = new JObject { ["type"] = "boolean", ["description"] = "True if lines/walls are dark on light background (default true)." },
                            ["min_segment_mm"] = new JObject { ["type"] = "number", ["description"] = "Minimum vectorized segment length in mm (default 150)." },
                            ["simplify_tolerance_mm"] = new JObject { ["type"] = "number", ["description"] = "Douglas-Peucker tolerance in mm (default 25)." },
                            ["min_wall_thickness_mm"] = new JObject { ["type"] = "number", ["description"] = "Minimum parallel wall thickness in mm (default 80)." },
                            ["max_wall_thickness_mm"] = new JObject { ["type"] = "number", ["description"] = "Maximum parallel wall thickness in mm (default 450)." },
                            ["max_segments"] = new JObject { ["type"] = "integer", ["description"] = "Maximum segments to return (default 300)." },
                            ["target"] = new JObject { ["type"] = "string", ["enum"] = new JArray("detail_lines", "walls"), ["description"] = "Intended downstream target (default detail_lines)." }
                        },
                        ["required"] = new JArray("image_path"),
                        ["additionalProperties"] = false
                    }
                },
                (req, ct) =>
                {
                    var argsCopy = (req.Arguments?.DeepClone() as JObject) ?? new JObject();
                    argsCopy["_request_id"] = req.RequestId;
                    return PlanFromImageTool.Handle(argsCopy, ct);
                });
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
