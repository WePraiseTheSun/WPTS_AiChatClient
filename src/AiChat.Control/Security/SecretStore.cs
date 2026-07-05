using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace AiChat.Control.Security
{
    /// <summary>
    /// API-key storage using Windows DPAPI (ProtectedData, CurrentUser scope).
    /// Keys are encrypted at rest in a small file next to the SQLite database — they never
    /// enter SQLite unencrypted, are never logged, and never cross the JS bridge (spec §2, §3.7).
    /// </summary>
    public sealed class SecretStore
    {
        private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("AiChat.Control.v1");
        private readonly string _filePath;
        private readonly object _lock = new object();
        private Dictionary<string, string> _cache; // provider -> base64(DPAPI blob)

        public SecretStore(string dataDirectory)
        {
            Directory.CreateDirectory(dataDirectory);
            _filePath = Path.Combine(dataDirectory, "secrets.dat");
        }

        public void SetApiKey(string provider, string apiKey)
        {
            if (string.IsNullOrWhiteSpace(provider)) throw new ArgumentNullException(nameof(provider));
            lock (_lock)
            {
                Load();
                if (string.IsNullOrEmpty(apiKey))
                {
                    _cache.Remove(provider.ToLowerInvariant());
                }
                else
                {
                    byte[] cipher = ProtectedData.Protect(
                        Encoding.UTF8.GetBytes(apiKey), Entropy, DataProtectionScope.CurrentUser);
                    _cache[provider.ToLowerInvariant()] = Convert.ToBase64String(cipher);
                }
                Save();
            }
        }

        /// <summary>Returns the decrypted key, or null. Decryption happens on demand only.</summary>
        public string GetApiKey(string provider)
        {
            lock (_lock)
            {
                Load();
                if (!_cache.TryGetValue(provider?.ToLowerInvariant() ?? "", out string b64)) return null;
                try
                {
                    byte[] plain = ProtectedData.Unprotect(
                        Convert.FromBase64String(b64), Entropy, DataProtectionScope.CurrentUser);
                    return Encoding.UTF8.GetString(plain);
                }
                catch (CryptographicException)
                {
                    // Encrypted under a different Windows account/profile — treat as absent.
                    return null;
                }
            }
        }

        public bool HasApiKey(string provider)
        {
            lock (_lock) { Load(); return _cache.ContainsKey(provider?.ToLowerInvariant() ?? ""); }
        }

        public void DeleteApiKey(string provider) => SetApiKey(provider, null);

        private void Load()
        {
            if (_cache != null) return;
            if (File.Exists(_filePath))
            {
                try
                {
                    _cache = JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(_filePath))
                             ?? new Dictionary<string, string>();
                    return;
                }
                catch { /* corrupt file: start fresh rather than crash */ }
            }
            _cache = new Dictionary<string, string>();
        }

        private void Save()
            => File.WriteAllText(_filePath, JsonSerializer.Serialize(_cache));
    }
}
