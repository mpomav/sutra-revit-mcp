using System;
using System.Collections.Generic;
using System.Linq;

namespace Scio.Abstractions
{
    public sealed class BarTypeMatchResult
    {
        public bool Ok { get; set; }
        public string? MatchedName { get; set; }
        public string? ErrorCode { get; set; }
        public string? ErrorMessage { get; set; }
        public List<string> AvailableTypes { get; set; } = new List<string>();
        public List<string> MatchingTypes { get; set; } = new List<string>();
        public string? DidYouMean { get; set; }
    }

    /// <summary>
    /// Exact-name matcher for RebarBarType names (F0 safety gate: never silently match substrings like "6" -> "16mm").
    /// </summary>
    public static class RebarBarTypeMatcher
    {
        public static BarTypeMatchResult MatchExact(string? requestedName, IEnumerable<string> availableNames)
        {
            var available = (availableNames ?? Enumerable.Empty<string>())
                .Where(n => !string.IsNullOrWhiteSpace(n))
                .Select(n => n.Trim())
                .Distinct(StringComparer.Ordinal)
                .OrderBy(n => n, StringComparer.OrdinalIgnoreCase)
                .ToList();

            string requested = (requestedName ?? string.Empty).Trim();
            if (string.IsNullOrEmpty(requested))
            {
                return new BarTypeMatchResult
                {
                    Ok = false,
                    ErrorCode = "missing_bar_type",
                    ErrorMessage = available.Count == 0
                        ? "Parameter 'bar_type' is required, and no RebarBarType is loaded in the active document."
                        : $"Parameter 'bar_type' is required (exact name). Loaded types: {string.Join(", ", available)}.",
                    AvailableTypes = available
                };
            }

            var exactMatches = available
                .Where(n => string.Equals(n, requested, StringComparison.OrdinalIgnoreCase))
                .ToList();

            if (exactMatches.Count == 1)
            {
                return new BarTypeMatchResult
                {
                    Ok = true,
                    MatchedName = exactMatches[0],
                    AvailableTypes = available,
                    MatchingTypes = exactMatches
                };
            }

            if (exactMatches.Count > 1)
            {
                return new BarTypeMatchResult
                {
                    Ok = false,
                    ErrorCode = "ambiguous_bar_type",
                    ErrorMessage = $"Ambiguous 'bar_type' '{requested}' matched multiple loaded types: {string.Join(", ", exactMatches)}.",
                    AvailableTypes = available,
                    MatchingTypes = exactMatches
                };
            }

            string? suggestion = ArgumentValidator.FindClosestMatch(requested, available);
            string hint = string.IsNullOrEmpty(suggestion) ? string.Empty : $" Did you mean '{suggestion}'?";
            return new BarTypeMatchResult
            {
                Ok = false,
                ErrorCode = "unknown_bar_type",
                ErrorMessage = available.Count == 0
                    ? $"No RebarBarType named '{requested}' found (no rebar bar types loaded in document)."
                    : $"No exact RebarBarType named '{requested}'.{hint} Loaded types: {string.Join(", ", available)}.",
                AvailableTypes = available,
                DidYouMean = suggestion
            };
        }
    }
}
