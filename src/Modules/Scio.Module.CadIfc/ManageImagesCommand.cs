using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Autodesk.Revit.DB;
using Newtonsoft.Json.Linq;
using Scio.Abstractions;
using Scio.Revit.Sdk;

namespace Scio.Module.CadIfc
{
    [ScioTool(
        "scio_manage_images",
        "cad_ifc",
        "Manage raster images (PNG, JPG, BMP, TIFF) in Revit views: list, import, transform, reload, or delete with dry_run=true default and post-commit verification.",
        ReadOnly = false)]
    public sealed class ManageImagesCommand : IRevitCommand
    {
        [ScioParam("operation", "Operation to execute.", Required = true, EnumValues = new[] { "list", "import", "transform", "reload", "delete" })]
        public string Operation { get; set; } = "list";

        [ScioParam("image_path", "Absolute path to PNG/JPG/BMP/TIFF file for operation=import.")]
        public string? ImagePath { get; set; }

        [ScioParam("view_id", "Target 2D View ElementId for import (defaults to active view).")]
        public long? ViewId { get; set; }

        [ScioParam("origin_mm", "Placement point [x, y] in mm for operation=import (default [0, 0]).")]
        public List<double>? OriginMm { get; set; }

        [ScioParam("delta_mm", "Translation [dx, dy] in mm for operation=transform.")]
        public List<double>? DeltaMm { get; set; }

        [ScioParam("width_mm", "Target image width in mm (preserves aspect ratio) for import or transform.")]
        public double? WidthMm { get; set; }

        [ScioParam("draw_layer", "Draw layer: background (default) or foreground.", EnumValues = new[] { "background", "foreground" })]
        public string? DrawLayer { get; set; }

        [ScioParam("image_instance_id", "Target ImageInstance ElementId for transform or delete.")]
        public long? ImageInstanceId { get; set; }

        [ScioParam("image_type_id", "Target ImageType ElementId for reload or delete.")]
        public long? ImageTypeId { get; set; }

        [ScioParam("dry_run", "Preview without committing (default true).")]
        public bool DryRun { get; set; } = true;

        public ToolDescriptor Descriptor { get; } = JsonSchemaBuilder.DescribeTool(typeof(ManageImagesCommand));

