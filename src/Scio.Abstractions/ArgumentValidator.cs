using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json.Linq;

namespace Scio.Abstractions
{
    public sealed class ArgumentValidationResult
    {
        public bool Ok => Missing.Count == 0 && Unknown.Count == 0;
        public List<string> Missing { get; set; } = new List<string>();
        public List<UnknownArgumentHint> Unknown { get; set; } = new List<UnknownArgumentHint>();
    }

    public sealed class UnknownArgumentHint
    {
        public string Name { get; set; } = string.Empty;
        public string? DidYouMean { get; set; }
    }

    /// <summary>
    /// Validates tool call arguments against a ToolDescriptor's JSON Schema and emits did_you_mean suggestions.
    /// </summary>
    public static class ArgumentValidator
    {
        private static readonly HashSet<string> CommonEnvelopeFields = new HashSet<string>(StringComparer.Ordinal)
        {
            "target_document",
            "verbosity",
            "idempotency_key",
            "confirmation_token"
        };

        public static ArgumentValidationResult Validate(ToolDescriptor descriptor, JObject? arguments)
        {
            var result = new ArgumentValidationResult();
            if (descriptor?.InputSchema == null) return result;

            var args = arguments ?? new JObject();
            var schema = descriptor.InputSchema;
            var props = schema["properties"] as JObject;
            var required = schema["required"] as JArray;
            bool forbidUnknown = schema["additionalProperties"]?.Type == JTokenType.Boolean &&
                                 schema.Value<bool>("additionalProperties") == false;

            if (required != null)
            {
                foreach (var reqTok in required)
                {
                    string? reqName = reqTok?.ToString();
                    if (string.IsNullOrWhiteSpace(reqName)) continue;
                    if (!args.TryGetValue(reqName!, StringComparison.Ordinal, out var val) ||
                        val == null ||
                        val.Type == JTokenType.Null ||
                        (val.Type == JTokenType.String && string.IsNullOrWhiteSpace(val.ToString())))
                    {
                        result.Missing.Add(reqName!);
                    }
                }
            }

            if (forbidUnknown && props != null)
            {
                var knownNames = props.Properties().Select(p => p.Name).ToList();
                var knownSet = new HashSet<string>(knownNames, StringComparer.Ordinal);
                foreach (var prop in args.Properties())
                {
                    if (knownSet.Contains(prop.Name) || CommonEnvelopeFields.Contains(prop.Name))
                        continue;

                    result.Unknown.Add(new UnknownArgumentHint
                    {
                        Name = prop.Name,
                        DidYouMean = FindClosestMatch(prop.Name, knownNames)
                    });
                }
            }

            return result;
        }

        public static JObject BuildErrorDetails(ArgumentValidationResult validation)
        {
            return new JObject
            {
                ["missing"] = new JArray(validation.Missing),
                ["unknown"] = new JArray(validation.Unknown.Select(u =>
                {
                    var obj = new JObject { ["name"] = u.Name };
                    if (!string.IsNullOrEmpty(u.DidYouMean))
                        obj["did_you_mean"] = u.DidYouMean;
                    return obj;
                }))
            };
        }

        public static string BuildErrorMessage(string toolName, ArgumentValidationResult validation)
        {
            var parts = new List<string>();
            if (validation.Missing.Count > 0)
                parts.Add($"missing required argument(s): {string.Join(", ", validation.Missing)}");

            if (validation.Unknown.Count > 0)
            {
                var unkParts = validation.Unknown.Select(u =>
                    string.IsNullOrEmpty(u.DidYouMean) ? $"'{u.Name}'" : $"'{u.Name}' (did you mean '{u.DidYouMean}'?)");
                parts.Add($"unknown argument(s): {string.Join(", ", unkParts)}");
            }

            return $"Invalid arguments for '{toolName}': {string.Join("; ", parts)}.";
        }

        public static string? FindClosestMatch(string input, IEnumerable<string> candidates)
        {
            if (string.IsNullOrWhiteSpace(input) || candidates == null) return null;

            string needle = input.Trim().ToLowerInvariant();
            string? best = null;
            int bestDist = int.MaxValue;
            int maxAllowed = Math.Max(2, needle.Length / 2 + 1);

            foreach (string raw in candidates)
            {
                if (string.IsNullOrWhiteSpace(raw)) continue;
                string cand = raw.Trim().ToLowerInvariant();
                int dist = Levenshtein(needle, cand);
                if (dist < bestDist && dist <= maxAllowed)
                {
                    bestDist = dist;
                    best = raw;
                }
            }

            return best;
        }

        internal static int Levenshtein(string a, string b)
        {
            if (a.Length == 0) return b.Length;
            if (b.Length == 0) return a.Length;

            var prev = new int[b.Length + 1];
            var curr = new int[b.Length + 1];
            for (int j = 0; j <= b.Length; j++) prev[j] = j;

            for (int i = 1; i <= a.Length; i++)
            {
                curr[0] = i;
                for (int j = 1; j <= b.Length; j++)
                {
                    int cost = a[i - 1] == b[j - 1] ? 0 : 1;
                    curr[j] = Math.Min(
                        Math.Min(curr[j - 1] + 1, prev[j] + 1),
                        prev[j - 1] + cost);
                }
                var tmp = prev;
                prev = curr;
                curr = tmp;
            }

            return prev[b.Length];
        }
    }
}
