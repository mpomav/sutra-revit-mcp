// -----------------------------------------------------------------------------
// SCIO Revit MCP - Native In-Process C# RFI Generator
//
// Composes affected elements, clash/audit findings, quantified impact, and
// proposed solutions into a structured Request for Information (RFI) draft.
// Streams complete formatted RFI to %USERPROFILE%\.scio\salidas\rfi-*.md
// and returns a compact ScioOutput (<500 tokens).
//
// Non-negotiable condition: Every output states explicitly that it is an automated
// draft requiring professional human review and validation before formal submission.
// -----------------------------------------------------------------------------
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Scio.Abstractions;
using Scio.Revit.Core;

namespace Scio.Revit.Commands
{
    public sealed class RfiGeneratorCommand : ICommand
    {
        public string Name => "scio_rfi_generator";

        public string Description =>
            "Genera un borrador formal de Solicitud de Informacion (RFI) para coordinacion BIM. " +
            "Compone elementos afectados, interferencias o discrepancias de diseno, metrados de impacto " +
            "y propuesta de solucion. Exporta el reporte detallado a %USERPROFILE%\\.scio\\salidas\\rfi-*.md " +
            "con advertencia explicita de revision humana obligatoria.";

        private const double CubicFeetToM3 = 0.028316846592;
        private const double SquareFeetToM2 = 0.09290304;
        private const double FeetToMeters = 0.3048;

        public static JObject BuildPayload(
            string subject,
            string question,
            string disciplineTo,
            string disciplineFrom = "Coordinación BIM",
            string proposedSolution = null,
            string priority = "media",
            string locationHint = null,
            string rfiNumber = null,
            IEnumerable<long> elementIds = null)
        {
            var obj = new JObject
            {
                ["subject"] = subject ?? string.Empty,
                ["question"] = question ?? string.Empty,
                ["discipline_to"] = disciplineTo ?? string.Empty,
                ["discipline_from"] = disciplineFrom ?? "Coordinación BIM",
                ["proposed_solution"] = !string.IsNullOrWhiteSpace(proposedSolution)
                    ? proposedSolution
                    : "Pendiente de coordinación técnica (no definida en el borrador inicial).",
                ["priority"] = priority ?? "media"
            };
            if (!string.IsNullOrWhiteSpace(locationHint)) obj["location_hint"] = locationHint;
            if (!string.IsNullOrWhiteSpace(rfiNumber)) obj["rfi_number"] = rfiNumber;
            if (elementIds != null && elementIds.Any())
            {
                obj["element_ids"] = new JArray(elementIds);
            }
            return obj;
        }

