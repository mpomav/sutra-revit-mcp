using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.Colors;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;
using Newtonsoft.Json.Linq;
using Scio.Cad.Core;

namespace Scio.Cad.Commands
{
    /// <summary>
    /// Comprehensive in-process DWG cleanup &amp; standardization command:
    ///   1. Large block detection &amp; remediation (explode oversized/heavy blocks + purge unused)
    ///   2. Degenerate entity removal (zero-length lines/polylines, empty texts)
    ///   3. Flatten all ModelSpace geometry to Z = 0
    ///   4. PEDIT / Join connected lines, arcs, and open polylines per layer
    ///   5. Sort entities by standardized layers (or custom layer_map) + normalize ByLayer
    /// Follows Sutra's rehearse (dry_run=true) -> confirmation_token -> Transaction commit -> post-commit re-read.
    /// </summary>
    internal static class CadCleanupCommand
    {
        private static readonly Dictionary<string, (string Layer, short Aci)> DefaultLayerRules =
            new Dictionary<string, (string, short)>(StringComparer.OrdinalIgnoreCase)
            {
                ["TEXT"] = ("SUTRA-TEXTOS", 7),
                ["MTEXT"] = ("SUTRA-TEXTOS", 7),
                ["MLEADER"] = ("SUTRA-TEXTOS", 7),
                ["DIMENSION"] = ("SUTRA-COTAS", 2),
                ["HATCH"] = ("SUTRA-HATCH", 8),
                ["INSERT"] = ("SUTRA-BLOQUES", 3),
                ["POINT"] = ("SUTRA-PUNTOS", 6),
                ["LINE"] = ("SUTRA-GEOMETRIA", 4),
                ["LWPOLYLINE"] = ("SUTRA-GEOMETRIA", 4),
                ["POLYLINE"] = ("SUTRA-GEOMETRIA", 4),
                ["ARC"] = ("SUTRA-GEOMETRIA", 4),
                ["CIRCLE"] = ("SUTRA-GEOMETRIA", 4),
                ["ELLIPSE"] = ("SUTRA-GEOMETRIA", 4),
                ["SPLINE"] = ("SUTRA-GEOMETRIA", 4)
            };

        public sealed class CleanupOptions
        {
            public bool Flatten = true;
            public bool PeditJoin = true;
            public double JoinTolerance = 0.001;
            public bool SortByLayer = true;
            public bool NormalizeByLayer = true;
            public Dictionary<string, string> LayerMap = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            public bool FixLargeBlocks = true;
            public int MaxBlockEntities = 500;
            public double MaxBlockExtent = 10000.0;
            public string LargeBlockAction = "explode"; // "explode" | "report_only"
            public bool PurgeUnused = true;
            public bool RemoveDegenerate = true;
            public bool DryRun = true;
            public string ConfirmationToken;
            public string TargetDocument;

            public static CleanupOptions FromJson(JObject args)
            {
                var o = new CleanupOptions
                {
                    Flatten = (bool?)args["flatten"] ?? true,
                    PeditJoin = (bool?)args["pedit_join"] ?? true,
                    JoinTolerance = Math.Max(0.0, (double?)args["join_tolerance"] ?? 0.001),
                    SortByLayer = (bool?)args["sort_by_layer"] ?? true,
                    NormalizeByLayer = (bool?)args["normalize_bylayer"] ?? true,
                    FixLargeBlocks = (bool?)args["fix_large_blocks"] ?? true,
                    MaxBlockEntities = Math.Max(10, (int?)args["max_block_entities"] ?? 500),
                    MaxBlockExtent = Math.Max(1.0, (double?)args["max_block_extent"] ?? 10000.0),
                    LargeBlockAction = ((string)args["large_block_action"] ?? "explode").Trim().ToLowerInvariant(),
                    PurgeUnused = (bool?)args["purge_unused"] ?? true,
                    RemoveDegenerate = (bool?)args["remove_degenerate"] ?? true,
                    DryRun = (bool?)args["dry_run"] ?? true,
                    ConfirmationToken = (string)args["confirmation_token"],
                    TargetDocument = ((string)args["target_document"] ?? "").Trim()
                };

                if (args["layer_map"] is JObject mapObj)
                {
                    foreach (JProperty prop in mapObj.Properties())
                    {
                        string target = (string)prop.Value;
                        if (!string.IsNullOrWhiteSpace(prop.Name) && !string.IsNullOrWhiteSpace(target))
                            o.LayerMap[prop.Name.Trim()] = target.Trim();
                    }
                }
                return o;
            }
        }

