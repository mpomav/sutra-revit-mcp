using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Autodesk.Revit.DB;
using Newtonsoft.Json.Linq;

namespace Scio.Revit.Sdk
{
    /// <summary>
    /// Clean-room Revit API helpers for element lookup, parameter I/O, category resolution, and serialization.
    /// </summary>
    public static class RevitElementKit
    {
        public static Element? ResolveElement(Document doc, long id)
        {
            if (doc == null || id <= 0) return null;
            return doc.GetElement(IdCompat.FromInt64(id));
        }

        public static Element? ResolveElement(Document doc, JToken? idOrUniqueId)
        {
            if (doc == null || idOrUniqueId == null || idOrUniqueId.Type == JTokenType.Null)
                return null;

            if (idOrUniqueId.Type == JTokenType.Integer)
                return ResolveElement(doc, idOrUniqueId.Value<long>());

            string s = idOrUniqueId.ToString().Trim();
            if (long.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out long parsed))
                return ResolveElement(doc, parsed);

            return doc.GetElement(s);
        }

        public static List<Element> ResolveElements(Document doc, JArray? ids)
        {
            var result = new List<Element>();
            if (doc == null || ids == null) return result;
            foreach (var tok in ids)
            {
                var el = ResolveElement(doc, tok);
                if (el != null) result.Add(el);
            }
            return result;
        }

        public static BuiltInCategory? ResolveBuiltInCategory(string? name)
        {
            if (string.IsNullOrWhiteSpace(name)) return null;
            string raw = name!.Trim();

            if (Enum.TryParse<BuiltInCategory>(raw, true, out var exact))
                return exact;

            if (!raw.StartsWith("OST_", StringComparison.OrdinalIgnoreCase) &&
                Enum.TryParse<BuiltInCategory>("OST_" + raw, true, out var prefixed))
                return prefixed;

            string norm = NormalizeKey(raw);
            foreach (var alias in CategoryAliases)
            {
                if (string.Equals(alias.Key, norm, StringComparison.Ordinal))
                    return alias.Value;
            }

            foreach (BuiltInCategory bic in Enum.GetValues(typeof(BuiltInCategory)))
            {
                string n = bic.ToString();
                if (n.StartsWith("OST_", StringComparison.Ordinal) &&
                    string.Equals(NormalizeKey(n.Substring(4)), norm, StringComparison.Ordinal))
                    return bic;
            }

            return null;
        }

        public static Category? ResolveDocumentCategory(Document doc, string? name)
        {
            if (doc == null || string.IsNullOrWhiteSpace(name)) return null;
            var bic = ResolveBuiltInCategory(name);
            if (bic.HasValue)
            {
                try { return Category.GetCategory(doc, bic.Value); }
                catch { /* fall through to display name match */ }
            }

            string norm = NormalizeKey(name!);
            foreach (Category cat in doc.Settings.Categories)
            {
                if (cat != null && string.Equals(NormalizeKey(cat.Name), norm, StringComparison.Ordinal))
                    return cat;
            }
            return null;
        }

        public static Parameter? FindParameter(Element element, string paramName, bool includeType = false)
        {
            if (element == null || string.IsNullOrWhiteSpace(paramName)) return null;

            Parameter? p = element.LookupParameter(paramName);
            if (p != null) return p;

            foreach (Parameter candidate in element.Parameters)
            {
                if (candidate?.Definition != null &&
                    string.Equals(candidate.Definition.Name, paramName, StringComparison.OrdinalIgnoreCase))
                    return candidate;
            }

            if (Enum.TryParse<BuiltInParameter>(paramName, true, out var bip))
            {
                p = element.get_Parameter(bip);
                if (p != null) return p;
            }

            if (includeType && element.Document != null)
            {
                ElementId typeId = element.GetTypeId();
                if (typeId != null && typeId != ElementId.InvalidElementId)
                {
                    Element? typeEl = element.Document.GetElement(typeId);
                    if (typeEl != null)
                        return FindParameter(typeEl, paramName, includeType: false);
                }
            }

            return null;
        }

        public static JToken ReadParameterToken(Parameter p)
        {
            if (p == null || !p.HasValue) return JValue.CreateNull();
            switch (p.StorageType)
            {
                case StorageType.String:
                    return p.AsString() ?? string.Empty;
                case StorageType.Integer:
                    return p.AsInteger();
                case StorageType.Double:
                    return Math.Round(p.AsDouble(), 6);
                case StorageType.ElementId:
                    return IdCompat.ToInt64(p.AsElementId());
                default:
                    return p.AsValueString() ?? string.Empty;
            }
        }

