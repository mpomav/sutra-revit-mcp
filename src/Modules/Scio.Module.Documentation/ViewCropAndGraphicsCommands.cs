using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Architecture;
using Newtonsoft.Json.Linq;
using Scio.Abstractions;
using Scio.Revit.Sdk;

namespace Scio.Module.Documentation
{
    internal static class ViewResolveKit
    {
        public static View ResolveView(Document doc, JToken? viewIdToken)
        {
            if (viewIdToken != null && viewIdToken.Type != JTokenType.Null)
            {
                var el = RevitElementKit.ResolveElement(doc, viewIdToken);
                if (el is View v) return v;
                throw new InvalidOperationException($"Element '{viewIdToken}' is not a valid View.");
            }

            if (doc.ActiveView != null) return doc.ActiveView;
            throw new InvalidOperationException("No active view and no 'view_id' supplied.");
        }

        public static JObject SerializeBoxMm(BoundingBoxXYZ? box)
        {
            if (box == null) return new JObject { ["defined"] = false };
            return new JObject
            {
                ["defined"] = true,
                ["min_mm"] = new JArray(
                    Math.Round(UnitKit.FeetToMm(box.Min.X), 1),
                    Math.Round(UnitKit.FeetToMm(box.Min.Y), 1),
                    Math.Round(UnitKit.FeetToMm(box.Min.Z), 1)),
                ["max_mm"] = new JArray(
                    Math.Round(UnitKit.FeetToMm(box.Max.X), 1),
                    Math.Round(UnitKit.FeetToMm(box.Max.Y), 1),
                    Math.Round(UnitKit.FeetToMm(box.Max.Z), 1))
            };
        }

        public static FillPatternElement? FindSolidFillPattern(Document doc)
        {
            return new FilteredElementCollector(doc)
                .OfClass(typeof(FillPatternElement))
                .Cast<FillPatternElement>()
                .FirstOrDefault(fp => fp.GetFillPattern().IsSolidFill);
        }
    }

    [ScioTool("scio_view_crop", "documentation", "Read or modify view crop box, annotation crop offsets, crop flags, or 3D section box (with dry-run preview).", ReadOnly = false)]
    public sealed class ViewCropCommand : IRevitCommand
    {
        [ScioParam("operation", "Operation to execute.", Required = true, EnumValues = new[]
        {
            "read", "set_crop_box", "reset_crop_shape", "set_crop_from_elements", "set_crop_from_room",
            "set_annotation_crop_offsets", "crop_active", "crop_visible",
            "read_section_box", "section_box_active", "set_section_box", "set_section_box_from_elements"
        })]
        public string Operation { get; set; } = "read";

        [ScioParam("view_id", "Optional target View ElementId (defaults to active view).")]
        public long? ViewId { get; set; }

        [ScioParam("min_mm", "Minimum corner [x, y, z] in mm for set_crop_box / set_section_box.")]
        public List<double>? MinMm { get; set; }

        [ScioParam("max_mm", "Maximum corner [x, y, z] in mm for set_crop_box / set_section_box.")]
        public List<double>? MaxMm { get; set; }

        [ScioParam("element_ids", "ElementIds for set_crop_from_elements or set_section_box_from_elements.")]
        public List<long>? ElementIds { get; set; }

        [ScioParam("room_id", "Room ElementId for set_crop_from_room.")]
        public long? RoomId { get; set; }

        [ScioParam("padding_mm", "Padding margin in mm around elements/room (default 500).")]
        public double PaddingMm { get; set; } = 500;

        [ScioParam("offset_mm", "Uniform annotation crop offset in mm (or use left/right/top/bottom_mm).")]
        public double? OffsetMm { get; set; }

        [ScioParam("enabled", "Boolean state for crop_active, crop_visible, or section_box_active.")]
        public bool? Enabled { get; set; }

        [ScioParam("dry_run", "Preview without committing (default true).")]
        public bool DryRun { get; set; } = true;

