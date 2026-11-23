using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Scio.Abstractions;
using Scio.Revit.Core;

namespace Scio.Revit.Commands
{
    public sealed class ExplainFindingCommand : ICommand
    {
        public string Name => "scio_explain_finding";

        public string Description =>
            "Explicacion tecnica y propuesta de correccion tipada para un hallazgo de auditoria (audit finding). " +
            "Identifica la causa raiz tecnica/normativa, los elementos afectados y proporciona el comando y el " +
            "payload JSON ejecutable exacto para remediarlos mediante herramientas tipadas o scio_apply_corrections.";

        public CommandResult Execute(UIApplication app, string paramsJson)
        {
            JObject request;
            try { request = string.IsNullOrWhiteSpace(paramsJson) ? new JObject() : JsonText.ParseObject(paramsJson); }
            catch (JsonException ex) { return CommandResult.Fail("Parameters must be a JSON object: " + ex.Message); }

            string findingId = request.Value<string>("finding_id");
            string checkName = request.Value<string>("check");
            string fingerprint = request.Value<string>("finding_set_fingerprint");
            long? targetElementId = request.Value<long?>("element_id");

            RecordedFinding recorded = null;
            if (!string.IsNullOrEmpty(fingerprint) && AuditFindingSetStore.Session.TryGet(fingerprint, out FindingSetRecord rec))
            {
                recorded = !string.IsNullOrEmpty(findingId) ? rec.Find(findingId) : rec.FindByCheck(checkName);
            }

            if (recorded != null)
            {
                if (string.IsNullOrEmpty(checkName)) checkName = recorded.Check;
                if (string.IsNullOrEmpty(findingId)) findingId = recorded.FindingId;
            }

            if (string.IsNullOrEmpty(checkName) && string.IsNullOrEmpty(findingId))
            {
                return CommandResult.Fail("scio_explain_finding requires 'finding_id' or 'check' name.");
            }

            string normCheck = (checkName ?? findingId ?? "").Trim().ToLowerInvariant();

            var elementIds = new List<long>();
            if (targetElementId.HasValue && targetElementId.Value > 0)
            {
                elementIds.Add(targetElementId.Value);
            }
            else if (recorded != null && recorded.ElementIds != null && recorded.ElementIds.Count > 0)
            {
                elementIds.AddRange(recorded.ElementIds);
            }

            Document doc = app?.ActiveUIDocument?.Document;
            string activeDocName = doc?.Title;
            string configuredClassParam = ProjectClassificationLookup.FindClassificationParameter(doc?.PathName, out _);

            var explanation = BuildExplanation(normCheck, findingId, fingerprint, elementIds, activeDocName, configuredClassParam);

            return CommandResult.Ok(explanation);
        }

