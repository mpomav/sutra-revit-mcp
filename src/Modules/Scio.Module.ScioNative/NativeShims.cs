#nullable disable
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Scio.Abstractions;
using Scio.Revit.Sdk;

namespace Scio.Contracts
{
    public sealed class CommandContract
    {
        public string Name { get; set; }
        public bool ReadOnly { get; set; }
    }

    public static class Contract
    {
        public static CommandContract Find(string toolName) =>
            new CommandContract { Name = toolName, ReadOnly = false };
    }
}

namespace Scio.Revit.Core
{
    public interface ICommand
    {
        string Name { get; }
        CommandResult Execute(UIApplication app, string paramsJson);
    }

    public sealed class CommandResult
    {
        public bool Success { get; set; }
        public string DataJson { get; set; }
        public string ErrorMessage { get; set; }
        public JObject Extra { get; set; }

        public static CommandResult Ok(object data) => new CommandResult
        {
            Success = true,
            DataJson = data == null ? "{}" : (data is string s ? s : JsonConvert.SerializeObject(data))
        };

        public static CommandResult Fail(string message) => new CommandResult
        {
            Success = false,
            ErrorMessage = message
        };
    }

    public static class JsonText
    {
        public static JObject ParseObject(string json)
        {
            if (string.IsNullOrWhiteSpace(json)) return new JObject();
            var tok = JToken.Parse(json);
            if (tok is JObject obj) return obj;
            throw new JsonException("Expected a JSON object.");
        }
    }

    public static class Rid
    {
        public static long Value(ElementId id) => IdCompat.ToInt64(id);
        public static ElementId From(long value) => IdCompat.FromInt64(value);
        public static ElementId Make(long value) => IdCompat.FromInt64(value);
        public static bool CanRepresent(long value) => value > 0;
        public static bool IsValid(ElementId id) => id != null && id != ElementId.InvalidElementId;
    }

    public static class Settings
    {
        public static bool ForceReadOnlyOnWorkshared { get; set; }
        public static bool IsToolAllowed(Scio.Contracts.CommandContract contract, out string reason)
        {
            reason = string.Empty;
            return true;
        }
        public static bool AllowsExternalSideEffect(out string reason)
        {
            reason = string.Empty;
            return true;
        }
    }

    public sealed class LevelResolution
    {
        public long LevelId { get; set; }
        public string LevelName { get; set; }
    }

    public sealed class ElementLevelReader
    {
        private readonly Document _doc;
        public ElementLevelReader(Document doc) => _doc = doc;

        public LevelResolution Resolve(Element el)
        {
            if (el == null || _doc == null) return null;
            ElementId lvlId = el.LevelId;
            if (lvlId == null || lvlId == ElementId.InvalidElementId)
            {
                var p = el.get_Parameter(BuiltInParameter.FAMILY_LEVEL_PARAM)
                        ?? el.get_Parameter(BuiltInParameter.SCHEDULE_LEVEL_PARAM)
                        ?? el.get_Parameter(BuiltInParameter.WALL_BASE_CONSTRAINT)
                        ?? el.get_Parameter(BuiltInParameter.INSTANCE_REFERENCE_LEVEL_PARAM);
                if (p != null && p.StorageType == StorageType.ElementId)
                    lvlId = p.AsElementId();
            }
            if (lvlId != null && lvlId != ElementId.InvalidElementId && _doc.GetElement(lvlId) is Level lvl)
            {
                return new LevelResolution { LevelId = IdCompat.ToInt64(lvl.Id), LevelName = lvl.Name };
            }
            return null;
        }
    }

    public sealed class DocumentIdentity
    {
        public string Title { get; set; }
        public string PathName { get; set; }

        public string FingerprintDigest()
        {
            using (var sha = SHA256.Create())
            {
                byte[] hash = sha.ComputeHash(Encoding.UTF8.GetBytes($"{Title}|{PathName}"));
                return BitConverter.ToString(hash).Replace("-", "").Substring(0, 16).ToLowerInvariant();
            }
        }
    }

    public sealed class GateResult
    {
        public bool Ok { get; set; } = true;
        public bool Allowed { get; set; } = true;
        public bool DryRun { get; set; } = true;
        public Document Document { get; set; }
        public string Error { get; set; }
        public string Fingerprint { get; set; }
        public DocumentIdentity Identity { get; set; }
        public CommandResult Refusal { get; set; }
    }