        public CommandResult Execute(UIApplication app, string paramsJson)
        {
            JObject request;
            try { request = string.IsNullOrWhiteSpace(paramsJson) ? new JObject() : Scio.Revit.Core.JsonText.ParseObject(paramsJson); }
            catch (JsonException ex) { return CommandResult.Fail("Parameters must be a JSON object: " + ex.Message); }

            Document doc = app.ActiveUIDocument?.Document;
            if (doc == null) return CommandResult.Fail("No active Revit document.");

            string subject = request.Value<string>("subject") ?? request.Value<string>("titulo") ?? request.Value<string>("title");
            if (string.IsNullOrWhiteSpace(subject))
            {
                return CommandResult.Fail("El parametro 'subject' es obligatorio.");
            }

            string question = request.Value<string>("question") ?? request.Value<string>("pregunta");
            if (string.IsNullOrWhiteSpace(question))
            {
                return CommandResult.Fail("El parametro 'question' es obligatorio.");
            }

            string disciplineTo = request.Value<string>("discipline_to") ?? request.Value<string>("disciplina_destino") ?? request.Value<string>("disciplina");
            if (string.IsNullOrWhiteSpace(disciplineTo))
            {
                return CommandResult.Fail("El parametro 'discipline_to' es obligatorio.");
            }

            string proposedSolution = request.Value<string>("proposed_solution") ?? request.Value<string>("propuesta");
            if (string.IsNullOrWhiteSpace(proposedSolution))
            {
                proposedSolution = "Pendiente de coordinacion tecnica (no definida en el borrador inicial).";
            }
            string disciplineFrom = request.Value<string>("discipline_from") ?? "Coordinacion BIM";
            string priority = request.Value<string>("priority") ?? "media";
            string locationHint = request.Value<string>("location_hint") ?? string.Empty;
            string customRfiNumber = request.Value<string>("rfi_number");
            bool saveToDisk = request["save_to_disk"] == null || request.Value<bool>("save_to_disk");

            if (saveToDisk && !Scio.Revit.Core.Settings.AllowsExternalSideEffect(out string sideEffectDeny))
            {
                return CommandResult.Fail(
                    sideEffectDeny + " Pass save_to_disk=false to generate the draft without writing to disk.");
            }

            string timestamp = DateTime.Now.ToString("yyyyMMdd-HHmmss");
            string rfiCode = !string.IsNullOrWhiteSpace(customRfiNumber) ? customRfiNumber.Trim() : $"RFI-{DateTime.Now:yyyyMMdd}-{DateTime.Now:HHmm}";

            var elementIds = new List<long>();
            if (request["element_ids"] != null && request["element_ids"].Type != JTokenType.Null)
            {
                var selRes = SelectionResolver.Resolve(app, request["element_ids"]);
                if (!selRes.Ok) return CommandResult.Fail(selRes.Error);
                elementIds.AddRange(selRes.Ids);
            }

            var affectedItems = new List<RfiElementInfo>();
            double totalVolumeM3 = 0.0;
            double totalAreaM2 = 0.0;
            double totalLengthM = 0.0;

            var levelReader = new ElementLevelReader(doc);
            foreach (long id in elementIds)
            {
                if (!Rid.CanRepresent(id)) continue;
                Element elem = doc.GetElement(Rid.Make(id));
                if (elem == null) continue;

                var info = ExtractElementInfo(doc, elem, levelReader);
                affectedItems.Add(info);

                totalVolumeM3 += info.VolumeM3;
                totalAreaM2 += info.AreaM2;
                totalLengthM += info.LengthM;
            }

            string mdContent = BuildRfiMarkdown(
                rfiCode,
                subject,
                doc.Title,
                disciplineFrom,
                disciplineTo,
                priority,
                locationHint,
                question,
                proposedSolution,
                affectedItems,
                totalVolumeM3,
                totalAreaM2,
                totalLengthM);

            string mdPath = null;
            bool written = false;
            if (saveToDisk)
            {
                string dir = ScioPaths.SalidasDir();
                if (!Directory.Exists(dir))
                {
                    try { Directory.CreateDirectory(dir); } catch { }
                }

                string safeCode = SanitizeFileToken(rfiCode);
                string filename = $"rfi-{safeCode}-{timestamp}.md";
                mdPath = Path.Combine(dir, filename);
                File.WriteAllText(mdPath, mdContent, new UTF8Encoding(false));
                written = File.Exists(mdPath) && new FileInfo(mdPath).Length > 0;
            }

            var cifras = new JObject
            {
                ["rfi_codigo"] = rfiCode,
                ["elementos_afectados"] = affectedItems.Count,
                ["prioridad"] = priority,
                ["volumen_impactado_m3"] = Math.Round(totalVolumeM3, 3),
                ["area_impactada_m2"] = Math.Round(totalAreaM2, 3),
                ["longitud_impactada_m"] = Math.Round(totalLengthM, 3),
                ["written"] = written
            };

            var muestra = new JArray();
            foreach (var item in affectedItems.Take(10))
            {
                muestra.Add(new JObject
                {
                    ["id"] = item.ElementId,
                    ["categoria"] = item.Category,
                    ["familia"] = item.Family,
                    ["tipo"] = item.Type,
                    ["nivel"] = item.Level
                });
            }

            var output = new ScioOutput
            {
                Resumen = written
                    ? $"Borrador de RFI '{rfiCode}' generado y guardado para '{subject}' con {affectedItems.Count} elemento(s). Requiere validacion humana."
                    : $"Borrador en memoria de RFI '{rfiCode}' generado para '{subject}' con {affectedItems.Count} elemento(s) (save_to_disk=false). Requiere validacion humana.",
                Cifras = cifras,
                Muestra = muestra,
                Archivo = mdPath,
                Advertencias = new JArray
                {
                    "BORRADOR AUTOMÁTICO: Requiere revisión técnica y validación formal de un profesional responsable antes de su tramitación."
                }
            };

            JObject payload = output.ToJson();
            payload["written"] = written;
            payload["rfi_codigo"] = rfiCode;
            if (!saveToDisk) payload["markdown_preview"] = mdContent;
            return CommandResult.Ok(payload);
        }

