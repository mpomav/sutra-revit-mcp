#nullable disable
// -----------------------------------------------------------------------------
// SCIO Revit MCP - Bilingual (ES/EN) BIM Semantic Search Index
//
// Maps natural-language user requests in Spanish or English to SCIO tools and
// procedures without network calls or external embeddings. Reuses TextFold.Fold
// so accented and unaccented BIM terms score uniformly.
// -----------------------------------------------------------------------------
using System;
using System.Collections.Generic;
using System.Linq;
using Scio.Abstractions;

namespace Scio.Server
{
    internal static class BimSearchIndex
    {
        private static readonly string[][] SynonymGroups =
        {
            new[] { "muro", "muros", "pared", "tabique", "wall", "walls", "ost_walls", "brick", "partition" },
            new[] { "losa", "losas", "suelo", "suelos", "piso", "pisos", "floor", "floors", "slab", "slabs", "ost_floors" },
            new[] { "columna", "columnas", "pilar", "pilares", "column", "columns", "ost_structuralcolumns", "ost_columns" },
            new[] { "viga", "vigas", "trabe", "trabes", "beam", "beams", "framing", "ost_structuralframing", "estructura", "structure" },
            new[] { "cimentacion", "zapata", "foundation", "footing", "ost_structuralfoundation" },
            new[] { "puerta", "puertas", "door", "doors", "ost_doors", "ventana", "ventanas", "window", "windows", "ost_windows" },
            new[] { "techo", "cubierta", "cielo", "cielorraso", "roof", "roofs", "ceiling", "ceilings" },
            new[] { "metrado", "metrados", "cubicacion", "cantidad", "cantidades", "partida", "partidas", "takeoff", "quantity", "quantities", "qto", "volumen", "area", "longitud" },
            new[] { "presupuesto", "costo", "costos", "bc3", "budget", "cost", "pricing", "excel", "xlsx", "presupuestal" },
            new[] { "censo", "inventario", "listar", "contar", "census", "count", "inventory", "list" },
            new[] { "auditoria", "auditar", "calidad", "revision", "revisar", "audit", "qa", "qc", "check", "hallazgo", "finding", "findings", "corregir", "correction" },
            new[] { "parametro", "parametros", "atributo", "atributos", "propiedad", "propiedades", "parameter", "parameters", "shared", "compartido", "keynote", "clasificacion", "classification" },
            new[] { "interferencia", "interferencias", "colision", "colisiones", "cruce", "clash", "clashes", "coordination", "coordinacion", "rfi" },
            new[] { "plano", "planos", "lamina", "laminas", "sheet", "sheets", "planimetria", "planimetry", "viewport", "leyenda", "legend" },
            new[] { "vista", "vistas", "planta", "corte", "seccion", "elevacion", "3d", "view", "views", "section", "elevation", "template", "plantilla", "filtro", "filter" },
            new[] { "captura", "capturar", "imagen", "foto", "pantallazo", "screenshot", "capture", "visual", "png", "render" },
            new[] { "tabla", "tablas", "planificacion", "schedule", "schedules", "cuadro" },
            new[] { "vinculo", "vinculos", "link", "links", "federado", "federated", "rvt", "coordenadas", "coordinates" },
            new[] { "cad", "dwg", "dxf", "pdf", "ifc", "gbxml", "nwc", "fbx", "cobie", "dwfx", "dgn", "exportar", "export", "importar", "import" },
            new[] { "acero", "fierro", "armadura", "refuerzo", "rebar", "reinforcement", "despiece" },
            new[] { "mep", "tuberia", "tuberias", "ducto", "ductos", "bandeja", "electrico", "sanitaria", "hvac", "pipe", "pipes", "duct", "ducts", "conduit", "electrical", "circuit", "pendiente", "slope", "hanger" },
            new[] { "topografia", "toposolido", "terreno", "excavacion", "topo", "toposolid", "grade", "site" },
            new[] { "limpiar", "limpieza", "purgar", "eliminar", "borrar", "delete", "purge", "cleanup", "unused", "huerfano", "orphan" },
            new[] { "nomenclatura", "nombre", "nombres", "renombrar", "naming", "iso19650", "iso", "beP", "bep", "eir" },
            new[] { "reporte", "informe", "resumen", "report", "executive", "dashboard", "powerbi" },
            new[] { "avance", "progreso", "comparar", "diferencia", "cambios", "progress", "diff", "compare", "delta", "version", "semana", "presync", "sincronizar", "sync" },
            new[] { "local", "sqlite", "sql", "historial", "run", "runs", "consulta", "query", "offline" },
            new[] { "seleccion", "seleccionar", "seleccionado", "selection", "contexto", "context", "navegar", "navigate", "zoom" },
            new[] { "regla", "reglas", "masivo", "lote", "rule", "rules", "batch", "mapeo", "map", "auto" },
            new[] { "familia", "familias", "family", "families", "rfa", "tipo", "tipos", "type", "instancia" },
            new[] { "mobiliario", "mueble", "muebles", "furniture", "ost_furniture", "equipamiento", "equipment", "silla", "mesa", "escritorio" },
            new[] { "nivel", "niveles", "level", "levels", "piso", "pisos", "planta", "plantas" },
            new[] { "crear", "modelar", "generar", "create", "build", "colocar", "place", "ubicar", "insertar", "insert", "mover", "rotar", "transform", "copiar", "copy", "dividir", "split" },
            new[] { "anotar", "cota", "cotas", "etiqueta", "etiquetas", "tag", "tags", "dimension", "dimensions", "annotate", "detalle", "detail" },
            new[] { "grupo", "grupos", "group", "groups", "workset", "worksets", "subproyecto" },
            new[] { "python", "script", "codigo", "macro", "procedimiento", "procedure", "workflow" }
        };