    public static class DocumentGate
    {
        public static GateResult ForMutation(UIApplication app, JObject request, string commandName)
        {
            Document doc = app?.ActiveUIDocument?.Document;
            if (doc == null)
            {
                return new GateResult
                {
                    Ok = false,
                    Allowed = false,
                    Error = "No active Revit document is open.",
                    Refusal = CommandResult.Fail("No active Revit document is open.")
                };
            }

            CommandResult mismatch = ReadGuard(doc, request, commandName);
            if (mismatch != null)
            {
                return new GateResult
                {
                    Ok = false,
                    Allowed = false,
                    Document = doc,
                    Error = mismatch.ErrorMessage,
                    Refusal = mismatch
                };
            }

            var id = new DocumentIdentity { Title = doc.Title ?? string.Empty, PathName = doc.PathName ?? string.Empty };
            bool dryRun = request?["dry_run"]?.Type == JTokenType.Boolean ? request.Value<bool>("dry_run") : true;
            return new GateResult
            {
                Ok = true,
                Allowed = true,
                DryRun = dryRun,
                Document = doc,
                Fingerprint = id.FingerprintDigest(),
                Identity = id
            };
        }

        public static CommandResult ReadGuard(Document doc, JObject request, string commandName)
        {
            if (doc == null) return CommandResult.Fail("No active Revit document.");
            string target = request?.Value<string>("target_document");
            if (!string.IsNullOrWhiteSpace(target) &&
                !string.Equals(doc.Title, target, StringComparison.OrdinalIgnoreCase) &&
                !string.Equals(doc.PathName, target, StringComparison.OrdinalIgnoreCase))
            {
                return CommandResult.Fail($"Active document '{doc.Title}' does not match target_document '{target}'.");
            }
            return null;
        }

        public static string PlanHash(JObject request, params string[] keys)
        {
            var sb = new StringBuilder();
            foreach (string k in keys ?? Array.Empty<string>())
            {
                sb.Append(k).Append('=').Append(request?[k]?.ToString(Formatting.None) ?? "").Append(';');
            }
            using (var sha = SHA256.Create())
            {
                byte[] h = sha.ComputeHash(Encoding.UTF8.GetBytes(sb.ToString()));
                return BitConverter.ToString(h).Replace("-", "").Substring(0, 16).ToLowerInvariant();
            }
        }

        public static void RecordResolvedPlan(ResolvedPlan plan)
        {
            if (plan == null) return;
            try
            {
                LocalPlanStore.Save(plan.Command ?? "plan", JObject.FromObject(plan), out _);
            }
            catch { /* non-fatal */ }
        }

        public static void StampConfirmation(JObject data, GateResult gate, string commandName, string planHash, bool isDryRun, string summary = null)
        {
            if (data == null) return;
            data["plan_hash"] = planHash ?? string.Empty;
            data["dry_run"] = isDryRun;
            if (!string.IsNullOrEmpty(summary)) data["confirmation_summary"] = summary;
            if (gate?.Fingerprint != null) data["document_fingerprint"] = gate.Fingerprint;
        }

        public static CommandResult RequireConfirmation(UIApplication app, GateResult gate, JObject request, string commandName, string planHash, ResolvedPlan plan, string extra)
        {
            return null;
        }
    }

    public static class ApplicationOutcome
    {
        public const string NotStarted = "not_started";
        public const string Committed = "committed";

        public static void StampRehearsal(JObject data, int planned, int skipped, int warnings, int errors)
        {
            if (data == null) return;
            data["outcome"] = "dry_run_rehearsal";
            data["planned_count"] = planned;
            data["skipped_count"] = skipped;
            data["warning_count"] = warnings;
            data["error_count"] = errors;
        }

        public static void StampApplied(JObject data, string status, int planned, int attempted, int verified, int skipped, int failed, int warnings)
        {
            if (data == null) return;
            data["outcome"] = status;
            data["planned_count"] = planned;
            data["attempted_count"] = attempted;
            data["verified_count"] = verified;
            data["skipped_count"] = skipped;
            data["failed_count"] = failed;
            data["warning_count"] = warnings;
        }
    }

    public static class Guard
    {
        public static void Commit(Transaction tx, string name)
        {
            if (tx == null) return;
            TransactionStatus st = tx.Commit();
            if (st != TransactionStatus.Committed)
                throw new InvalidOperationException($"Transaction '{name}' ended with status {st}.");
        }

        public static void RollBack(Transaction tx)
        {
            if (tx != null && tx.HasStarted())
                tx.RollBack();
        }
    }

    public sealed class RecordedFinding
    {
        public string FindingId { get; set; }
        public string Check { get; set; }
        public string Severity { get; set; }
        public string Message { get; set; }
        public List<long> ElementIds { get; set; } = new List<long>();
    }

    public sealed class FindingSetRecord
    {
        public string Fingerprint { get; set; }
        public List<RecordedFinding> Findings { get; set; } = new List<RecordedFinding>();

        public RecordedFinding Find(string findingId) =>
            Findings.FirstOrDefault(f => string.Equals(f.FindingId, findingId, StringComparison.OrdinalIgnoreCase));

        public RecordedFinding FindByCheck(string checkName) =>
            Findings.FirstOrDefault(f => string.Equals(f.Check, checkName, StringComparison.OrdinalIgnoreCase));
    }