        public static string SanitizeFileToken(string raw)
        {
            if (string.IsNullOrWhiteSpace(raw)) return "rfi";
            var invalid = new HashSet<char>(Path.GetInvalidFileNameChars());
            invalid.Add('/');
            invalid.Add('\\');
            invalid.Add(':');
            invalid.Add('*');
            invalid.Add('?');
            invalid.Add('"');
            invalid.Add('<');
            invalid.Add('>');
            invalid.Add('|');

            var sb = new StringBuilder(raw.Length);
            foreach (char ch in raw.Trim().ToLowerInvariant())
            {
                if (ch == ' ' || invalid.Contains(ch))
                {
                    sb.Append('_');
                }
                else if (ch == '.' && sb.Length > 0 && sb[sb.Length - 1] == '.')
                {
                    continue;
                }
                else
                {
                    sb.Append(ch);
                }
            }
            string clean = sb.ToString().Trim('.', '_', '-', ' ');
            return string.IsNullOrWhiteSpace(clean) ? "rfi" : clean;
        }

        private static RfiElementInfo ExtractElementInfo(Document doc, Element elem, ElementLevelReader levelReader)
        {
            var info = new RfiElementInfo
            {
                ElementId = Rid.Value(elem.Id),
                UniqueId = elem.UniqueId ?? string.Empty,
                Category = elem.Category?.Name ?? "(sin categoria)",
                Level = levelReader?.Resolve(elem)?.LevelName ?? "(sin nivel)"
            };

            ElementType type = doc.GetElement(elem.GetTypeId()) as ElementType;
            if (type != null)
            {
                info.Family = type.FamilyName ?? string.Empty;
                info.Type = type.Name ?? string.Empty;
            }
            else
            {
                info.Family = elem.Name ?? string.Empty;
                info.Type = string.Empty;
            }

            // Extract quantities if available
            Parameter pVol = elem.get_Parameter(BuiltInParameter.HOST_VOLUME_COMPUTED)
                          ?? elem.LookupParameter("Volumen")
                          ?? elem.LookupParameter("Volume");
            if (pVol != null && pVol.StorageType == StorageType.Double)
            {
                double cuFt = pVol.AsDouble();
                if (cuFt > 0) info.VolumeM3 = cuFt * CubicFeetToM3;
            }

            Parameter pArea = elem.get_Parameter(BuiltInParameter.HOST_AREA_COMPUTED)
                           ?? elem.LookupParameter("Área")
                           ?? elem.LookupParameter("Area");
            if (pArea != null && pArea.StorageType == StorageType.Double)
            {
                double sqFt = pArea.AsDouble();
                if (sqFt > 0) info.AreaM2 = sqFt * SquareFeetToM2;
            }

            Parameter pLen = elem.get_Parameter(BuiltInParameter.CURVE_ELEM_LENGTH)
                          ?? elem.LookupParameter("Longitud")
                          ?? elem.LookupParameter("Length");
            if (pLen != null && pLen.StorageType == StorageType.Double)
            {
                double ft = pLen.AsDouble();
                if (ft > 0) info.LengthM = ft * FeetToMeters;
            }

            return info;
        }

