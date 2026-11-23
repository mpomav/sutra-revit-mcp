using System;
using System.Globalization;
using System.IO;
using System.Text;

namespace Scio.Abstractions
{
    /// <summary>
    /// Clean-room data directory helpers for SCIO Revit MCP v4.
    /// </summary>
    public static class ScioPaths
    {
        public static string DataRoot() => ScioRuntimePaths.DataRoot;
        public static string ReportsDir() => ScioRuntimePaths.ReportsDirectory;
        public static string RunsDir() => Path.Combine(DataRoot(), "runs");
        public static string PlansDir() => Path.Combine(DataRoot(), "plans");
        public static string SalidasDir() => EnsureDir(Path.Combine(DataRoot(), "salidas"));

        public static string EnsureDir(string path)
        {
            Directory.CreateDirectory(path);
            return path;
        }
    }

    /// <summary>
    /// Clean-room diacritic-insensitive, lowercase text folder for bilingual (ES/EN) BIM search.
    /// </summary>
    public static class TextFold
    {
        public static string Fold(string? input)
        {
            if (string.IsNullOrEmpty(input)) return string.Empty;
            string formD = input!.Normalize(NormalizationForm.FormD);
            var sb = new StringBuilder(formD.Length);
            foreach (char c in formD)
            {
                if (CharUnicodeInfo.GetUnicodeCategory(c) != UnicodeCategory.NonSpacingMark)
                    sb.Append(char.ToLowerInvariant(c));
            }
            return sb.ToString().Normalize(NormalizationForm.FormC);
        }
    }
}
