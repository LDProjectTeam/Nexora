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
