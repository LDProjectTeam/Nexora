using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Vellum.Core.Helpers;
using Vellum.Core.Interfaces;
using Vellum.Core.Models;

namespace Vellum.Core.Services
{
    /// <summary>
    /// HTTP manifest provider. Supports two signature transports:
    ///  1. Detached headers <c>X-Vellum-Signature</c> / <c>X-Vellum-Key-Id</c> over the
    ///     exact response body bytes (preferred for a plain manifest file).
    ///  2. A <see cref="SignedManifestEnvelope"/> JSON document whose <c>payload</c> is the
    ///     base64 manifest and whose signature covers the decoded payload bytes.
    /// Unsigned manifests are rejected when signatures are required.
    /// </summary>
    public class HttpUpdateManifestProvider : IUpdateManifestProvider
    {
        private const int MaxManifestBytes = 32 * 1024 * 1024;
        private const string SignatureHeader = "X-Vellum-Signature";
        private const string KeyIdHeader = "X-Vellum-Key-Id";

        private static readonly JsonSerializerOptions _json = new()
        {
            PropertyNameCaseInsensitive = true,
            AllowTrailingCommas = true,
            ReadCommentHandling = JsonCommentHandling.Skip,
            Converters = { new JsonStringEnumConverter() }
        };

        private readonly HttpClient _http;
        private readonly ISettingsService _settings;
        private readonly IManifestSignatureVerifier _signatureVerifier;
        private readonly ILogger<HttpUpdateManifestProvider> _logger;

        public HttpUpdateManifestProvider(
            HttpClient http,
            ISettingsService settings,
            IManifestSignatureVerifier signatureVerifier,
            ILogger<HttpUpdateManifestProvider> logger)
        {
            _http = http;
            _settings = settings;
            _signatureVerifier = signatureVerifier;
            _logger = logger;
        }