        public ToolDescriptor Descriptor { get; } = JsonSchemaBuilder.DescribeTool(typeof(ViewCropCommand));

        public ToolResponse Execute(RevitContext context, ToolRequest request)
        {
            var doc = context.RequireActiveDocument(request.TargetDocument);
            var view = ViewResolveKit.ResolveView(doc, request.Arguments["view_id"]);
            string op = (request.Arguments.Value<string>("operation") ?? "read").Trim().ToLowerInvariant();

            if (op == "read")
            {
                var mgr = view.GetCropRegionShapeManager();
                return ToolResponse.Success(request.RequestId, new JObject
                {
                    ["view_id"] = IdCompat.ToInt64(view.Id),
                    ["view_name"] = view.Name,
                    ["view_type"] = view.ViewType.ToString(),
                    ["crop_box_active"] = view.CropBoxActive,
                    ["crop_box_visible"] = view.CropBoxVisible,
                    ["crop_box"] = ViewResolveKit.SerializeBoxMm(view.CropBox),
                    ["can_have_shape"] = mgr?.CanHaveShape ?? false,
                    ["annotation_crop_active"] = view.get_Parameter(BuiltInParameter.VIEWER_ANNOTATION_CROP_ACTIVE)?.AsInteger() == 1
                }, $"Crop state read for view '{view.Name}'.");
            }

            if (op == "read_section_box")
            {
                if (view is not View3D v3d)
                    return ToolResponse.Refusal(request.RequestId, "not_3d_view", $"View '{view.Name}' is not a 3D view.");
                return ToolResponse.Success(request.RequestId, new JObject
                {
                    ["view_id"] = IdCompat.ToInt64(v3d.Id),
                    ["view_name"] = v3d.Name,
                    ["section_box_active"] = v3d.IsSectionBoxActive,
                    ["section_box"] = ViewResolveKit.SerializeBoxMm(v3d.GetSectionBox())
                }, $"Section box read for 3D view '{v3d.Name}'.");
            }

            bool dryRun = request.DryRun ?? request.Arguments.Value<bool?>("dry_run") ?? true;
            return TransactionPipeline.RunVerified(
                doc,
                request.RequestId,
                $"View Crop ({op})",
                dryRun,
                d =>
                {
                    switch (op)
                    {
                        case "crop_active":
                        {
                            bool flag = request.Arguments.Value<bool?>("enabled") ?? true;
                            view.CropBoxActive = flag;
                            return new JObject { ["view_id"] = IdCompat.ToInt64(view.Id), ["crop_box_active"] = view.CropBoxActive };
                        }
                        case "crop_visible":
                        {
                            bool flag = request.Arguments.Value<bool?>("enabled") ?? true;
                            view.CropBoxVisible = flag;
                            return new JObject { ["view_id"] = IdCompat.ToInt64(view.Id), ["crop_box_visible"] = view.CropBoxVisible };
                        }
                        case "reset_crop_shape":
                        {
                            var mgr = view.GetCropRegionShapeManager();
                            mgr?.RemoveCropRegionShape();
                            return new JObject { ["view_id"] = IdCompat.ToInt64(view.Id), ["crop_shape_reset"] = true };
                        }
                        case "set_crop_box":
                        {
                            var box = BuildBoxFromArgs(request.Arguments, view.CropBox?.Transform ?? Transform.Identity);
                            view.CropBoxActive = true;
                            view.CropBox = box;
                            return new JObject { ["view_id"] = IdCompat.ToInt64(view.Id), ["crop_box"] = ViewResolveKit.SerializeBoxMm(view.CropBox) };
                        }
                        case "set_crop_from_elements":
                        {
                            var elems = RevitElementKit.ResolveElements(d, request.Arguments["element_ids"] as JArray);
                            if (elems.Count == 0) throw new InvalidOperationException("No valid elements in 'element_ids'.");
                            double padFt = UnitKit.MmToFeet(request.Arguments.Value<double?>("padding_mm") ?? 500.0);
                            var bb = ComputeCombinedBox(elems, view, padFt);
                            view.CropBoxActive = true;
                            view.CropBox = bb;
                            return new JObject
                            {
                                ["view_id"] = IdCompat.ToInt64(view.Id),
                                ["element_count"] = elems.Count,
                                ["crop_box"] = ViewResolveKit.SerializeBoxMm(view.CropBox)
                            };
                        }
                        case "set_crop_from_room":
                        {
                            var el = RevitElementKit.ResolveElement(d, request.Arguments["room_id"]);
                            if (el is not Room room) throw new InvalidOperationException("Valid 'room_id' required.");
                            double padFt = UnitKit.MmToFeet(request.Arguments.Value<double?>("padding_mm") ?? 500.0);
                            var bb = ComputeCombinedBox(new List<Element> { room }, view, padFt);
                            view.CropBoxActive = true;
                            view.CropBox = bb;
                            return new JObject
                            {
                                ["view_id"] = IdCompat.ToInt64(view.Id),
                                ["room_id"] = IdCompat.ToInt64(room.Id),
                                ["room_name"] = room.Name,
                                ["crop_box"] = ViewResolveKit.SerializeBoxMm(view.CropBox)
                            };
                        }
                        case "set_annotation_crop_offsets":
                        {
                            double defOff = request.Arguments.Value<double?>("offset_mm") ?? 10.0;
                            var mgr = view.GetCropRegionShapeManager();
                            if (mgr == null) throw new InvalidOperationException("View does not support annotation crop offsets.");
                            var pAct = view.get_Parameter(BuiltInParameter.VIEWER_ANNOTATION_CROP_ACTIVE);
                            if (pAct != null && !pAct.IsReadOnly) pAct.Set(1);
                            mgr.LeftAnnotationCropOffset = UnitKit.MmToFeet(request.Arguments.Value<double?>("left_mm") ?? defOff);
                            mgr.RightAnnotationCropOffset = UnitKit.MmToFeet(request.Arguments.Value<double?>("right_mm") ?? defOff);
                            mgr.TopAnnotationCropOffset = UnitKit.MmToFeet(request.Arguments.Value<double?>("top_mm") ?? defOff);
                            mgr.BottomAnnotationCropOffset = UnitKit.MmToFeet(request.Arguments.Value<double?>("bottom_mm") ?? defOff);
                            return new JObject { ["view_id"] = IdCompat.ToInt64(view.Id), ["offset_mm"] = defOff };
                        }
                        case "section_box_active":
                        {
                            if (view is not View3D v3d) throw new InvalidOperationException("Target view is not a 3D view.");
                            v3d.IsSectionBoxActive = request.Arguments.Value<bool?>("enabled") ?? true;
                            return new JObject { ["view_id"] = IdCompat.ToInt64(v3d.Id), ["section_box_active"] = v3d.IsSectionBoxActive };
                        }
                        case "set_section_box":
                        {
                            if (view is not View3D v3d) throw new InvalidOperationException("Target view is not a 3D view.");
                            var sb = BuildBoxFromArgs(request.Arguments, Transform.Identity);
                            v3d.IsSectionBoxActive = true;
                            v3d.SetSectionBox(sb);
                            return new JObject { ["view_id"] = IdCompat.ToInt64(v3d.Id), ["section_box_active"] = v3d.IsSectionBoxActive, ["section_box"] = ViewResolveKit.SerializeBoxMm(v3d.GetSectionBox()) };
                        }
                        case "set_section_box_from_elements":
                        {
                            if (view is not View3D v3d) throw new InvalidOperationException("Target view is not a 3D view.");
                            var elems = RevitElementKit.ResolveElements(d, request.Arguments["element_ids"] as JArray);
                            if (elems.Count == 0) throw new InvalidOperationException("No valid elements in 'element_ids'.");
                            double padMm = request.Arguments.Value<double?>("padding_mm") ?? 150.0;
                            var sb = ComputeWorldSectionBox(elems, padMm);
                            v3d.IsSectionBoxActive = true;
                            v3d.SetSectionBox(sb);
                            return new JObject
                            {
                                ["view_id"] = IdCompat.ToInt64(v3d.Id),
                                ["element_count"] = elems.Count,
                                ["padding_mm"] = padMm,
                                ["section_box_active"] = v3d.IsSectionBoxActive,
                                ["section_box"] = ViewResolveKit.SerializeBoxMm(v3d.GetSectionBox())
                            };
                        }
                        default:
                            throw new ArgumentException($"Unsupported operation '{op}'.");
                    }
                },
                (d, staged) =>
                {
                    if ((op == "set_section_box" || op == "set_section_box_from_elements") &&
                        d.GetElement(view.Id) is View3D verified3d)
                    {
                        staged["verified_section_box_active"] = verified3d.IsSectionBoxActive;
                        staged["verified_section_box"] = ViewResolveKit.SerializeBoxMm(verified3d.GetSectionBox());
                    }
                    return staged;
                },
                $"View crop operation '{op}' applied to '{view.Name}'.");
        }