        private static string BuildRfiMarkdown(
            string rfiCode,
            string subject,
            string docTitle,
            string from,
            string to,
            string priority,
            string locationHint,
            string question,
            string proposedSolution,
            List<RfiElementInfo> items,
            double totalVol,
            double totalArea,
            double totalLen)
        {

            var sb = new StringBuilder();
            sb.AppendLine($"# Solicitud de Información (RFI) - {rfiCode}");
            sb.AppendLine();
            sb.AppendLine("> [!IMPORTANT]");
            sb.AppendLine("> **AVISO DE RESPONSABILIDAD / DISCLAIMER**");
            sb.AppendLine("> Este documento es un **borrador generado automáticamente** mediante SCIO Revit MCP para fines de coordinación técnica y detección de incompatibilidades. Requiere revisión, ajuste y validación formal por parte del Coordinador BIM o Profesional Responsable antes de su emisión o tramitación formal.");
            sb.AppendLine();
            sb.AppendLine("## 1. Datos Generales de la Solicitud");
            sb.AppendLine();
            sb.AppendLine($"| Campo | Detalle |");
            sb.AppendLine($"| :--- | :--- |");
            sb.AppendLine($"| **Código RFI** | `{rfiCode}` |");
            sb.AppendLine($"| **Fecha de Emisión** | {DateTime.Now:dd/MM/yyyy HH:mm} |");
            sb.AppendLine($"| **Estado** | `BORRADOR / DRAFT (Pendiente de Aprobación)` |");
            sb.AppendLine($"| **Modelo / Documento** | `{docTitle}` |");
            sb.AppendLine($"| **Remitente** | {from} |");
            sb.AppendLine($"| **Destinatario** | {to} |");
            sb.AppendLine($"| **Prioridad** | **{priority.ToUpperInvariant()}** |");
            if (!string.IsNullOrWhiteSpace(locationHint))
            {
                sb.AppendLine($"| **Ubicación / Sector** | {locationHint} |");
            }
            sb.AppendLine();
            sb.AppendLine($"## 2. Asunto / Consulta Técnica");
            sb.AppendLine();
            sb.AppendLine($"**Asunto:** {subject}");
            sb.AppendLine();
            sb.AppendLine("### Descripción del Problema / Consulta:");
            sb.AppendLine(question);
            sb.AppendLine();
            sb.AppendLine("### Solución Técnica Propuesta:");
            sb.AppendLine(proposedSolution);
            sb.AppendLine();
            sb.AppendLine("## 3. Elementos del Modelo Afectados");
            sb.AppendLine();
            sb.AppendLine($"Total de elementos involucrados: **{items.Count}**");
            sb.AppendLine();
            if (items.Count > 0)
            {
                sb.AppendLine("| ElementId | Categoría | Familia | Tipo | Nivel | Vol (m³) | Área (m²) | Long (m) |");
                sb.AppendLine("| :---: | :--- | :--- | :--- | :--- | :---: | :---: | :---: |");
                foreach (var it in items)
                {
                    sb.AppendLine($"| `{it.ElementId}` | {it.Category} | {it.Family} | {it.Type} | {it.Level} | {it.VolumeM3:F2} | {it.AreaM2:F2} | {it.LengthM:F2} |");
                }
                sb.AppendLine();
            }
            else
            {
                sb.AppendLine("*No se especificaron ElementIds directos en la solicitud.*");
                sb.AppendLine();
            }

            if (totalVol > 0 || totalArea > 0 || totalLen > 0)
            {
                sb.AppendLine("## 4. Estimación de Impacto Cuantitativo");
                sb.AppendLine();
                if (totalVol > 0) sb.AppendLine($"- **Volumen de obra comprometido:** {totalVol:F3} m³");
                if (totalArea > 0) sb.AppendLine($"- **Área comprometida:** {totalArea:F3} m²");
                if (totalLen > 0) sb.AppendLine($"- **Longitud comprometida:** {totalLen:F3} m");
                sb.AppendLine();
            }

            sb.AppendLine("## 5. Control de Firmas y Validación");
            sb.AppendLine();
            sb.AppendLine("| Rol | Nombre | Firma / V°B° | Fecha |");
            sb.AppendLine("| :--- | :--- | :---: | :---: |");
            sb.AppendLine("| **Elaborado por (Coordinador BIM)** | | | |");
            sb.AppendLine("| **Revisado por (Jefe de Proyecto)** | | | |");
            sb.AppendLine("| **Respuesta de Proyectista** | | | |");
            sb.AppendLine();

            return sb.ToString();
        }

        private sealed class RfiElementInfo
        {
            public long ElementId { get; set; }
            public string UniqueId { get; set; }
            public string Category { get; set; }
            public string Family { get; set; }
            public string Type { get; set; }
            public string Level { get; set; }
            public double VolumeM3 { get; set; }
            public double AreaM2 { get; set; }
            public double LengthM { get; set; }
        }
    }
}

