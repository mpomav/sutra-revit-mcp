// -----------------------------------------------------------------------------
// SCIO Revit MCP - Declarative Rule-Based Bulk Parameter Coder (scio_rule_apply)
//
// Evaluates ordered rules (when/else -> set) in memory over the scoped Revit
// elements, rehearses with dry_run=true by default + ConfirmationToken/ResolvedPlan,
// and applies all parameter writes in one Transaction with post-commit re-read.
// -----------------------------------------------------------------------------
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Scio.Abstractions;
using Scio.Revit.Core;

namespace Scio.Revit.Commands
{
    public sealed class RuleApplyCommand : ICommand
    {
        public string Name => "scio_rule_apply";
        public string Description =>
            "Evaluate declarative classification/parameter rules over Revit elements and apply all assignments " +
            "in one atomic verified transaction (dry_run default true, post-commit re-read).";

        private const double CubicFeetToCubicMeters = 0.028316846592;

        public CommandResult Execute(UIApplication app, string paramsJson)
        {
            JObject request;
            try { request = string.IsNullOrWhiteSpace(paramsJson) ? new JObject() : Scio.Revit.Core.JsonText.ParseObject(paramsJson); }
            catch (JsonException ex) { return CommandResult.Fail("Parameters must be a JSON object: " + ex.Message); }

            GateResult gate = DocumentGate.ForMutation(app, request, Name);
            if (!gate.Ok) return gate.Refusal;
            Document doc = gate.Document;

            JArray rules = request["rules"] as JArray;
            string mode = (request.Value<string>("mode") ?? RuleMatchEngine.ModeFirstMatch).Trim().ToLowerInvariant();
            string ruleErr = RuleMatchEngine.ValidateRules(rules, mode);
            if (ruleErr != null) return CommandResult.Fail(ruleErr + " Nothing was written.");

            int maxElements = Math.Max(1, Math.Min(10000, request.Value<int?>("max_elements") ?? 2000));
            bool dryRun = request["dry_run"] == null || request.Value<bool>("dry_run");
            string txName = request.Value<string>("transaction_name");
            if (string.IsNullOrWhiteSpace(txName)) txName = "SCIO: rule apply";

            if (!CollectScope(app, doc, request, maxElements, out List<Element> scoped, out bool truncated, out string scopeErr))
                return CommandResult.Fail(scopeErr + " Nothing was written.");

            var refParams = RuleMatchEngine.CollectReferencedParameters(rules).ToList();
            var elemById = new Dictionary<long, Element>();
            var snapshots = new List<RuleElementSnapshot>(scoped.Count);
            var levelCache = new Dictionary<long, string>();

            foreach (Element el in scoped)
            {
                long idVal = Rid.Value(el.Id);
                elemById[idVal] = el;
                snapshots.Add(BuildSnapshot(doc, el, idVal, refParams, levelCache));
            }

            RuleEvaluationResult eval = RuleMatchEngine.Evaluate(snapshots, rules, mode);
            string planHash = DocumentGate.PlanHash(request, "rules", "mode", "categories", "element_ids", "max_elements");

            var resolvedPlan = BuildResolvedPlan(app, gate, doc, elemById, eval.Assignments);

            if (dryRun)
            {
                string matchRunRef = null;
                if (eval.MatchedElementIds.Count > 0)
                {
                    string sidecarId = "rule-" + Guid.NewGuid().ToString("N").Substring(0, 10);
                    RunRefResolver.WriteIdsSidecar(sidecarId, Name, SafeTitle(doc), eval.MatchedElementIds);
                    matchRunRef = "@run:" + sidecarId;
                }

                var sample = new JArray(eval.Assignments.Take(20).Select(a => new JObject
                {
                    ["element_id"] = a.ElementId,
                    ["matched_rule"] = a.MatchedRule,
                    ["parameter"] = a.Parameter,
                    ["proposed_value"] = a.Value
                }));

                var data = new JObject
                {
                    ["dry_run"] = true,
                    ["document"] = SafeTitle(doc),
                    ["evaluated_count"] = eval.EvaluatedCount,
                    ["scope_truncated"] = truncated,
                    ["matched_elements"] = eval.MatchedElementCount,
                    ["unmatched_elements"] = eval.UnmatchedElementCount,
                    ["assignments_planned"] = eval.Assignments.Count,
                    ["verified_count"] = 0,
                    ["by_rule"] = JObject.FromObject(eval.MatchesByRule),
                    ["sample"] = sample
                };
                if (matchRunRef != null) data["matched_run_ref"] = matchRunRef;

                ApplicationOutcome.StampRehearsal(data, eval.Assignments.Count, 0, 0, 0);
                DocumentGate.RecordResolvedPlan(resolvedPlan);
                DocumentGate.StampConfirmation(data, gate, Name, planHash, true,
                    "Bound to exact matched assignments. Apply with dry_run=false and confirmation_token.");
                return CommandResult.Ok(data);
            }

            CommandResult confirmRefusal = DocumentGate.RequireConfirmation(app, gate, request, Name, planHash, resolvedPlan, null);
            if (confirmRefusal != null) return confirmRefusal;

            if (eval.Assignments.Count == 0)
            {
                var noop = new JObject
                {
                    ["dry_run"] = false,
                    ["document"] = SafeTitle(doc),
                    ["evaluated_count"] = eval.EvaluatedCount,
                    ["matched_elements"] = 0,
                    ["verified_count"] = 0,
                    ["failed_count"] = 0,
                    ["rows"] = new JArray()
                };
                ApplicationOutcome.StampApplied(noop, ApplicationOutcome.NotStarted, 0, 0, 0, 0, 0, 0);
                return CommandResult.Ok(noop);
            }

            var applyOutcomes = new List<JObject>();
            int appliedCount = 0, failedCount = 0;

            using (var tx = new Transaction(doc, txName))
            {
                tx.Start();
                foreach (RuleAssignment a in eval.Assignments)
                {
                    if (!elemById.TryGetValue(a.ElementId, out Element el))
                    {
                        failedCount++;
                        applyOutcomes.Add(RowResult(a, false, "Element disappeared before commit."));
                        continue;
                    }
                    Parameter param = ResolveParam(doc, el, a.Parameter);
                    if (param == null)
                    {
                        failedCount++;
                        applyOutcomes.Add(RowResult(a, false, $"Parameter '{a.Parameter}' not found on element {a.ElementId}."));
                        continue;
                    }
                    if (param.IsReadOnly)
                    {
                        failedCount++;
                        applyOutcomes.Add(RowResult(a, false, $"Parameter '{a.Parameter}' is read-only."));
                        continue;
                    }
                    if (!TrySetParameterValue(param, a.Value, out string setErr))
                    {
                        failedCount++;
                        applyOutcomes.Add(RowResult(a, false, setErr));
                        continue;
                    }
                    appliedCount++;
                    applyOutcomes.Add(RowResult(a, true, null));
                }

                doc.Regenerate();
                try { Guard.Commit(tx, txName); }
                catch (Exception ex)
                {
                    return CommandResult.Fail("Rule commit rolled back: " + ex.Message);
                }
            }

            // Mandatory post-commit re-read verification
            int verifiedCount = 0, driftFailedCount = 0;
            var rowsOut = new JArray();
            for (int i = 0; i < eval.Assignments.Count; i++)
            {
                RuleAssignment a = eval.Assignments[i];
                JObject row = applyOutcomes[i];
                if (row["error"] != null && row["error"].Type != JTokenType.Null)
                {
                    rowsOut.Add(row);
                    continue;
                }

                Element fresh = Rid.CanRepresent(a.ElementId) ? doc.GetElement(Rid.Make(a.ElementId)) : null;
                Parameter fp = fresh != null ? ResolveParam(doc, fresh, a.Parameter) : null;
                string readBack = ReadParamString(fp);
                bool matches = fp != null && ValueMatches(fp, a.Value, readBack);
                row["verified"] = matches;
                row["read_back"] = readBack;
                if (matches) verifiedCount++;
                else
                {
                    driftFailedCount++;
                    row["error"] = $"Post-commit read-back mismatch for '{a.Parameter}': got '{readBack}'.";
                }
                rowsOut.Add(row);
            }

            int totalFailed = failedCount + driftFailedCount;
            var result = new JObject
            {
                ["dry_run"] = false,
                ["document"] = SafeTitle(doc),
                ["evaluated_count"] = eval.EvaluatedCount,
                ["matched_elements"] = eval.MatchedElementCount,
                ["unmatched_elements"] = eval.UnmatchedElementCount,
                ["assignments_requested"] = eval.Assignments.Count,
                ["verified_count"] = verifiedCount,
                ["failed_count"] = totalFailed,
                ["by_rule"] = JObject.FromObject(eval.MatchesByRule),
                ["rows"] = rowsOut
            };
            ApplicationOutcome.StampApplied(result, ApplicationOutcome.Committed, eval.Assignments.Count, appliedCount, verifiedCount, 0, totalFailed, 0);
            DocumentGate.StampConfirmation(result, gate, Name, planHash, false, null);
            return CommandResult.Ok(result);
        }