        public ToolResponse Execute(RevitContext context, ToolRequest request)
        {
            var doc = context.RequireActiveDocument(request.TargetDocument);
            var args = request.Arguments ?? new JObject();
            string op = (args.Value<string>("operation") ?? "list").Trim().ToLowerInvariant();

            if (op == "list")
                return ListImages(doc, request.RequestId);

            bool dryRun = request.DryRun ?? args.Value<bool?>("dry_run") ?? true;

            switch (op)
            {
                case "import":
                {
                    string path = (args.Value<string>("image_path") ?? string.Empty).Trim();
                    if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
                        return ToolResponse.Refusal(request.RequestId, "file_not_found", $"Image file not found: '{path}'.");

                    var view = ResolveTargetView(doc, args["view_id"]);
                    if (view == null)
                        return ToolResponse.Refusal(request.RequestId, "invalid_view", "Provide a valid 2D view_id or activate a non-template view.");

                    var originArr = args["origin_mm"] as JArray;
                    double ox = originArr != null && originArr.Count >= 2 ? originArr[0].Value<double>() : 0.0;
                    double oy = originArr != null && originArr.Count >= 2 ? originArr[1].Value<double>() : 0.0;
                    double? widthMm = args.Value<double?>("width_mm");
                    string layer = (args.Value<string>("draw_layer") ?? "background").Trim().ToLowerInvariant();

                    return TransactionPipeline.RunVerified(
                        doc,
                        request.RequestId,
                        "Import Raster Image",
                        dryRun,
                        d =>
                        {
                            var typeOpts = new ImageTypeOptions(path, false, ImageTypeSource.Import);
                            var imgType = ImageType.Create(d, typeOpts);
                            var placeOpts = new ImagePlacementOptions(
                                new XYZ(UnitKit.MmToFeet(ox), UnitKit.MmToFeet(oy), 0.0),
                                BoxPlacement.BottomLeft);
                            var inst = ImageInstance.Create(d, view, imgType.Id, placeOpts);

                            if (widthMm.HasValue && widthMm.Value > 0)
                            {
                                inst.LockProportions = true;
                                inst.Width = UnitKit.MmToFeet(widthMm.Value);
                            }

                            inst.DrawLayer = layer == "foreground" ? Autodesk.Revit.DB.DrawLayer.Foreground : Autodesk.Revit.DB.DrawLayer.Background;

                            return SerializeInstance(inst, imgType);
                        },
                        (d, staged) =>
                        {
                            var reRead = RevitElementKit.ResolveElement(d, staged["image_instance_id"]) as ImageInstance;
                            staged["verified"] = reRead != null;
                            if (reRead != null)
                            {
                                staged["verified_width_mm"] = Math.Round(UnitKit.FeetToMm(reRead.Width), 1);
                                staged["verified_height_mm"] = Math.Round(UnitKit.FeetToMm(reRead.Height), 1);
                                staged["verified_draw_layer"] = reRead.DrawLayer.ToString().ToLowerInvariant();
                            }
                            return staged;
                        },
                        $"Imported image '{Path.GetFileName(path)}' into view '{view.Name}'.");
                }

                case "transform":
                {
                    var inst = RevitElementKit.ResolveElement(doc, args["image_instance_id"]) as ImageInstance;
                    if (inst == null)
                        return ToolResponse.Refusal(request.RequestId, "not_found", "Provide a valid 'image_instance_id'.");

                    var deltaArr = args["delta_mm"] as JArray;
                    double dx = deltaArr != null && deltaArr.Count >= 2 ? deltaArr[0].Value<double>() : 0.0;
                    double dy = deltaArr != null && deltaArr.Count >= 2 ? deltaArr[1].Value<double>() : 0.0;
                    double? widthMm = args.Value<double?>("width_mm");
                    string? layer = args.Value<string>("draw_layer")?.Trim().ToLowerInvariant();

                    return TransactionPipeline.RunVerified(
                        doc,
                        request.RequestId,
                        "Transform Raster Image",
                        dryRun,
                        d =>
                        {
                            var target = (ImageInstance)d.GetElement(inst.Id);
                            if (Math.Abs(dx) > 1e-6 || Math.Abs(dy) > 1e-6)
                                ElementTransformUtils.MoveElement(d, target.Id, new XYZ(UnitKit.MmToFeet(dx), UnitKit.MmToFeet(dy), 0.0));

                            if (widthMm.HasValue && widthMm.Value > 0)
                            {
                                target.LockProportions = true;
                                target.Width = UnitKit.MmToFeet(widthMm.Value);
                            }

                            if (!string.IsNullOrWhiteSpace(layer))
                                target.DrawLayer = layer == "foreground" ? Autodesk.Revit.DB.DrawLayer.Foreground : Autodesk.Revit.DB.DrawLayer.Background;

                            var imgType = d.GetElement(target.GetTypeId()) as ImageType;
                            return SerializeInstance(target, imgType);
                        },
                        (d, staged) =>
                        {
                            var reRead = RevitElementKit.ResolveElement(d, staged["image_instance_id"]) as ImageInstance;
                            staged["verified"] = reRead != null;
                            if (reRead != null)
                            {
                                staged["verified_width_mm"] = Math.Round(UnitKit.FeetToMm(reRead.Width), 1);
                                staged["verified_height_mm"] = Math.Round(UnitKit.FeetToMm(reRead.Height), 1);
                            }
                            return staged;
                        },
                        $"Transformed image instance {IdCompat.ToInt64(inst.Id)}.");
                }

                case "reload":
                {
                    var imgType = RevitElementKit.ResolveElement(doc, args["image_type_id"]) as ImageType;
                    if (imgType == null)
                        return ToolResponse.Refusal(request.RequestId, "not_found", "Provide a valid 'image_type_id'.");

                    return TransactionPipeline.RunVerified(
                        doc,
                        request.RequestId,
                        "Reload Raster ImageType",
                        dryRun,
                        d =>
                        {
                            var target = (ImageType)d.GetElement(imgType.Id);
                            target.Reload();
                            return new JObject
                            {
                                ["image_type_id"] = IdCompat.ToInt64(target.Id),
                                ["name"] = target.Name,
                                ["path"] = target.Path ?? string.Empty,
                                ["status"] = target.Status.ToString()
                            };
                        },
                        (d, staged) => staged,
                        $"Reloaded image type '{imgType.Name}'.");
                }

                case "delete":
                {
                    var target = RevitElementKit.ResolveElement(doc, args["image_instance_id"])
                        ?? RevitElementKit.ResolveElement(doc, args["image_type_id"]);
                    if (target == null || (target is not ImageInstance && target is not ImageType))
                        return ToolResponse.Refusal(request.RequestId, "not_found", "Provide a valid 'image_instance_id' or 'image_type_id'.");

                    long targetId = IdCompat.ToInt64(target.Id);
                    return TransactionPipeline.RunVerified(
                        doc,
                        request.RequestId,
                        "Delete Raster Image",
                        dryRun,
                        d =>
                        {
                            var deleted = d.Delete(target.Id);
                            return new JObject
                            {
                                ["target_id"] = targetId,
                                ["deleted_count"] = deleted.Count
                            };
                        },
                        (d, staged) =>
                        {
                            staged["verified_deleted"] = RevitElementKit.ResolveElement(d, staged["target_id"]) == null;
                            return staged;
                        },
                        $"Deleted raster image element {targetId}.");
                }

                default:
                    return ToolResponse.Refusal(request.RequestId, "invalid_operation", $"Unsupported operation '{op}'.");
            }
        }

