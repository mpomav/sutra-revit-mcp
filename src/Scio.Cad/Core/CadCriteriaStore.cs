using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Scio.Revit.Core;

namespace Scio.Cad.Core
{
    /// <summary>
    /// Local store for Sutra design-criteria packs (schema: sutra.design_criteria.v1)
    /// under %USERPROFILE%\.scio\criteria\. Shared cleanly across AutoCAD UI (SUTRACRITERIA),
    /// scio_cad_criteria MCP tool, and all parametric CAD drafting commands.
    /// </summary>
    internal static class CadCriteriaStore
    {
        public const string SchemaVersion = "sutra.design_criteria.v1";

        public static string CriteriaRoot()
        {
            string dir = Path.Combine(ScioPaths.DataRoot(), "criteria");
            Directory.CreateDirectory(dir);
            return dir;
        }

        public static string PacksDir()
        {
            string dir = Path.Combine(CriteriaRoot(), "packs");
            Directory.CreateDirectory(dir);
            return dir;
        }

        public static string ActiveFile()
        {
            return Path.Combine(CriteriaRoot(), "active.json");
        }

        public static bool ValidatePack(JObject pack, out string error)
        {
            error = null;
            if (pack == null) { error = "Criteria JSON must be an object."; return false; }

            string schema = ((string)pack["schema"] ?? "").Trim();
            if (!string.Equals(schema, SchemaVersion, StringComparison.OrdinalIgnoreCase))
            {
                error = "Expected schema '" + SchemaVersion + "', got '" + schema + "'.";
                return false;
            }

            string packId = ((string)pack["pack_id"] ?? "").Trim();
            if (string.IsNullOrEmpty(packId))
            {
                error = "Criteria pack requires a non-empty 'pack_id'.";
                return false;
            }

            string discipline = ((string)pack["discipline"] ?? "").Trim();
            if (string.IsNullOrEmpty(discipline))
            {
                error = "Criteria pack requires a non-empty 'discipline' (e.g. arch, details, mep, struct, civil).";
                return false;
            }

            bool hasRules = pack["rules"] is JArray ra && ra.Count > 0;
            bool hasAssemblies = pack["assemblies"] is JArray aa && aa.Count > 0;
            bool hasDefaults = pack["cad_defaults"] is JObject;
            if (!hasRules && !hasAssemblies && !hasDefaults)
            {
                error = "Criteria pack must contain at least one of 'rules', 'assemblies', or 'cad_defaults'.";
                return false;
            }

            return true;
        }

        public static JObject ImportPackFile(string filePath, bool activate = true)
        {
            if (string.IsNullOrWhiteSpace(filePath) || !File.Exists(filePath))
                throw new FileNotFoundException("Criteria file not found: " + filePath);

            string raw = File.ReadAllText(filePath, Encoding.UTF8);
            JObject pack = JObject.Parse(raw);
            return ImportPackObject(pack, activate);
        }

        public static JObject ImportPackObject(JObject pack, bool activate = true)
        {
            if (!ValidatePack(pack, out string err))
                throw new ArgumentException(err);

            string packId = SanitizeFileName(((string)pack["pack_id"]).Trim());
            string destPath = Path.Combine(PacksDir(), packId + ".criteria.json");
            File.WriteAllText(destPath, pack.ToString(Formatting.Indented), new UTF8Encoding(false));

            if (activate)
            {
                var active = LoadActivePackIds();
                if (!active.Contains(packId, StringComparer.OrdinalIgnoreCase))
                {
                    active.Add(packId);
                    SaveActivePackIds(active);
                }
            }

            return new JObject
            {
                ["pack_id"] = packId,
                ["name"] = (string)pack["name"] ?? packId,
                ["discipline"] = (string)pack["discipline"] ?? "",
                ["rules_count"] = (pack["rules"] as JArray)?.Count ?? 0,
                ["assemblies_count"] = (pack["assemblies"] as JArray)?.Count ?? 0,
                ["sources_count"] = (pack["sources"] as JObject)?.Count ?? 0,
                ["active"] = activate,
                ["saved_path"] = destPath
            };
        }

