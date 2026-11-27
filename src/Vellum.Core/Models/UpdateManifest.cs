using System;
using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace Vellum.Core.Models
{
    /// <summary>
    /// Remote file manifest for a game build. The manifest is the source of truth for
    /// what a game version consists of; the launcher never deletes or downloads files
    /// that are not described here (plus files tracked by the previous manifest).
    /// </summary>
    public class UpdateManifest
    {
        /// <summary>Manifest schema. Only <see cref="CurrentSchemaVersion"/> is accepted.</summary>
        public const int CurrentSchemaVersion = 1;

        public int SchemaVersion { get; set; } = CurrentSchemaVersion;

        public string GameId { get; set; } = string.Empty;

        public string Version { get; set; } = string.Empty;

        /// <summary>Opaque build identifier (Steam-style). A changed build with the same version is an update.</summary>
        public string BuildId { get; set; } = string.Empty;

        public DateTimeOffset? ReleaseDate { get; set; }

        /// <summary>Update channel this manifest belongs to: stable or beta.</summary>
        public string Channel { get; set; } = "stable";

        public List<ManifestFile> Files { get; set; } = new();

        /// <summary>Files removed in this build compared with older builds.</summary>
        public List<string> DeletedFiles { get; set; } = new();

        public string ReleaseNotes { get; set; } = string.Empty;
    }

    /// <summary>One file described by a manifest. Paths are always relative to the game install root.</summary>
    public class ManifestFile
    {
        public string Path { get; set; } = string.Empty;

        public long Size { get; set; }

        /// <summary>Lower-case or upper-case hex SHA-256 of the file contents.</summary>
        public string Sha256 { get; set; } = string.Empty;

        /// <summary>Absolute URL for downloading this file, or a path relative to the manifest URL.</summary>
        public string Url { get; set; } = string.Empty;

        [JsonIgnore]
        public string FileName => System.IO.Path.GetFileName(Path);
    }

    /// <summary>
    /// Signed document envelope. The signature is computed over the exact UTF-8 bytes of
    /// the base64-decoded <see cref="Payload"/>, using RSA PKCS#1 v1.5 + SHA-256.
    /// </summary>
    public class SignedManifestEnvelope
    {
        public int SchemaVersion { get; set; } = 1;
        public string KeyId { get; set; } = string.Empty;
        public string Signature { get; set; } = string.Empty;
        public string Payload { get; set; } = string.Empty;
    }

    /// <summary>
    /// Launcher self-update manifest. The package is a zip archive containing the files
    /// listed in <see cref="Files"/>; only those files are ever touched on disk.
    /// </summary>
    public class LauncherUpdateManifest
    {
        public const int CurrentSchemaVersion = 1;

        public int SchemaVersion { get; set; } = CurrentSchemaVersion;

        public string AppId { get; set; } = "vellum-launcher";

        public string Version { get; set; } = string.Empty;

        public string Channel { get; set; } = "stable";

        public string PackageUrl { get; set; } = string.Empty;

        public string PackageSha256 { get; set; } = string.Empty;

        public long PackageSize { get; set; }

        public string EntryPoint { get; set; } = "Vellum.exe";

        public List<ManifestFile> Files { get; set; } = new();

        public List<string> DeletedFiles { get; set; } = new();

        public string ReleaseNotes { get; set; } = string.Empty;

        public DateTimeOffset? ReleaseDate { get; set; }

        /// <summary>Optional standalone bootstrapper update delivered with the launcher manifest.</summary>
        public BootstrapperUpdateInfo? Bootstrapper { get; set; }
    }

    /// <summary>Executable that runs before the launcher and can replace itself safely.</summary>
    public class BootstrapperUpdateInfo
    {
        public string Version { get; set; } = string.Empty;
        public string Url { get; set; } = string.Empty;
        public long Size { get; set; }
        public string Sha256 { get; set; } = string.Empty;
    }

    /// <summary>Persisted request consumed by the standalone bootstrapper (and by tests).</summary>
    public class LauncherApplyRequest
    {
        public int SchemaVersion { get; set; } = 1;

        /// <summary>Process that must exit before files can be replaced. 0 means no wait.</summary>
        public int WaitProcessId { get; set; }

        public string InstallDir { get; set; } = string.Empty;

        public string StagingDir { get; set; } = string.Empty;

        public string BackupDir { get; set; } = string.Empty;

        public string EntryPoint { get; set; } = "Vellum.exe";

        public string Version { get; set; } = string.Empty;

        public string PreviousVersion { get; set; } = string.Empty;

        public bool Relaunch { get; set; } = true;

        public List<ManifestFile> Files { get; set; } = new();

        public List<string> DeletedFiles { get; set; } = new();

        /// <summary>Text written to the launcher on the next start (success or rollback).</summary>
        public string ResultFile { get; set; } = string.Empty;
    }

    public class LauncherApplyResult
    {
        public bool Success { get; set; }
        public bool RolledBack { get; set; }
        public string? ErrorMessage { get; set; }
        public string? Version { get; set; }

        public static LauncherApplyResult Ok(string version) => new() { Success = true, Version = version };
        public static LauncherApplyResult Fail(string message, bool rolledBack = false)
            => new() { Success = false, ErrorMessage = message, RolledBack = rolledBack };
    }

    public class LauncherUpdateResultRecord
    {
        public string Version { get; set; } = string.Empty;
        public bool Success { get; set; }
        public bool RolledBack { get; set; }
        public string? ErrorMessage { get; set; }
        public DateTimeOffset TimestampUtc { get; set; } = DateTimeOffset.UtcNow;
    }
}
