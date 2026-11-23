// -----------------------------------------------------------------------------
// SCIO Revit MCP - Unified Element Selection & @run:<id> Resolver
//
// Resolves element_ids / ids arguments across integer lists, "@selection",
// "@run:<id>", "@run:latest", and "@run:latest:<command>" uniformly across tools.
// -----------------------------------------------------------------------------
using System;
using System.Collections.Generic;
using System.Globalization;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Newtonsoft.Json.Linq;
using Scio.Abstractions;
using Scio.Revit.Core;

namespace Scio.Revit.Commands
{
    public sealed class SelectionResolution
    {
        public bool Ok { get; set; }
        public List<long> Ids { get; set; } = new List<long>();
        public bool UsedSelection { get; set; }
        public List<string> ResolvedRunIds { get; set; } = new List<string>();
        public string Error { get; set; }
    }

    public static class SelectionResolver
    {
        public static SelectionResolution Resolve(UIApplication app, JToken token)
        {
            var res = new SelectionResolution();
            if (token == null || token.Type == JTokenType.Null)
            {
                res.Ok = true;
                return res;
            }

            var seen = new HashSet<long>();

            if (token.Type == JTokenType.String)
            {
                if (!ResolveSingleToken(app, (string)token, seen, res))
                    return res;
                res.Ok = true;
                return res;
            }

            if (token is JArray arr)
            {
                foreach (JToken item in arr)
                {
                    if (item == null || item.Type == JTokenType.Null) continue;
                    if (item.Type == JTokenType.Integer || item.Type == JTokenType.Float)
                    {
                        if (long.TryParse(item.ToString(Newtonsoft.Json.Formatting.None),
                            NumberStyles.Integer, CultureInfo.InvariantCulture, out long v))
                        {
                            if (seen.Add(v)) res.Ids.Add(v);
                        }
                    }
                    else if (item.Type == JTokenType.String)
                    {
                        string s = (string)item;
                        if (string.Equals(s.Trim(), "@selection", StringComparison.OrdinalIgnoreCase) ||
                            RunRefResolver.IsRunRef(s))
                        {
                            if (!ResolveSingleToken(app, s, seen, res))
                                return res;
                        }
                        else if (long.TryParse(s.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out long v))
                        {
                            if (seen.Add(v)) res.Ids.Add(v);
                        }
                        else
                        {
                            res.Error = $"Invalid element reference '{s}'. Expected integer ElementId, '@selection', or '@run:<id>'.";
                            return res;
                        }
                    }
                    else
                    {
                        res.Error = $"Unexpected element id token type '{item.Type}'.";
                        return res;
                    }
                }
                res.Ok = true;
                return res;
            }

            res.Error = "element_ids must be an array of integers or '@selection' / '@run:<id>'.";
            return res;
        }

        public static bool IsSymbolicToken(string raw)
        {
            if (string.IsNullOrWhiteSpace(raw)) return false;
            string t = raw.Trim();
            return string.Equals(t, "@selection", StringComparison.OrdinalIgnoreCase) ||
                   RunRefResolver.IsRunRef(t);
        }

        public static bool TryExpandSymbolicTokens(UIApplication app, JToken token, out JArray expanded, out string error)
        {
            expanded = null;
            error = null;
            if (token == null || token.Type == JTokenType.Null) return false;

            if (token.Type == JTokenType.String)
            {
                string s = (string)token;
                if (!IsSymbolicToken(s)) return false;
                SelectionResolution res = Resolve(app, token);
                if (!res.Ok) { error = res.Error; return true; }
                expanded = new JArray();
                foreach (long id in res.Ids) expanded.Add(new JValue(id));
                return true;
            }

            if (token is JArray arr)
            {
                bool hasSymbolic = false;
                foreach (JToken item in arr)
                {
                    if (item != null && item.Type == JTokenType.String && IsSymbolicToken((string)item))
                    {
                        hasSymbolic = true;
                        break;
                    }
                }
                if (!hasSymbolic) return false;

                expanded = new JArray();
                var seen = new HashSet<long>();
                foreach (JToken item in arr)
                {
                    if (item != null && item.Type == JTokenType.String && IsSymbolicToken((string)item))
                    {
                        SelectionResolution res = Resolve(app, item);
                        if (!res.Ok) { error = res.Error; return true; }
                        foreach (long id in res.Ids)
                            if (seen.Add(id)) expanded.Add(new JValue(id));
                    }
                    else
                    {
                        expanded.Add(item == null ? JValue.CreateNull() : item.DeepClone());
                    }
                }
                return true;
            }

            return false;
        }

        private static bool ResolveSingleToken(UIApplication app, string raw, HashSet<long> seen, SelectionResolution res)
        {
            string trimmed = (raw ?? string.Empty).Trim();
            if (string.Equals(trimmed, "@selection", StringComparison.OrdinalIgnoreCase))
            {
                res.UsedSelection = true;
                var selIds = app?.ActiveUIDocument?.Selection?.GetElementIds();
                if (selIds != null)
                {
                    foreach (ElementId eid in selIds)
                    {
                        long v = Rid.Value(eid);
                        if (seen.Add(v)) res.Ids.Add(v);
                    }
                }
                return true;
            }

            if (RunRefResolver.IsRunRef(trimmed))
            {
                if (!RunRefResolver.TryResolve(trimmed, out List<long> runIds, out string runId, out string err))
                {
                    res.Error = err;
                    return false;
                }
                if (!string.IsNullOrEmpty(runId)) res.ResolvedRunIds.Add(runId);
                foreach (long id in runIds)
                {
                    if (seen.Add(id)) res.Ids.Add(id);
                }
                return true;
            }

            if (long.TryParse(trimmed, NumberStyles.Integer, CultureInfo.InvariantCulture, out long parsed))
            {
                if (seen.Add(parsed)) res.Ids.Add(parsed);
                return true;
            }

            res.Error = $"Unknown element_ids string token '{raw}'. Expected '@selection' or '@run:<id>'.";
            return false;
        }
    }
}
