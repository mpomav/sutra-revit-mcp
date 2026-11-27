using System;
using System.Collections.Generic;
using Newtonsoft.Json.Linq;
using Scio.Cad.Core;

namespace Scio.Cad.Commands
{
    /// <summary>
    /// MCP command for loading, querying, and validating structured design criteria packs
    /// (sutra.design_criteria.v1) exported from Gemini Notebook / File Search or loaded
    /// via the SUTRACRITERIA AutoCAD Ribbon button.
    /// </summary>
    internal static class CadCriteriaCommand
    {
        public static JObject Execute(JObject args)
        {
            string op = ((string)args["operation"] ?? "list_packs").Trim().ToLowerInvariant();
            switch (op)
            {
                case "list_packs":
                {
                    var packs = CadCriteriaStore.ListPacks();
                    return new JObject
                    {
                        ["operation"] = op,
                        ["criteria_root"] = CadCriteriaStore.CriteriaRoot(),
                        ["count"] = packs.Count,
                        ["packs"] = packs
                    };
                }

                case "query":
                {
                    string disc = ((string)args["discipline"] ?? "").Trim();
                    string cat = ((string)args["category"] ?? "").Trim();
                    string text = ((string)args["text"] ?? "").Trim();
                    var tags = new List<string>();
                    if (args["tags"] is JArray ta)
                    {
                        foreach (JToken t in ta) tags.Add(t?.ToString() ?? "");
                    }
                    bool incAsm = (bool?)args["include_assemblies"] ?? true;
                    bool incDef = (bool?)args["include_cad_defaults"] ?? false;
                    bool activeOnly = (bool?)args["active_only"] ?? true;
                    int limit = Math.Max(1, Math.Min(100, (int?)args["limit"] ?? 20));

                    JObject res = CadCriteriaStore.Query(disc, cat, text, tags, incAsm, incDef, activeOnly, limit);
                    res["operation"] = op;
                    return res;
                }

                case "get_assembly":
                {
                    string asmId = ((string)args["assembly_id"] ?? "").Trim();
                    JObject asm = CadCriteriaStore.GetAssembly(asmId);
                    return new JObject
                    {
                        ["operation"] = op,
                        ["assembly"] = asm
                    };
                }

                case "validate_values":
                {
                    string ruleId = ((string)args["rule_id"] ?? "").Trim();
                    var proposed = args["proposed_values"] as JObject;
                    JObject res = CadCriteriaStore.ValidateValues(ruleId, proposed);
                    res["operation"] = op;
                    return res;
                }

                case "import_pack":
                {
                    bool activate = (bool?)args["activate"] ?? true;
                    JObject summary;
                    if (args["pack_json"] is JObject packObj)
                        summary = CadCriteriaStore.ImportPackObject(packObj, activate);
                    else
                        summary = CadCriteriaStore.ImportPackFile(((string)args["file_path"] ?? "").Trim(), activate);

                    summary["operation"] = op;
                    return summary;
                }

                case "get_export_prompt":
                {
                    return new JObject
                    {
                        ["operation"] = op,
                        ["schema"] = CadCriteriaStore.SchemaVersion,
                        ["prompt"] = CadCriteriaStore.GetGeminiNotebookExportPrompt()
                    };
                }

                default:
                    throw new ArgumentException("Unsupported cad_criteria operation '" + op + "'.");
            }
        }
    }
}