        private static ToolResponse ListImages(Document doc, string requestId)
        {
            var types = new FilteredElementCollector(doc).OfClass(typeof(ImageType)).Cast<ImageType>()
                .Select(t => new JObject
                {
                    ["image_type_id"] = IdCompat.ToInt64(t.Id),
                    ["name"] = t.Name,
                    ["path"] = t.Path ?? string.Empty,
                    ["width_px"] = t.WidthInPixels,
                    ["height_px"] = t.HeightInPixels,
                    ["status"] = t.Status.ToString()
                }).ToList();

            var instances = new FilteredElementCollector(doc).OfClass(typeof(ImageInstance)).Cast<ImageInstance>()
                .Select(i => SerializeInstance(i, doc.GetElement(i.GetTypeId()) as ImageType))
                .ToList();

            return ToolResponse.Success(requestId, new JObject
            {
                ["type_count"] = types.Count,
                ["instance_count"] = instances.Count,
                ["types"] = new JArray(types),
                ["instances"] = new JArray(instances)
            }, $"Found {types.Count} ImageType(s) and {instances.Count} ImageInstance(s).");
        }

        private static View? ResolveTargetView(Document doc, JToken? viewIdToken)
        {
            if (viewIdToken != null && viewIdToken.Type != JTokenType.Null)
            {
                var v = RevitElementKit.ResolveElement(doc, viewIdToken) as View;
                if (v != null && !v.IsTemplate) return v;
            }
            return doc.ActiveView != null && !doc.ActiveView.IsTemplate ? doc.ActiveView : null;
        }

        private static JObject SerializeInstance(ImageInstance inst, ImageType? imgType)
        {
            var loc = inst.GetLocation(BoxPlacement.BottomLeft);
            return new JObject
            {
                ["image_instance_id"] = IdCompat.ToInt64(inst.Id),
                ["image_type_id"] = IdCompat.ToInt64(inst.GetTypeId()),
                ["name"] = imgType?.Name ?? inst.Name ?? string.Empty,
                ["path"] = imgType?.Path ?? string.Empty,
                ["view_id"] = IdCompat.ToInt64(inst.OwnerViewId),
                ["location_mm"] = new JArray(Math.Round(UnitKit.FeetToMm(loc.X), 1), Math.Round(UnitKit.FeetToMm(loc.Y), 1)),
                ["width_mm"] = Math.Round(UnitKit.FeetToMm(inst.Width), 1),
                ["height_mm"] = Math.Round(UnitKit.FeetToMm(inst.Height), 1),
                ["draw_layer"] = inst.DrawLayer.ToString().ToLowerInvariant()
            };
        }
    }
}
