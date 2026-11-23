using System;
using System.IO;

namespace Scio.Abstractions
{
    /// <summary>
    /// Clean-room runtime path and external-side-effect guard for SCIO Revit MCP v4.
    /// </summary>
    public static class ScioRuntimePaths
    {
        public static string DataRoot
        {
            get
            {
                string? env = Environment.GetEnvironmentVariable("SCIO_DATA_ROOT");
                if (!string.IsNullOrWhiteSpace(env)) return env!;
                return Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                    ".scio", "v4");
            }
        }

        public static string LocalDbPath => Path.Combine(DataRoot, "scio_local.db");

        public static string ReportsDirectory => Path.Combine(DataRoot, "reports");

        public static bool AllowsExternalSideEffect(PermissionProfile profile, out string denyReason)
        {
            if (profile == PermissionProfile.ReadOnly)
            {
                denyReason = "External file writes are not permitted under ReadOnly profile.";
                return false;
            }
            denyReason = string.Empty;
            return true;
        }

        public static string? ValidateLocalOutputPath(string? path, string? parameterName = null)
        {
            if (string.IsNullOrWhiteSpace(path)) return null;
            string nonNullPath = path!;
            string label = string.IsNullOrWhiteSpace(parameterName) ? "Output path" : parameterName!;
            if (!Path.IsPathRooted(nonNullPath))
                return $"{label} must be an absolute path: '{nonNullPath}'.";
            if (nonNullPath.StartsWith(@"\\", StringComparison.Ordinal))
                return $"UNC network paths are not allowed for {label}: '{nonNullPath}'.";
            return null;
        }
    }
}