        private static BoundingBoxXYZ ComputeWorldSectionBox(List<Element> elems, double paddingMm)
        {
            var boxes = new List<Box3DMm>();
            foreach (var el in elems)
            {
                var bb = el.get_BoundingBox(null);
                if (bb == null) continue;
                boxes.Add(new Box3DMm
                {
                    MinX = UnitKit.FeetToMm(bb.Min.X),
                    MinY = UnitKit.FeetToMm(bb.Min.Y),
                    MinZ = UnitKit.FeetToMm(bb.Min.Z),
                    MaxX = UnitKit.FeetToMm(bb.Max.X),
                    MaxY = UnitKit.FeetToMm(bb.Max.Y),
                    MaxZ = UnitKit.FeetToMm(bb.Max.Z)
                });
            }

            var padded = PlanimetryMathKit.ComputePaddedBox(boxes, paddingMm)
                ?? throw new InvalidOperationException("None of the target elements has a valid 3D bounding box.");

            return new BoundingBoxXYZ
            {
                Transform = Transform.Identity,
                Min = new XYZ(UnitKit.MmToFeet(padded.MinX), UnitKit.MmToFeet(padded.MinY), UnitKit.MmToFeet(padded.MinZ)),
                Max = new XYZ(UnitKit.MmToFeet(padded.MaxX), UnitKit.MmToFeet(padded.MaxY), UnitKit.MmToFeet(padded.MaxZ))
            };
        }