        private static readonly Dictionary<string, string[]> TermToSynonyms = BuildSynonymMap();

        private static readonly char[] SplitChars =
        {
            ' ', '\t', '\r', '\n', ',', '.', ';', ':', '-', '_', '/', '\\', '(', ')', '[', ']', '{', '}', '"', '\''
        };

        private static Dictionary<string, string[]> BuildSynonymMap()
        {
            var map = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
            foreach (string[] group in SynonymGroups)
            {
                var normalized = group.Select(TextFold.Fold).Where(s => s.Length > 0).Distinct(StringComparer.Ordinal).ToArray();
                foreach (string term in normalized)
                {
                    if (!map.TryGetValue(term, out var set))
                    {
                        set = new HashSet<string>(StringComparer.Ordinal);
                        map[term] = set;
                    }
                    foreach (string syn in normalized)
                    {
                        if (!string.Equals(syn, term, StringComparison.Ordinal)) set.Add(syn);
                    }
                }
            }
            return map.ToDictionary(kv => kv.Key, kv => kv.Value.ToArray(), StringComparer.Ordinal);
        }

        public static string[] Tokenize(string text)
        {
            if (string.IsNullOrWhiteSpace(text)) return Array.Empty<string>();
            string folded = TextFold.Fold(text);
            return folded.Split(SplitChars, StringSplitOptions.RemoveEmptyEntries)
                         .Where(t => t.Length > 1)
                         .Distinct(StringComparer.Ordinal)
                         .ToArray();
        }

        public static int ScoreText(string query, string targetText, string identifier = null)
        {
            string[] tokens = Tokenize(query);
            if (tokens.Length == 0) return 0;

            string foldedId = TextFold.Fold(identifier ?? string.Empty);
            string foldedTarget = TextFold.Fold(targetText ?? string.Empty);
            var targetWords = new HashSet<string>(
                foldedTarget.Split(SplitChars, StringSplitOptions.RemoveEmptyEntries),
                StringComparer.Ordinal);

            int score = 0;
            foreach (string token in tokens)
            {
                if (!string.IsNullOrEmpty(foldedId) && foldedId.Contains(token))
                {
                    score += string.Equals(foldedId, token, StringComparison.Ordinal) ? 14 : 8;
                    continue;
                }
                if (targetWords.Contains(token))
                {
                    score += 6;
                    continue;
                }
                if (token.Length >= 3 && foldedTarget.Contains(token))
                {
                    score += 4;
                    continue;
                }

                if (TermToSynonyms.TryGetValue(token, out string[] syns))
                {
                    int bestSyn = 0;
                    foreach (string syn in syns)
                    {
                        if (!string.IsNullOrEmpty(foldedId) && foldedId.Contains(syn))
                        {
                            bestSyn = Math.Max(bestSyn, 6);
                        }
                        else if (targetWords.Contains(syn))
                        {
                            bestSyn = Math.Max(bestSyn, 4);
                        }
                        else if (syn.Length >= 4 && foldedTarget.Contains(syn))
                        {
                            bestSyn = Math.Max(bestSyn, 2);
                        }
                    }
                    score += bestSyn;
                }
            }

            return score;
        }

        public static bool MatchesEffectFilter(ToolDescriptor tool, string effectFilter)
        {
            if (tool == null || string.IsNullOrWhiteSpace(effectFilter)) return true;
            string f = effectFilter.Trim().ToLowerInvariant();
            switch (f)
            {
                case "read_only":
                case "read":
                    return tool.ReadOnly;
                case "write":
                case "mutating":
                    return !tool.ReadOnly;
                case "destructive":
                    return tool.Destructive;
                case "external":
                    return tool.OpenWorld;
                default:
                    return true;
            }
        }

        public static List<ToolDescriptor> SearchTools(
            IEnumerable<ToolDescriptor> candidates,
            string query,
            string effectFilter = null,
            int limit = 15)
        {
            var filtered = (candidates ?? Array.Empty<ToolDescriptor>())
                .Where(t => MatchesEffectFilter(t, effectFilter));

            if (string.IsNullOrWhiteSpace(query))
            {
                return filtered.Take(Math.Max(1, limit)).ToList();
            }

            return filtered
                .Select(t => new
                {
                    Tool = t,
                    Score = ScoreText(
                        query,
                        (t.Description ?? "") + " " + (t.Pack ?? ""),
                        t.Name)
                })
                .Where(x => x.Score > 0)
                .OrderByDescending(x => x.Score)
                .ThenBy(x => x.Tool.ReadOnly ? 0 : 1)
                .ThenBy(x => x.Tool.Name, StringComparer.Ordinal)
                .Take(Math.Max(1, limit))
                .Select(x => x.Tool)
                .ToList();
        }
    }
}