        public static List<string> LoadActivePackIds()
        {
            string path = ActiveFile();
            if (!File.Exists(path)) return new List<string>();
            try
            {
                var obj = JObject.Parse(File.ReadAllText(path, Encoding.UTF8));
                var arr = obj["active_packs"] as JArray;
                if (arr == null) return new List<string>();
                return arr.Select(t => (t?.ToString() ?? "").Trim())
                          .Where(s => !string.IsNullOrEmpty(s))
                          .Distinct(StringComparer.OrdinalIgnoreCase)
                          .ToList();
            }
            catch
            {
                return new List<string>();
            }
        }

        public static void SaveActivePackIds(IEnumerable<string> packIds)
        {
            var unique = (packIds ?? Enumerable.Empty<string>())
                .Select(s => (s ?? "").Trim())
                .Where(s => !string.IsNullOrEmpty(s))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            var obj = new JObject
            {
                ["schema"] = "sutra.design_criteria_active.v1",
                ["updated_utc"] = DateTime.UtcNow.ToString("o"),
                ["active_packs"] = new JArray(unique)
            };
            File.WriteAllText(ActiveFile(), obj.ToString(Formatting.Indented), new UTF8Encoding(false));
        }

        public static void SetPackActive(string packId, bool active)
        {
            var list = LoadActivePackIds();
            list.RemoveAll(s => string.Equals(s, packId, StringComparison.OrdinalIgnoreCase));
            if (active) list.Add(packId);
            SaveActivePackIds(list);
        }

        public static JArray ListPacks()
        {
            var activeSet = new HashSet<string>(LoadActivePackIds(), StringComparer.OrdinalIgnoreCase);
            var arr = new JArray();

            foreach (string file in Directory.GetFiles(PacksDir(), "*.criteria.json").OrderBy(f => f, StringComparer.OrdinalIgnoreCase))
            {
                try
                {
                    JObject pack = JObject.Parse(File.ReadAllText(file, Encoding.UTF8));
                    string id = ((string)pack["pack_id"] ?? Path.GetFileNameWithoutExtension(file)).Trim();
                    arr.Add(new JObject
                    {
                        ["pack_id"] = id,
                        ["name"] = (string)pack["name"] ?? id,
                        ["discipline"] = (string)pack["discipline"] ?? "",
                        ["disciplines_covered"] = pack["disciplines_covered"] as JArray ?? new JArray(),
                        ["rules_count"] = (pack["rules"] as JArray)?.Count ?? 0,
                        ["assemblies_count"] = (pack["assemblies"] as JArray)?.Count ?? 0,
                        ["sources_count"] = (pack["sources"] as JObject)?.Count ?? 0,
                        ["active"] = activeSet.Contains(id),
                        ["file_path"] = file
                    });
                }
                catch
                {
                    // Skip malformed JSON files gracefully
                }
            }
            return arr;
        }

