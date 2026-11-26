using System;
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
    /// Permission governance tools: scio_audit_access and scio_request_python_access.
    /// Never self-approves Python execution; only the machine owner grants persistent access.
    /// </summary>
    public static class AuditAccessTool
    {
        public static JObject HandleAuditAccess(ServerToolRegistry registry)
        {
            bool pyAuthorized = IsPythonAuthorized();
            return new JObject
            {
                ["permission_profile"] = registry.PermissionProfile.ToString(),
                ["schema_mode"] = registry.SchemaMode,
                ["mock_mode"] = registry.MockMode,
                ["active_packs"] = new JArray(registry.ActivePacks.OrderBy(p => p, StringComparer.Ordinal)),
                ["visible_tool_count"] = registry.ListVisibleTools().Count,
                ["python_authorized"] = pyAuthorized,
                ["telemetry_opt_in"] = TelemetryTool.IsOptedIn(),
                ["trace_recording_enabled"] = registry.TraceRecorder.Enabled
            };
        }

        public static JObject HandleRequestPythonAccess(JObject args, CancellationToken ct)
        {
            string op = (args?.Value<string>("operation") ?? "status").Trim().ToLowerInvariant();
            bool authorized = IsPythonAuthorized();

            if (op == "status")
            {
                return new JObject
                {
                    ["operation"] = "status",
                    ["python_authorized"] = authorized,
                    ["owner_approval_required"] = !authorized
                };
            }

            if (op != "request")
                throw new ToolRefusalException($"Unsupported operation '{op}'. Expected: status, request.");

            if (authorized)
            {
                return new JObject
                {
                    ["operation"] = "request",
                    ["python_authorized"] = true,
                    ["status"] = "already_authorized"
                };
            }

            string reason = TraceRedactor.RedactString(args?.Value<string>("reason") ?? "Client requested owner approval for scio_execute_python.");
            string reqPath = Path.Combine(ScioRuntimePaths.DataRoot, "python-access-request.json");
            Directory.CreateDirectory(ScioRuntimePaths.DataRoot);
            var payload = new JObject
            {
                ["requested_utc"] = DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture),
                ["reason"] = reason,
                ["status"] = "pending_owner_approval"
            };
            File.WriteAllText(reqPath, payload.ToString(Formatting.Indented), new UTF8Encoding(false));

            return new JObject
            {
                ["operation"] = "request",
                ["python_authorized"] = false,
                ["status"] = "pending_owner_approval",
                ["request_file"] = reqPath,
                ["instruction"] = "Only the machine owner can enable Python via the Sutra ribbon button or settings.json."
            };
        }

        private static bool IsPythonAuthorized()
        {
            try
            {
                string path = Path.Combine(ScioRuntimePaths.DataRoot, "settings.json");
                if (!File.Exists(path)) return false;
                var obj = JObject.Parse(File.ReadAllText(path, Encoding.UTF8));
                return obj["allow_execute_python"]?.Type == JTokenType.Boolean && (bool)obj["allow_execute_python"]!;
            }
            catch { return false; }
        }
    }
}