        public async Task<UpdateManifest> GetGameManifestAsync(string gameId, string manifestUrl,
            CancellationToken ct = default)
        {
            var document = await FetchSignedDocumentAsync(manifestUrl, ct).ConfigureAwait(false);
            UpdateManifest? manifest;
            try
            {
                manifest = JsonSerializer.Deserialize<UpdateManifest>(document.Payload, _json);
            }
            catch (JsonException ex)
            {
                throw new InvalidDataException($"The update manifest is not valid JSON: {ex.Message}", ex);
            }

            if (manifest == null)
                throw new InvalidDataException("The update manifest is empty.");

            if (!UpdateVersion.IsSupportedSchema(manifest.SchemaVersion))
                throw new InvalidDataException(
                    $"Unsupported update manifest schema version {manifest.SchemaVersion}.");

            if (string.IsNullOrWhiteSpace(manifest.GameId))
                throw new InvalidDataException("The update manifest does not declare a game id.");

            if (!string.Equals(manifest.GameId.Trim(), gameId.Trim(), StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException(
                    $"The update manifest belongs to '{manifest.GameId}', not '{gameId}'.");

            ValidateCommon(manifest.Files, manifest.DeletedFiles, manifest.SchemaVersion, "game manifest");

            if (string.IsNullOrWhiteSpace(manifest.Version))
                throw new InvalidDataException("The update manifest does not declare a version.");

            var baseUri = new Uri(document.BaseUrl, UriKind.Absolute);
            foreach (var file in manifest.Files)
            {
                if (string.IsNullOrWhiteSpace(file.Url))
                    throw new InvalidDataException($"Manifest file '{file.Path}' has no download URL.");

                Uri? resolved;
                if (!Uri.TryCreate(baseUri, file.Url, out resolved)
                    || (resolved.Scheme != Uri.UriSchemeHttp && resolved.Scheme != Uri.UriSchemeHttps))
                {
                    throw new InvalidDataException(
                        $"Manifest file '{file.Path}' has an invalid download URL '{file.Url}'.");
                }
                file.Url = resolved.AbsoluteUri;
            }

            _logger.LogInformation(
                "Loaded signed manifest for {GameId} {Version} ({BuildId}) with {Files} files from {Url}.",
                manifest.GameId, manifest.Version, manifest.BuildId, manifest.Files.Count, manifestUrl);
            return manifest;
        }

        public async Task<LauncherUpdateManifest> GetLauncherManifestAsync(string manifestUrl,
            CancellationToken ct = default)
        {
            var document = await FetchSignedDocumentAsync(manifestUrl, ct).ConfigureAwait(false);
            LauncherUpdateManifest? manifest;
            try
            {
                manifest = JsonSerializer.Deserialize<LauncherUpdateManifest>(document.Payload, _json);
            }
            catch (JsonException ex)
            {
                throw new InvalidDataException($"The launcher manifest is not valid JSON: {ex.Message}", ex);
            }

            if (manifest == null)
                throw new InvalidDataException("The launcher manifest is empty.");

            if (manifest.SchemaVersion != LauncherUpdateManifest.CurrentSchemaVersion)
                throw new InvalidDataException(
                    $"Unsupported launcher manifest schema version {manifest.SchemaVersion}.");

            if (string.IsNullOrWhiteSpace(manifest.Version))
                throw new InvalidDataException("The launcher manifest does not declare a version.");

            if (!string.Equals(manifest.AppId, "vellum-launcher", StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("The launcher manifest is not for Vellum.");

            ValidateCommon(manifest.Files, manifest.DeletedFiles, manifest.SchemaVersion, "launcher manifest");
            if (manifest.Files.Count == 0)
                throw new InvalidDataException("The launcher manifest has no files.");

            var baseUri = new Uri(document.BaseUrl, UriKind.Absolute);
            if (string.IsNullOrWhiteSpace(manifest.PackageUrl))
                throw new InvalidDataException("The launcher manifest has no package URL.");
            if (!Uri.TryCreate(baseUri, manifest.PackageUrl, out var packageUri)
                || (packageUri.Scheme != Uri.UriSchemeHttp && packageUri.Scheme != Uri.UriSchemeHttps))
            {
                throw new InvalidDataException("The launcher package URL is invalid.");
            }
            manifest.PackageUrl = packageUri.AbsoluteUri;

            if (manifest.PackageSize <= 0)
                throw new InvalidDataException("The launcher package size must be positive.");
            if (!IsSha256Hex(manifest.PackageSha256))
                throw new InvalidDataException("The launcher package SHA-256 is invalid.");

            if (manifest.Bootstrapper != null)
            {
                var boot = manifest.Bootstrapper;
                if (string.IsNullOrWhiteSpace(boot.Version))
                    throw new InvalidDataException("The bootstrapper update has no version.");
                if (!Uri.TryCreate(baseUri, boot.Url, out var bootUri)
                    || (bootUri.Scheme != Uri.UriSchemeHttp && bootUri.Scheme != Uri.UriSchemeHttps))
                    throw new InvalidDataException("The bootstrapper update URL is invalid.");
                if (boot.Size <= 0)
                    throw new InvalidDataException("The bootstrapper update size must be positive.");
                if (!IsSha256Hex(boot.Sha256))
                    throw new InvalidDataException("The bootstrapper update SHA-256 is invalid.");
                boot.Url = bootUri.AbsoluteUri;
            }

            // Launcher payload files must also be safe relative paths.
            foreach (var file in manifest.Files)
            {
                file.Path = SafeRelativePath.RequireNormalized(file.Path, "launcher manifest");
                var normalized = file.Path.Replace('\\', '/');
                if (normalized.StartsWith("Bootstrapper/", StringComparison.OrdinalIgnoreCase)
                    || normalized.Equals("Vellum.Bootstrapper.exe", StringComparison.OrdinalIgnoreCase))
                {
                    throw new InvalidDataException(
                        "The launcher package cannot replace the running bootstrapper. " +
                        "Use the bootstrapper update section instead.");
                }
            }
            foreach (var deleted in manifest.DeletedFiles)
            {
                var normalized = SafeRelativePath.NormalizeRelative(deleted);
                var replaced = normalized?.Replace('\\', '/') ?? string.Empty;
                if (replaced.StartsWith("Bootstrapper/", StringComparison.OrdinalIgnoreCase)
                    || replaced.Equals("Vellum.Bootstrapper.exe", StringComparison.OrdinalIgnoreCase))
                {
                    throw new InvalidDataException(
                        "The launcher package cannot delete the running bootstrapper.");
                }
            }

            _logger.LogInformation("Loaded signed launcher manifest {Version} from {Url}.",
                manifest.Version, manifestUrl);
            return manifest;
        }

        private void ValidateCommon(List<ManifestFile>? files, List<string>? deletedFiles,
            int schemaVersion, string context)
        {
            if (!UpdateVersion.IsSupportedSchema(schemaVersion))
                throw new InvalidDataException($"Unsupported {context} schema version {schemaVersion}.");

            files ??= new List<ManifestFile>();
            if (files.Count > 1_000_000)
                throw new InvalidDataException($"The {context} contains too many files.");

            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            long total = 0;
            foreach (var file in files)
            {
                if (file == null)
                    throw new InvalidDataException($"The {context} contains a null file entry.");

                var normalized = SafeRelativePath.RequireNormalized(file.Path, context);
                file.Path = normalized;

                if (file.Size < 0)
                    throw new InvalidDataException($"File '{file.Path}' has a negative size.");
                if (file.Size > 512L * 1024 * 1024 * 1024)
                    throw new InvalidDataException($"File '{file.Path}' is unreasonably large.");
                if (!IsSha256Hex(file.Sha256))
                    throw new InvalidDataException($"File '{file.Path}' has an invalid SHA-256.");
                if (!seen.Add(normalized))
                    throw new InvalidDataException($"The {context} lists '{file.Path}' more than once.");

                checked { total += file.Size; }
                if (total > 4L * 1024 * 1024 * 1024 * 1024)
                    throw new InvalidDataException($"The {context} declares an unreasonable total size.");
            }

            if (deletedFiles != null)
            {
                for (var i = 0; i < deletedFiles.Count; i++)
                    deletedFiles[i] = SafeRelativePath.RequireNormalized(deletedFiles[i], context);
            }
        }

        private static bool IsSha256Hex(string? value)
        {
            if (value == null || value.Length != 64)
                return false;
            foreach (var c in value)
            {
                if (!Uri.IsHexDigit(c))
                    return false;
            }
            return true;
        }

        private async Task<SignedDocument> FetchSignedDocumentAsync(string url, CancellationToken ct)
        {
            if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)
                || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
            {
                throw new InvalidDataException("The manifest URL must be an absolute HTTP or HTTPS URL.");
            }

            using var response = await _http.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, ct)
                .ConfigureAwait(false);
            response.EnsureSuccessStatusCode();

            await using var stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
            using var buffer = new MemoryStream();
            var chunk = new byte[64 * 1024];
            while (true)
            {
                var read = await stream.ReadAsync(chunk, 0, chunk.Length, ct).ConfigureAwait(false);
                if (read <= 0)
                    break;
                if (buffer.Length + read > MaxManifestBytes)
                    throw new InvalidDataException("The manifest response is too large.");
                buffer.Write(chunk, 0, read);
            }

            var body = buffer.ToArray();
            string? signature = null;
            string? keyId = null;
            if (response.Headers.TryGetValues(SignatureHeader, out var signatures))
                signature = signatures.FirstOrDefault();
            if (response.Headers.TryGetValues(KeyIdHeader, out var keyIds))
                keyId = keyIds.FirstOrDefault();

            byte[] payload = body;
            if (string.IsNullOrWhiteSpace(signature))
            {
                // Try the envelope form.
                try
                {
                    var envelope = JsonSerializer.Deserialize<SignedManifestEnvelope>(body, _json);
                    if (envelope != null
                        && !string.IsNullOrWhiteSpace(envelope.Signature)
                        && !string.IsNullOrWhiteSpace(envelope.Payload))
                    {
                        payload = Convert.FromBase64String(envelope.Payload);
                        signature = envelope.Signature;
                        keyId = string.IsNullOrWhiteSpace(envelope.KeyId) ? keyId : envelope.KeyId;
                    }
                }
                catch (JsonException)
                {
                    // Plain manifest without an envelope.
                }
                catch (FormatException ex)
                {
                    throw new InvalidDataException("The signed manifest payload is not valid base64.", ex);
                }
            }

            var requireSignature = _settings.Settings.RequireSignedManifests;
            if (string.IsNullOrWhiteSpace(signature))
            {
                if (requireSignature)
                    throw new InvalidDataException(
                        "The manifest is not signed. Vellum refuses unsigned update sources.");
                _logger.LogWarning("Accepting an UNSIGNED manifest from {Url} because signature verification is disabled.", url);
            }
            else
            {
                if (_settings.Settings.RequireSignedManifests && !_signatureVerifier.HasTrustedKeys)
                {
                    throw new InvalidDataException(
                        "The manifest is signed, but no trusted public key is configured. " +
                        "Add the publisher public key in settings before using this update source.");
                }

                if (_signatureVerifier.HasTrustedKeys
                    && !_signatureVerifier.Verify(payload, signature, keyId, out var error))
                {
                    throw new InvalidDataException(error ?? "The manifest signature is invalid.");
                }
            }

            return new SignedDocument(payload, signature, keyId,
                response.RequestMessage?.RequestUri?.AbsoluteUri ?? uri.AbsoluteUri);
        }

        private sealed class SignedDocument
        {
            public SignedDocument(byte[] payload, string? signature, string? keyId, string baseUrl)
            {
                Payload = payload;
                Signature = signature;
                KeyId = keyId;
                BaseUrl = baseUrl;
            }

            public byte[] Payload { get; }
            public string? Signature { get; }
            public string? KeyId { get; }
            public string BaseUrl { get; }
        }
    }
}