        private static BoundingBoxXYZ BuildBoxFromArgs(JObject args, Transform transform)
        {
            var minArr = args["min_mm"] as JArray;
            var maxArr = args["max_mm"] as JArray;
            if (minArr == null || minArr.Count < 3 || maxArr == null || maxArr.Count < 3)
                throw new InvalidOperationException("Both 'min_mm' and 'max_mm' ([x,y,z] in mm) are required.");

            return new BoundingBoxXYZ
            {
                Transform = transform,
                Min = new XYZ(UnitKit.MmToFeet(minArr[0].Value<double>()), UnitKit.MmToFeet(minArr[1].Value<double>()), UnitKit.MmToFeet(minArr[2].Value<double>())),
                Max = new XYZ(UnitKit.MmToFeet(maxArr[0].Value<double>()), UnitKit.MmToFeet(maxArr[1].Value<double>()), UnitKit.MmToFeet(maxArr[2].Value<double>()))
            };
        }

        private static BoundingBoxXYZ ComputeCombinedBox(List<Element> elems, View view, double padFt)
        {
            double minX = double.MaxValue, minY = double.MaxValue, minZ = double.MaxValue;
            double maxX = double.MinValue, maxY = double.MinValue, maxZ = double.MinValue;
            var inv = (view.CropBox?.Transform ?? Transform.Identity).Inverse;
            int valid = 0;

            foreach (var el in elems)
            {
                var bb = el.get_BoundingBox(view) ?? el.get_BoundingBox(null);
                if (bb == null) continue;
                var corners = new[]
                {
                    new XYZ(bb.Min.X, bb.Min.Y, bb.Min.Z),
                    new XYZ(bb.Max.X, bb.Min.Y, bb.Min.Z),
                    new XYZ(bb.Min.X, bb.Max.Y, bb.Min.Z),
                    new XYZ(bb.Max.X, bb.Max.Y, bb.Min.Z),
                    new XYZ(bb.Min.X, bb.Min.Y, bb.Max.Z),
                    new XYZ(bb.Max.X, bb.Min.Y, bb.Max.Z),
                    new XYZ(bb.Min.X, bb.Max.Y, bb.Max.Z),
                    new XYZ(bb.Max.X, bb.Max.Y, bb.Max.Z)
                };
                foreach (var c in corners)
                {
                    var lc = inv.OfPoint(c);
                    if (lc.X < minX) minX = lc.X;
                    if (lc.Y < minY) minY = lc.Y;
                    if (lc.Z < minZ) minZ = lc.Z;
                    if (lc.X > maxX) maxX = lc.X;
                    if (lc.Y > maxY) maxY = lc.Y;
                    if (lc.Z > maxZ) maxZ = lc.Z;
                }
                valid++;
            }

            if (valid == 0) throw new InvalidOperationException("None of the target elements has a valid bounding box.");
            return new BoundingBoxXYZ
            {
                Transform = view.CropBox?.Transform ?? Transform.Identity,
                Min = new XYZ(minX - padFt, minY - padFt, minZ - padFt),
                Max = new XYZ(maxX + padFt, maxY + padFt, maxZ + padFt)
            };
        }
    }

