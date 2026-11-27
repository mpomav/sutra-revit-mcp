using System;
using System.Collections.Generic;
using System.IO;
using Newtonsoft.Json.Linq;
using Scio.Abstractions;
using Xunit;

namespace Scio.Core.Tests
{
    public sealed class MeshExportKitTests
    {
        private static MeshNodeData CreateSampleBoxNode()
        {
            var node = new MeshNodeData
            {
                ElementId = 4201,
                UniqueId = "abc-123-unique",
                Name = "Wall_200mm",
                Category = "Walls",
                Level = "Level 1",
                BboxMm = new Box3DMm { MinX = 0, MinY = 0, MinZ = 0, MaxX = 1000, MaxY = 200, MaxZ = 3000 }
            };
            node.Parameters["Mark"] = "W-01";

            var prim = new MeshPrimitiveBucket { R = 120, G = 160, B = 200, Opacity = 1.0 };
            prim.PositionsMm.AddRange(new float[]
            {
                0f, 0f, 0f,
                1000f, 0f, 0f,
                1000f, 0f, 3000f,
                0f, 0f, 3000f
            });
            prim.Indices.AddRange(new[] { 0, 1, 2, 0, 2, 3 });
            node.Primitives.Add(prim);
            return node;
        }

        [Fact]
        public void WriteGlb_And_VerifyExportFile_ProducesValidGltf2Binary()
        {
            string tmp = Path.Combine(Path.GetTempPath(), $"sutra_mesh_test_{Guid.NewGuid():N}.glb");
            try
            {
                MeshExportKit.WriteGlb(tmp, new List<MeshNodeData> { CreateSampleBoxNode() }, "SampleProject");
                var ver = MeshExportKit.VerifyExportFile(tmp, "glb");

                Assert.True(ver.Verified, ver.Error);
                Assert.Equal(1, ver.NodeCount);
                Assert.True(ver.SizeBytes > 100);
                Assert.Equal(64, ver.Sha256.Length);
            }
            finally
            {
                if (File.Exists(tmp)) File.Delete(tmp);
            }
        }

        [Fact]
        public void WriteGeometryJson_And_VerifyExportFile_ProducesValidScioMeshSchema()
        {
            string tmp = Path.Combine(Path.GetTempPath(), $"sutra_mesh_test_{Guid.NewGuid():N}.json");
            try
            {
                var omitted = new List<MeshOmittedItem>
                {
                    new MeshOmittedItem { ElementId = 9999, UniqueId = "u-9999", Category = "Doors", Reason = "no_exported_geometry" }
                };

                MeshExportKit.WriteGeometryJson(tmp, new List<MeshNodeData> { CreateSampleBoxNode() }, omitted, "SampleProject", "mm");
                var ver = MeshExportKit.VerifyExportFile(tmp, "geometry_json");

                Assert.True(ver.Verified, ver.Error);
                Assert.Equal(1, ver.NodeCount);
                Assert.Equal(64, ver.Sha256.Length);

                var doc = JObject.Parse(File.ReadAllText(tmp));
                Assert.Equal("scio.mesh/1", doc.Value<string>("schema"));
                Assert.Equal(2, doc.Value<int>("triangle_count"));
                Assert.Equal(1, doc.Value<int>("omitted_count"));
            }
            finally
            {
                if (File.Exists(tmp)) File.Delete(tmp);
            }
        }
    }
}
