#nullable disable
// -----------------------------------------------------------------------------
// SCIO Revit MCP - Deterministic Parameter Rule Matching Engine
//
// Revit-free and net48-safe rule evaluator used by scio_rule_apply. Evaluates
// ordered declarative rules ({name, when, else, set}) against element snapshots
// before opening any Revit transaction.
// -----------------------------------------------------------------------------
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Newtonsoft.Json.Linq;

namespace Scio.Abstractions
{
    public sealed class RuleElementSnapshot
    {
        public long ElementId { get; set; }
        public string Category { get; set; }
        public string Family { get; set; }
        public string TypeName { get; set; }
        public string Level { get; set; }
        public double? VolumeM3 { get; set; }
        public Dictionary<string, string> Parameters { get; set; } = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
    }

    public sealed class RuleAssignment
    {
        public long ElementId { get; set; }
        public string MatchedRule { get; set; }
        public string Parameter { get; set; }
        public JToken Value { get; set; }
    }

    public sealed class RuleEvaluationResult
    {
        public int EvaluatedCount { get; set; }
        public int MatchedElementCount { get; set; }
        public int UnmatchedElementCount { get; set; }
        public Dictionary<string, int> MatchesByRule { get; set; } = new Dictionary<string, int>(StringComparer.Ordinal);
        public List<RuleAssignment> Assignments { get; set; } = new List<RuleAssignment>();
        public List<long> MatchedElementIds { get; set; } = new List<long>();
        public List<long> UnmatchedElementIds { get; set; } = new List<long>();
    }

    public static class RuleMatchEngine
    {
        public const string ModeFirstMatch = "first_match";
        public const string ModeAllMatches = "all_matches";

        public static string ValidateRules(JArray rules, string mode)
        {
            string m = string.IsNullOrWhiteSpace(mode) ? ModeFirstMatch : mode.Trim().ToLowerInvariant();
            if (m != ModeFirstMatch && m != ModeAllMatches)
                return "mode must be 'first_match' or 'all_matches'.";
            if (rules == null || rules.Count == 0)
                return "rules must be a non-empty array of rule objects.";
            if (rules.Count > 200)
                return "rules cannot exceed 200 entries per call.";

            for (int i = 0; i < rules.Count; i++)
            {
                if (!(rules[i] is JObject rule))
                    return $"rules[{i}] must be an object.";

                bool isElse = rule.Value<bool?>("else") == true;
                if (!isElse && !(rule["when"] is JObject))
                    return $"rules[{i}] requires 'when' object (or else=true for default fallback).";

                if (!(rule["set"] is JObject setObj) || !setObj.Properties().Any())
                    return $"rules[{i}] requires a non-empty 'set' {{ parameter: value }} map.";
            }
            return null;
        }

        public static RuleEvaluationResult Evaluate(
            IEnumerable<RuleElementSnapshot> elements,
            JArray rules,
            string mode = ModeFirstMatch)
        {
            var result = new RuleEvaluationResult();
            if (elements == null || rules == null) return result;
            string normMode = string.IsNullOrWhiteSpace(mode) ? ModeFirstMatch : mode.Trim().ToLowerInvariant();

            foreach (RuleElementSnapshot elem in elements)
            {
                if (elem == null) continue;
                result.EvaluatedCount++;

                bool matchedAny = false;
                var pendingForElement = new Dictionary<string, RuleAssignment>(StringComparer.OrdinalIgnoreCase);

                for (int i = 0; i < rules.Count; i++)
                {
                    if (!(rules[i] is JObject rule)) continue;
                    string ruleName = rule.Value<string>("name");
                    if (string.IsNullOrWhiteSpace(ruleName))
                        ruleName = "rule_" + (i + 1);

                    bool isElse = rule.Value<bool?>("else") == true;
                    bool hit = isElse
                        ? !matchedAny
                        : MatchesWhen(elem, rule["when"] as JObject);

                    if (!hit) continue;

                    matchedAny = true;
                    result.MatchesByRule[ruleName] = result.MatchesByRule.TryGetValue(ruleName, out int c) ? c + 1 : 1;

                    if (rule["set"] is JObject setObj)
                    {
                        foreach (JProperty p in setObj.Properties())
                        {
                            pendingForElement[p.Name] = new RuleAssignment
                            {
                                ElementId = elem.ElementId,
                                MatchedRule = ruleName,
                                Parameter = p.Name,
                                Value = p.Value?.DeepClone() ?? JValue.CreateNull()
                            };
                        }
                    }

                    if (normMode == ModeFirstMatch)
                        break;
                }

                if (matchedAny)
                {
                    result.MatchedElementCount++;
                    result.MatchedElementIds.Add(elem.ElementId);
                    result.Assignments.AddRange(pendingForElement.Values);
                }
                else
                {
                    result.UnmatchedElementCount++;
                    result.UnmatchedElementIds.Add(elem.ElementId);
                }
            }

            return result;
        }

