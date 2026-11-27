using System;
using System.Collections.Generic;
using System.Security.Cryptography;
using System.Text;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace Scio.Cad.Core
{
    /// <summary>
    /// Fingerprint-bound, single-use confirmation token store for transactional CAD writes
    /// (rehearse -> token -> commit -> re-read).
    /// </summary>
    internal static class CadConfirmationStore
    {
        private sealed class Entry
        {
            public string Fingerprint;
            public DateTime ExpiresUtc;
        }

        private static readonly object Gate = new object();
        private static readonly Dictionary<string, Entry> Tokens = new Dictionary<string, Entry>(StringComparer.Ordinal);
        private static readonly TimeSpan Ttl = TimeSpan.FromMinutes(10);

        public static string ComputeFingerprint(string command, string documentKey, JObject request)
        {
            var normalized = (JObject)(request?.DeepClone() ?? new JObject());
            normalized.Remove("dry_run");
            normalized.Remove("confirmation_token");
            normalized.Remove("idempotency_key");

            string canonical = command + "|" + (documentKey ?? "") + "|" + normalized.ToString(Formatting.None);
            using (var sha = SHA256.Create())
            {
                byte[] hash = sha.ComputeHash(Encoding.UTF8.GetBytes(canonical));
                return BitConverter.ToString(hash).Replace("-", "").ToLowerInvariant();
            }
        }

        public static string Issue(string fingerprint)
        {
            byte[] bytes = new byte[16];
            using (var rng = RandomNumberGenerator.Create()) rng.GetBytes(bytes);
            string token = "cad_" + BitConverter.ToString(bytes).Replace("-", "").ToLowerInvariant();

            lock (Gate)
            {
                PruneExpired();
                Tokens[token] = new Entry
                {
                    Fingerprint = fingerprint,
                    ExpiresUtc = DateTime.UtcNow.Add(Ttl)
                };
            }
            return token;
        }

        public static string Consume(string token, string expectedFingerprint)
        {
            if (string.IsNullOrWhiteSpace(token))
                return "confirmation_token is required when dry_run=false. Rehearse first with dry_run=true to obtain a fingerprint-bound token.";

            lock (Gate)
            {
                PruneExpired();
                if (!Tokens.TryGetValue(token, out Entry entry))
                    return "confirmation_token is unknown, expired, or already used. Re-run with dry_run=true to obtain a fresh token.";

                if (!string.Equals(entry.Fingerprint, expectedFingerprint, StringComparison.Ordinal))
                    return "confirmation_token does not match this request or active document. Re-run with dry_run=true for the exact parameters.";

                Tokens.Remove(token);
                return null;
            }
        }

        private static void PruneExpired()
        {
            DateTime now = DateTime.UtcNow;
            var dead = new List<string>();
            foreach (var kv in Tokens)
                if (kv.Value.ExpiresUtc <= now) dead.Add(kv.Key);
            foreach (string k in dead) Tokens.Remove(k);
        }
    }
}
