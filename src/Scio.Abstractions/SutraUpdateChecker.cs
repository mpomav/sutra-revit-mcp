using System;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace Scio.Abstractions
{
    public sealed class SutraUpdateInfo
    {
        [JsonProperty("current_version")]
        public string CurrentVersion { get; set; } = string.Empty;

        [JsonProperty("latest_version")]
        public string LatestVersion { get; set; } = string.Empty;

        [JsonProperty("update_available")]
        public bool UpdateAvailable { get; set; }

        [JsonProperty("release_url")]
        public string ReleaseUrl { get; set; } = SutraUpdateChecker.DefaultReleasesPageUrl;

        [JsonProperty("installer_url")]
        public string InstallerUrl { get; set; } = SutraUpdateChecker.DefaultReleasesPageUrl;

        [JsonProperty("release_notes")]
        public string ReleaseNotes { get; set; } = string.Empty;

        [JsonProperty("published_utc")]
        public string PublishedUtc { get; set; } = string.Empty;

        [JsonProperty("checked_utc")]
        public string CheckedUtc { get; set; } = string.Empty;

        [JsonIgnore]
        public string DownloadUrl => !string.IsNullOrWhiteSpace(InstallerUrl) ? InstallerUrl : ReleaseUrl;
    }

    /// <summary>
    /// Lightweight, non-blocking GitHub Releases version checker for Sutra — Revit MCP.
    /// Caches results for 24 hours under %USERPROFILE%\.scio\v4\update_cache.json and
    /// never throws or blocks startup when offline.
    /// </summary>
    public static class SutraUpdateChecker
    {
        public const string DefaultLatestApiUrl = "https://api.github.com/repos/mpomav/sutra-revit-mcp/releases/latest";
        public const string DefaultReleasesPageUrl = "https://github.com/mpomav/sutra-revit-mcp/releases/latest";

        private static readonly TimeSpan CacheTtl = TimeSpan.FromHours(24);
        private static readonly object SettingsLock = new object();
        private static readonly HttpClient SharedHttpClient = CreateHttpClient();

        /// <summary>Optional test hook overriding the GitHub Releases JSON fetch.</summary>
        public static Func<CancellationToken, Task<string?>>? FetchLatestJsonOverride { get; set; }

        /// <summary>Optional test hook overriding the data root directory.</summary>
        public static string? DataRootOverride { get; set; }

        private static string EffectiveDataRoot =>
            !string.IsNullOrWhiteSpace(DataRootOverride) ? DataRootOverride! : ScioRuntimePaths.DataRoot;

        public static string CachePath => Path.Combine(EffectiveDataRoot, "update_cache.json");
        public static string SettingsPath => Path.Combine(EffectiveDataRoot, "settings.json");

        private static HttpClient CreateHttpClient()
        {
            var client = new HttpClient { Timeout = TimeSpan.FromSeconds(4) };
            client.DefaultRequestHeaders.UserAgent.ParseAdd("Sutra-Revit-MCP/1.3");
            client.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
            return client;
        }

        public static bool IsUpdateCheckDisabled()
        {
            string? env = Environment.GetEnvironmentVariable("SCIO_DISABLE_UPDATE_CHECK");
            if (string.Equals(env, "1", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(env, "true", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            try
            {
                string path = SettingsPath;
                if (!File.Exists(path)) return false;
                var obj = JObject.Parse(File.ReadAllText(path, Encoding.UTF8));
                return obj["update_check_disabled"]?.Type == JTokenType.Boolean &&
                       (bool)obj["update_check_disabled"]!;
            }
            catch
            {
                return false;
            }
        }

        public static bool IsVersionDismissed(string? version)
        {
            if (string.IsNullOrWhiteSpace(version)) return false;
            try
            {
                string path = SettingsPath;
                if (!File.Exists(path)) return false;
                var obj = JObject.Parse(File.ReadAllText(path, Encoding.UTF8));
                string? dismissed = obj.Value<string>("update_dismissed_version");
                return !string.IsNullOrWhiteSpace(dismissed) &&
                       string.Equals(NormalizeVersionString(dismissed), NormalizeVersionString(version), StringComparison.OrdinalIgnoreCase);
            }
            catch
            {
                return false;
            }
        }

        public static bool DismissVersion(string version)
        {
            if (string.IsNullOrWhiteSpace(version)) return false;
            lock (SettingsLock)
            {
                try
                {
                    string path = SettingsPath;
                    string? dir = Path.GetDirectoryName(path);
                    if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

                    JObject obj = new JObject();
                    if (File.Exists(path))
                    {
                        try { obj = JObject.Parse(File.ReadAllText(path, Encoding.UTF8)); }
                        catch { obj = new JObject(); }
                    }

                    obj["update_dismissed_version"] = NormalizeVersionString(version);
                    obj["update_dismissed_at_utc"] = DateTimeOffset.UtcNow.ToString("O");

                    WriteAtomicUtf8(path, obj.ToString(Formatting.Indented));
                    return true;
                }
                catch
                {
                    return false;
                }
            }
        }

        /// <summary>
        /// Reads the cached update status from disk without performing any network call.
        /// </summary>
        public static SutraUpdateInfo? TryGetCachedUpdate(string currentVersion)
        {
            try
            {
                string path = CachePath;
                if (!File.Exists(path)) return null;
                var cached = JsonConvert.DeserializeObject<SutraUpdateInfo>(File.ReadAllText(path, Encoding.UTF8));
                if (cached == null || string.IsNullOrWhiteSpace(cached.LatestVersion)) return null;

                if (!IsTrustedReleaseUrl(cached.ReleaseUrl))
                    cached.ReleaseUrl = DefaultReleasesPageUrl;
                if (!IsTrustedReleaseUrl(cached.InstallerUrl))
                    cached.InstallerUrl = cached.ReleaseUrl;

                cached.CurrentVersion = NormalizeVersionString(currentVersion);
                cached.UpdateAvailable = IsNewerVersion(cached.CurrentVersion, cached.LatestVersion);
                return cached;
            }
            catch
            {
                return null;
            }
        }

        /// <summary>
        /// Checks GitHub Releases for a newer Sutra version (respecting the 24h cache unless forceRefresh=true).
        /// Returns null if update checking is disabled or the network is unavailable.
        /// </summary>
        public static async Task<SutraUpdateInfo?> CheckForUpdateAsync(
            string currentVersion,
            bool forceRefresh = false,
            CancellationToken ct = default)
        {
            if (!forceRefresh && IsUpdateCheckDisabled())
                return null;

            string normCurrent = NormalizeVersionString(currentVersion);

            if (!forceRefresh)
            {
                var cached = TryGetCachedUpdate(normCurrent);
                if (cached != null &&
                    DateTimeOffset.TryParse(cached.CheckedUtc, out var checkedAt) &&
                    (DateTimeOffset.UtcNow - checkedAt) < CacheTtl)
                {
                    return cached;
                }
            }

            string? json = await FetchLatestReleaseJsonAsync(ct).ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(json))
                return null;

            var info = ParseReleaseJson(json!, normCurrent);
            if (info == null)
                return null;

            try
            {
                string path = CachePath;
                string? dir = Path.GetDirectoryName(path);
                if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
                WriteAtomicUtf8(path, JsonConvert.SerializeObject(info, Formatting.Indented));
            }
            catch
            {
                // Cache write failure is non-fatal
            }

            return info;
        }

        public static SutraUpdateInfo? ParseReleaseJson(string json, string currentVersion)
        {
            try
            {
                var obj = JObject.Parse(json);
                if (obj.Value<bool?>("draft") == true || obj.Value<bool?>("prerelease") == true)
                    return null;

                string tag = obj.Value<string>("tag_name") ?? string.Empty;
                string latest = NormalizeVersionString(tag);
                if (string.IsNullOrWhiteSpace(latest))
                    return null;

                string releaseUrl = obj.Value<string>("html_url") ?? DefaultReleasesPageUrl;
                if (!IsTrustedReleaseUrl(releaseUrl))
                    releaseUrl = DefaultReleasesPageUrl;
                string installerUrl = releaseUrl;

                if (obj["assets"] is JArray assets)
                {
                    foreach (var asset in assets)
                    {
                        string name = asset.Value<string>("name") ?? string.Empty;
                        string url = asset.Value<string>("browser_download_url") ?? string.Empty;
                        if (!string.IsNullOrWhiteSpace(url) &&
                            name.EndsWith("-setup.exe", StringComparison.OrdinalIgnoreCase) &&
                            IsTrustedReleaseUrl(url))
                        {
                            installerUrl = url;
                            break;
                        }
                    }
                }

                string body = (obj.Value<string>("body") ?? string.Empty).Trim();
                if (body.Length > 600)
                    body = body.Substring(0, 597).TrimEnd() + "...";

                string normCurrent = NormalizeVersionString(currentVersion);
                return new SutraUpdateInfo
                {
                    CurrentVersion = normCurrent,
                    LatestVersion = latest,
                    UpdateAvailable = IsNewerVersion(normCurrent, latest),
                    ReleaseUrl = releaseUrl,
                    InstallerUrl = installerUrl,
                    ReleaseNotes = body,
                    PublishedUtc = obj.Value<string>("published_at") ?? string.Empty,
                    CheckedUtc = DateTimeOffset.UtcNow.ToString("O")
                };
            }
            catch
            {
                return null;
            }
        }

        /// <summary>
        /// Security guard (SEC-04): ensures release/installer URLs point exclusively to the official
        /// Sutra GitHub repository or GitHub release asset hosts over HTTPS before launching via ShellExecute.
        /// </summary>
        public static bool IsTrustedReleaseUrl(string? url)
        {
            if (string.IsNullOrWhiteSpace(url)) return false;
            if (!Uri.TryCreate(url!.Trim(), UriKind.Absolute, out var uri)) return false;
            if (!string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase)) return false;

            string host = uri.Host.ToLowerInvariant();
            if (string.Equals(host, "github.com", StringComparison.Ordinal))
            {
                return uri.AbsolutePath.StartsWith("/mpomav/sutra-revit-mcp/", StringComparison.OrdinalIgnoreCase);
            }
            if (string.Equals(host, "objects.githubusercontent.com", StringComparison.Ordinal) ||
                string.Equals(host, "release-assets.githubusercontent.com", StringComparison.Ordinal))
            {
                return true;
            }
            return false;
        }

        public static string NormalizeVersionString(string? raw)
        {
            if (string.IsNullOrWhiteSpace(raw)) return "0.0.0";
            string s = raw!.Trim();
            if (s.StartsWith("v", StringComparison.OrdinalIgnoreCase))
                s = s.Substring(1);
            int plus = s.IndexOf('+');
            if (plus > 0) s = s.Substring(0, plus);
            int dash = s.IndexOf('-');
            if (dash > 0) s = s.Substring(0, dash);
            return s.Trim();
        }

        public static bool IsNewerVersion(string? current, string? candidate)
        {
            if (!TryParseSemVer(current, out var curVer)) return false;
            if (!TryParseSemVer(candidate, out var candVer)) return false;
            return candVer > curVer;
        }

        private static bool TryParseSemVer(string? raw, out Version version)
        {
            string norm = NormalizeVersionString(raw);
            if (Version.TryParse(norm, out var parsed))
            {
                version = new Version(
                    Math.Max(0, parsed.Major),
                    Math.Max(0, parsed.Minor),
                    Math.Max(0, parsed.Build));
                return true;
            }
            version = new Version(0, 0, 0);
            return false;
        }

        private static async Task<string?> FetchLatestReleaseJsonAsync(CancellationToken ct)
        {
            if (FetchLatestJsonOverride != null)
            {
                try { return await FetchLatestJsonOverride(ct).ConfigureAwait(false); }
                catch { return null; }
            }

            try
            {
                try { ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12; } catch { }

                string url = Environment.GetEnvironmentVariable("SCIO_UPDATE_CHECK_URL");
                if (string.IsNullOrWhiteSpace(url))
                    url = DefaultLatestApiUrl;

                using (var resp = await SharedHttpClient.GetAsync(url, ct).ConfigureAwait(false))
                {
                    if (!resp.IsSuccessStatusCode) return null;
                    return await resp.Content.ReadAsStringAsync().ConfigureAwait(false);
                }
            }
            catch
            {
                return null;
            }
        }

        private static void WriteAtomicUtf8(string path, string content)
        {
            string tmp = path + ".tmp-" + Guid.NewGuid().ToString("N");
            File.WriteAllText(tmp, content, new UTF8Encoding(false));
            if (File.Exists(path))
            {
                try { File.Replace(tmp, path, null); }
                catch
                {
                    File.Copy(tmp, path, overwrite: true);
                    try { File.Delete(tmp); } catch { }
                }
            }
            else
            {
                File.Move(tmp, path);
            }
        }
    }
}