        /// <summary>MCP entry point (enforces confirmation_token when dry_run=false).</summary>
        public static JObject Execute(JObject args)
        {
            CleanupOptions opts = CleanupOptions.FromJson(args ?? new JObject());
            return Run(opts, enforceToken: true, rawArgs: args ?? new JObject());
        }

        /// <summary>Direct UI entry point (user explicitly clicked Analyze or Apply inside AutoCAD).</summary>
        public static JObject RunFromUi(CleanupOptions opts)
        {
            return Run(opts, enforceToken: false, rawArgs: new JObject());
        }

        private static JObject Run(CleanupOptions opts, bool enforceToken, JObject rawArgs)
        {
            Document doc = Application.DocumentManager.MdiActiveDocument;
            if (doc == null)
                throw new InvalidOperationException("No active AutoCAD / Civil 3D drawing is open.");

            string activeTitle = Path.GetFileName(doc.Name);
            if (!opts.DryRun && !string.IsNullOrEmpty(opts.TargetDocument) &&
                !string.Equals(opts.TargetDocument, activeTitle, StringComparison.OrdinalIgnoreCase) &&
                !string.Equals(opts.TargetDocument, doc.Name, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException(
                    "Active drawing is '" + activeTitle + "', not requested target_document '" + opts.TargetDocument + "'. Nothing was changed.");
            }

            string fingerprint = CadConfirmationStore.ComputeFingerprint("cad_cleanup", doc.Name, rawArgs);

            if (opts.DryRun)
            {
                JObject audit = AuditDrawing(doc, opts);
                audit["dry_run"] = true;
                audit["verification_state"] = "rehearsed";
                audit["active_document"] = activeTitle;
                if (enforceToken) audit["confirmation_token"] = CadConfirmationStore.Issue(fingerprint);
                return audit;
            }

            if (enforceToken)
            {
                string err = CadConfirmationStore.Consume(opts.ConfirmationToken, fingerprint);
                if (err != null) throw new InvalidOperationException(err);
            }

            JObject applied = ApplyCleanup(doc, opts);
            applied["dry_run"] = false;
            applied["active_document"] = activeTitle;
            return applied;
        }

        private static JObject AuditDrawing(Document doc, CleanupOptions opts)
        {
            Database db = doc.Database;
            int totalEntities = 0;
            int nonZeroZ = 0;
            int joinCandidates = 0;
            int layerMoves = 0;
            int degenerateCount = 0;
            int largeBlockInstances = 0;
            int purgableBlocks = 0;
            int purgableLayers = 0;
            var largeBlocksArr = new JArray();

            using (doc.LockDocument())
            using (Transaction tr = db.TransactionManager.StartTransaction())
            {
                var bt = (BlockTable)tr.GetObject(db.BlockTableId, OpenMode.ForRead);
                var ms = (BlockTableRecord)tr.GetObject(bt[BlockTableRecord.ModelSpace], OpenMode.ForRead);

                // 1. Analyze block definitions for heavy/large blocks
                var blockEntityCounts = new Dictionary<ObjectId, int>();
                var blockNames = new Dictionary<ObjectId, string>();
                var candidatePurgeBlocks = new ObjectIdCollection();

                foreach (ObjectId bid in bt)
                {
                    var btr = (BlockTableRecord)tr.GetObject(bid, OpenMode.ForRead);
                    if (btr.IsLayout || btr.IsFromExternalReference) continue;
                    int cnt = 0;
                    foreach (ObjectId _ in btr) cnt++;
                    blockEntityCounts[bid] = cnt;
                    blockNames[bid] = btr.Name;
                    candidatePurgeBlocks.Add(bid);
                }

                if (opts.PurgeUnused && candidatePurgeBlocks.Count > 0)
                {
                    db.Purge(candidatePurgeBlocks);
                    purgableBlocks = candidatePurgeBlocks.Count;
                }

                if (opts.PurgeUnused)
                {
                    var lt = (LayerTable)tr.GetObject(db.LayerTableId, OpenMode.ForRead);
                    var candidateLayers = new ObjectIdCollection();
                    foreach (ObjectId lid in lt) candidateLayers.Add(lid);
                    db.Purge(candidateLayers);
                    purgableLayers = candidateLayers.Count;
                }

                // 2. Scan ModelSpace entities
                var largeBlockSeen = new Dictionary<string, (int Instances, int InternalEntities, double MaxExtent)>(StringComparer.OrdinalIgnoreCase);

                foreach (ObjectId eid in ms)
                {
                    Entity ent = tr.GetObject(eid, OpenMode.ForRead) as Entity;
                    if (ent == null || ent.IsErased) continue;
                    totalEntities++;

                    if (opts.RemoveDegenerate && IsDegenerate(ent)) degenerateCount++;
                    if (opts.Flatten && HasNonZeroZ(ent)) nonZeroZ++;
                    if (opts.PeditJoin && (ent is Line || ent is Arc || (ent is Polyline pl && !pl.Closed))) joinCandidates++;

                    if (opts.SortByLayer)
                    {
                        string targetLayer = ResolveTargetLayer(ent, opts);
                        if (!string.Equals(ent.Layer, targetLayer, StringComparison.OrdinalIgnoreCase))
                            layerMoves++;
                    }

                    if (opts.FixLargeBlocks && ent is BlockReference br)
                    {
                        ObjectId btrId = br.DynamicBlockTableRecord.IsValid ? br.DynamicBlockTableRecord : br.BlockTableRecord;
                        int internalCount = blockEntityCounts.TryGetValue(btrId, out int c) ? c : 0;
                        double extent = TryGetMaxExtent(br);
                        if (internalCount >= opts.MaxBlockEntities || extent >= opts.MaxBlockExtent)
                        {
                            largeBlockInstances++;
                            string bName = blockNames.TryGetValue(btrId, out string bn) ? bn : br.Name;
                            if (!largeBlockSeen.TryGetValue(bName, out var info))
                                info = (0, internalCount, 0.0);
                            largeBlockSeen[bName] = (info.Instances + 1, Math.Max(info.InternalEntities, internalCount), Math.Max(info.MaxExtent, extent));
                        }
                    }
                }

                foreach (var kv in largeBlockSeen.OrderByDescending(x => x.Value.InternalEntities).Take(30))
                {
                    largeBlocksArr.Add(new JObject
                    {
                        ["block_name"] = kv.Key,
                        ["instances"] = kv.Value.Instances,
                        ["internal_entities"] = kv.Value.InternalEntities,
                        ["max_extent"] = Math.Round(kv.Value.MaxExtent, 2)
                    });
                }

                tr.Commit();
            }

            return new JObject
            {
                ["total_modelspace_entities"] = totalEntities,
                ["flatten_candidates_z_nonzero"] = nonZeroZ,
                ["pedit_join_candidates"] = joinCandidates,
                ["layer_sort_moves_planned"] = layerMoves,
                ["degenerate_entities_found"] = degenerateCount,
                ["large_block_instances_found"] = largeBlockInstances,
                ["large_blocks"] = largeBlocksArr,
                ["purgable_blocks"] = purgableBlocks,
                ["purgable_layers"] = purgableLayers
            };
        }

        private static JObject ApplyCleanup(Document doc, CleanupOptions opts)
        {
            Database db = doc.Database;
            int explodedBlocks = 0;
            int explodedNewEntities = 0;
            int degenerateRemoved = 0;
            int flattenedCount = 0;
            int joinedPolylines = 0;
            int absorbedSegments = 0;
            int movedToLayer = 0;
            int normalizedByLayer = 0;
            int purgedBlocks = 0;
            int purgedLayers = 0;

            using (doc.LockDocument())
            {
                // Unlock any locked layers temporarily if we are mutating geometry/layers
                var relockIds = new List<ObjectId>();

                using (Transaction tr = db.TransactionManager.StartTransaction())
                {
                    var lt = (LayerTable)tr.GetObject(db.LayerTableId, OpenMode.ForRead);
                    foreach (ObjectId lid in lt)
                    {
                        var ltr = (LayerTableRecord)tr.GetObject(lid, OpenMode.ForRead);
                        if (ltr.IsLocked)
                        {
                            ltr.UpgradeOpen();
                            ltr.IsLocked = false;
                            relockIds.Add(lid);
                        }
                    }

                    var bt = (BlockTable)tr.GetObject(db.BlockTableId, OpenMode.ForRead);
                    var ms = (BlockTableRecord)tr.GetObject(bt[BlockTableRecord.ModelSpace], OpenMode.ForWrite);

                    // Pre-compute block internal entity counts
                    var blockEntityCounts = new Dictionary<ObjectId, int>();
                    foreach (ObjectId bid in bt)
                    {
                        var btr = (BlockTableRecord)tr.GetObject(bid, OpenMode.ForRead);
                        if (btr.IsLayout || btr.IsFromExternalReference) continue;
                        int cnt = 0;
                        foreach (ObjectId _ in btr) cnt++;
                        blockEntityCounts[bid] = cnt;
                    }

                    // PASS 1: Explode oversized/heavy blocks (if requested)
                    if (opts.FixLargeBlocks && opts.LargeBlockAction == "explode")
                    {
                        var toExplode = new List<ObjectId>();
                        foreach (ObjectId eid in ms)
                        {
                            if (!(tr.GetObject(eid, OpenMode.ForRead) is BlockReference br) || br.IsErased) continue;
                            ObjectId btrId = br.DynamicBlockTableRecord.IsValid ? br.DynamicBlockTableRecord : br.BlockTableRecord;
                            int internalCount = blockEntityCounts.TryGetValue(btrId, out int c) ? c : 0;
                            double extent = TryGetMaxExtent(br);
                            if (internalCount >= opts.MaxBlockEntities || extent >= opts.MaxBlockExtent)
                                toExplode.Add(eid);
                        }

                        foreach (ObjectId bid in toExplode)
                        {
                            var br = tr.GetObject(bid, OpenMode.ForWrite) as BlockReference;
                            if (br == null || br.IsErased) continue;
                            var exploded = new DBObjectCollection();
                            try
                            {
                                br.Explode(exploded);
                                foreach (DBObject obj in exploded)
                                {
                                    if (obj is Entity child)
                                    {
                                        ms.AppendEntity(child);
                                        tr.AddNewlyCreatedDBObject(child, true);
                                        explodedNewEntities++;
                                    }
                                    else obj.Dispose();
                                }
                                br.Erase(true);
                                explodedBlocks++;
                            }
                            catch
                            {
                                foreach (DBObject obj in exploded) try { obj.Dispose(); } catch { }
                            }
                        }
                    }

                    // PASS 2: Remove degenerate entities & Flatten to Z=0 & Sort by layer
                    var joinByLayer = new Dictionary<string, List<ObjectId>>(StringComparer.OrdinalIgnoreCase);

                    foreach (ObjectId eid in ms)
                    {
                        Entity ent = tr.GetObject(eid, OpenMode.ForRead) as Entity;
                        if (ent == null || ent.IsErased) continue;

                        if (opts.RemoveDegenerate && IsDegenerate(ent))
                        {
                            ent.UpgradeOpen();
                            ent.Erase(true);
                            degenerateRemoved++;
                            continue;
                        }

                        if (opts.Flatten && FlattenEntity(ent, tr))
                            flattenedCount++;

                        if (opts.SortByLayer)
                        {
                            string targetLayer = ResolveTargetLayer(ent, opts);
                            short aci = ResolveTargetAci(ent);
                            EnsureLayer(db, tr, targetLayer, aci);

                            if (!string.Equals(ent.Layer, targetLayer, StringComparison.OrdinalIgnoreCase))
                            {
                                if (!ent.IsWriteEnabled) ent.UpgradeOpen();
                                ent.Layer = targetLayer;
                                movedToLayer++;
                            }
                        }

                        if (opts.NormalizeByLayer)
                        {
                            bool needsColor = ent.ColorIndex != 256; // 256 = ByLayer
                            bool needsLt = !string.Equals(ent.Linetype, "ByLayer", StringComparison.OrdinalIgnoreCase);
                            if (needsColor || needsLt)
                            {
                                if (!ent.IsWriteEnabled) ent.UpgradeOpen();
                                if (needsColor) ent.ColorIndex = 256;
                                if (needsLt) try { ent.Linetype = "ByLayer"; } catch { }
                                normalizedByLayer++;
                            }
                        }

                        if (opts.PeditJoin && (ent is Line || ent is Arc || (ent is Polyline p && !p.Closed)))
                        {
                            string lKey = ent.Layer ?? "0";
                            if (!joinByLayer.TryGetValue(lKey, out List<ObjectId> list))
                                joinByLayer[lKey] = list = new List<ObjectId>();
                            list.Add(eid);
                        }
                    }

                    // PASS 3: PEDIT / Join connected Line/Arc/Polyline chains per layer
                    if (opts.PeditJoin)
                    {
                        foreach (var kv in joinByLayer)
                        {
                            if (kv.Value.Count < 2) continue;
                            JoinLayerCurves(ms, tr, kv.Key, kv.Value, opts.JoinTolerance, ref joinedPolylines, ref absorbedSegments);
                        }
                    }

                    // PASS 4: Purge unused block definitions and layers
                    if (opts.PurgeUnused)
                    {
                        var purgeBlocks = new ObjectIdCollection();
                        foreach (ObjectId bid in bt)
                        {
                            var btr = (BlockTableRecord)tr.GetObject(bid, OpenMode.ForRead);
                            if (!btr.IsLayout && !btr.IsFromExternalReference) purgeBlocks.Add(bid);
                        }
                        if (purgeBlocks.Count > 0)
                        {
                            db.Purge(purgeBlocks);
                            foreach (ObjectId pid in purgeBlocks)
                            {
                                try
                                {
                                    DBObject obj = tr.GetObject(pid, OpenMode.ForWrite);
                                    obj.Erase(true);
                                    purgedBlocks++;
                                }
                                catch { }
                            }
                        }

                        var purgeLayers = new ObjectIdCollection();
                        foreach (ObjectId lid in lt) purgeLayers.Add(lid);
                        if (purgeLayers.Count > 0)
                        {
                            db.Purge(purgeLayers);
                            foreach (ObjectId pid in purgeLayers)
                            {
                                try
                                {
                                    DBObject obj = tr.GetObject(pid, OpenMode.ForWrite);
                                    obj.Erase(true);
                                    purgedLayers++;
                                }
                                catch { }
                            }
                        }
                    }

                    // Restore previously locked layers that were not purged
                    foreach (ObjectId lid in relockIds)
                    {
                        if (!lid.IsErased)
                        {
                            try
                            {
                                var ltr = (LayerTableRecord)tr.GetObject(lid, OpenMode.ForWrite);
                                ltr.IsLocked = true;
                            }
                            catch { }
                        }
                    }

                    tr.Commit();
                }

                // Post-commit re-read verification
                int remainingNonZeroZ = 0;
                int finalCount = 0;
                using (Transaction verifyTr = db.TransactionManager.StartTransaction())
                {
                    var bt = (BlockTable)verifyTr.GetObject(db.BlockTableId, OpenMode.ForRead);
                    var ms = (BlockTableRecord)verifyTr.GetObject(bt[BlockTableRecord.ModelSpace], OpenMode.ForRead);
                    foreach (ObjectId eid in ms)
                    {
                        Entity ent = verifyTr.GetObject(eid, OpenMode.ForRead) as Entity;
                        if (ent == null || ent.IsErased) continue;
                        finalCount++;
                        if (opts.Flatten && HasNonZeroZ(ent)) remainingNonZeroZ++;
                    }
                    verifyTr.Commit();
                }

                return new JObject
                {
                    ["large_blocks_exploded"] = explodedBlocks,
                    ["entities_from_exploded_blocks"] = explodedNewEntities,
                    ["degenerate_entities_removed"] = degenerateRemoved,
                    ["entities_flattened"] = flattenedCount,
                    ["polylines_joined"] = joinedPolylines,
                    ["segments_absorbed_by_join"] = absorbedSegments,
                    ["entities_sorted_by_layer"] = movedToLayer,
                    ["entities_normalized_bylayer"] = normalizedByLayer,
                    ["blocks_purged"] = purgedBlocks,
                    ["layers_purged"] = purgedLayers,
                    ["final_modelspace_entities"] = finalCount,
                    ["remaining_nonzero_z"] = remainingNonZeroZ,
                    ["verification_state"] = "verified"
                };
            }
        }

        // ---- Geometry & Cleanup Helpers ---------------------------------------

        private static void JoinLayerCurves(BlockTableRecord ms, Transaction tr, string layer,
            List<ObjectId> ids, double tol, ref int joinedPolylines, ref int absorbedSegments)
        {
            var consumed = new HashSet<ObjectId>();
            var pool = new List<Entity>();
            foreach (ObjectId id in ids)
            {
                if (tr.GetObject(id, OpenMode.ForRead) is Entity e && !e.IsErased) pool.Add(e);
            }

            foreach (Entity seed in pool)
            {
                if (consumed.Contains(seed.ObjectId) || seed.IsErased) continue;

                // Build candidate array of remaining curves on the same layer
                var others = new List<Entity>();
                foreach (Entity cand in pool)
                {
                    if (cand.ObjectId == seed.ObjectId || consumed.Contains(cand.ObjectId) || cand.IsErased) continue;
                    others.Add(cand);
                }
                if (others.Count == 0) continue;

                Polyline host = null;
                bool createdNew = false;

                if (seed is Polyline existingPl)
                {
                    if (!existingPl.IsWriteEnabled) existingPl.UpgradeOpen();
                    host = existingPl;
                }
                else if (seed is Line ln)
                {
                    host = new Polyline();
                    host.AddVertexAt(0, new Point2d(ln.StartPoint.X, ln.StartPoint.Y), 0, 0, 0);
                    host.AddVertexAt(1, new Point2d(ln.EndPoint.X, ln.EndPoint.Y), 0, 0, 0);
                    host.Layer = layer;
                    host.ColorIndex = 256;
                    createdNew = true;
                }
                else if (seed is Arc arc)
                {
                    double bulge = Math.Tan(arc.TotalAngle / 4.0);
                    host = new Polyline();
                    host.AddVertexAt(0, new Point2d(arc.StartPoint.X, arc.StartPoint.Y), bulge, 0, 0);
                    host.AddVertexAt(1, new Point2d(arc.EndPoint.X, arc.EndPoint.Y), 0, 0, 0);
                    host.Layer = layer;
                    host.ColorIndex = 256;
                    createdNew = true;
                }

                if (host == null) continue;

                try
                {
                    Entity[] joinInput = others.ToArray();
                    IntegerCollection joinedIndices = host.JoinEntities(joinInput);
                    if (joinedIndices != null && joinedIndices.Count > 0)
                    {
                        if (createdNew)
                        {
                            ms.AppendEntity(host);
                            tr.AddNewlyCreatedDBObject(host, true);
                            if (!seed.IsWriteEnabled) seed.UpgradeOpen();
                            seed.Erase(true);
                            consumed.Add(seed.ObjectId);
                        }
                        else
                        {
                            consumed.Add(seed.ObjectId);
                        }

                        foreach (int idx in joinedIndices)
                        {
                            if (idx >= 0 && idx < joinInput.Length)
                            {
                                Entity absorbed = joinInput[idx];
                                if (!absorbed.IsErased)
                                {
                                    if (!absorbed.IsWriteEnabled) absorbed.UpgradeOpen();
                                    absorbed.Erase(true);
                                }
                                consumed.Add(absorbed.ObjectId);
                                absorbedSegments++;
                            }
                        }

                        // Close polyline if start and end points meet within tolerance
                        if (!host.Closed && host.NumberOfVertices >= 3 &&
                            host.StartPoint.DistanceTo(host.EndPoint) <= Math.Max(tol, 1e-6))
                        {
                            host.Closed = true;
                        }

                        joinedPolylines++;
                    }
                    else if (createdNew)
                    {
                        host.Dispose();
                    }
                }
                catch
                {
                    if (createdNew) try { host.Dispose(); } catch { }
                }
            }
        }

        private static bool IsDegenerate(Entity ent)
        {
            if (ent is Line ln) return ln.Length < 1e-6;
            if (ent is Polyline pl) return pl.NumberOfVertices < 2 || pl.Length < 1e-6;
            if (ent is DBText dt) return string.IsNullOrWhiteSpace(dt.TextString);
            if (ent is MText mt) return string.IsNullOrWhiteSpace(mt.Text);
            return false;
        }

        private static bool HasNonZeroZ(Entity ent)
        {
            const double eps = 1e-6;
            if (ent is Line ln) return Math.Abs(ln.StartPoint.Z) > eps || Math.Abs(ln.EndPoint.Z) > eps;
            if (ent is Polyline pl) return Math.Abs(pl.Elevation) > eps;
            if (ent is Circle c) return Math.Abs(c.Center.Z) > eps;
            if (ent is Arc a) return Math.Abs(a.Center.Z) > eps;
            if (ent is Ellipse e) return Math.Abs(e.Center.Z) > eps;
            if (ent is DBText dt) return Math.Abs(dt.Position.Z) > eps;
            if (ent is MText mt) return Math.Abs(mt.Location.Z) > eps;
            if (ent is BlockReference br) return Math.Abs(br.Position.Z) > eps;
            if (ent is DBPoint pt) return Math.Abs(pt.Position.Z) > eps;
            if (ent is Hatch h) return Math.Abs(h.Elevation) > eps;
            return false;
        }

        private static bool FlattenEntity(Entity ent, Transaction tr)
        {
            const double eps = 1e-6;
            try
            {
                if (ent is Line ln && (Math.Abs(ln.StartPoint.Z) > eps || Math.Abs(ln.EndPoint.Z) > eps))
                {
                    if (!ln.IsWriteEnabled) ln.UpgradeOpen();
                    ln.StartPoint = new Point3d(ln.StartPoint.X, ln.StartPoint.Y, 0);
                    ln.EndPoint = new Point3d(ln.EndPoint.X, ln.EndPoint.Y, 0);
                    return true;
                }
                if (ent is Polyline pl && Math.Abs(pl.Elevation) > eps)
                {
                    if (!pl.IsWriteEnabled) pl.UpgradeOpen();
                    pl.Elevation = 0;
                    return true;
                }
                if (ent is Polyline3d pl3)
                {
                    bool changed = false;
                    foreach (ObjectId vid in pl3)
                    {
                        var v = tr.GetObject(vid, OpenMode.ForRead) as PolylineVertex3d;
                        if (v != null && Math.Abs(v.Position.Z) > eps)
                        {
                            v.UpgradeOpen();
                            v.Position = new Point3d(v.Position.X, v.Position.Y, 0);
                            changed = true;
                        }
                    }
                    return changed;
                }
                if (ent is Circle c && Math.Abs(c.Center.Z) > eps)
                {
                    if (!c.IsWriteEnabled) c.UpgradeOpen();
                    c.Center = new Point3d(c.Center.X, c.Center.Y, 0);
                    return true;
                }
                if (ent is Arc a && Math.Abs(a.Center.Z) > eps)
                {
                    if (!a.IsWriteEnabled) a.UpgradeOpen();
                    a.Center = new Point3d(a.Center.X, a.Center.Y, 0);
                    return true;
                }
                if (ent is DBText dt && Math.Abs(dt.Position.Z) > eps)
                {
                    if (!dt.IsWriteEnabled) dt.UpgradeOpen();
                    dt.Position = new Point3d(dt.Position.X, dt.Position.Y, 0);
                    if (Math.Abs(dt.AlignmentPoint.Z) > eps)
                        dt.AlignmentPoint = new Point3d(dt.AlignmentPoint.X, dt.AlignmentPoint.Y, 0);
                    return true;
                }
                if (ent is MText mt && Math.Abs(mt.Location.Z) > eps)
                {
                    if (!mt.IsWriteEnabled) mt.UpgradeOpen();
                    mt.Location = new Point3d(mt.Location.X, mt.Location.Y, 0);
                    return true;
                }
                if (ent is BlockReference br && Math.Abs(br.Position.Z) > eps)
                {
                    if (!br.IsWriteEnabled) br.UpgradeOpen();
                    br.Position = new Point3d(br.Position.X, br.Position.Y, 0);
                    return true;
                }
                if (ent is DBPoint pt && Math.Abs(pt.Position.Z) > eps)
                {
                    if (!pt.IsWriteEnabled) pt.UpgradeOpen();
                    pt.Position = new Point3d(pt.Position.X, pt.Position.Y, 0);
                    return true;
                }
                if (ent is Hatch h && Math.Abs(h.Elevation) > eps)
                {
                    if (!h.IsWriteEnabled) h.UpgradeOpen();
                    h.Elevation = 0;
                    return true;
                }
            }
            catch { }
            return false;
        }

        private static double TryGetMaxExtent(Entity ent)
        {
            try
            {
                Extents3d ext = ent.GeometricExtents;
                double dx = ext.MaxPoint.X - ext.MinPoint.X;
                double dy = ext.MaxPoint.Y - ext.MinPoint.Y;
                double dz = ext.MaxPoint.Z - ext.MinPoint.Z;
                return Math.Max(dx, Math.Max(dy, dz));
            }
            catch { return 0.0; }
        }

        private static string ResolveTargetLayer(Entity ent, CleanupOptions opts)
        {
            string dxf = ent.GetRXClass().DxfName;
            if (opts.LayerMap.TryGetValue(dxf, out string custom)) return custom;
            if (DefaultLayerRules.TryGetValue(dxf, out var rule)) return rule.Layer;
            return "SUTRA-OTROS";
        }

        private static short ResolveTargetAci(Entity ent)
        {
            string dxf = ent.GetRXClass().DxfName;
            return DefaultLayerRules.TryGetValue(dxf, out var rule) ? rule.Aci : (short)7;
        }

        private static void EnsureLayer(Database db, Transaction tr, string name, short aci)
        {
            var lt = (LayerTable)tr.GetObject(db.LayerTableId, OpenMode.ForRead);
            if (lt.Has(name)) return;
            lt.UpgradeOpen();
            var ltr = new LayerTableRecord
            {
                Name = name,
                Color = Color.FromColorIndex(ColorMethod.ByAci, aci)
            };
            lt.Add(ltr);
            tr.AddNewlyCreatedDBObject(ltr, true);
        }
    }
}