    [ScioTool("scio_view_visibility", "documentation", "Inspect or modify view visibility, element graphic overrides, view scale, override report, or copy view graphics.", ReadOnly = false)]
    public sealed class ViewVisibilityCommand : IRevitCommand
    {
        [ScioParam("operation", "Operation to execute.", Required = true, EnumValues = new[]
        {
            "read", "hide", "unhide", "set_overrides", "clear_overrides", "set_scale",
            "override_report", "copy_view_graphics", "datum_cleanup"
        })]
        public string Operation { get; set; } = "read";

        [ScioParam("view_id", "Optional target View ElementId (defaults to active view).")]
        public long? ViewId { get; set; }

        [ScioParam("source_view_id", "Source View ElementId for copy_view_graphics.")]
        public long? SourceViewId { get; set; }

        [ScioParam("element_ids", "ElementIds for hide, unhide, set_overrides, or clear_overrides.")]
        public List<long>? ElementIds { get; set; }

        [ScioParam("color_rgb", "RGB array [r, g, b] (0-255) for set_overrides.")]
        public List<int>? ColorRgb { get; set; }

        [ScioParam("transparency", "Surface transparency 0-100 for set_overrides.")]
        public int? Transparency { get; set; }

        [ScioParam("halftone", "Halftone flag for set_overrides.")]
        public bool? Halftone { get; set; }

        [ScioParam("scale", "Integer view scale denominator (e.g. 50, 100) for set_scale.")]
        public int? Scale { get; set; }

        [ScioParam("dry_run", "Preview without committing (default true).")]
        public bool DryRun { get; set; } = true;

        public ToolDescriptor Descriptor { get; } = JsonSchemaBuilder.DescribeTool(typeof(ViewVisibilityCommand));

