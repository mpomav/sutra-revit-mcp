// -----------------------------------------------------------------------------
// SCIO Revit MCP - Local Model Hygiene & Cleanup (scio_model_cleanup)
//
// Audits and optionally deletes unused/orphaned model clutter across explicitly
// chosen buckets (unplaced_rooms, unused_filters, unplaced_views, imported_dwgs)
// with ConfirmationToken, ResolvedPlan, protect_ids, Transaction & post-commit reread.
// -----------------------------------------------------------------------------
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Architecture;
using Autodesk.Revit.UI;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Scio.Abstractions;
using Scio.Revit.Core;

namespace Scio.Revit.Commands
{
    public sealed class ModelCleanupCommand : ICommand
    {
        public string Name => "scio_model_cleanup";
        public string Description =>
            "Audit and optionally delete unplaced rooms, unused filters, unplaced views or imported DWGs " +
            "with dry_run=true default, protect_ids, ConfirmationToken and post-commit element verification.";

        private static readonly string[] ValidBuckets = { "unplaced_rooms", "unused_filters", "unplaced_views", "imported_dwgs" };

        public CommandResult Execute(UIApplication app, string paramsJson)
        {
            JObject request;
            try { request = string.IsNullOrWhiteSpace(paramsJson) ? new JObject() : Scio.Revit.Core.JsonText.ParseObject(paramsJson); }
            catch (JsonException ex) { return CommandResult.Fail("Parameters must be a JSON object: " + ex.Message); }

            GateResult gate = DocumentGate.ForMutation(app, request, Name);
            if (!gate.Ok) return gate.Refusal;
            Document doc = gate.Document;
            if (doc.IsReadOnly) return CommandResult.Fail("The document is read-only; cleanup cannot delete elements.");

            List<string> buckets = ParseBuckets(request["buckets"] as JArray, out string bErr);
            if (bErr != null) return CommandResult.Fail(bErr);

            bool dryRun = request["dry_run"] == null || request.Value<bool>("dry_run");
            string txName = request.Value<string>("transaction_name");
            if (string.IsNullOrWhiteSpace(txName)) txName = "SCIO: model cleanup";

            var protectIds = new HashSet<long>();
            if (request["protect_ids"] != null && request["protect_ids"].Type != JTokenType.Null)
            {
                SelectionResolution protSel = SelectionResolver.Resolve(app, request["protect_ids"]);
                if (!protSel.Ok) return CommandResult.Fail(protSel.Error);
                foreach (long id in protSel.Ids) protectIds.Add(id);
            }
            ElementId activeViewId = app.ActiveUIDocument?.ActiveView?.Id;
            if (activeViewId != null && activeViewId != ElementId.InvalidElementId)
                protectIds.Add(Rid.Value(activeViewId));

            var excludeRegexes = BuildRegexList(request["exclude_view_patterns"] as JArray, out string rxErr);
            if (rxErr != null) return CommandResult.Fail(rxErr);

            var candidates = FindCandidates(doc, buckets, protectIds, excludeRegexes, out JObject bucketCounts);
            string planHash = DocumentGate.PlanHash(request, "buckets", "protect_ids", "exclude_view_patterns");
            ResolvedPlan resolvedPlan = BuildResolvedPlan(app, gate, candidates);

            if (dryRun)
            {
                var idsList = candidates.Select(c => c.Id).ToList();
                string sidecarRef = null;
                if (idsList.Count > 0)
                {
                    string cid = "cln-" + Guid.NewGuid().ToString("N").Substring(0, 10);
                    RunRefResolver.WriteIdsSidecar(cid, Name, SafeTitle(doc), idsList);
                    sidecarRef = "@run:" + cid;
                }

                var data = new JObject
                {
                    ["dry_run"] = true,
                    ["document"] = SafeTitle(doc),
                    ["buckets"] = new JArray(buckets),
                    ["candidates_total"] = candidates.Count,
                    ["deleted_count"] = 0,
                    ["by_bucket"] = bucketCounts,
                    ["sample"] = new JArray(candidates.Take(25).Select(c => new JObject
                    {
                        ["element_id"] = c.Id,
                        ["bucket"] = c.Bucket,
                        ["name"] = c.Name
                    }))
                };
                if (sidecarRef != null) data["candidates_run_ref"] = sidecarRef;

                ApplicationOutcome.StampRehearsal(data, candidates.Count, 0, 0, 0);
                DocumentGate.RecordResolvedPlan(resolvedPlan);
                DocumentGate.StampConfirmation(data, gate, Name, planHash, true,
                    "Bound to exact candidate IDs. Pass dry_run=false and confirmation_token to delete.");
                return CommandResult.Ok(data);
            }

            CommandResult confirmRefusal = DocumentGate.RequireConfirmation(app, gate, request, Name, planHash, resolvedPlan, null);
            if (confirmRefusal != null) return confirmRefusal;

            if (candidates.Count == 0)
            {
                var noop = new JObject
                {
                    ["dry_run"] = false,
                    ["document"] = SafeTitle(doc),
                    ["candidates_total"] = 0,
                    ["deleted_count"] = 0,
                    ["survived_count"] = 0,
                    ["by_bucket"] = bucketCounts,
                    ["deleted"] = new JArray()
                };
                ApplicationOutcome.StampApplied(noop, ApplicationOutcome.NotStarted, 0, 0, 0, 0, 0, 0);
                return CommandResult.Ok(noop);
            }

            var targetEids = candidates.Where(c => Rid.CanRepresent(c.Id)).Select(c => Rid.Make(c.Id)).ToList();
            using (var tx = new Transaction(doc, txName))
            {
                tx.Start();
                try { doc.Delete(targetEids); }
                catch (Exception ex)
                {
                    Guard.RollBack(tx);
                    return CommandResult.Fail("Model cleanup delete failed and rolled back: " + ex.Message);
                }
                doc.Regenerate();
                try { Guard.Commit(tx, txName); }
                catch (Exception ex) { return CommandResult.Fail("Model cleanup commit rolled back: " + ex.Message); }
            }

            // Re-resolve every requested ElementId post-commit
            int deletedCount = 0, survivedCount = 0;
            var deletedArr = new JArray();
            foreach (Candidate c in candidates)
            {
                Element after = Rid.CanRepresent(c.Id) ? doc.GetElement(Rid.Make(c.Id)) : null;
                if (after == null)
                {
                    deletedCount++;
                    deletedArr.Add(c.Id);
                }
                else
                {
                    survivedCount++;
                }
            }

            var result = new JObject
            {
                ["dry_run"] = false,
                ["document"] = SafeTitle(doc),
                ["buckets"] = new JArray(buckets),
                ["candidates_total"] = candidates.Count,
                ["deleted_count"] = deletedCount,
                ["survived_count"] = survivedCount,
                ["by_bucket"] = bucketCounts,
                ["deleted"] = deletedArr
            };
            ApplicationOutcome.StampApplied(result, ApplicationOutcome.Committed, candidates.Count, candidates.Count, deletedCount, 0, survivedCount, 0);
            DocumentGate.StampConfirmation(result, gate, Name, planHash, false, null);
            return CommandResult.Ok(result);
        }

