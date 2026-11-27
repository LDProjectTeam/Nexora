using System.Collections.Generic;

namespace Vellum.Core.Models
{
    /// <summary>
    /// Application-level settings. Persisted as settings.json in AppData.
    /// </summary>
    public class AppSettings
    {
        // --- Paths ---
        /// <summary>
        /// Корневая папка библиотеки игр. Пустая строка означает первый запуск —
        /// лаунчер покажет wizard и запишет выбранный путь.
        /// </summary>
        public string DefaultInstallPath { get; set; } = string.Empty;

        // --- Locale & Theme ---
        public string Language { get; set; } = string.Empty;
        public bool IsLanguageManuallySelected { get; set; }

        /// <summary>Ключ цвета акцента: White | Emerald | Blue | Amber | Violet | Rose.</summary>
        public string AccentColor { get; set; } = "White";

        // --- Download ---
        /// <summary>Max download speed in bytes/sec. 0 = unlimited.</summary>
        public long MaxDownloadSpeedBps { get; set; } = 0;

        /// <summary>Max upload speed for torrent in bytes/sec. 0 = unlimited.</summary>
        public long MaxUploadSpeedBps { get; set; } = 0;

        public int MaxConcurrentDownloads { get; set; } = 1;

        /// <summary>Torrent: allow seeding after download completes.</summary>
        public bool AllowSeeding { get; set; } = false;

        // --- Behaviour ---
        public bool AutoCheckUpdates { get; set; } = true;

        /// <summary>Check installed games for newer builds every N minutes while the launcher is open. 0 disables the periodic check.</summary>
        public int UpdateCheckIntervalMinutes { get; set; } = 30;

        // --- Game updates (manifest based) ---
        /// <summary>
        /// Base URL of the manifest server. The launcher requests
        /// {base}/{gameId}/{channel}/manifest.json when a game has no per-game manifest URL.
        /// Leave empty when update sources are not configured.
        /// </summary>
        public string UpdateManifestBaseUrl { get; set; } = string.Empty;

        /// <summary>stable or beta. Sent to the manifest server and stored with the game.</summary>
        public string UpdateChannel { get; set; } = "stable";

        /// <summary>Automatically download and install game updates when an update is detected.</summary>
        public bool AutoUpdateGames { get; set; } = false;

        /// <summary>ask | block | allow. What to do when an update is pending and the user launches the game.</summary>
        public string UpdateLaunchBehavior { get; set; } = "ask";

        /// <summary>Maximum number of files downloaded in parallel for a single update.</summary>
        public int MaxConcurrentUpdateFiles { get; set; } = 3;

        /// <summary>
        /// PEM encoded RSA public keys trusted to sign game and launcher manifests.
        /// When this list is empty manifests cannot be trusted and update checks fail closed.
        /// </summary>
        public List<string> UpdateTrustedPublicKeys { get; set; } = new();

        /// <summary>
        /// Signature verification is mandatory by default. Turning this off is intended for
        /// local development only; production sources must always be signed.
        /// </summary>
        public bool RequireSignedManifests { get; set; } = true;

        // --- Launcher self-update ---
        /// <summary>URL of the signed launcher update manifest. Empty disables launcher self-update.</summary>
        public string LauncherUpdateManifestUrl { get; set; } = string.Empty;

        /// <summary>Automatically check for a newer launcher build.</summary>
        public bool AutoCheckLauncherUpdates { get; set; } = true;

        /// <summary>Automatically download launcher updates in the background.</summary>
        public bool AutoDownloadLauncherUpdates { get; set; } = true;

        public bool LaunchWithWindows { get; set; } = false;
        public bool MinimizeToTray { get; set; } = true;
        public bool MinimizeOnGameLaunch { get; set; } = true;
        public bool RestoreOnGameExit { get; set; } = true;

        /// <summary>Разрешить запуск нескольких игр одновременно.</summary>
        public bool AllowMultipleGames { get; set; } = false;

        /// <summary>
        /// Если true — при каждой установке открывается диалог выбора папки.
        /// Если false — игры ставятся в DefaultInstallPath без вопросов.
        /// </summary>
        public bool AskInstallFolderOnEachInstall { get; set; } = false;

        // --- Catalog & Sources ---
        /// <summary>
        /// Additional catalog URLs. The launcher loads the built-in games.json first,
        /// then merges any catalogs from these URLs.
        /// </summary>
        public List<string> AdditionalCatalogUrls { get; set; } = new();


        /// <summary>
        /// Source priority mapping (lower number = higher priority).
        /// Default: "catalog": 0, "rutor": 1.
        /// </summary>
        public Dictionary<string, int> SourcePriorities { get; set; } = new()
        {
            { "catalog", 0 },
            { "rutor", 1 }
        };

        /// <summary>Always prefer portable releases when available.</summary>
        public bool PreferPortable { get; set; } = true;
    }
}
