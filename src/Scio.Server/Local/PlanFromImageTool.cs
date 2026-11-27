using System;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using Scio.Abstractions;

namespace Scio.Server
{
    /// <summary>
    /// Host-resident read-only raster-to-vector planner (scio_plan_from_image).
    /// Requires explicit scale calibration; never guesses scale and never blocks Revit's UI thread.
    /// </summary>
    internal static class PlanFromImageTool
    {
        public static Task<ToolResponse> Handle(JObject args, CancellationToken ct)
        {
            string requestId = args.Value<string>("_request_id") ?? Guid.NewGuid().ToString("N");
            string imagePath = (args.Value<string>("image_path") ?? string.Empty).Trim();
            if (string.IsNullOrWhiteSpace(imagePath) || !File.Exists(imagePath))
                return Task.FromResult(ToolResponse.Refusal(requestId, "file_not_found", $"Image file not found: '{imagePath}'."));

            var opt = new ImageVectorizeOptions
            {
                MmPerPixel = args.Value<double?>("mm_per_pixel"),
                KnownDistanceMm = args.Value<double?>("known_distance_mm"),
                OriginXMm = args.Value<double?>("origin_x_mm") ?? 0.0,
                OriginYMm = args.Value<double?>("origin_y_mm") ?? 0.0,
                ThresholdMode = (args.Value<string>("threshold_mode") ?? "otsu").Trim().ToLowerInvariant(),
                ManualThreshold = args.Value<int?>("manual_threshold") ?? 128,
                DarkForeground = args.Value<bool?>("dark_foreground") ?? true,
                MinSegmentMm = Math.Max(10.0, args.Value<double?>("min_segment_mm") ?? 150.0),
                SimplifyToleranceMm = Math.Max(1.0, args.Value<double?>("simplify_tolerance_mm") ?? 25.0),
                MinWallThicknessMm = Math.Max(20.0, args.Value<double?>("min_wall_thickness_mm") ?? 80.0),
                MaxWallThicknessMm = Math.Max(50.0, args.Value<double?>("max_wall_thickness_mm") ?? 450.0),
                MaxSegments = Math.Max(1, Math.Min(2000, args.Value<int?>("max_segments") ?? 300))
            };

            if (args["calibration_point_1_px"] is JArray p1 && p1.Count >= 2)
                opt.CalibrationPoint1Px = new[] { p1[0].Value<double>(), p1[1].Value<double>() };
            if (args["calibration_point_2_px"] is JArray p2 && p2.Count >= 2)
                opt.CalibrationPoint2Px = new[] { p2[0].Value<double>(), p2[1].Value<double>() };

            try
            {
                ImageVectorizeKit.ResolveMmPerPixel(opt);
            }
            catch (InvalidOperationException ex)
            {
                return Task.FromResult(ToolResponse.Refusal(requestId, "missing_calibration", ex.Message));
            }

            try
            {
                var (gray, width, height) = LoadGrayscaleImage(imagePath);
                ct.ThrowIfCancellationRequested();

                var vec = ImageVectorizeKit.Vectorize(gray, width, height, opt);
                string target = (args.Value<string>("target") ?? "detail_lines").Trim().ToLowerInvariant();

                var segArr = new JArray(vec.Segments.Select(s => new JObject
                {
                    ["start_mm"] = new JArray(s.StartX, s.StartY),
                    ["end_mm"] = new JArray(s.EndX, s.EndY),
                    ["length_mm"] = s.LengthMm
                }));

                var wallArr = new JArray(vec.WallCandidates.Select(w => new JObject
                {
                    ["start_mm"] = new JArray(w.StartX, w.StartY),
                    ["end_mm"] = new JArray(w.EndX, w.EndY),
                    ["length_mm"] = w.LengthMm,
                    ["thickness_mm"] = w.ThicknessMm,
                    ["confidence"] = w.Confidence
                }));

                var data = new JObject
                {
                    ["image_path"] = imagePath,
                    ["width_px"] = vec.WidthPx,
                    ["height_px"] = vec.HeightPx,
                    ["mm_per_pixel"] = vec.MmPerPixel,
                    ["threshold_used"] = vec.ThresholdUsed,
                    ["foreground_ratio"] = vec.ForegroundRatio,
                    ["target"] = target,
                    ["segment_count"] = segArr.Count,
                    ["wall_candidate_count"] = wallArr.Count,
                    ["segments"] = segArr,
                    ["wall_candidates"] = wallArr,
                    ["next_tool"] = target == "walls" ? "scio_create_elements" : "scio_create_detail_lines"
                };

                return Task.FromResult(ToolResponse.Success(
                    requestId,
                    data,
                    $"Vectorized '{Path.GetFileName(imagePath)}' ({width}x{height}px, {vec.MmPerPixel} mm/px): {segArr.Count} segment(s), {wallArr.Count} wall candidate(s)."));
            }
            catch (Exception ex)
            {
                return Task.FromResult(ToolResponse.Failure(requestId, "image_vectorize_error", ex.Message));
            }
        }