        public static JObject Query(
            string discipline,
            string category,
            string text,
            IList<string> tags,
            bool includeAssemblies,
            bool includeCadDefaults,
            bool activeOnly,
            int limit)
        {
            var packs = LoadPacks(activeOnly, discipline);
            var matchedRules = new JArray();
            var matchedAssemblies = new JArray();
            var cadDefaults = new JObject();

            string catFilter = (category ?? "").Trim();
            string txtFilter = (text ?? "").Trim();
            var tagFilters = (tags ?? new List<string>())
                .Select(t => (t ?? "").Trim())
                .Where(t => !string.IsNullOrEmpty(t))
                .ToList();

            foreach (JObject pack in packs)
            {
                string packId = (string)pack["pack_id"] ?? "";
                var sources = pack["sources"] as JObject;

                if (includeCadDefaults && pack["cad_defaults"] is JObject cd)
                    cadDefaults[packId] = cd.DeepClone();

                if (pack["rules"] is JArray rules)
                {
                    foreach (JObject rule in rules)
                    {
                        if (matchedRules.Count >= limit) break;
                        if (!MatchesRule(rule, catFilter, txtFilter, tagFilters)) continue;

                        var clone = (JObject)rule.DeepClone();
                        clone["pack_id"] = packId;
                        ResolveCitation(clone["citation"] as JObject, sources);
                        matchedRules.Add(clone);
                    }
                }

                if (includeAssemblies && pack["assemblies"] is JArray assemblies)
                {
                    foreach (JObject asm in assemblies)
                    {
                        if (matchedAssemblies.Count >= limit) break;
                        if (!MatchesAssembly(asm, txtFilter, tagFilters)) continue;

                        var clone = (JObject)asm.DeepClone();
                        clone["pack_id"] = packId;
                        matchedAssemblies.Add(clone);
                    }
                }
            }

            var result = new JObject
            {
                ["packs_searched"] = packs.Count,
                ["rules_matched"] = matchedRules.Count,
                ["rules"] = matchedRules
            };
            if (includeAssemblies)
            {
                result["assemblies_matched"] = matchedAssemblies.Count;
                result["assemblies"] = matchedAssemblies;
            }
            if (includeCadDefaults && cadDefaults.Count > 0)
            {
                result["cad_defaults"] = cadDefaults;
            }
            return result;
        }

        public static JObject GetAssembly(string assemblyId)
        {
            if (string.IsNullOrWhiteSpace(assemblyId))
                throw new ArgumentException("get_assembly requires 'assembly_id'.");

            foreach (JObject pack in LoadPacks(activeOnly: false, disciplineFilter: null))
            {
                if (pack["assemblies"] is JArray assemblies)
                {
                    foreach (JObject asm in assemblies)
                    {
                        string id = ((string)asm["id"] ?? "").Trim();
                        if (string.Equals(id, assemblyId.Trim(), StringComparison.OrdinalIgnoreCase))
                        {
                            var clone = (JObject)asm.DeepClone();
                            clone["pack_id"] = (string)pack["pack_id"] ?? "";
                            return clone;
                        }
                    }
                }
            }
            throw new InvalidOperationException("Assembly '" + assemblyId + "' was not found in loaded criteria packs.");
        }

        public static JObject ValidateValues(string ruleId, JObject proposedValues)
        {
            if (string.IsNullOrWhiteSpace(ruleId))
                throw new ArgumentException("validate_values requires 'rule_id'.");
            if (proposedValues == null || proposedValues.Count == 0)
                throw new ArgumentException("validate_values requires a non-empty 'proposed_values' object.");

            foreach (JObject pack in LoadPacks(activeOnly: false, disciplineFilter: null))
            {
                var sources = pack["sources"] as JObject;
                if (!(pack["rules"] is JArray rules)) continue;
                foreach (JObject rule in rules)
                {
                    if (!string.Equals(((string)rule["id"] ?? "").Trim(), ruleId.Trim(), StringComparison.OrdinalIgnoreCase))
                        continue;

                    var ruleParams = rule["params"] as JObject ?? new JObject();
                    var checks = new JArray();
                    int violations = 0;

                    foreach (var prop in proposedValues.Properties())
                    {
                        if (!(ruleParams[prop.Name] is JObject spec)) continue;
                        double val = (double)prop.Value;
                        double? min = (double?)spec["min"];
                        double? max = (double?)spec["max"];
                        bool ok = true;
                        string reason = "ok";

                        if (min.HasValue && val < min.Value)
                        {
                            ok = false;
                            reason = "below_min (" + val + " < " + min.Value + ")";
                        }
                        else if (max.HasValue && val > max.Value)
                        {
                            ok = false;
                            reason = "above_max (" + val + " > " + max.Value + ")";
                        }

                        if (!ok) violations++;
                        checks.Add(new JObject
                        {
                            ["parameter"] = prop.Name,
                            ["proposed"] = val,
                            ["min"] = min,
                            ["max"] = max,
                            ["unit"] = (string)spec["unit"] ?? "",
                            ["compliant"] = ok,
                            ["status"] = reason
                        });
                    }

                    var citation = rule["citation"]?.DeepClone() as JObject;
                    ResolveCitation(citation, sources);

                    return new JObject
                    {
                        ["rule_id"] = ruleId,
                        ["pack_id"] = (string)pack["pack_id"] ?? "",
                        ["compliant"] = violations == 0,
                        ["violations"] = violations,
                        ["checks"] = checks,
                        ["citation"] = citation
                    };
                }
            }

            throw new InvalidOperationException("Rule '" + ruleId + "' was not found in loaded criteria packs.");
        }