        private static List<string> ParseBuckets(JArray raw, out string error)
        {
            error = null;
            if (raw == null || raw.Count == 0)
                return new List<string> { "unplaced_rooms", "unused_filters" };

            var list = new List<string>();
            foreach (JToken t in raw)
            {
                string b = (t?.ToString() ?? string.Empty).Trim().ToLowerInvariant();
                if (Array.IndexOf(ValidBuckets, b) < 0)
                {
                    error = $"Unknown cleanup bucket '{b}'. Valid buckets: {string.Join(", ", ValidBuckets)}.";
                    return null;
                }
                if (!list.Contains(b)) list.Add(b);
            }
            return list;
        }

        private static List<Regex> BuildRegexList(JArray patterns, out string error)
        {
            error = null;
            var res = new List<Regex>();
            if (patterns == null) return res;
            foreach (JToken t in patterns)
            {
                string p = t?.ToString();
                if (string.IsNullOrWhiteSpace(p)) continue;
                try { res.Add(new Regex(p, RegexOptions.IgnoreCase, TimeSpan.FromMilliseconds(200))); }
                catch (Exception ex) { error = $"Invalid exclude_view_patterns regex '{p}': {ex.Message}"; return null; }
            }
            return res;
        }

        private static List<Candidate> FindCandidates(
            Document doc,
            List<string> buckets,
            HashSet<long> protectIds,
            List<Regex> excludeViews,
            out JObject bucketCounts)
        {
            var results = new List<Candidate>();
            var counts = new Dictionary<string, int>(StringComparer.Ordinal);
            foreach (string b in buckets) counts[b] = 0;

            if (buckets.Contains("unplaced_rooms"))
            {
                foreach (Element el in new FilteredElementCollector(doc).OfCategory(BuiltInCategory.OST_Rooms).WhereElementIsNotElementType())
                {
                    if (!(el is Room r)) continue;
                    long id = Rid.Value(r.Id);
                    if (protectIds.Contains(id)) continue;
                    if (r.Location == null && Math.Abs(r.Area) < 1e-9)
                    {
                        counts["unplaced_rooms"]++;
                        results.Add(new Candidate { Id = id, Bucket = "unplaced_rooms", Name = r.Name ?? ("Room " + id), Category = "Rooms", UniqueId = r.UniqueId });
                    }
                }
            }

            if (buckets.Contains("unused_filters"))
            {
                var usedFilterIds = new HashSet<long>();
                foreach (View v in new FilteredElementCollector(doc).OfClass(typeof(View)).Cast<View>())
                {
                    try
                    {
                        foreach (ElementId fid in v.GetFilters()) usedFilterIds.Add(Rid.Value(fid));
                    }
                    catch { }
                }

                foreach (ParameterFilterElement pf in new FilteredElementCollector(doc).OfClass(typeof(ParameterFilterElement)).Cast<ParameterFilterElement>())
                {
                    long id = Rid.Value(pf.Id);
                    if (protectIds.Contains(id) || usedFilterIds.Contains(id)) continue;
                    counts["unused_filters"]++;
                    results.Add(new Candidate { Id = id, Bucket = "unused_filters", Name = pf.Name ?? ("Filter " + id), Category = "ParameterFilterElement", UniqueId = pf.UniqueId });
                }
            }

            if (buckets.Contains("unplaced_views"))
            {
                var placedViewIds = new HashSet<long>();
                foreach (Viewport vp in new FilteredElementCollector(doc).OfClass(typeof(Viewport)).Cast<Viewport>())
                {
                    if (vp.ViewId != null && vp.ViewId != ElementId.InvalidElementId)
                        placedViewIds.Add(Rid.Value(vp.ViewId));
                }

                foreach (View v in new FilteredElementCollector(doc).OfClass(typeof(View)).Cast<View>())
                {
                    try
                    {
                        if (v.IsTemplate || v is ViewSheet) continue;
                        if (v.ViewType == ViewType.Internal || v.ViewType == ViewType.ProjectBrowser ||
                            v.ViewType == ViewType.SystemBrowser || v.ViewType == ViewType.Schedule ||
                            v.ViewType == ViewType.ColumnSchedule || v.ViewType == ViewType.PanelSchedule ||
                            v.ViewType == ViewType.Legend || v.ViewType == ViewType.DrawingSheet)
                            continue;
                    }
                    catch { continue; }

                    long id = Rid.Value(v.Id);
                    if (protectIds.Contains(id) || placedViewIds.Contains(id)) continue;
                    string vname = v.Name ?? string.Empty;
                    if (MatchesAny(vname, excludeViews)) continue;

                    counts["unplaced_views"]++;
                    results.Add(new Candidate { Id = id, Bucket = "unplaced_views", Name = vname, Category = v.ViewType.ToString(), UniqueId = v.UniqueId });
                }
            }

            if (buckets.Contains("imported_dwgs"))
            {
                foreach (ImportInstance imp in new FilteredElementCollector(doc).OfClass(typeof(ImportInstance)).Cast<ImportInstance>())
                {
                    if (imp.IsLinked) continue;
                    long id = Rid.Value(imp.Id);
                    if (protectIds.Contains(id)) continue;
                    counts["imported_dwgs"]++;
                    results.Add(new Candidate { Id = id, Bucket = "imported_dwgs", Name = imp.Category?.Name ?? ("Import " + id), Category = "ImportInstance", UniqueId = imp.UniqueId });
                }
            }

            bucketCounts = JObject.FromObject(counts);
            return results;
        }

        private static bool MatchesAny(string text, List<Regex> regexes)
        {
            foreach (Regex rx in regexes)
            {
                try { if (rx.IsMatch(text ?? string.Empty)) return true; }
                catch (RegexMatchTimeoutException) { }
            }
            return false;
        }

        private static ResolvedPlan BuildResolvedPlan(UIApplication app, GateResult gate, List<Candidate> candidates)
        {
            var plan = new ResolvedPlan
            {
                Command = "scio_model_cleanup",
                DocumentKey = gate.Fingerprint,
                RevitVersion = app?.Application?.VersionNumber,
                DocumentFingerprint = gate.Identity?.FingerprintDigest()
            };
            foreach (Candidate c in candidates)
            {
                plan.Elements.Add(new PlannedElement
                {
                    UniqueId = c.UniqueId ?? c.Id.ToString(CultureInfo.InvariantCulture),
                    ElementId = c.Id,
                    Category = c.Category,
                    Action = PlannedAction.Delete
                });
            }
            return plan;
        }

        private static string SafeTitle(Document d) { try { return d?.Title; } catch { return null; } }

        private sealed class Candidate
        {
            public long Id;
            public string Bucket;
            public string Name;
            public string Category;
            public string UniqueId;
        }
    }
}