        public static string ReadParameterDisplay(Parameter? p)
        {
            if (p == null || !p.HasValue) return string.Empty;
            string? vs = p.AsValueString();
            if (!string.IsNullOrEmpty(vs)) return vs!;
            switch (p.StorageType)
            {
                case StorageType.String: return p.AsString() ?? string.Empty;
                case StorageType.Integer: return p.AsInteger().ToString(CultureInfo.InvariantCulture);
                case StorageType.Double: return p.AsDouble().ToString("G6", CultureInfo.InvariantCulture);
                case StorageType.ElementId: return IdCompat.ToInt64(p.AsElementId()).ToString(CultureInfo.InvariantCulture);
                default: return string.Empty;
            }
        }

        public static bool TrySetParameter(Parameter p, JToken? value, out string error, string? doubleUnit = null)
        {
            error = string.Empty;
            if (p == null) { error = "Parameter not found."; return false; }
            if (p.IsReadOnly) { error = $"Parameter '{p.Definition?.Name}' is read-only."; return false; }
            if (value == null || value.Type == JTokenType.Null)
            {
                if (p.StorageType == StorageType.String)
                {
                    p.Set(string.Empty);
                    return true;
                }
                error = $"Cannot assign null to {p.StorageType} parameter '{p.Definition?.Name}'.";
                return false;
            }

            try
            {
                switch (p.StorageType)
                {
                    case StorageType.String:
                        return p.Set(value.Type == JTokenType.String ? value.Value<string>() ?? string.Empty : value.ToString());

                    case StorageType.Integer:
                        if (value.Type == JTokenType.Boolean)
                            return p.Set(value.Value<bool>() ? 1 : 0);
                        if (int.TryParse(value.ToString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out int iv))
                            return p.Set(iv);
                        error = $"Value '{value}' is not a valid integer.";
                        return false;

                    case StorageType.Double:
                        if (!double.TryParse(value.ToString(), NumberStyles.Float, CultureInfo.InvariantCulture, out double dv))
                        {
                            if (p.SetValueString(value.ToString()))
                                return true;
                            error = $"Value '{value}' is not a valid number.";
                            return false;
                        }
                        double internalVal = ConvertDoubleToInternal(p, dv, doubleUnit);
                        return p.Set(internalVal);

                    case StorageType.ElementId:
                        if (long.TryParse(value.ToString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out long eid))
                            return p.Set(IdCompat.FromInt64(eid));
                        error = $"Value '{value}' is not a valid ElementId.";
                        return false;

                    default:
                        error = $"Unsupported parameter storage type: {p.StorageType}.";
                        return false;
                }
            }
            catch (Exception ex)
            {
                error = ex.Message;
                return false;
            }
        }

        private static double ConvertDoubleToInternal(Parameter p, double value, string? unit)
        {
            if (string.IsNullOrWhiteSpace(unit)) return value;
            string u = unit!.Trim().ToLowerInvariant();
            if (u == "auto" || u == "metric")
            {
                try
                {
                    ForgeTypeId dt = p.Definition.GetDataType();
                    if (dt == SpecTypeId.Length) return UnitKit.MmToFeet(value);
                    if (dt == SpecTypeId.Angle) return UnitKit.DegToRad(value);
                    if (dt == SpecTypeId.Area) return UnitKit.SqMToSqFeet(value);
                    if (dt == SpecTypeId.Volume) return UnitKit.CuMToCuFeet(value);
                }
                catch { }
                return value;
            }
            switch (u)
            {
                case "mm": return UnitKit.MmToFeet(value);
                case "cm": return UnitKit.MmToFeet(value * 10.0);
                case "m": return UnitKit.MmToFeet(value * 1000.0);
                case "ft":
                case "feet":
                case "internal": return value;
                case "in":
                case "inch": return value / 12.0;
                case "deg":
                case "degrees": return UnitKit.DegToRad(value);
                case "m2":
                case "sqm": return UnitKit.SqMToSqFeet(value);
                case "m3":
                case "cum": return UnitKit.CuMToCuFeet(value);
                default: return value;
            }
        }

        public static JObject SummarizeElement(Element el, bool includeParams = false, bool includeBbox = false)
        {
            var obj = new JObject
            {
                ["id"] = IdCompat.ToInt64(el.Id),
                ["unique_id"] = el.UniqueId,
                ["name"] = el.Name ?? string.Empty,
                ["category"] = el.Category?.Name ?? string.Empty
            };

            ElementId typeId = el.GetTypeId();
            if (typeId != null && typeId != ElementId.InvalidElementId)
            {
                obj["type_id"] = IdCompat.ToInt64(typeId);
                if (el.Document?.GetElement(typeId) is ElementType et)
                {
                    obj["family"] = et.FamilyName ?? string.Empty;
                    obj["type"] = et.Name ?? string.Empty;
                }
            }

            if (el.LevelId != null && el.LevelId != ElementId.InvalidElementId && el.Document != null)
            {
                if (el.Document.GetElement(el.LevelId) is Level lvl)
                    obj["level"] = lvl.Name;
            }

            if (includeBbox)
            {
                var bbox = SerializeBoundingBoxMm(el.get_BoundingBox(null));
                if (bbox != null) obj["bbox_mm"] = bbox;
            }

            if (includeParams)
            {
                var pObj = new JObject();
                foreach (Parameter p in el.Parameters)
                {
                    if (p?.Definition == null || !p.HasValue) continue;
                    string pName = p.Definition.Name;
                    if (!pObj.ContainsKey(pName))
                        pObj[pName] = ReadParameterDisplay(p);
                }
                obj["parameters"] = pObj;
            }

            return obj;
        }

