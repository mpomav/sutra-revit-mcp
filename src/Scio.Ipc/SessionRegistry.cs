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
        public static string SessionsDirectory =>
            Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "Scio", "RevitMcpV4", "sessions");

        /// <summary>Optional test hook overriding the Revit sessions directory.</summary>
        public static string? DirectoryOverride { get; set; }

        /// <summary>Optional test hook overriding PID liveness checks.</summary>
        public static Func<int, bool>? LivenessProbe { get; set; }

        private static string EffectiveDirectory =>
            !string.IsNullOrWhiteSpace(DirectoryOverride) ? DirectoryOverride! : SessionsDirectory;

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
            Directory.CreateDirectory(SessionsDirectory);
            string path = Path.Combine(SessionsDirectory, $"revit-{beacon.RevitYear}-{beacon.Pid}.json");
            string tmp = path + ".tmp";
            File.WriteAllText(tmp, JsonConvert.SerializeObject(beacon, Formatting.Indented), Encoding.UTF8);
            if (File.Exists(path)) File.Delete(path);
            File.Move(tmp, path);
            return path;
        }

        public static void RemoveBeacon(int revitYear, int pid)
        {
            string path = Path.Combine(SessionsDirectory, $"revit-{revitYear}-{pid}.json");
            try { if (File.Exists(path)) File.Delete(path); } catch { /* best-effort cleanup */ }
        }

        public static List<RevitSessionBeacon> DiscoverLiveSessions()
        {
            var live = new List<RevitSessionBeacon>();
            string dir = EffectiveDirectory;
            if (!Directory.Exists(dir)) return live;

            foreach (string file in Directory.GetFiles(dir, "revit-*.json"))
            {
                try
                {
                    var beacon = JsonConvert.DeserializeObject<RevitSessionBeacon>(File.ReadAllText(file, Encoding.UTF8));
                    if (beacon == null || string.IsNullOrWhiteSpace(beacon.PipeName)) continue;

                    if (IsProcessAlive(beacon.Pid))
                    {
                        live.Add(beacon);
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

            return live.OrderByDescending(b => b.RevitYear).ThenByDescending(b => b.StartedUtc, StringComparer.Ordinal).ToList();
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
