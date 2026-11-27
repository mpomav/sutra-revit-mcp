using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using Newtonsoft.Json;

namespace Scio.Ipc
{
    public sealed class RevitSessionBeacon
    {
        [JsonProperty("pipe_name")]
        public string PipeName { get; set; } = string.Empty;

        [JsonProperty("pid")]
        public int Pid { get; set; }

        [JsonProperty("revit_year")]
        public int RevitYear { get; set; }

        [JsonProperty("version")]
        public string Version { get; set; } = string.Empty;

        [JsonProperty("contract_hash")]
        public string ContractHash { get; set; } = string.Empty;

        [JsonProperty("started_utc")]
        public string StartedUtc { get; set; } = DateTime.UtcNow.ToString("O");
    }

    public static class SessionRegistry
    {
        /// <summary>
        /// Legacy LocalAppData sessions directory (kept for backward compatibility with older servers).
        /// </summary>
        public static string SessionsDirectory =>
            Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "Scio", "RevitMcpV4", "sessions");

        /// <summary>
        /// MSIX-safe sessions directory under %USERPROFILE%\.scio\v4\sessions.
        /// Windows MSIX/Microsoft Store apps (e.g. Claude Desktop from MS Store) virtualize %LOCALAPPDATA%,
        /// whereas %USERPROFILE%\.scio is shared directly between Win32 Revit and MSIX-spawned MCP servers.
        /// </summary>
        public static string UserProfileSessionsDirectory =>
            Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                ".scio", "v4", "sessions");

        /// <summary>Optional test hook overriding the Revit sessions directory.</summary>
        public static string? DirectoryOverride { get; set; }

        /// <summary>Optional test hook overriding PID liveness checks.</summary>
        public static Func<int, bool>? LivenessProbe { get; set; }

        private static IEnumerable<string> GetSearchDirectories()
        {
            if (!string.IsNullOrWhiteSpace(DirectoryOverride))
            {
                yield return DirectoryOverride!;
                yield break;
            }

            yield return UserProfileSessionsDirectory;
            if (!string.Equals(SessionsDirectory, UserProfileSessionsDirectory, StringComparison.OrdinalIgnoreCase))
            {
                yield return SessionsDirectory;
            }
        }

        public static string BuildPipeName(int revitYear, int pid)
        {
            string user = Environment.UserName ?? "user";
            using (var sha = SHA256.Create())
            {
                byte[] hash = sha.ComputeHash(Encoding.UTF8.GetBytes(user.ToLowerInvariant()));
                string tag = BitConverter.ToString(hash, 0, 4).Replace("-", "").ToLowerInvariant();
                return $"scio-revit-v4-{tag}-{revitYear}-{pid}";
            }
        }

        public static string WriteBeacon(RevitSessionBeacon beacon)
        {
            string fileName = $"revit-{beacon.RevitYear}-{beacon.Pid}.json";
            string json = JsonConvert.SerializeObject(beacon, Formatting.Indented);

            if (!string.IsNullOrWhiteSpace(DirectoryOverride))
            {
                return WriteBeaconToDirectory(DirectoryOverride!, fileName, json);
            }

            string primaryPath = WriteBeaconToDirectory(UserProfileSessionsDirectory, fileName, json);
            try
            {
                WriteBeaconToDirectory(SessionsDirectory, fileName, json);
            }
            catch
            {
                // Best-effort mirror to legacy LocalAppData path
            }
            return primaryPath;
        }

        private static string WriteBeaconToDirectory(string dir, string fileName, string json)
        {
            Directory.CreateDirectory(dir);
            string path = Path.Combine(dir, fileName);
            string tmp = path + ".tmp";
            File.WriteAllText(tmp, json, Encoding.UTF8);
            if (File.Exists(path)) File.Delete(path);
            File.Move(tmp, path);
            return path;
        }

        public static void RemoveBeacon(int revitYear, int pid)
        {
            string fileName = $"revit-{revitYear}-{pid}.json";
            foreach (string dir in GetSearchDirectories())
            {
                string path = Path.Combine(dir, fileName);
                try { if (File.Exists(path)) File.Delete(path); } catch { /* best-effort cleanup */ }
            }
        }

        public static List<RevitSessionBeacon> DiscoverLiveSessions()
        {
            var byPipe = new Dictionary<string, RevitSessionBeacon>(StringComparer.OrdinalIgnoreCase);

            foreach (string dir in GetSearchDirectories())
            {
                if (!Directory.Exists(dir)) continue;

                foreach (string file in Directory.GetFiles(dir, "revit-*.json"))
                {
                    try
                    {
                        var beacon = JsonConvert.DeserializeObject<RevitSessionBeacon>(File.ReadAllText(file, Encoding.UTF8));
                        if (beacon == null || string.IsNullOrWhiteSpace(beacon.PipeName)) continue;

                        if (IsProcessAlive(beacon.Pid))
                        {
                            if (!byPipe.TryGetValue(beacon.PipeName, out var existing) ||
                                string.CompareOrdinal(beacon.StartedUtc, existing.StartedUtc) > 0)
                            {
                                byPipe[beacon.PipeName] = beacon;
                            }
                        }
                        else
                        {
                            try { File.Delete(file); } catch { }
                        }
                    }
                    catch
                    {
                        // Ignore malformed or transient files
                    }
                }
            }

            return byPipe.Values
                .OrderByDescending(b => b.RevitYear)
                .ThenByDescending(b => b.StartedUtc, StringComparer.Ordinal)
                .ToList();
        }

        private static bool IsProcessAlive(int pid)
        {
            if (LivenessProbe != null) return LivenessProbe(pid);
            if (pid <= 0) return false;
            try
            {
                using (var proc = Process.GetProcessById(pid))
                {
                    return !proc.HasExited;
                }
            }
            catch
            {
                return false;
            }
        }
    }
}