        private static bool CollectScope(
            UIApplication app,
            Document doc,
            JObject request,
            int maxElements,
            out List<Element> scoped,
            out bool truncated,
            out string error)
        {
            scoped = new List<Element>();
            truncated = false;
            error = null;

            if (request["element_ids"] != null && request["element_ids"].Type != JTokenType.Null)
            {
                SelectionResolution selRes = SelectionResolver.Resolve(app, request["element_ids"]);
                if (!selRes.Ok) { error = selRes.Error; return false; }
                foreach (long id in selRes.Ids)
                {
                    if (scoped.Count >= maxElements) { truncated = true; break; }
                    if (!Rid.CanRepresent(id)) continue;
                    Element el = doc.GetElement(Rid.Make(id));
                    if (el != null && !(el is ElementType)) scoped.Add(el);
                }
                return true;
            }

            var catIds = new List<ElementId>();
            if (request["categories"] is JArray catsArr && catsArr.Count > 0)
            {
                foreach (JToken t in catsArr)
                {
                    string name = (t?.ToString() ?? string.Empty).Trim();
                    if (string.IsNullOrEmpty(name)) continue;
                    if (Enum.TryParse(name, true, out BuiltInCategory bic))
                    {
                        Category cat = Category.GetCategory(doc, bic);
                        if (cat != null) { catIds.Add(cat.Id); continue; }
                    }
                    foreach (Category c in doc.Settings.Categories)
                    {
                        if (string.Equals(c.Name, name, StringComparison.OrdinalIgnoreCase))
                        {
                            catIds.Add(c.Id);
                            break;
                        }
                    }
                }
                if (catIds.Count == 0) { error = "None of the specified categories resolved in this document."; return false; }
            }

            var collector = new FilteredElementCollector(doc).WhereElementIsNotElementType();
            if (catIds.Count > 0) collector.WherePasses(new ElementMulticategoryFilter(catIds));

            foreach (Element el in collector)
            {
                if (el == null || el.Category == null) continue;
                if (scoped.Count >= maxElements) { truncated = true; break; }
                scoped.Add(el);
            }
            return true;
        }