    public sealed class AuditFindingSetStore
    {
        public static AuditFindingSetStore Session { get; } = new AuditFindingSetStore();
        private readonly ConcurrentDictionary<string, FindingSetRecord> _store = new ConcurrentDictionary<string, FindingSetRecord>(StringComparer.OrdinalIgnoreCase);

        public void Put(FindingSetRecord record)
        {
            if (record != null && !string.IsNullOrEmpty(record.Fingerprint))
                _store[record.Fingerprint] = record;
        }

        public bool TryGet(string fingerprint, out FindingSetRecord record)
        {
            if (string.IsNullOrEmpty(fingerprint)) { record = null; return false; }
            return _store.TryGetValue(fingerprint, out record);
        }
    }

    public enum PlannedAction { Modify, Delete, Create }

    public sealed class PlannedElement
    {
        public string UniqueId { get; set; }
        public long ElementId { get; set; }
        public string Category { get; set; }
        public PlannedAction Action { get; set; }
        public Dictionary<string, string> ProposedValues { get; set; } = new Dictionary<string, string>();
    }

    public sealed class ResolvedPlan
    {
        public string Command { get; set; }
        public string DocumentKey { get; set; }
        public string RevitVersion { get; set; }
        public string DocumentFingerprint { get; set; }
        public List<PlannedElement> Elements { get; } = new List<PlannedElement>();
    }
}

namespace Scio.Module.ScioNative
{
    using Scio.Revit.Commands;
    using Scio.Revit.Core;

    internal sealed class LegacyScioCommandAdapter : IRevitCommand
    {
        private readonly ICommand _inner;
        public ToolDescriptor Descriptor { get; }

        public LegacyScioCommandAdapter(ICommand inner, string pack, string description, bool readOnly)
        {
            _inner = inner ?? throw new ArgumentNullException(nameof(inner));
            Descriptor = new ToolDescriptor
            {
                Name = inner.Name,
                Pack = pack,
                Description = description,
                ReadOnly = readOnly,
                InputSchema = new JObject
                {
                    ["type"] = "object",
                    ["properties"] = new JObject(),
                    ["additionalProperties"] = true
                }
            };
        }

        public ToolResponse Execute(RevitContext context, ToolRequest request)
        {
            string argsJson = request.Arguments?.ToString(Formatting.None) ?? "{}";
            CommandResult res = _inner.Execute(context.UiApplication, argsJson);
            if (!res.Success)
                return ToolResponse.Failure(request.RequestId, "scio_native_error", res.ErrorMessage ?? "Command failed.");

            JToken data = null;
            if (!string.IsNullOrWhiteSpace(res.DataJson))
            {
                try { data = JToken.Parse(res.DataJson); }
                catch { data = res.DataJson; }
            }
            return ToolResponse.Success(request.RequestId, data, $"{_inner.Name} completed.");
        }
    }

    public sealed class ScioNativeModule : IRevitModule
    {
        public string ModuleName => "Scio.Module.ScioNative";

        public IEnumerable<IRevitCommand> CreateCommands()
        {
            yield return new LegacyScioCommandAdapter(new ContextCommand(), "core", "Read compact SCIO working context of the active Revit session (active view, selection, levels, recent work).", true);
            yield return new LegacyScioCommandAdapter(new ExplainFindingCommand(), "audit", "Explain an audit finding with root cause, BIM impact, and concrete remediation steps.", true);
            yield return new LegacyScioCommandAdapter(new ModelCleanupCommand(), "audit", "Identify and clean up unused/orphan model items (groups, views, unplaced rooms) with dry-run preview.", false);
            yield return new LegacyScioCommandAdapter(new NamingCheckCommand(), "audit", "Audit family, type, view, sheet, and level naming conventions against project rules.", true);
            yield return new LegacyScioCommandAdapter(new PresyncCheckCommand(), "audit", "Run pre-sync health check before Synchronize with Central (warnings, worksets, classification, links).", true);
            yield return new LegacyScioCommandAdapter(new RfiGeneratorCommand(), "audit", "Generate structured RFI packages (JSON/Markdown/HTML) from selected or flagged model elements.", false);
            yield return new LegacyScioCommandAdapter(new RuleApplyCommand(), "modeling", "Apply declarative parameter rules ({when, set}) across model elements with dry-run verification.", false);
            yield return new LegacyScioCommandAdapter(new VisualQaCommand(), "audit", "Capture multi-angle visual QA views and generate a SCIO HTML visual inspection pack.", true);
            yield return new LegacyScioCommandAdapter(new RecipeDispatcherCommand(_ => null), "modeling", "Unified dispatcher for SCIO modeling recipes (split_floor_loops, split_multilayer_walls, etc.).", false);
        }
    }
}
