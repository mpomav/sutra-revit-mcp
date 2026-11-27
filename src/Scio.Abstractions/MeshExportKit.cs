using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace Scio.Abstractions
{
    public sealed class MeshPrimitiveBucket
    {
        public byte R { get; set; } = 180;
        public byte G { get; set; } = 180;
        public byte B { get; set; } = 180;
        public double Opacity { get; set; } = 1.0;

        /// <summary>Flat XYZ vertex coordinates in millimeters (Revit Z-up).</summary>
        public List<float> PositionsMm { get; } = new List<float>();

        /// <summary>Triangle vertex indices (3 per triangle).</summary>
        public List<int> Indices { get; } = new List<int>();

        public int TriangleCount => Indices.Count / 3;
    }

    public sealed class MeshNodeData
    {
        public long ElementId { get; set; }
        public string UniqueId { get; set; } = string.Empty;
        public string Name { get; set; } = string.Empty;
        public string Category { get; set; } = string.Empty;
        public string Level { get; set; } = string.Empty;
        public Box3DMm? BboxMm { get; set; }
        public Dictionary<string, string> Parameters { get; } = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        public List<MeshPrimitiveBucket> Primitives { get; } = new List<MeshPrimitiveBucket>();

        public int TriangleCount => Primitives.Sum(p => p.TriangleCount);
    }

    public sealed class MeshOmittedItem
    {
        public long ElementId { get; set; }
        public string UniqueId { get; set; } = string.Empty;
        public string Category { get; set; } = string.Empty;
        public string Reason { get; set; } = "no_exported_geometry";
    }

    public sealed class MeshExportVerification
    {
        public bool Verified { get; set; }
        public string Format { get; set; } = string.Empty;
        public string Path { get; set; } = string.Empty;
        public long SizeBytes { get; set; }
        public string Sha256 { get; set; } = string.Empty;
        public int NodeCount { get; set; }
        public string? Error { get; set; }
    }

    /// <summary>
    /// Clean-room binary glTF 2.0 (.glb) and compact geometry_json writer and post-commit verifier.
    /// Zero external dependencies beyond Newtonsoft.Json.
    /// </summary>
    public static class MeshExportKit
    {
        private const uint GlbMagic = 0x46546C67; // "glTF"
        private const uint GlbVersion = 2;
        private const uint ChunkTypeJson = 0x4E4F534A; // "JSON"
        private const uint ChunkTypeBin = 0x004E4942; // "BIN\0"

        /// <summary>
        /// Writes a standard glTF 2.0 binary (.glb) file atomically.
        /// Converts Revit Z-up millimeters (x, y, z) to glTF right-handed Y-up meters (x/1000, z/1000, -y/1000).
        /// </summary>
        public static void WriteGlb(string outputPath, IReadOnlyList<MeshNodeData> nodes, string documentTitle)
        {
            if (string.IsNullOrWhiteSpace(outputPath))
                throw new ArgumentException("outputPath is required.", nameof(outputPath));

            var materialIndexByKey = new Dictionary<string, int>(StringComparer.Ordinal);
            var materials = new JArray();
            var bufferViews = new JArray();
            var accessors = new JArray();
            var meshes = new JArray();
            var gltfNodes = new JArray();
            var sceneNodeIndices = new JArray();

            using var binStream = new MemoryStream();
            using var binWriter = new BinaryWriter(binStream, Encoding.UTF8, leaveOpen: true);

            foreach (var node in nodes)
            {
                var validPrimitives = node.Primitives
                    .Where(p => p.PositionsMm.Count >= 9 && p.Indices.Count >= 3)
                    .ToList();
                if (validPrimitives.Count == 0) continue;

                var gltfPrimitives = new JArray();
                foreach (var prim in validPrimitives)
                {
                    int matIdx = GetOrAddMaterial(materialIndexByKey, materials, prim.R, prim.G, prim.B, prim.Opacity);

                    // 1. Write POSITION bufferView (VEC3 FLOAT, glTF Y-up meters)
                    PadStreamTo4(binStream, 0x00);
                    int posOffset = (int)binStream.Position;
                    int vertexCount = prim.PositionsMm.Count / 3;
                    float minX = float.MaxValue, minY = float.MaxValue, minZ = float.MaxValue;
                    float maxX = float.MinValue, maxY = float.MinValue, maxZ = float.MinValue;

                    for (int i = 0; i < vertexCount; i++)
                    {
                        float rx = prim.PositionsMm[i * 3];
                        float ry = prim.PositionsMm[i * 3 + 1];
                        float rz = prim.PositionsMm[i * 3 + 2];

                        // Right-handed Revit Z-up mm -> glTF Y-up meters
                        float gx = rx * 0.001f;
                        float gy = rz * 0.001f;
                        float gz = -ry * 0.001f;

                        if (gx < minX) minX = gx;
                        if (gy < minY) minY = gy;
                        if (gz < minZ) minZ = gz;
                        if (gx > maxX) maxX = gx;
                        if (gy > maxY) maxY = gy;
                        if (gz > maxZ) maxZ = gz;

                        binWriter.Write(gx);
                        binWriter.Write(gy);
                        binWriter.Write(gz);
                    }

                    int posByteLength = vertexCount * 12;
                    int posViewIdx = bufferViews.Count;
                    bufferViews.Add(new JObject
                    {
                        ["buffer"] = 0,
                        ["byteOffset"] = posOffset,
                        ["byteLength"] = posByteLength,
                        ["target"] = 34962 // ARRAY_BUFFER
                    });

                    int posAccessorIdx = accessors.Count;
                    accessors.Add(new JObject
                    {
                        ["bufferView"] = posViewIdx,
                        ["componentType"] = 5126, // FLOAT
                        ["count"] = vertexCount,
                        ["type"] = "VEC3",
                        ["min"] = new JArray(minX, minY, minZ),
                        ["max"] = new JArray(maxX, maxY, maxZ)
                    });

                    // 2. Write INDICES bufferView (SCALAR UNSIGNED_INT)
                    PadStreamTo4(binStream, 0x00);
                    int idxOffset = (int)binStream.Position;
                    for (int i = 0; i < prim.Indices.Count; i++)
                        binWriter.Write((uint)prim.Indices[i]);

                    int idxByteLength = prim.Indices.Count * 4;
                    int idxViewIdx = bufferViews.Count;
                    bufferViews.Add(new JObject
                    {
                        ["buffer"] = 0,
                        ["byteOffset"] = idxOffset,
                        ["byteLength"] = idxByteLength,
                        ["target"] = 34963 // ELEMENT_ARRAY_BUFFER
                    });

                    int idxAccessorIdx = accessors.Count;
                    accessors.Add(new JObject
                    {
                        ["bufferView"] = idxViewIdx,
                        ["componentType"] = 5125, // UNSIGNED_INT
                        ["count"] = prim.Indices.Count,
                        ["type"] = "SCALAR"
                    });

                    gltfPrimitives.Add(new JObject
                    {
                        ["attributes"] = new JObject { ["POSITION"] = posAccessorIdx },
                        ["indices"] = idxAccessorIdx,
                        ["material"] = matIdx,
                        ["mode"] = 4 // TRIANGLES
                    });
                }

                int meshIdx = meshes.Count;
                meshes.Add(new JObject
                {
                    ["name"] = string.IsNullOrWhiteSpace(node.Name) ? $"Element_{node.ElementId}" : node.Name,
                    ["primitives"] = gltfPrimitives
                });

                var extras = new JObject
                {
                    ["element_id"] = node.ElementId,
                    ["unique_id"] = node.UniqueId,
                    ["category"] = node.Category,
                    ["level"] = node.Level
                };
                if (node.BboxMm != null)
                {
                    extras["bbox_mm"] = new JObject
                    {
                        ["min"] = new JArray(node.BboxMm.MinX, node.BboxMm.MinY, node.BboxMm.MinZ),
                        ["max"] = new JArray(node.BboxMm.MaxX, node.BboxMm.MaxY, node.BboxMm.MaxZ)
                    };
                }
                if (node.Parameters.Count > 0)
                    extras["parameters"] = JObject.FromObject(node.Parameters);

                int nodeIdx = gltfNodes.Count;
                gltfNodes.Add(new JObject
                {
                    ["name"] = string.IsNullOrWhiteSpace(node.Name) ? $"Element_{node.ElementId}" : node.Name,
                    ["mesh"] = meshIdx,
                    ["extras"] = extras
                });
                sceneNodeIndices.Add(nodeIdx);
            }

            PadStreamTo4(binStream, 0x00);
            byte[] binBytes = binStream.ToArray();

            var root = new JObject
            {
                ["asset"] = new JObject
                {
                    ["version"] = "2.0",
                    ["generator"] = "Sutra Revit MCP (scio_export_mesh)",
                    ["extras"] = new JObject { ["document"] = documentTitle ?? string.Empty }
                },
                ["scene"] = 0,
                ["scenes"] = new JArray(new JObject { ["name"] = documentTitle ?? "RevitModel", ["nodes"] = sceneNodeIndices }),
                ["nodes"] = gltfNodes
            };

            if (meshes.Count > 0) root["meshes"] = meshes;
            if (materials.Count > 0) root["materials"] = materials;
            if (accessors.Count > 0) root["accessors"] = accessors;
            if (bufferViews.Count > 0) root["bufferViews"] = bufferViews;
            if (binBytes.Length > 0)
                root["buffers"] = new JArray(new JObject { ["byteLength"] = binBytes.Length });

            byte[] jsonRaw = Encoding.UTF8.GetBytes(root.ToString(Formatting.None));
            int jsonPad = (4 - (jsonRaw.Length % 4)) % 4;
            int jsonChunkLen = jsonRaw.Length + jsonPad;

            bool hasBin = binBytes.Length > 0;
            uint totalLength = (uint)(12 + 8 + jsonChunkLen + (hasBin ? 8 + binBytes.Length : 0));

            WriteAtomically(outputPath, fs =>
            {
                using var w = new BinaryWriter(fs, Encoding.UTF8, leaveOpen: true);
                // GLB header
                w.Write(GlbMagic);
                w.Write(GlbVersion);
                w.Write(totalLength);

                // JSON chunk
                w.Write((uint)jsonChunkLen);
                w.Write(ChunkTypeJson);
                w.Write(jsonRaw);
                for (int i = 0; i < jsonPad; i++) w.Write((byte)0x20); // space padding per glTF 2.0 spec

                // BIN chunk
                if (hasBin)
                {
                    w.Write((uint)binBytes.Length);
                    w.Write(ChunkTypeBin);
                    w.Write(binBytes);
                }
            });
        }

        /// <summary>
        /// Writes compact triangulated geometry JSON (schema scio.mesh/1) atomically.
        /// </summary>
        public static void WriteGeometryJson(
            string outputPath,
            IReadOnlyList<MeshNodeData> nodes,
            IReadOnlyList<MeshOmittedItem> omitted,
            string documentTitle,
            string units = "mm")
        {
            if (string.IsNullOrWhiteSpace(outputPath))
                throw new ArgumentException("outputPath is required.", nameof(outputPath));

            bool meters = string.Equals(units, "m", StringComparison.OrdinalIgnoreCase);
            double scale = meters ? 0.001 : 1.0;

            var nodeArr = new JArray();
            int totalTriangles = 0;

            foreach (var n in nodes)
            {
                var primArr = new JArray();
                foreach (var p in n.Primitives)
                {
                    if (p.PositionsMm.Count < 9 || p.Indices.Count < 3) continue;
                    var verts = new JArray();
                    for (int i = 0; i < p.PositionsMm.Count; i++)
                        verts.Add(Math.Round(p.PositionsMm[i] * scale, meters ? 4 : 1));

                    primArr.Add(new JObject
                    {
                        ["material_rgb"] = new JArray(p.R, p.G, p.B),
                        ["opacity"] = Math.Round(p.Opacity, 3),
                        ["vertices"] = verts,
                        ["indices"] = new JArray(p.Indices)
                    });
                    totalTriangles += p.TriangleCount;
                }

                if (primArr.Count == 0) continue;

                var nodeObj = new JObject
                {
                    ["element_id"] = n.ElementId,
                    ["unique_id"] = n.UniqueId,
                    ["name"] = n.Name,
                    ["category"] = n.Category,
                    ["level"] = n.Level,
                    ["primitives"] = primArr
                };
                if (n.BboxMm != null)
                {
                    nodeObj["bbox_mm"] = new JObject
                    {
                        ["min"] = new JArray(n.BboxMm.MinX, n.BboxMm.MinY, n.BboxMm.MinZ),
                        ["max"] = new JArray(n.BboxMm.MaxX, n.BboxMm.MaxY, n.BboxMm.MaxZ)
                    };
                }
                if (n.Parameters.Count > 0)
                    nodeObj["parameters"] = JObject.FromObject(n.Parameters);

                nodeArr.Add(nodeObj);
            }

            var omittedArr = new JArray(omitted.Select(o => new JObject
            {
                ["element_id"] = o.ElementId,
                ["unique_id"] = o.UniqueId,
                ["category"] = o.Category,
                ["reason"] = o.Reason
            }));

            var payload = new JObject
            {
                ["schema"] = "scio.mesh/1",
                ["document"] = documentTitle ?? string.Empty,
                ["units"] = meters ? "m" : "mm",
                ["coordinate_system"] = "revit_z_up",
                ["node_count"] = nodeArr.Count,
                ["triangle_count"] = totalTriangles,
                ["omitted_count"] = omittedArr.Count,
                ["nodes"] = nodeArr,
                ["omitted_elements"] = omittedArr
            };

            byte[] bytes = Encoding.UTF8.GetBytes(payload.ToString(Formatting.None));
            WriteAtomically(outputPath, fs => fs.Write(bytes, 0, bytes.Length));
        }

        /// <summary>
        /// Post-commit file verifier: checks existence, non-zero length, SHA-256, and format header/JSON validity.
        /// </summary>
        public static MeshExportVerification VerifyExportFile(string path, string format)
        {
            var res = new MeshExportVerification
            {
                Format = format ?? string.Empty,
                Path = path ?? string.Empty
            };

            if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
            {
                res.Error = "Exported file does not exist on disk.";
                return res;
            }

            var fi = new FileInfo(path);
            res.SizeBytes = fi.Length;
            if (fi.Length == 0)
            {
                res.Error = "Exported file is empty.";
                return res;
            }

            using (var sha = SHA256.Create())
            using (var fs = File.OpenRead(path))
                res.Sha256 = BitConverter.ToString(sha.ComputeHash(fs)).Replace("-", "").ToLowerInvariant();

            string fmt = (format ?? string.Empty).Trim().ToLowerInvariant();
            try
            {
                if (fmt == "glb")
                {
                    if (fi.Length < 20)
                    {
                        res.Error = "GLB file is shorter than minimum header size.";
                        return res;
                    }

                    using var fs = File.OpenRead(path);
                    using var r = new BinaryReader(fs, Encoding.UTF8, leaveOpen: true);
                    uint magic = r.ReadUInt32();
                    uint version = r.ReadUInt32();
                    uint totalLen = r.ReadUInt32();
                    uint jsonLen = r.ReadUInt32();
                    uint jsonType = r.ReadUInt32();

                    if (magic != GlbMagic || version != GlbVersion || jsonType != ChunkTypeJson)
                    {
                        res.Error = "Invalid GLB header or JSON chunk signature.";
                        return res;
                    }
                    if (totalLen != fi.Length)
                    {
                        res.Error = $"GLB declared length ({totalLen}) does not match file length ({fi.Length}).";
                        return res;
                    }

                    byte[] jsonBytes = r.ReadBytes((int)jsonLen);
                    var doc = JObject.Parse(Encoding.UTF8.GetString(jsonBytes));
                    if (!string.Equals(doc["asset"]?.Value<string>("version"), "2.0", StringComparison.Ordinal))
                    {
                        res.Error = "GLB asset.version is not 2.0.";
                        return res;
                    }

                    res.NodeCount = (doc["nodes"] as JArray)?.Count ?? 0;
                    res.Verified = true;
                    return res;
                }

                if (fmt == "geometry_json")
                {
                    var doc = JObject.Parse(File.ReadAllText(path, Encoding.UTF8));
                    if (!string.Equals(doc.Value<string>("schema"), "scio.mesh/1", StringComparison.Ordinal))
                    {
                        res.Error = "Unexpected geometry_json schema header.";
                        return res;
                    }
                    res.NodeCount = (doc["nodes"] as JArray)?.Count ?? 0;
                    res.Verified = true;
                    return res;
                }

                res.Error = $"Unsupported mesh format '{format}'.";
                return res;
            }
            catch (Exception ex)
            {
                res.Error = $"Verification parse error: {ex.Message}";
                return res;
            }
        }

        private static int GetOrAddMaterial(
            Dictionary<string, int> indexByKey,
            JArray materials,
            byte r,
            byte g,
            byte b,
            double opacity)
        {
            double clampedOpacity = Math.Max(0.0, Math.Min(1.0, opacity));
            string key = $"{r},{g},{b},{Math.Round(clampedOpacity, 2):F2}";
            if (indexByKey.TryGetValue(key, out int existing))
                return existing;

            int idx = materials.Count;
            var mat = new JObject
            {
                ["name"] = $"Mat_{r}_{g}_{b}",
                ["pbrMetallicRoughness"] = new JObject
                {
                    ["baseColorFactor"] = new JArray(
                        Math.Round(r / 255.0, 4),
                        Math.Round(g / 255.0, 4),
                        Math.Round(b / 255.0, 4),
                        Math.Round(clampedOpacity, 3)),
                    ["metallicFactor"] = 0.0,
                    ["roughnessFactor"] = 0.85
                }
            };
            if (clampedOpacity < 0.99)
                mat["alphaMode"] = "BLEND";

            materials.Add(mat);
            indexByKey[key] = idx;
            return idx;
        }

        private static void PadStreamTo4(MemoryStream stream, byte padByte)
        {
            long rem = stream.Length % 4;
            if (rem == 0) return;
            for (int i = 0; i < 4 - rem; i++)
                stream.WriteByte(padByte);
        }

        private static void WriteAtomically(string outputPath, Action<FileStream> writeAction)
        {
            string? dir = Path.GetDirectoryName(outputPath);
            if (!string.IsNullOrEmpty(dir))
                Directory.CreateDirectory(dir);

            string tmpPath = outputPath + ".tmp." + Guid.NewGuid().ToString("N");
            try
            {
                using (var fs = new FileStream(tmpPath, FileMode.Create, FileAccess.Write, FileShare.None))
                {
                    writeAction(fs);
                    fs.Flush(true);
                }

                if (File.Exists(outputPath))
                    File.Delete(outputPath);
                File.Move(tmpPath, outputPath);
            }
            catch
            {
                try { if (File.Exists(tmpPath)) File.Delete(tmpPath); } catch { }
                throw;
            }
        }
    }
}