        private static RuleElementSnapshot BuildSnapshot(
            Document doc,
            Element el,
            long idVal,
            List<string> refParams,
            Dictionary<long, string> levelCache)
        {
            var snap = new RuleElementSnapshot
            {
                ElementId = idVal,
                Category = el.Category?.Name ?? string.Empty,
                Level = ReadLevelName(doc, el, levelCache)
            };

            try
            {
                ElementId tid = el.GetTypeId();
                if (tid != null && tid != ElementId.InvalidElementId && doc.GetElement(tid) is ElementType et)
                {
                    snap.TypeName = et.Name ?? el.Name ?? string.Empty;
                    snap.Family = et.FamilyName ?? string.Empty;
                }
                else
                {
                    snap.TypeName = el.Name ?? string.Empty;
                }
            }
            catch { snap.TypeName = string.Empty; }

            try
            {
                Parameter vp = el.get_Parameter(BuiltInParameter.HOST_VOLUME_COMPUTED);
                if (vp != null && vp.HasValue && vp.StorageType == StorageType.Double)
                    snap.VolumeM3 = vp.AsDouble() * CubicFeetToCubicMeters;
            }
            catch { }

            foreach (string pname in refParams)
            {
                Parameter p = ResolveParam(doc, el, pname);
                snap.Parameters[pname] = ReadParamString(p);
            }
            return snap;
        }

        private static string ReadLevelName(Document doc, Element el, Dictionary<long, string> cache)
        {
            try
            {
                ElementId lid = el.LevelId;
                if (lid == null || lid == ElementId.InvalidElementId) return string.Empty;
                long lv = Rid.Value(lid);
                if (!cache.TryGetValue(lv, out string name))
                    cache[lv] = name = doc.GetElement(lid)?.Name ?? string.Empty;
                return name;
            }
            catch { return string.Empty; }
        }

        private static Parameter ResolveParam(Document doc, Element el, string name)
        {
            Parameter p = el.LookupParameter(name);
            if (p != null) return p;
            if (Enum.TryParse(name, true, out BuiltInParameter bip))
            {
                p = el.get_Parameter(bip);
                if (p != null) return p;
            }
            ElementId tid = el.GetTypeId();
            if (tid != null && tid != ElementId.InvalidElementId)
            {
                Element et = doc.GetElement(tid);
                if (et != null) return et.LookupParameter(name);
            }
            return null;
        }