        internal static (byte[] Gray, int Width, int Height) LoadGrayscaleImage(string path)
        {
            if (TryReadUncompressedBmp(path, out var bmpGray, out int bw, out int bh))
                return (bmpGray, bw, bh);

            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
                return LoadViaGdiPlus(path);

            throw new NotSupportedException("Non-BMP image decoding requires Windows GDI+.");
        }

        internal static bool TryReadUncompressedBmp(string path, out byte[] gray, out int width, out int height)
        {
            gray = Array.Empty<byte>();
            width = 0;
            height = 0;

            using var fs = File.OpenRead(path);
            if (fs.Length < 54) return false;
            using var br = new BinaryReader(fs);
            if (br.ReadByte() != (byte)'B' || br.ReadByte() != (byte)'M') return false;

            fs.Seek(10, SeekOrigin.Begin);
            int pixelOffset = br.ReadInt32();
            int headerSize = br.ReadInt32();
            if (headerSize < 40) return false;

            width = br.ReadInt32();
            int rawHeight = br.ReadInt32();
            bool topDown = rawHeight < 0;
            height = Math.Abs(rawHeight);
            short planes = br.ReadInt16();
            short bpp = br.ReadInt16();
            int compression = br.ReadInt32();

            if (planes != 1 || compression != 0 || (bpp != 24 && bpp != 32) || width <= 0 || height <= 0)
                return false;

            int bytesPerPx = bpp / 8;
            int stride = ((width * bytesPerPx) + 3) & ~3;
            if (pixelOffset + (long)stride * height > fs.Length) return false;

            fs.Seek(pixelOffset, SeekOrigin.Begin);
            gray = new byte[width * height];
            var rowBuf = new byte[stride];

            for (int row = 0; row < height; row++)
            {
                int read = fs.Read(rowBuf, 0, stride);
                if (read < width * bytesPerPx) return false;
                int dstY = topDown ? row : (height - 1 - row);
                int dstRow = dstY * width;
                for (int x = 0; x < width; x++)
                {
                    int off = x * bytesPerPx;
                    byte b = rowBuf[off];
                    byte g = rowBuf[off + 1];
                    byte r = rowBuf[off + 2];
                    gray[dstRow + x] = (byte)Math.Round(0.299 * r + 0.587 * g + 0.114 * b);
                }
            }

            return true;
        }