        public static string GetGeminiNotebookExportPrompt()
        {
            return
                "Extrae los criterios de diseño y normas técnicas de los libros cargados en este cuaderno " +
                "y devuélvelos EXCLUSIVAMENTE como un JSON válido con el esquema 'sutra.design_criteria.v1':\n\n" +
                "{\n" +
                "  \"schema\": \"sutra.design_criteria.v1\",\n" +
                "  \"pack_id\": \"<disciplina-norma-año>\",\n" +
                "  \"name\": \"<Nombre descriptivo del paquete>\",\n" +
                "  \"discipline\": \"arch | details | mep | struct | civil\",\n" +
                "  \"disciplines_covered\": [\"arch\", \"details\"],\n" +
                "  \"units\": { \"length\": \"m\", \"area\": \"m2\", \"angle\": \"deg\", \"slope\": \"percent\" },\n" +
                "  \"sources\": { \"COD-01\": { \"title\": \"<Libro o Norma>\", \"edition\": \"<Año>\" } },\n" +
                "  \"cad_defaults\": { \"layers\": [ { \"name\": \"A-WALL\", \"color_aci\": 7, \"linetype\": \"CONTINUOUS\", \"lineweight_mm\": 0.35 } ] },\n" +
                "  \"rules\": [\n" +
                "    {\n" +
                "      \"id\": \"ARCH-001\",\n" +
                "      \"category\": \"openings | circulation | piping | rebar | geometry\",\n" +
                "      \"element\": \"<nombre_elemento>\",\n" +
                "      \"tags\": [\"tag1\", \"tag2\"],\n" +
                "      \"params\": { \"width\": { \"min\": 0.90, \"recommended\": 1.00, \"unit\": \"m\" } },\n" +
                "      \"note\": \"<resumen técnico breve>\",\n" +
                "      \"citation\": { \"source_id\": \"COD-01\", \"section\": \"Art. X\", \"page\": \"Y\" }\n" +
                "    }\n" +
                "  ],\n" +
                "  \"assemblies\": [\n" +
                "    {\n" +
                "      \"id\": \"DET-001\",\n" +
                "      \"kind\": \"wall_section | layer_buildup\",\n" +
                "      \"title\": \"<TITULO DEL DETALLE>\",\n" +
                "      \"tags\": [\"muro\", \"detalle\"],\n" +
                "      \"layers_stack\": [\n" +
                "        { \"name\": \"<capa>\", \"thickness\": 0.15, \"spec\": \"<especificación>\", \"hatch_pattern\": \"ANSI31\", \"hatch_scale\": 0.04 }\n" +
                "      ],\n" +
                "      \"citations\": [ { \"source\": \"<Norma>\", \"section\": \"<Art.>\", \"page\": \"<pág>\" } ]\n" +
                "    }\n" +
                "  ]\n" +
                "}\n\n" +
                "Reglas estrictas: incluye siempre valores numéricos verificables con unidad explícita y cita exacta de sección/página. No inventes valores que no estén en las fuentes.";
        }