        public static IEnumerable<string> CollectReferencedParameters(JArray rules)
        {
            var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (rules == null) return set;

            foreach (JObject rule in rules.OfType<JObject>())
            {
                if (rule["set"] is JObject sObj)
                {
                    foreach (var p in sObj.Properties())
                        if (!string.IsNullOrWhiteSpace(p.Name)) set.Add(p.Name);
                }
                if (rule["when"] is JObject when)
                {
                    AddMapKeys(when["param_equals"] as JObject, set);
                    AddMapKeys(when["param_contains"] as JObject, set);
                    if (when["param_empty"] is JArray pe)
                    {
                        foreach (JToken t in pe)
                            if (t?.Type == JTokenType.String && !string.IsNullOrWhiteSpace((string)t))
                                set.Add((string)t);
                    }
                    else if (when["param_empty"]?.Type == JTokenType.String && !string.IsNullOrWhiteSpace((string)when["param_empty"]))
                    {
                        set.Add((string)when["param_empty"]);
                    }
                }
            }
            return set;
        }

        private static void AddMapKeys(JObject map, ISet<string> acc)
        {
            if (map == null) return;
            foreach (var p in map.Properties())
                if (!string.IsNullOrWhiteSpace(p.Name)) acc.Add(p.Name);
        }

        private static bool MatchesWhen(RuleElementSnapshot e, JObject when)
        {
            if (when == null) return false;

            string category = when.Value<string>("category");
            if (!string.IsNullOrWhiteSpace(category) &&
                !string.Equals((e.Category ?? string.Empty).Trim(), category.Trim(), StringComparison.OrdinalIgnoreCase))
                return false;

            string famContains = when.Value<string>("family_contains");
            if (!string.IsNullOrWhiteSpace(famContains) &&
                (e.Family ?? string.Empty).IndexOf(famContains, StringComparison.OrdinalIgnoreCase) < 0)
                return false;

            string typeContains = when.Value<string>("type_contains");
            if (!string.IsNullOrWhiteSpace(typeContains) &&
                (e.TypeName ?? string.Empty).IndexOf(typeContains, StringComparison.OrdinalIgnoreCase) < 0)
                return false;

            string level = when.Value<string>("level");
            if (!string.IsNullOrWhiteSpace(level) &&
                !string.Equals((e.Level ?? string.Empty).Trim(), level.Trim(), StringComparison.OrdinalIgnoreCase))
                return false;

            double? minVol = when.Value<double?>("min_volume_m3");
            if (minVol.HasValue && (!e.VolumeM3.HasValue || e.VolumeM3.Value < minVol.Value))
                return false;

            double? maxVol = when.Value<double?>("max_volume_m3");
            if (maxVol.HasValue && (!e.VolumeM3.HasValue || e.VolumeM3.Value > maxVol.Value))
                return false;

            if (when["param_equals"] is JObject peq)
            {
                foreach (JProperty prop in peq.Properties())
                {
                    e.Parameters.TryGetValue(prop.Name, out string actual);
                    string expected = prop.Value == null || prop.Value.Type == JTokenType.Null
                        ? string.Empty
                        : prop.Value.ToString();
                    if (!string.Equals((actual ?? string.Empty).Trim(), expected.Trim(), StringComparison.OrdinalIgnoreCase))
                        return false;
                }
            }

            if (when["param_contains"] is JObject pcon)
            {
                foreach (JProperty prop in pcon.Properties())
                {
                    e.Parameters.TryGetValue(prop.Name, out string actual);
                    string substr = prop.Value?.ToString() ?? string.Empty;
                    if ((actual ?? string.Empty).IndexOf(substr, StringComparison.OrdinalIgnoreCase) < 0)
                        return false;
                }
            }

            if (when["param_empty"] != null && when["param_empty"].Type != JTokenType.Null)
            {
                var checkNames = new List<string>();
                if (when["param_empty"] is JArray arr)
                {
                    foreach (JToken t in arr)
                        if (t != null && !string.IsNullOrWhiteSpace(t.ToString())) checkNames.Add(t.ToString());
                }
                else if (when["param_empty"].Type == JTokenType.String && !string.IsNullOrWhiteSpace((string)when["param_empty"]))
                {
                    checkNames.Add((string)when["param_empty"]);
                }

                foreach (string name in checkNames)
                {
                    e.Parameters.TryGetValue(name, out string actual);
                    if (!string.IsNullOrWhiteSpace(actual))
                        return false;
                }
            }

            return true;
        }
    }
}

