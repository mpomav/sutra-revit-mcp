#nullable disable
// -----------------------------------------------------------------------------
// SCIO Revit MCP - Generic Local Plan Store (%USERPROFILE%\.scio\plans\)
//
// Keeps bulky planner outputs (execute_plan, plan_from_ifc, plan_reinforcement,
// plan_views) on local disk under a compact plan_id with SHA-256 verification so
// clients apply them without echoing thousands of lines across MCP turns.
// Revit-free and net48-safe.
// -----------------------------------------------------------------------------
using System;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace Scio.Abstractions
{
    public static class LocalPlanStore
    {
        public static readonly TimeSpan KeepFor = TimeSpan.FromDays(14);

        public static string DefaultRoot => Path.Combine(ScioPaths.DataRoot(), "plans");

        public static string SanitizePlanId(string raw)
        {
            if (string.IsNullOrWhiteSpace(raw)) return string.Empty;
            var sb = new StringBuilder(raw.Length);
            foreach (char ch in raw.Trim())
            {
                if (char.IsLetterOrDigit(ch) || ch == '-' || ch == '_') sb.Append(ch);
            }
            return sb.ToString();
        }

        public static string Save(string toolName, JObject payload, out string planId, string rootOverride = null)
        {
            string root = string.IsNullOrWhiteSpace(rootOverride) ? DefaultRoot : rootOverride;
            planId = "plan-" + DateTime.UtcNow.ToString("yyyyMMddHHmmss", CultureInfo.InvariantCulture) +
                     "-" + Guid.NewGuid().ToString("N").Substring(0, 8);
            if (payload == null) payload = new JObject();

            try
            {
                Directory.CreateDirectory(root);
                Prune(root);

                string canonicalPayload = payload.ToString(Formatting.None);
                string payloadSha = ComputeSha256(canonicalPayload);

                var envelope = new JObject
                {
                    ["plan_id"] = planId,
                    ["tool"] = toolName ?? string.Empty,
                    ["created_utc"] = DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture),
                    ["payload_sha256"] = payloadSha,
                    ["payload"] = payload.DeepClone()
                };

                string path = Path.Combine(root, planId + ".json");
                string tmp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
                File.WriteAllText(tmp, envelope.ToString(Formatting.None), new UTF8Encoding(false));
                if (File.Exists(path)) File.Delete(path);
                File.Move(tmp, path);
                return path;
            }
            catch
            {
                planId = null;
                return null;
            }
        }

        public static bool TryLoad(
            string planId,
            string expectedTool,
            out JObject payload,
            out string error,
            string rootOverride = null)
        {
            payload = null;
            error = null;

            string safeId = SanitizePlanId(planId);
            if (safeId.Length == 0)
            {
                error = "plan_id is empty or invalid.";
                return false;
            }

            string root = string.IsNullOrWhiteSpace(rootOverride) ? DefaultRoot : rootOverride;
            string path = Path.Combine(root, safeId + ".json");
            if (!File.Exists(path))
            {
                error = $"plan_not_kept: no kept plan found under plan_id '{safeId}'.";
                return false;
            }

            try
            {
                if (DateTime.UtcNow - File.GetLastWriteTimeUtc(path) > KeepFor)
                {
                    error = $"plan_expired: plan_id '{safeId}' is older than {(int)KeepFor.TotalDays} days.";
                    return false;
                }

                var envelope = JObject.Parse(File.ReadAllText(path, Encoding.UTF8));
                if (!string.Equals(envelope.Value<string>("plan_id"), safeId, StringComparison.Ordinal))
                {
                    error = $"plan_corrupt: stored plan_id inside '{safeId}' does not match.";
                    return false;
                }

                string storedTool = envelope.Value<string>("tool");
                if (!string.IsNullOrWhiteSpace(expectedTool) &&
                    !string.IsNullOrWhiteSpace(storedTool) &&
                    !string.Equals(storedTool, expectedTool, StringComparison.OrdinalIgnoreCase))
                {
                    error = $"plan_tool_mismatch: plan_id '{safeId}' belongs to '{storedTool}', not '{expectedTool}'.";
                    return false;
                }

                if (!(envelope["payload"] is JObject pObj))
                {
                    error = $"plan_corrupt: plan_id '{safeId}' has no payload object.";
                    return false;
                }

                string expectedSha = envelope.Value<string>("payload_sha256");
                if (!string.IsNullOrWhiteSpace(expectedSha))
                {
                    string actualSha = ComputeSha256(pObj.ToString(Formatting.None));
                    if (!string.Equals(expectedSha, actualSha, StringComparison.OrdinalIgnoreCase))
                    {
                        error = $"plan_tampered: SHA-256 verification failed for plan_id '{safeId}'.";
                        return false;
                    }
                }

                payload = pObj;
                return true;
            }
            catch (Exception ex)
            {
                error = $"plan_unreadable: could not read plan_id '{safeId}': {ex.Message}";
                return false;
            }
        }

        private static string ComputeSha256(string text)
        {
            using (var sha = SHA256.Create())
            {
                byte[] hash = sha.ComputeHash(Encoding.UTF8.GetBytes(text ?? string.Empty));
                return BitConverter.ToString(hash).Replace("-", "").ToLowerInvariant();
            }
        }

        private static void Prune(string root)
        {
            if (!Directory.Exists(root)) return;
            foreach (string f in Directory.GetFiles(root, "*.json"))
            {
                try
                {
                    if (DateTime.UtcNow - File.GetLastWriteTimeUtc(f) > KeepFor)
                        File.Delete(f);
                }
                catch { }
            }
        }
    }
}

