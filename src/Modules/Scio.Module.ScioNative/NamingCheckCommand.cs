using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Scio.Abstractions;
using Scio.Revit.Core;

namespace Scio.Revit.Commands
{
    public sealed class NamingCheckCommand : ICommand
    {
        public string Name => "scio_naming_check";

        public string Description =>
            "Validacion de nomenclatura BIM / ISO 19650: verifica nombres de vistas, laminas (sheets) " +
            "y familias cargables contra expresiones regulares estandar o definidas en el proyecto. " +
            "Calcula el ratio de conformidad y entrega elementos observados con sugerencias de correccion.";

        public CommandResult Execute(UIApplication app, string paramsJson)
        {
            JObject request;
            try { request = string.IsNullOrWhiteSpace(paramsJson) ? new JObject() : JsonText.ParseObject(paramsJson); }
            catch (JsonException ex) { return CommandResult.Fail("Parameters must be a JSON object: " + ex.Message); }

            Document doc = app.ActiveUIDocument?.Document;
            if (doc == null) return CommandResult.Fail("No active Revit document.");

            var scopes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (request["scope"] is JArray sArr && sArr.Count > 0)
            {
                foreach (var t in sArr) scopes.Add(t.ToString().Trim());
            }
            else
            {
                scopes.Add("views");
                scopes.Add("sheets");
            }

            int maxPerScope = Math.Max(1, Math.Min(20000, request.Value<int?>("max_per_scope") ?? 2000));
            bool exportReport = request["export_report"] == null || request.Value<bool>("export_report");

            string defaultSheet = @"^[A-Z]{1,4}-[0-9]{2,4}[A-Za-z]?$";
            string defaultView = @"^[A-Z0-9]+(?:[-_][A-Z0-9]+)+$";
            string defaultFamily = @"^[A-Z0-9]+(?:[-_][A-Za-z0-9]+)+$";

            var ctxPatterns = ProjectClassificationLookup.FindNamingPatterns(doc.PathName, out string ctxSource);

            string reqSheet = request.Value<string>("sheet_pattern");
            string reqView = request.Value<string>("view_pattern");
            string reqFamily = request.Value<string>("family_pattern");

            string sheetPatternStr = !string.IsNullOrWhiteSpace(reqSheet) ? reqSheet.Trim() : (ctxPatterns?.SheetPattern ?? defaultSheet);
            string viewPatternStr = !string.IsNullOrWhiteSpace(reqView) ? reqView.Trim() : (ctxPatterns?.ViewPattern ?? defaultView);
            string familyPatternStr = !string.IsNullOrWhiteSpace(reqFamily) ? reqFamily.Trim() : (ctxPatterns?.FamilyPattern ?? defaultFamily);

            string patternsSource = (!string.IsNullOrWhiteSpace(reqSheet) || !string.IsNullOrWhiteSpace(reqView) || !string.IsNullOrWhiteSpace(reqFamily))
                ? "request"
                : (ctxPatterns != null && ctxPatterns.HasAny ? "project_context" : "default");

            var warnings = new JArray();
            Regex sheetRegex = TryCompile(sheetPatternStr, defaultSheet, "sheet_pattern", warnings);
            Regex viewRegex = TryCompile(viewPatternStr, defaultView, "view_pattern", warnings);
            Regex familyRegex = TryCompile(familyPatternStr, defaultFamily, "family_pattern", warnings);

            int totalChecked = 0;
            int compliant = 0;
            bool coverageComplete = true;
            var scopeCoverage = new JObject();
            var nonCompliant = new List<JObject>();
            var csvRows = new List<List<string>>();

            // 1. Sheets
            if (scopes.Contains("sheets") || scopes.Contains("laminas") || scopes.Contains("planos"))
            {
                var allSheets = new FilteredElementCollector(doc)
                    .OfClass(typeof(ViewSheet))
                    .Cast<ViewSheet>()
                    .Where(s => !s.IsTemplate)
                    .ToList();
                int totalAvail = allSheets.Count;
                int checkedHere = 0;
                int compliantHere = 0;

                foreach (ViewSheet s in allSheets)
                {
                    if (checkedHere >= maxPerScope) { coverageComplete = false; break; }
                    checkedHere++;
                    totalChecked++;
                    string number = s.SheetNumber ?? "";
                    bool ok = SafeMatch(sheetRegex, number);
                    if (ok)
                    {
                        compliant++;
                        compliantHere++;
                        if (exportReport) csvRows.Add(new List<string> { "sheet", Rid.Value(s.Id).ToString(), $"{number} - {s.Name}", number, sheetPatternStr, "CONFORME", "" });
                    }
                    else
                    {
                        string suggestion = SuggestValidName(number);
                        if (exportReport) csvRows.Add(new List<string> { "sheet", Rid.Value(s.Id).ToString(), $"{number} - {s.Name}", number, sheetPatternStr, "NO_CONFORME", suggestion });
                        if (nonCompliant.Count < 30)
                        {
                            nonCompliant.Add(new JObject
                            {
                                ["id"] = Rid.Value(s.Id),
                                ["type"] = "sheet",
                                ["name"] = $"{number} - {s.Name}",
                                ["tested_string"] = number,
                                ["expected_pattern"] = sheetPatternStr,
                                ["reason"] = "El codigo de lamina no coincide con el patron de codificacion de planos.",
                                ["suggested_fix"] = suggestion
                            });
                        }
                    }
                }
                scopeCoverage["sheets"] = new JObject { ["total_available"] = totalAvail, ["checked"] = checkedHere, ["compliant"] = compliantHere, ["non_compliant"] = checkedHere - compliantHere, ["complete"] = checkedHere >= totalAvail };
            }

            // 2. Views
            if (scopes.Contains("views") || scopes.Contains("vistas"))
            {
                var allViews = new FilteredElementCollector(doc)
                    .OfClass(typeof(View))
                    .Cast<View>()
                    .Where(v => !v.IsTemplate && v.ViewType != ViewType.Internal && v.ViewType != ViewType.DrawingSheet && v.ViewType != ViewType.SystemBrowser && v.ViewType != ViewType.ProjectBrowser)
                    .ToList();
                int totalAvail = allViews.Count;
                int checkedHere = 0;
                int compliantHere = 0;

                foreach (View v in allViews)
                {
                    if (checkedHere >= maxPerScope) { coverageComplete = false; break; }
                    checkedHere++;
                    totalChecked++;
                    string name = v.Name ?? "";
                    bool ok = SafeMatch(viewRegex, name);
                    if (ok)
                    {
                        compliant++;
                        compliantHere++;
                        if (exportReport) csvRows.Add(new List<string> { "view", Rid.Value(v.Id).ToString(), name, name, viewPatternStr, "CONFORME", "" });
                    }
                    else
                    {
                        string suggestion = SuggestValidName(name);
                        if (exportReport) csvRows.Add(new List<string> { "view", Rid.Value(v.Id).ToString(), name, name, viewPatternStr, "NO_CONFORME", suggestion });
                        if (nonCompliant.Count < 30)
                        {
                            nonCompliant.Add(new JObject
                            {
                                ["id"] = Rid.Value(v.Id),
                                ["type"] = "view",
                                ["name"] = name,
                                ["tested_string"] = name,
                                ["expected_pattern"] = viewPatternStr,
                                ["reason"] = "El nombre de la vista contiene caracteres no estandar, espacios o no sigue la convencion de codificacion.",
                                ["suggested_fix"] = suggestion
                            });
                        }
                    }
                }
                scopeCoverage["views"] = new JObject { ["total_available"] = totalAvail, ["checked"] = checkedHere, ["compliant"] = compliantHere, ["non_compliant"] = checkedHere - compliantHere, ["complete"] = checkedHere >= totalAvail };
            }

            // 3. Families
            if (scopes.Contains("families") || scopes.Contains("familias"))
            {
                var allFamilies = new FilteredElementCollector(doc)
                    .OfClass(typeof(Family))
                    .Cast<Family>()
                    .Where(f => f.IsEditable)
                    .ToList();
                int totalAvail = allFamilies.Count;
                int checkedHere = 0;
                int compliantHere = 0;

                foreach (Family f in allFamilies)
                {
                    if (checkedHere >= maxPerScope) { coverageComplete = false; break; }
                    checkedHere++;
                    totalChecked++;
                    string name = f.Name ?? "";
                    bool ok = SafeMatch(familyRegex, name);
                    if (ok)
                    {
                        compliant++;
                        compliantHere++;
                        if (exportReport) csvRows.Add(new List<string> { "family", Rid.Value(f.Id).ToString(), name, name, familyPatternStr, "CONFORME", "" });
                    }
                    else
                    {
                        string suggestion = SuggestValidName(name);
                        if (exportReport) csvRows.Add(new List<string> { "family", Rid.Value(f.Id).ToString(), name, name, familyPatternStr, "NO_CONFORME", suggestion });
                        if (nonCompliant.Count < 30)
                        {
                            nonCompliant.Add(new JObject
                            {
                                ["id"] = Rid.Value(f.Id),
                                ["type"] = "family",
                                ["name"] = name,
                                ["tested_string"] = name,
                                ["expected_pattern"] = familyPatternStr,
                                ["reason"] = "El nombre de la familia no sigue la convencion de nomenclatura prefijada.",
                                ["suggested_fix"] = suggestion
                            });
                        }
                    }
                }
                scopeCoverage["families"] = new JObject { ["total_available"] = totalAvail, ["checked"] = checkedHere, ["compliant"] = compliantHere, ["non_compliant"] = checkedHere - compliantHere, ["complete"] = checkedHere >= totalAvail };
            }

            int nonCompliantCount = totalChecked - compliant;
            double ratio = totalChecked > 0 ? Math.Round((double)compliant / totalChecked, 4) : 1.0;

            string summary = $"Nomenclatura evaluada: {compliant}/{totalChecked} conformes ({ratio:P1}). Se observaron {nonCompliantCount} elementos.";
            string csvPath = null;
            string htmlPath = null;
            if (exportReport && csvRows.Count > 0)
            {
                try
                {
                    var kpis = new JObject
                    {
                        ["total_evaluado"] = totalChecked,
                        ["conformes"] = compliant,
                        ["no_conformes"] = nonCompliantCount,
                        ["conformidad_pct"] = Math.Round(ratio * 100.0, 1)
                    };
                    var meta = new Dictionary<string, string>
                    {
                        ["Modelo"] = doc.Title,
                        ["Origen de patrones"] = patternsSource + (ctxSource != null ? $" ({ctxSource})" : "")
                    };
                    var (cp, hp) = ScioOutput.WriteStructuredReport(
                        "scio-naming-check",
                        "Validacion de Nomenclatura BIM",
                        summary,
                        meta,
                        kpis,
                        new[] { "Ambito", "ElementId", "Nombre", "CadenaEvaluada", "Patron", "Estado", "Sugerencia" },
                        csvRows,
                        warnings.Count > 0 ? warnings : null);
                    csvPath = cp;
                    htmlPath = hp;
                }
                catch { }
            }

            var result = new JObject
            {
                ["total_checked"] = totalChecked,
                ["compliant_count"] = compliant,
                ["non_compliant_count"] = nonCompliantCount,
                ["compliance_ratio"] = ratio,
                ["coverage_complete"] = coverageComplete,
                ["scope_coverage"] = scopeCoverage,
                ["non_compliant_sample"] = new JArray(nonCompliant.Take(30)),
                ["patterns_source"] = patternsSource,
                ["patterns_used"] = new JObject
                {
                    ["sheet"] = sheetPatternStr,
                    ["view"] = viewPatternStr,
                    ["family"] = familyPatternStr
                },
                ["archivo_csv"] = csvPath,
                ["archivo"] = htmlPath ?? csvPath,
                ["summary"] = summary
            };
            if (warnings.Count > 0) result["warnings"] = warnings;

            return CommandResult.Ok(result);
        }

        private static Regex TryCompile(string pattern, string fallback, string label, JArray warnings)
        {
            var timeout = TimeSpan.FromMilliseconds(200);
            try { return new Regex(pattern, RegexOptions.CultureInvariant, timeout); }
            catch (Exception ex)
            {
                warnings?.Add($"Patron invalido en '{label}': {ex.Message}. Se uso patron por defecto.");
                return new Regex(fallback, RegexOptions.CultureInvariant, timeout);
            }
        }

        private static bool SafeMatch(Regex regex, string input)
        {
            try { return regex.IsMatch(input ?? string.Empty); }
            catch (RegexMatchTimeoutException) { return false; }
        }

        private static string SuggestValidName(string original)
        {
            if (string.IsNullOrWhiteSpace(original)) return "ELEM-001";
            string clean = original.Trim().Replace(' ', '-').Replace('.', '-').ToUpperInvariant();
            clean = Regex.Replace(clean, @"-+", "-");
            return clean;
        }
    }
}

