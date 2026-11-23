using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json.Linq;
using Scio.Abstractions;

namespace Scio.Server
{
    /// <summary>
    /// Clean-room bilingual workflow & tool recommender backed by BimSearchIndex.
    /// </summary>
    internal static class SuggestWorkflowTool
    {
        public static JObject Handle(ServerToolRegistry registry, JObject args)
        {
            string goal = (args.Value<string>("goal") ?? args.Value<string>("query") ?? string.Empty).Trim();
            int limit = Math.Max(1, Math.Min(20, args.Value<int?>("limit") ?? 8));

            var tools = registry.ListVisibleTools().ToList();
            if (string.IsNullOrWhiteSpace(goal))
            {
                return new JObject
                {
                    ["goal"] = string.Empty,
                    ["total_visible_tools"] = tools.Count,
                    ["recommended_steps"] = new JArray
                    {
                        "1. Call scio_health to verify the active Revit document and loaded modules.",
                        "2. Call get_document_info or scio_model_scan to inspect levels, categories, and warnings.",
                        "3. Preview writes with dry_run=true before committing."
                    },
                    ["matching_tools"] = new JArray(tools.Take(limit).Select(Summarize))
                };
            }

            var hits = tools
                .Select(t => new { Tool = t, Score = BimSearchIndex.ScoreText(goal, $"{t.Name} {t.Pack} {t.Description}", t.Name) })
                .Where(x => x.Score > 0)
                .OrderByDescending(x => x.Score)
                .ThenBy(x => x.Tool.Name, StringComparer.Ordinal)
                .Take(limit)
                .ToList();

            return new JObject
            {
                ["goal"] = goal,
                ["total_visible_tools"] = tools.Count,
                ["match_count"] = hits.Count,
                ["matching_tools"] = new JArray(hits.Select(h => new JObject
                {
                    ["name"] = h.Tool.Name,
                    ["pack"] = h.Tool.Pack,
                    ["read_only"] = h.Tool.ReadOnly,
                    ["score"] = h.Score,
                    ["description"] = h.Tool.Description
                }))
            };
        }

        private static JObject Summarize(ToolDescriptor t) => new JObject
        {
            ["name"] = t.Name,
            ["pack"] = t.Pack,
            ["read_only"] = t.ReadOnly,
            ["description"] = t.Description
        };
    }
}
