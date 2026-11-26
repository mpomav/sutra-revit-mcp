using System;
using System.Collections.Generic;
using System.Linq;

namespace Scio.Abstractions
{
    /// <summary>
    /// Resolves active tool packs and their transitive dependencies.
    /// </summary>
    public static class ToolPackResolver
    {
        public static readonly IReadOnlyDictionary<string, string[]> Dependencies =
            new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase)
            {
                ["core"] = Array.Empty<string>(),
                ["read"] = Array.Empty<string>(),
                ["local"] = Array.Empty<string>(),
                ["administration"] = Array.Empty<string>(),
                ["unsafe_code"] = Array.Empty<string>(),
                ["model"] = new[] { "read" },
                ["modeling"] = new[] { "read" },
                ["architecture"] = new[] { "model" },
                ["structure"] = new[] { "model" },
                ["mep"] = new[] { "model" },
                ["documentation"] = new[] { "read" },
                ["planimetry"] = new[] { "documentation" },
                ["audit"] = new[] { "read" },
                ["coordination"] = new[] { "read" },
                ["schedules"] = new[] { "read" },
                ["family"] = new[] { "read" },
                ["interoperability"] = new[] { "read" },
                ["interop"] = new[] { "read" },
                ["iso19650"] = new[] { "read" },
                ["powerbi"] = new[] { "interoperability" },
                ["cad"] = new[] { "read" },
                ["cad_ifc"] = new[] { "read" },
                ["legacy_recipes"] = new[] { "read" }
            };

        public static HashSet<string> ResolveActivePacks(IEnumerable<string>? requestedPacks)
        {
            var active = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "core" };

            if (requestedPacks == null)
            {
                foreach (string key in Dependencies.Keys)
                    active.Add(key);
                return active;
            }

            var queue = new Queue<string>();
            foreach (string raw in requestedPacks)
            {
                string trimmed = (raw ?? string.Empty).Trim();
                if (string.IsNullOrEmpty(trimmed)) continue;
                if (string.Equals(trimmed, "all", StringComparison.OrdinalIgnoreCase))
                {
                    foreach (string key in Dependencies.Keys)
                        active.Add(key);
                    return active;
                }
                if (Dependencies.ContainsKey(trimmed) && active.Add(trimmed))
                    queue.Enqueue(trimmed);
            }

            while (queue.Count > 0)
            {
                string current = queue.Dequeue();
                if (!Dependencies.TryGetValue(current, out string[]? deps)) continue;
                foreach (string dep in deps)
                {
                    if (active.Add(dep))
                        queue.Enqueue(dep);
                }
            }

            return active;
        }

        public static bool IsToolVisible(ToolDescriptor tool, HashSet<string> activePacks, PermissionProfile profile)
        {
            if (tool == null) return false;
            if (!activePacks.Contains(tool.Pack)) return false;

            if (profile == PermissionProfile.ReadOnly && !tool.ReadOnly)
                return false;

            if (profile != PermissionProfile.UnsafeCode &&
                string.Equals(tool.Pack, "unsafe_code", StringComparison.OrdinalIgnoreCase))
                return false;

            return true;
        }
    }
}