        private static JObject BuildExplanation(
            string check,
            string findingId,
            string fingerprint,
            List<long> elementIds,
            string activeDocTitle,
            string configuredClassParam)
        {
            string title;
            string rootCause;
            string impact;
            string suggestedTool;
            string action;
            JObject executablePayload;
            var requiredInputs = new JArray();

            string docToken = !string.IsNullOrWhiteSpace(activeDocTitle) ? activeDocTitle : "<target_document_title>";
            string fpToken = !string.IsNullOrWhiteSpace(fingerprint) ? fingerprint : "<finding_set_fingerprint>";
            string fIdToken = !string.IsNullOrWhiteSpace(findingId) ? findingId : check;

            if (check.Contains("group") || check.Contains("orphan_group"))
            {
                title = "Tipos de Grupo huerfanos / no utilizados (Orphan Groups)";
                rootCause = "Existen tipos de grupo cargados en la definicion del proyecto que no tienen ninguna instancia colocada en el modelo.";
                impact = "Sobrecarga innecesaria en la base de datos de Revit (.rvt), lentitud al copiar/pegar y riesgo de corrupcion por definiciones huerfanas.";
                suggestedTool = "scio_apply_corrections";
                action = "purge_unused_group_types";
                executablePayload = new JObject
                {
                    ["target_document"] = docToken,
                    ["finding_set_fingerprint"] = fpToken,
                    ["actions"] = new JArray
                    {
                        new JObject
                        {
                            ["finding_id"] = findingId ?? "finding-orphan-groups",
                            ["element_ids"] = new JArray(elementIds.Take(100))
                        }
                    },
                    ["dry_run"] = true
                };
                if (string.IsNullOrWhiteSpace(fingerprint)) requiredInputs.Add("finding_set_fingerprint");
            }
            else if (check.Contains("room") || check.Contains("unplaced"))
            {
                title = "Habitaciones / Espacios no colocados (Unplaced Rooms)";
                rootCause = "Se crearon habitaciones en tablas de planificacion o se eliminaron sus delimitadores fisicos, quedando en estado 'No colocado' o 'No delimitado'.";
                impact = "Distorsiona tablas de metrado de areas, reportes de acabados y genera advertencias en exportaciones IFC.";
                suggestedTool = "scio_delete_verified";
                action = "delete_ids";
                executablePayload = new JObject
                {
                    ["target_document"] = docToken,
                    ["mode"] = "ids",
                    ["ids"] = new JArray(elementIds.Take(100)),
                    ["dry_run"] = true
                };
                if (elementIds.Count == 0) requiredInputs.Add("ids");
            }
            else if (check.Contains("views_without_template") || check.Contains("view_template"))
            {
                title = "Vistas sin plantilla de vista asignada (Views without Template)";
                rootCause = "Existen vistas graficas de proyecto que no tienen vinculada una ViewTemplate controlada, permitiendo discrepancias de visibilidad.";
                impact = "Inconsistencia grafica en laminas entregables y riesgo de ocultar elementos requeridos antes de exportar.";
                suggestedTool = "scio_apply_corrections";
                action = "assign_view_template";
                requiredInputs.Add("inputs.template_view_id");
                executablePayload = new JObject
                {
                    ["target_document"] = docToken,
                    ["finding_set_fingerprint"] = fpToken,
                    ["actions"] = new JArray
                    {
                        new JObject
                        {
                            ["finding_id"] = fIdToken,
                            ["inputs"] = new JObject { ["template_view_id"] = "<template_view_id>" }
                        }
                    },
                    ["dry_run"] = true
                };
            }
            else if (check.Contains("imported_cad") || check.Contains("cad"))
            {
                title = "CAD importados (no vinculados) en el modelo";
                rootCause = "Se importaron archivos DWG/DXF directamente dentro del .rvt en lugar de vincularlos (Link CAD).";
                impact = "Inflacion severa del peso del archivo, polucion de estilos de linea/patrones y deterioro del rendimiento grafico.";
                suggestedTool = "scio_manage_cad_links";
                action = "inspect_and_replace_imports";
                executablePayload = new JObject
                {
                    ["operation"] = "list"
                };
            }
            else if (check.Contains("unpinned"))
            {
                title = "Vinculos o ejes/niveles sin bloqueo (Unpinned)";
                rootCause = "Elementos criticos de referencia (links, rejillas o niveles) permanecen desbloqueados y expuestos a desplazamientos accidentales.";
                impact = "Riesgo de desalineacion de coordenadas compartidas y errores de coordinacion multidisciplina.";
                suggestedTool = "scio_apply_corrections";
                action = "pin_elements";
                executablePayload = new JObject
                {
                    ["target_document"] = docToken,
                    ["finding_set_fingerprint"] = fpToken,
                    ["actions"] = new JArray { new JObject { ["finding_id"] = fIdToken } },
                    ["dry_run"] = true
                };
            }
            else if (check.Contains("wall_sketch_drift"))
            {
                title = "Perfil editado de muro desfasado del eje (Wall Sketch Drift)";
                rootCause = "Un muro fue desplazado dejando su Wall.SketchId desfasado respecto al plano de su LocationCurve actual.";
                impact = "Geometria invalida, areas/volumenes erroneos y posible corrupcion en operaciones posteriores.";
                suggestedTool = "scio_query_model";
                action = "inspect_affected_walls";
                executablePayload = new JObject
                {
                    ["element_ids"] = new JArray(elementIds.Take(50)),
                    ["include_parameters"] = true
                };
            }
            else if (check.Contains("wall") && check.Contains("height"))
            {
                title = "Muros sin restriccion superior de nivel (Unconnected Walls)";
                rootCause = "Los muros tienen su restriccion superior como 'No conectada' (Unconnected) en lugar de estar asociados a un Nivel del proyecto.";
                impact = "Inconsistencias geometricas cuando cambian las cotas de entrepiso; los muros no se adaptan automaticamente al variar alturas de nivel.";
                suggestedTool = "scio_write_params_verified";
                action = "set_top_constraint";
                requiredInputs.Add("target_level_id_or_name");
                var sampleWrites = new JArray();
                foreach (long id in elementIds.Take(20))
                {
                    sampleWrites.Add(new JObject
                    {
                        ["target_id"] = id,
                        ["parameter"] = "WALL_HEIGHT_TYPE",
                        ["value"] = "<target_level_id_or_name>"
                    });
                }
                executablePayload = new JObject
                {
                    ["target_document"] = docToken,
                    ["writes"] = sampleWrites,
                    ["dry_run"] = true
                };
            }
            else if (check.Contains("param") || check.Contains("codigo") || check.Contains("clasificacion"))
            {
                title = "Falta de parametro de clasificacion / codigo de partida";
                rootCause = "Los elementos no tienen asignado el valor del parametro de clasificacion requerido segun el BEP o Guia BIM.";
                impact = "Imposibilita el metrado automatico y la interoperabilidad 5D con presupuestos y analisis de costos.";
                suggestedTool = "scio_write_params_verified";
                action = "assign_classification_parameter";
                string paramToken = !string.IsNullOrWhiteSpace(configuredClassParam) ? configuredClassParam : "<classification_parameter>";
                if (string.IsNullOrWhiteSpace(configuredClassParam)) requiredInputs.Add("classification_parameter");
                requiredInputs.Add("classification_code");
                var sampleWrites = new JArray();
                foreach (long id in elementIds.Take(20))
                {
                    sampleWrites.Add(new JObject
                    {
                        ["target_id"] = id,
                        ["parameter"] = paramToken,
                        ["value"] = "<classification_code>"
                    });
                }
                executablePayload = new JObject
                {
                    ["target_document"] = docToken,
                    ["writes"] = sampleWrites,
                    ["dry_run"] = true
                };
            }
            else if (check.Contains("open_mep") || check.Contains("connector"))
            {
                title = "Conectores MEP abiertos / sistemas desconectados";
                rootCause = "Ductos, tuberias o bandejas terminan sin tapon, equipo ni continuidad con otro conector del sistema.";
                impact = "Impide calculos de perdida de carga/flujo y genera inconsistencias en planos isometricos y fabricacion.";
                suggestedTool = "scio_connect_mep";
                action = "inspect_and_connect_open_ends";
                requiredInputs.Add("element_pairs");
                executablePayload = new JObject
                {
                    ["target_document"] = docToken,
                    ["dry_run"] = true
                };
            }
            else if (check.Contains("in_place"))
            {
                title = "Familias in-situ (In-Place Families) excesivas o repetidas";
                rootCause = "Se modelaron componentes repetitivos como familias in-situ en lugar de familias cargables (.rfa).";
                impact = "Aumento del tamaño del modelo, duplicacion de geometria y dificultad para estandarizar cuantificaciones.";
                suggestedTool = "scio_query_model";
                action = "inspect_in_place_instances";
                executablePayload = new JObject
                {
                    ["element_ids"] = new JArray(elementIds.Take(50))
                };
            }
            else
            {
                title = $"Hallazgo de auditoria: {check}";
                rootCause = "Discrepancia detectada respecto a las reglas de calidad, estandares de modelado o integridad de datos del proyecto.";
                impact = "Riesgo de advertencias en coordinacion o metrados inexactos.";
                suggestedTool = "scio_apply_corrections";
                action = "guided_correction";
                if (string.IsNullOrWhiteSpace(fingerprint)) requiredInputs.Add("finding_set_fingerprint");
                executablePayload = new JObject
                {
                    ["target_document"] = docToken,
                    ["finding_set_fingerprint"] = fpToken,
                    ["actions"] = new JArray { new JObject { ["finding_id"] = fIdToken } },
                    ["dry_run"] = true
                };
            }

            if (string.IsNullOrWhiteSpace(activeDocTitle)) requiredInputs.Add("target_document");

            return new JObject
            {
                ["check"] = check,
                ["finding_id"] = findingId ?? "N/A",
                ["title"] = title,
                ["root_cause"] = rootCause,
                ["technical_impact"] = impact,
                ["affected_element_count"] = elementIds.Count,
                ["sample_element_ids"] = new JArray(elementIds.Take(20)),
                ["remediation"] = new JObject
                {
                    ["suggested_tool"] = suggestedTool,
                    ["action"] = action,
                    ["dry_run_supported"] = true,
                    ["required_user_inputs"] = requiredInputs,
                    ["sample_payload"] = executablePayload
                },
                ["guidance"] = requiredInputs.Count > 0
                    ? $"Completa primero los valores faltantes ({string.Join(", ", requiredInputs)}) y ejecuta '{suggestedTool}' con 'dry_run: true' antes de confirmar."
                    : $"Ejecuta '{suggestedTool}' utilizando el payload recomendado con 'dry_run: true' primero para previsualizar el impacto antes de confirmar."
            };
        }
    }
}