        public ToolResponse Execute(RevitContext context, ToolRequest request)
        {
            var doc = context.RequireActiveDocument(request.TargetDocument);
            var view = ViewResolveKit.ResolveView(doc, request.Arguments["view_id"]);
            string op = (request.Arguments.Value<string>("operation") ?? "read").Trim().ToLowerInvariant();

            if (op == "read")
            {
                return ToolResponse.Success(request.RequestId, new JObject
                {
                    ["view_id"] = IdCompat.ToInt64(view.Id),
                    ["view_name"] = view.Name,
                    ["scale"] = view.Scale,
                    ["detail_level"] = view.DetailLevel.ToString(),
                    ["display_style"] = view.DisplayStyle.ToString(),
                    ["view_template_id"] = IdCompat.ToInt64(view.ViewTemplateId),
                    ["are_model_categories_hidden"] = view.AreModelCategoriesHidden,
                    ["are_annotation_categories_hidden"] = view.AreAnnotationCategoriesHidden
                }, $"Visibility summary read for '{view.Name}'.");
            }

            if (op == "override_report")
            {
                var overridden = new JArray();
                foreach (var el in new FilteredElementCollector(doc, view.Id).WhereElementIsNotElementType())
                {
                    var ogs = view.GetElementOverrides(el.Id);
                    bool hasAny = ogs.Halftone || ogs.Transparency > 0 ||
                                  ogs.ProjectionLineColor.IsValid || ogs.SurfaceForegroundPatternColor.IsValid;
                    if (!hasAny) continue;
                    overridden.Add(new JObject
                    {
                        ["id"] = IdCompat.ToInt64(el.Id),
                        ["category"] = el.Category?.Name ?? string.Empty,
                        ["halftone"] = ogs.Halftone,
                        ["transparency"] = ogs.Transparency,
                        ["has_line_color"] = ogs.ProjectionLineColor.IsValid,
                        ["has_fill_color"] = ogs.SurfaceForegroundPatternColor.IsValid
                    });
                    if (overridden.Count >= 500) break;
                }
                return ToolResponse.Success(request.RequestId, new JObject
                {
                    ["view_id"] = IdCompat.ToInt64(view.Id),
                    ["overridden_count"] = overridden.Count,
                    ["elements"] = overridden
                }, $"Found {overridden.Count} element(s) with graphic overrides in '{view.Name}'.");
            }

            bool dryRun = request.DryRun ?? request.Arguments.Value<bool?>("dry_run") ?? true;
            return TransactionPipeline.RunVerified(
                doc,
                request.RequestId,
                $"View Visibility ({op})",
                dryRun,
                d =>
                {
                    switch (op)
                    {
                        case "hide":
                        {
                            var ids = RevitElementKit.ResolveElements(d, request.Arguments["element_ids"] as JArray)
                                .Where(e => e.CanBeHidden(view))
                                .Select(e => e.Id).ToList();
                            if (ids.Count > 0) view.HideElements(ids);
                            return new JObject { ["hidden_count"] = ids.Count };
                        }
                        case "unhide":
                        {
                            var ids = RevitElementKit.ResolveElements(d, request.Arguments["element_ids"] as JArray)
                                .Select(e => e.Id).ToList();
                            if (ids.Count > 0) view.UnhideElements(ids);
                            return new JObject { ["unhidden_count"] = ids.Count };
                        }
                        case "set_overrides":
                        {
                            var elems = RevitElementKit.ResolveElements(d, request.Arguments["element_ids"] as JArray);
                            var ogs = new OverrideGraphicSettings();
                            var rgb = request.Arguments["color_rgb"] as JArray;
                            if (rgb != null && rgb.Count >= 3)
                            {
                                var col = new Color((byte)Math.Max(0, Math.Min(255, rgb[0].Value<int>())),
                                                    (byte)Math.Max(0, Math.Min(255, rgb[1].Value<int>())),
                                                    (byte)Math.Max(0, Math.Min(255, rgb[2].Value<int>())));
                                ogs.SetProjectionLineColor(col);
                                var solid = ViewResolveKit.FindSolidFillPattern(d);
                                if (solid != null)
                                {
                                    ogs.SetSurfaceForegroundPatternId(solid.Id);
                                    ogs.SetSurfaceForegroundPatternColor(col);
                                }
                            }
                            if (request.Arguments["transparency"] != null)
                                ogs.SetSurfaceTransparency(Math.Max(0, Math.Min(100, request.Arguments.Value<int>("transparency"))));
                            if (request.Arguments["halftone"] != null)
                                ogs.SetHalftone(request.Arguments.Value<bool>("halftone"));

                            foreach (var e in elems) view.SetElementOverrides(e.Id, ogs);
                            return new JObject { ["overridden_count"] = elems.Count };
                        }
                        case "clear_overrides":
                        {
                            var elems = request.Arguments["element_ids"] is JArray arr && arr.Count > 0
                                ? RevitElementKit.ResolveElements(d, arr)
                                : new FilteredElementCollector(d, view.Id).WhereElementIsNotElementType().ToList();
                            var empty = new OverrideGraphicSettings();
                            foreach (var e in elems) view.SetElementOverrides(e.Id, empty);
                            return new JObject { ["cleared_count"] = elems.Count };
                        }
                        case "set_scale":
                        {
                            int scale = Math.Max(1, request.Arguments.Value<int?>("scale") ?? 100);
                            view.Scale = scale;
                            return new JObject { ["view_id"] = IdCompat.ToInt64(view.Id), ["scale"] = view.Scale };
                        }
                        case "copy_view_graphics":
                        {
                            var src = ViewResolveKit.ResolveView(d, request.Arguments["source_view_id"]);
                            view.Scale = src.Scale;
                            view.DetailLevel = src.DetailLevel;
                            view.DisplayStyle = src.DisplayStyle;
                            int copied = 0;
                            foreach (var el in new FilteredElementCollector(d, src.Id).WhereElementIsNotElementType())
                            {
                                var ogs = src.GetElementOverrides(el.Id);
                                if (ogs.Halftone || ogs.Transparency > 0 || ogs.ProjectionLineColor.IsValid || ogs.SurfaceForegroundPatternColor.IsValid)
                                {
                                    view.SetElementOverrides(el.Id, ogs);
                                    copied++;
                                }
                            }
                            return new JObject { ["source_view"] = src.Name, ["target_view"] = view.Name, ["copied_overrides"] = copied };
                        }
                        case "datum_cleanup":
                        {
                            int grids = 0;
                            foreach (var g in new FilteredElementCollector(d, view.Id).OfClass(typeof(Grid)).Cast<Grid>())
                            {
                                g.SetDatumExtentType(DatumEnds.End0, view, DatumExtentType.ViewSpecific);
                                g.SetDatumExtentType(DatumEnds.End1, view, DatumExtentType.ViewSpecific);
                                grids++;
                            }
                            return new JObject { ["view_id"] = IdCompat.ToInt64(view.Id), ["grids_set_view_specific"] = grids };
                        }
                        default:
                            throw new ArgumentException($"Unsupported operation '{op}'.");
                    }
                },
                (d, staged) => staged,
                $"View visibility operation '{op}' completed on '{view.Name}'.");
        }
    }