        private static List<JObject> LoadPacks(bool activeOnly, string disciplineFilter)
        {
            var activeSet = new HashSet<string>(LoadActivePackIds(), StringComparer.OrdinalIgnoreCase);
            string disc = (disciplineFilter ?? "").Trim();
            var list = new List<JObject>();

            foreach (string file in Directory.GetFiles(PacksDir(), "*.criteria.json").OrderBy(f => f, StringComparer.OrdinalIgnoreCase))
            {
                try
                {
                    JObject pack = JObject.Parse(File.ReadAllText(file, Encoding.UTF8));
                    string id = ((string)pack["pack_id"] ?? "").Trim();
                    if (activeOnly && activeSet.Count > 0 && !activeSet.Contains(id)) continue;

                    if (!string.IsNullOrEmpty(disc))
                    {
                        string mainDisc = ((string)pack["discipline"] ?? "").Trim();
                        var covered = (pack["disciplines_covered"] as JArray)?.Select(t => (t?.ToString() ?? "").Trim()) ?? Enumerable.Empty<string>();
                        bool matchesDisc = string.Equals(mainDisc, disc, StringComparison.OrdinalIgnoreCase) ||
                                           covered.Any(d => string.Equals(d, disc, StringComparison.OrdinalIgnoreCase));
                        if (!matchesDisc) continue;
                    }
                    list.Add(pack);
                }
                catch
                {
                    // Ignore malformed file
                }
            }
            return list;
        }

        private static bool MatchesRule(JObject rule, string category, string text, List<string> tags)
        {
            if (!string.IsNullOrEmpty(category))
            {
                string rc = ((string)rule["category"] ?? "").Trim();
                if (!string.Equals(rc, category, StringComparison.OrdinalIgnoreCase)) return false;
            }
            if (tags.Count > 0)
            {
                var rTags = (rule["tags"] as JArray)?.Select(t => (t?.ToString() ?? "").Trim()).ToList() ?? new List<string>();
                if (!tags.Any(tf => rTags.Any(rt => rt.IndexOf(tf, StringComparison.OrdinalIgnoreCase) >= 0)))
                    return false;
            }
            if (!string.IsNullOrEmpty(text))
            {
                string blob = ((string)rule["id"] ?? "") + " " + ((string)rule["element"] ?? "") + " " + ((string)rule["note"] ?? "") + " " + (rule["tags"]?.ToString() ?? "");
                if (blob.IndexOf(text, StringComparison.OrdinalIgnoreCase) < 0) return false;
            }
            return true;
        }

        private static bool MatchesAssembly(JObject asm, string text, List<string> tags)
        {
            if (tags.Count > 0)
            {
                var aTags = (asm["tags"] as JArray)?.Select(t => (t?.ToString() ?? "").Trim()).ToList() ?? new List<string>();
                if (!tags.Any(tf => aTags.Any(at => at.IndexOf(tf, StringComparison.OrdinalIgnoreCase) >= 0)))
                    return false;
            }
            if (!string.IsNullOrEmpty(text))
            {
                string blob = ((string)asm["id"] ?? "") + " " + ((string)asm["title"] ?? "") + " " + (asm["tags"]?.ToString() ?? "");
                if (blob.IndexOf(text, StringComparison.OrdinalIgnoreCase) < 0) return false;
            }
            return true;
        }

        private static void ResolveCitation(JObject citation, JObject sources)
        {
            if (citation == null || sources == null) return;
            string sid = ((string)citation["source_id"] ?? "").Trim();
            if (!string.IsNullOrEmpty(sid) && sources[sid] is JObject src)
            {
                citation["source_title"] = (string)src["title"] ?? sid;
                if (src["edition"] != null) citation["source_edition"] = (string)src["edition"];
            }
        }

        private static string SanitizeFileName(string name)
        {
            var sb = new StringBuilder(name.Length);
            foreach (char ch in name)
            {
                if (char.IsLetterOrDigit(ch) || ch == '-' || ch == '_' || ch == '.') sb.Append(ch);
                else sb.Append('_');
            }
            return sb.ToString();
        }
    }
}
