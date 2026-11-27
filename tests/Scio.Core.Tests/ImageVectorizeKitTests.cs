using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using Scio.Abstractions;
using Scio.Server;
using Xunit;

namespace Scio.Core.Tests
{
    public sealed class ImageVectorizeKitTests
    {
        [Fact]
        public void ResolveMmPerPixel_RefusesWhenCalibrationMissing()
        {
            var opt = new ImageVectorizeOptions();
            Assert.Throws<InvalidOperationException>(() => ImageVectorizeKit.ResolveMmPerPixel(opt));
        }

        [Fact]
        public void ResolveMmPerPixel_ComputesFromTwoCalibrationPoints()
        {
            var opt = new ImageVectorizeOptions
            {
                CalibrationPoint1Px = new[] { 10.0, 20.0 },
                CalibrationPoint2Px = new[] { 110.0, 20.0 },
                KnownDistanceMm = 5000.0
            };

            Assert.Equal(50.0, ImageVectorizeKit.ResolveMmPerPixel(opt), 3);
        }

        [Fact]
        public void Vectorize_DetectsParallelWallFacesFromSyntheticPlan()
        {
            int w = 120, h = 60;
            var gray = new byte[w * h];
            for (int i = 0; i < gray.Length; i++) gray[i] = 250; // white background

            // Draw two parallel horizontal black lines separated by 10 px (200 mm at 20 mm/px)
            for (int x = 10; x <= 105; x++)
            {
                gray[20 * w + x] = 10;
                gray[21 * w + x] = 10;
                gray[30 * w + x] = 10;
                gray[31 * w + x] = 10;
            }

            var res = ImageVectorizeKit.Vectorize(gray, w, h, new ImageVectorizeOptions
            {
                MmPerPixel = 20.0,
                MinSegmentMm = 500.0,
                SimplifyToleranceMm = 20.0,
                MinWallThicknessMm = 100.0,
                MaxWallThicknessMm = 350.0
            });

            Assert.True(res.Segments.Count >= 2);
            Assert.Single(res.WallCandidates);
            Assert.InRange(res.WallCandidates[0].ThicknessMm, 160.0, 240.0);
            Assert.True(res.WallCandidates[0].LengthMm >= 1500.0);
        }

        [Fact]
        public async Task PlanFromImage_ServerTool_DecodesSyntheticBmpAndEnforcesCalibration()
        {
            string bmpPath = Path.Combine(Path.GetTempPath(), $"sutra_plan_img_{Guid.NewGuid():N}.bmp");
            try
            {
                WriteSynthetic24BitBmp(bmpPath, 80, 40);

                // Without calibration -> refusal
                var registry = new ServerToolRegistry { MockMode = true };
                _ = new McpStdioHost(registry, "full");

                var refused = await registry.DispatchAsync(new ToolRequest
                {
                    Tool = "scio_plan_from_image",
                    Arguments = new JObject { ["image_path"] = bmpPath }
                }, CancellationToken.None);
                Assert.False(refused.Ok);
                Assert.Equal("missing_calibration", refused.Error?.Code);

                // With explicit calibration -> success
                var ok = await registry.DispatchAsync(new ToolRequest
                {
                    Tool = "scio_plan_from_image",
                    Arguments = new JObject
                    {
                        ["image_path"] = bmpPath,
                        ["mm_per_pixel"] = 25.0,
                        ["min_segment_mm"] = 400.0,
                        ["target"] = "detail_lines"
                    }
                }, CancellationToken.None);

                Assert.True(ok.Ok, ok.Error?.Message);
                Assert.Equal(80, ok.Data?["width_px"]?.Value<int>());
                Assert.True((ok.Data?["segment_count"]?.Value<int>() ?? 0) >= 1);
            }
            finally
            {
                if (File.Exists(bmpPath)) File.Delete(bmpPath);
            }
        }

        private static void WriteSynthetic24BitBmp(string path, int width, int height)
        {
            int stride = ((width * 3) + 3) & ~3;
            int imageSize = stride * height;
            using var fs = File.Create(path);
            using var bw = new BinaryWriter(fs);

            // BITMAPFILEHEADER (14 bytes)
            bw.Write((byte)'B');
            bw.Write((byte)'M');
            bw.Write(54 + imageSize);
            bw.Write(0);
            bw.Write(54);

            // BITMAPINFOHEADER (40 bytes)
            bw.Write(40);
            bw.Write(width);
            bw.Write(height);
            bw.Write((short)1);
            bw.Write((short)24);
            bw.Write(0);
            bw.Write(imageSize);
            bw.Write(2835);
            bw.Write(2835);
            bw.Write(0);
            bw.Write(0);

            var row = new byte[stride];
            for (int y = 0; y < height; y++)
            {
                for (int x = 0; x < width; x++)
                {
                    bool black = (y == 20 || y == 21) && x >= 10 && x <= 70;
                    byte v = black ? (byte)0 : (byte)255;
                    row[x * 3] = v;
                    row[x * 3 + 1] = v;
                    row[x * 3 + 2] = v;
                }
                bw.Write(row);
            }
        }
    }
}