    [ScioTool("scio_color_elements", "documentation", "Color-code elements in a view by parameter value, Category, or Type for visual QA, or clear colors.", ReadOnly = false)]
    public sealed class ColorElementsCommand : IRevitCommand
    {
        [ScioParam("action", "Operation: apply (default) or clear.", EnumValues = new[] { "apply", "clear" })]
        public string Action { get; set; } = "apply";

        [ScioParam("view_id", "Optional View ElementId (defaults to active view).")]
        public long? ViewId { get; set; }

        [ScioParam("parameter_name", "Parameter name to group by (or 'Category' / 'Type', default 'Category').")]
        public string ParameterName { get; set; } = "Category";

        [ScioParam("category", "Optional category name filter (e.g. 'Walls', 'Structural Columns').")]
        public string? Category { get; set; }

        [ScioParam("transparency", "Optional surface transparency 0-100 (default 0).")]
        public int Transparency { get; set; } = 0;

        [ScioParam("dry_run", "Preview legend without committing (default true).")]
        public bool DryRun { get; set; } = true;

        public ToolDescriptor Descriptor { get; } = JsonSchemaBuilder.DescribeTool(typeof(ColorElementsCommand));

        public ToolResponse Execute(RevitContext context, ToolRequest request)
        {
            var doc = context.RequireActiveDocument(request.TargetDocument);
            var view = ViewResolveKit.ResolveView(doc, request.Arguments["view_id"]);
            string action = (request.Arguments.Value<string>("action") ?? "apply").Trim().ToLowerInvariant();
            string paramName = (request.Arguments.Value<string>("parameter_name") ?? "Category").Trim();
            string? catFilter = request.Arguments.Value<string>("category")?.Trim();
            int transparency = Math.Max(0, Math.Min(100, request.Arguments.Value<int?>("transparency") ?? 0));
            bool dryRun = request.DryRun ?? request.Arguments.Value<bool?>("dry_run") ?? true;

            var elements = new FilteredElementCollector(doc, view.Id)
                .WhereElementIsNotElementType()
                .Where(e => e.Category != null && e.Category.CategoryType == CategoryType.Model)
                .Where(e => string.IsNullOrWhiteSpace(catFilter) ||
                            string.Equals(e.Category!.Name, catFilter, StringComparison.OrdinalIgnoreCase))
                .ToList();

            return TransactionPipeline.RunVerified(
                doc,
                request.RequestId,
                $"Color Elements ({action})",
                dryRun,
                d =>
                {
                    if (action == "clear")
                    {
                        var empty = new OverrideGraphicSettings();
                        foreach (var e in elements) view.SetElementOverrides(e.Id, empty);
                        return new JObject { ["action"] = "clear", ["cleared_count"] = elements.Count };
                    }

                    var solid = ViewResolveKit.FindSolidFillPattern(d);
                    var groups = elements
                        .GroupBy(e => ReadGroupKey(d, e, paramName), StringComparer.OrdinalIgnoreCase)
                        .OrderBy(g => g.Key, StringComparer.OrdinalIgnoreCase)
                        .ToList();

                    var legend = new JArray();
                    int idx = 0;
                    foreach (var g in groups)
                    {
                        byte[] rgb = PlanimetryMathKit.ColorForIndex(idx++);
                        var col = new Color(rgb[0], rgb[1], rgb[2]);
                        var ogs = new OverrideGraphicSettings();
                        ogs.SetProjectionLineColor(col);
                        if (solid != null)
                        {
                            ogs.SetSurfaceForegroundPatternId(solid.Id);
                            ogs.SetSurfaceForegroundPatternColor(col);
                        }
                        if (transparency > 0) ogs.SetSurfaceTransparency(transparency);

                        foreach (var el in g) view.SetElementOverrides(el.Id, ogs);

                        legend.Add(new JObject
                        {
                            ["value"] = g.Key,
                            ["rgb"] = new JArray(rgb[0], rgb[1], rgb[2]),
                            ["count"] = g.Count()
                        });
                    }

                    return new JObject
                    {
                        ["action"] = "apply",
                        ["view_name"] = view.Name,
                        ["parameter_name"] = paramName,
                        ["colored_elements"] = elements.Count,
                        ["distinct_groups"] = groups.Count,
                        ["legend"] = legend
                    };
                },
                (d, staged) => staged,
                $"Color-coded {elements.Count} element(s) in '{view.Name}' by '{paramName}'.");
        }

        private static string ReadGroupKey(Document doc, Element el, string paramName)
        {
            if (string.Equals(paramName, "Category", StringComparison.OrdinalIgnoreCase))
                return el.Category?.Name ?? "<No Category>";
            if (string.Equals(paramName, "Type", StringComparison.OrdinalIgnoreCase))
                return doc.GetElement(el.GetTypeId())?.Name ?? el.Name ?? "<No Type>";

            var p = el.LookupParameter(paramName) ?? doc.GetElement(el.GetTypeId())?.LookupParameter(paramName);
            if (p == null || !p.HasValue) return "<Unset>";
            string val = p.AsValueString() ?? p.AsString() ?? string.Empty;
            return string.IsNullOrWhiteSpace(val) ? "<Empty>" : val.Trim();
        }
    }
}