        private static (byte[] Gray, int Width, int Height) LoadViaGdiPlus(string path)
        {
            var input = new GdiplusStartupInput { GdiplusVersion = 1 };
            int st = GdiplusStartup(out IntPtr token, ref input, IntPtr.Zero);
            if (st != 0) throw new InvalidOperationException($"GdiplusStartup failed with status {st}.");

            IntPtr bitmap = IntPtr.Zero;
            try
            {
                st = GdipCreateBitmapFromFile(path, out bitmap);
                if (st != 0 || bitmap == IntPtr.Zero)
                    throw new InvalidOperationException($"Failed to decode raster image '{path}' (GDI+ status {st}).");

                GdipGetImageWidth(bitmap, out uint w);
                GdipGetImageHeight(bitmap, out uint h);
                int width = (int)w;
                int height = (int)h;
                if (width <= 0 || height <= 0)
                    throw new InvalidOperationException("Decoded image has invalid dimensions.");

                var rect = new GpRect { X = 0, Y = 0, Width = width, Height = height };
                var bmpData = new GpBitmapData();
                const int PixelFormat32bppArgb = 0x26200A;
                const uint ImageLockModeRead = 1;

                st = GdipBitmapLockBits(bitmap, ref rect, ImageLockModeRead, PixelFormat32bppArgb, ref bmpData);
                if (st != 0) throw new InvalidOperationException($"GdipBitmapLockBits failed (status {st}).");

                try
                {
                    var gray = new byte[width * height];
                    int absStride = Math.Abs(bmpData.Stride);
                    var rowBuf = new byte[absStride];

                    for (int y = 0; y < height; y++)
                    {
                        IntPtr rowPtr = IntPtr.Add(bmpData.Scan0, y * bmpData.Stride);
                        Marshal.Copy(rowPtr, rowBuf, 0, Math.Min(absStride, width * 4));
                        int dstRow = y * width;
                        for (int x = 0; x < width; x++)
                        {
                            byte b = rowBuf[x * 4];
                            byte g = rowBuf[x * 4 + 1];
                            byte r = rowBuf[x * 4 + 2];
                            gray[dstRow + x] = (byte)Math.Round(0.299 * r + 0.587 * g + 0.114 * b);
                        }
                    }

                    return (gray, width, height);
                }
                finally
                {
                    GdipBitmapUnlockBits(bitmap, ref bmpData);
                }
            }
            finally
            {
                if (bitmap != IntPtr.Zero) GdipDisposeImage(bitmap);
                GdiplusShutdown(token);
            }
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct GdiplusStartupInput
        {
            public uint GdiplusVersion;
            public IntPtr DebugEventCallback;
            public int SuppressBackgroundThread;
            public int SuppressExternalCodecs;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct GpRect
        {
            public int X, Y, Width, Height;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct GpBitmapData
        {
            public uint Width;
            public uint Height;
            public int Stride;
            public int PixelFormat;
            public IntPtr Scan0;
            public UIntPtr Reserved;
        }

        [DllImport("gdiplus.dll", ExactSpelling = true)]
        private static extern int GdiplusStartup(out IntPtr token, ref GdiplusStartupInput input, IntPtr output);

        [DllImport("gdiplus.dll", ExactSpelling = true)]
        private static extern void GdiplusShutdown(IntPtr token);

        [DllImport("gdiplus.dll", ExactSpelling = true, CharSet = CharSet.Unicode)]
        private static extern int GdipCreateBitmapFromFile(string filename, out IntPtr bitmap);

        [DllImport("gdiplus.dll", ExactSpelling = true)]
        private static extern int GdipDisposeImage(IntPtr image);

        [DllImport("gdiplus.dll", ExactSpelling = true)]
        private static extern int GdipGetImageWidth(IntPtr image, out uint width);

        [DllImport("gdiplus.dll", ExactSpelling = true)]
        private static extern int GdipGetImageHeight(IntPtr image, out uint height);

        [DllImport("gdiplus.dll", ExactSpelling = true)]
        private static extern int GdipBitmapLockBits(IntPtr bitmap, ref GpRect rect, uint flags, int format, ref GpBitmapData lockedBitmapData);

        [DllImport("gdiplus.dll", ExactSpelling = true)]
        private static extern int GdipBitmapUnlockBits(IntPtr bitmap, ref GpBitmapData lockedBitmapData);
    }
}
