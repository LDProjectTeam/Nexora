using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Logging;
using Vellum.Core.Interfaces;

namespace Vellum.Core.Services
{
    /// <summary>
    /// Verifies RSA-PKCS#1/SHA-256 signatures of manifest payloads against public keys
    /// configured by the user/admin. Private keys never leave the publisher.
    /// </summary>
    public class ManifestSignatureVerifier : IManifestSignatureVerifier
    {
        private readonly ILogger<ManifestSignatureVerifier> _logger;
        private readonly List<(string? KeyId, RSA Key)> _keys = new();
        private readonly List<string> _errors = new();

        public ManifestSignatureVerifier(ISettingsService settings, ILogger<ManifestSignatureVerifier> logger)
            : this(settings.Settings.UpdateTrustedPublicKeys, logger)
        {
        }

        internal ManifestSignatureVerifier(IEnumerable<string> pemPublicKeys, ILogger<ManifestSignatureVerifier> logger)
        {
            _logger = logger;

            var index = 0;
            foreach (var pem in pemPublicKeys ?? Enumerable.Empty<string>())
            {
                if (string.IsNullOrWhiteSpace(pem))
                    continue;

                try
                {
                    var rsa = RSA.Create();
                    rsa.ImportFromPem(pem);
                    _keys.Add((TryReadKeyId(pem), rsa));
                }
                catch (Exception ex)
                {
                    _errors.Add(ex.Message);
                    _logger.LogError(ex, "Ignoring invalid trusted update public key at index {Index}.", index);
                }
                index++;
            }

            if (_keys.Count == 0 && _errors.Count > 0)
                _logger.LogError("No valid trusted update public keys are configured; manifest signatures cannot be verified.");
        }

        public bool HasTrustedKeys => _keys.Count > 0;

        public bool Verify(ReadOnlySpan<byte> payload, string signatureBase64, string? keyId, out string? error)
        {
            error = null;
            if (!HasTrustedKeys)
            {
                error = "No trusted manifest public key is configured.";
                return false;
            }

            if (string.IsNullOrWhiteSpace(signatureBase64))
            {
                error = "The manifest signature is missing.";
                return false;
            }

            byte[] signature;
            try
            {
                signature = Convert.FromBase64String(signatureBase64.Trim());
            }
            catch (FormatException)
            {
                error = "The manifest signature is not valid base64.";
                return false;
            }

            if (signature.Length == 0)
            {
                error = "The manifest signature is empty.";
                return false;
            }

            var candidates = _keys;
            if (!string.IsNullOrWhiteSpace(keyId))
            {
                var filtered = _keys
                    .Where(k => string.Equals(k.KeyId, keyId.Trim(), StringComparison.OrdinalIgnoreCase))
                    .ToList();
                if (filtered.Count > 0)
                    candidates = filtered;
            }

            foreach (var (_, key) in candidates)
            {
                try
                {
                    if (key.VerifyData(payload, signature, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1))
                        return true;
                }
                catch (CryptographicException)
                {
                    // Wrong key or malformed signature: try the next trusted key.
                }
            }

            error = string.IsNullOrWhiteSpace(keyId)
                ? "The manifest signature does not match any trusted public key."
                : $"The manifest signature does not match trusted key '{keyId}'.";
            return false;
        }

        /// <summary>
        /// Optional human-readable key id. Vellum supports a PEM comment "<c># key-id: name</c>"
        /// immediately preceding the PEM block so one settings entry can be scoped to a key id.
        /// </summary>
        private static string? TryReadKeyId(string pem)
        {
            foreach (var line in pem.Split('\n'))
            {
                var trimmed = line.Trim();
                const string prefix = "# key-id:";
                if (trimmed.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                    return trimmed[prefix.Length..].Trim();
                if (trimmed.StartsWith("-----BEGIN", StringComparison.Ordinal))
                    break;
            }
            return null;
        }
    }
}