        private static string ReadParamString(Parameter p)
        {
            if (p == null || !p.HasValue) return string.Empty;
            try
            {
                switch (p.StorageType)
                {
                    case StorageType.String: return p.AsString() ?? string.Empty;
                    case StorageType.Integer: return p.AsInteger().ToString(CultureInfo.InvariantCulture);
                    case StorageType.Double: return p.AsValueString() ?? p.AsDouble().ToString("G", CultureInfo.InvariantCulture);
                    case StorageType.ElementId: return Rid.Value(p.AsElementId()).ToString(CultureInfo.InvariantCulture);
                    default: return p.AsValueString() ?? string.Empty;
                }
            }
            catch { return string.Empty; }
        }

        private static bool TrySetParameterValue(Parameter p, JToken val, out string error)
        {
            error = null;
            try
            {
                switch (p.StorageType)
                {
                    case StorageType.String:
                        if (!p.Set(val == null || val.Type == JTokenType.Null ? string.Empty : val.ToString()))
                        { error = "Parameter.Set(string) returned false."; return false; }
                        return true;
                    case StorageType.Integer:
                        int iv;
                        if (val?.Type == JTokenType.Boolean) iv = (bool)val ? 1 : 0;
                        else if (!int.TryParse(val?.ToString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out iv))
                        { error = $"Cannot parse integer from '{val}'."; return false; }
                        if (!p.Set(iv)) { error = "Parameter.Set(int) returned false."; return false; }
                        return true;
                    case StorageType.Double:
                        if (val?.Type == JTokenType.String)
                        {
                            if (double.TryParse((string)val, NumberStyles.Float, CultureInfo.InvariantCulture, out double dDirect) && p.Set(dDirect))
                                return true;
                            if (p.SetValueString((string)val)) return true;
                            error = $"Parameter.SetValueString('{val}') returned false.";
                            return false;
                        }
                        if (double.TryParse(val?.ToString(), NumberStyles.Float, CultureInfo.InvariantCulture, out double dv) && p.Set(dv))
                            return true;
                        error = $"Cannot set double parameter to '{val}'.";
                        return false;
                    case StorageType.ElementId:
                        if (!long.TryParse(val?.ToString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out long lid) || !Rid.CanRepresent(lid))
                        { error = $"Invalid ElementId '{val}'."; return false; }
                        if (!p.Set(Rid.Make(lid))) { error = "Parameter.Set(ElementId) returned false."; return false; }
                        return true;
                    default:
                        error = $"Unsupported parameter StorageType {p.StorageType}.";
                        return false;
                }
            }
            catch (Exception ex) { error = ex.Message; return false; }
        }

        private static bool ValueMatches(Parameter p, JToken expected, string actualStr)
        {
            if (p.StorageType == StorageType.Double)
            {
                if (double.TryParse(expected?.ToString(), NumberStyles.Float, CultureInfo.InvariantCulture, out double ev))
                    return Math.Abs(p.AsDouble() - ev) < 1e-4;
                return p.HasValue;
            }
            if (p.StorageType == StorageType.Integer)
            {
                int ev = expected?.Type == JTokenType.Boolean ? ((bool)expected ? 1 : 0)
                    : (int.TryParse(expected?.ToString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out int x) ? x : int.MinValue);
                return p.AsInteger() == ev;
            }
            string expStr = expected == null || expected.Type == JTokenType.Null ? string.Empty : expected.ToString();
            return string.Equals(actualStr ?? string.Empty, expStr, StringComparison.Ordinal);
        }

        private static JObject RowResult(RuleAssignment a, bool ok, string err) => new JObject
        {
            ["element_id"] = a.ElementId,
            ["matched_rule"] = a.MatchedRule,
            ["parameter"] = a.Parameter,
            ["value"] = a.Value,
            ["verified"] = ok,
            ["error"] = err == null ? JValue.CreateNull() : new JValue(err)
        };

        private static ResolvedPlan BuildResolvedPlan(UIApplication app, GateResult gate, Document doc,
            Dictionary<long, Element> elems, List<RuleAssignment> assignments)
        {
            var plan = new ResolvedPlan
            {
                Command = "scio_rule_apply",
                DocumentKey = gate.Fingerprint,
                RevitVersion = app?.Application?.VersionNumber,
                DocumentFingerprint = gate.Identity?.FingerprintDigest()
            };
            foreach (RuleAssignment a in assignments)
            {
                elems.TryGetValue(a.ElementId, out Element e);
                plan.Elements.Add(new PlannedElement
                {
                    UniqueId = e != null ? e.UniqueId : a.ElementId.ToString(CultureInfo.InvariantCulture),
                    ElementId = a.ElementId,
                    Category = e?.Category?.Name,
                    Action = PlannedAction.Modify,
                    ProposedValues = new Dictionary<string, string> { { a.Parameter, a.Value?.ToString() ?? string.Empty } }
                });
            }
            return plan;
        }

        private static string SafeTitle(Document d) { try { return d?.Title; } catch { return null; } }
    }
}