        public static JObject? SerializeBoundingBoxMm(BoundingBoxXYZ? bb)
        {
            if (bb == null) return null;
            return new JObject
            {
                ["min"] = new JArray(
                    Math.Round(UnitKit.FeetToMm(bb.Min.X), 1),
                    Math.Round(UnitKit.FeetToMm(bb.Min.Y), 1),
                    Math.Round(UnitKit.FeetToMm(bb.Min.Z), 1)),
                ["max"] = new JArray(
                    Math.Round(UnitKit.FeetToMm(bb.Max.X), 1),
                    Math.Round(UnitKit.FeetToMm(bb.Max.Y), 1),
                    Math.Round(UnitKit.FeetToMm(bb.Max.Z), 1))
            };
        }

        private static string NormalizeKey(string s)
        {
            return new string(s.ToLowerInvariant().Where(char.IsLetterOrDigit).ToArray());
        }

        private static readonly Dictionary<string, BuiltInCategory> CategoryAliases =
            new Dictionary<string, BuiltInCategory>(StringComparer.Ordinal)
            {
                ["wall"] = BuiltInCategory.OST_Walls,
                ["walls"] = BuiltInCategory.OST_Walls,
                ["muros"] = BuiltInCategory.OST_Walls,
                ["door"] = BuiltInCategory.OST_Doors,
                ["doors"] = BuiltInCategory.OST_Doors,
                ["puertas"] = BuiltInCategory.OST_Doors,
                ["window"] = BuiltInCategory.OST_Windows,
                ["windows"] = BuiltInCategory.OST_Windows,
                ["ventanas"] = BuiltInCategory.OST_Windows,
                ["floor"] = BuiltInCategory.OST_Floors,
                ["floors"] = BuiltInCategory.OST_Floors,
                ["suelos"] = BuiltInCategory.OST_Floors,
                ["roof"] = BuiltInCategory.OST_Roofs,
                ["roofs"] = BuiltInCategory.OST_Roofs,
                ["ceiling"] = BuiltInCategory.OST_Ceilings,
                ["ceilings"] = BuiltInCategory.OST_Ceilings,
                ["column"] = BuiltInCategory.OST_StructuralColumns,
                ["columns"] = BuiltInCategory.OST_StructuralColumns,
                ["structuralcolumns"] = BuiltInCategory.OST_StructuralColumns,
                ["beam"] = BuiltInCategory.OST_StructuralFraming,
                ["beams"] = BuiltInCategory.OST_StructuralFraming,
                ["framing"] = BuiltInCategory.OST_StructuralFraming,
                ["structuralframing"] = BuiltInCategory.OST_StructuralFraming,
                ["foundation"] = BuiltInCategory.OST_StructuralFoundation,
                ["foundations"] = BuiltInCategory.OST_StructuralFoundation,
                ["rebar"] = BuiltInCategory.OST_Rebar,
                ["room"] = BuiltInCategory.OST_Rooms,
                ["rooms"] = BuiltInCategory.OST_Rooms,
                ["habitaciones"] = BuiltInCategory.OST_Rooms,
                ["level"] = BuiltInCategory.OST_Levels,
                ["levels"] = BuiltInCategory.OST_Levels,
                ["grid"] = BuiltInCategory.OST_Grids,
                ["grids"] = BuiltInCategory.OST_Grids,
                ["view"] = BuiltInCategory.OST_Views,
                ["views"] = BuiltInCategory.OST_Views,
                ["sheet"] = BuiltInCategory.OST_Sheets,
                ["sheets"] = BuiltInCategory.OST_Sheets,
                ["pipe"] = BuiltInCategory.OST_PipeCurves,
                ["pipes"] = BuiltInCategory.OST_PipeCurves,
                ["duct"] = BuiltInCategory.OST_DuctCurves,
                ["ducts"] = BuiltInCategory.OST_DuctCurves,
                ["cabletray"] = BuiltInCategory.OST_CableTray,
                ["conduit"] = BuiltInCategory.OST_Conduit,
                ["furniture"] = BuiltInCategory.OST_Furniture,
                ["genericmodel"] = BuiltInCategory.OST_GenericModel,
                ["genericmodels"] = BuiltInCategory.OST_GenericModel,
                ["stair"] = BuiltInCategory.OST_Stairs,
                ["stairs"] = BuiltInCategory.OST_Stairs,
                ["railing"] = BuiltInCategory.OST_StairsRailing,
                ["railings"] = BuiltInCategory.OST_StairsRailing
            };
    }
}
