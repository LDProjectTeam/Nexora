using System;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Tasks;
using Nexora.Core.Helpers;
using Nexora.Core.Interfaces;
using Nexora.Core.Models;
using Microsoft.Extensions.Logging;

namespace Nexora.Core.Services
{
    public class SettingsService : ISettingsService
    {
        private readonly ILogger<SettingsService> _logger;
        private static readonly JsonSerializerOptions _jsonOptions = new()
        {
            WriteIndented = true,
            DefaultIgnoreCondition = JsonIgnoreCondition.Never
        };

        public AppSettings Settings { get; private set; } = new();

        private readonly SemaphoreSlim _saveLock = new(1, 1);
        private string? _lastSavedJson;

        /// <summary>
        /// True если settings.json не существовал при загрузке (первый запуск),
        /// или если DefaultInstallPath не был задан.
        /// </summary>
        public bool IsFirstRun { get; private set; }

        public SettingsService(ILogger<SettingsService> logger)
        {
            _logger = logger;
        }

        public void Load()
        {
            PathHelper.EnsureDirectory(PathHelper.AppDataDir);
            var path = PathHelper.SettingsFile;

            if (!File.Exists(path))
            {
                _logger.LogInformation("Settings file not found, using defaults.");
                Settings = new AppSettings();
                IsFirstRun = true;
                Save(); // write defaults
                return;
            }

            try
            {
                ApplyLoadedJson(File.ReadAllText(path));
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to load settings, using defaults.");
                Settings = new AppSettings();
                IsFirstRun = true;
            }
        }

        public async Task LoadAsync()
        {
            PathHelper.EnsureDirectory(PathHelper.AppDataDir);
            var path = PathHelper.SettingsFile;

            if (!File.Exists(path))
            {
                _logger.LogInformation("Settings file not found, using defaults.");
                Settings = new AppSettings();
                IsFirstRun = true;
                await SaveAsync(); // write defaults
                return;
            }

            try
            {
                ApplyLoadedJson(await File.ReadAllTextAsync(path).ConfigureAwait(false));
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to load settings, using defaults.");
                Settings = new AppSettings();
                IsFirstRun = true;
            }
        }

        public void Save()
        {
            var json = JsonSerializer.Serialize(Settings, _jsonOptions);

            // The lock is released by a thread-pool continuation (ConfigureAwait(false)),
            // so blocking the UI thread here cannot deadlock.
            SaveJsonAsync(json).GetAwaiter().GetResult();
        }

        public async Task SaveAsync()
        {
            var json = JsonSerializer.Serialize(Settings, _jsonOptions);
            await SaveJsonAsync(json).ConfigureAwait(false);
        }

        /// <summary>
        /// Writes settings.json atomically (temp file + replace) and keeps one previous
        /// generation in .bak. Previously a crash or a concurrent save could leave a
        /// truncated JSON and silently reset every setting on the next launch.
        /// </summary>
        private async Task SaveJsonAsync(string json)
        {
            PathHelper.EnsureDirectory(PathHelper.AppDataDir);
            await _saveLock.WaitAsync().ConfigureAwait(false);
            try
            {
                var path = PathHelper.SettingsFile;

                // Settings are saved on every toggle; skip a write when nothing changed.
                if (string.Equals(json, _lastSavedJson, StringComparison.Ordinal) && File.Exists(path))
                    return;

                var tempPath = path + ".tmp";
                await File.WriteAllTextAsync(tempPath, json).ConfigureAwait(false);

                if (File.Exists(path))
                {
                    try
                    {
                        File.Replace(tempPath, path, path + ".bak", ignoreMetadataErrors: true);
                    }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or PlatformNotSupportedException)
                    {
                        File.Copy(tempPath, path, overwrite: true);
                        try { File.Delete(tempPath); } catch { }
                    }
                }
                else
                {
                    File.Move(tempPath, path);
                }

                _lastSavedJson = json;
                _logger.LogDebug("Settings saved.");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to save settings.");
            }
            finally
            {
                _saveLock.Release();
            }
        }

        private void ApplyLoadedJson(string json)
        {
            Settings = JsonSerializer.Deserialize<AppSettings>(json, _jsonOptions) ?? new AppSettings();
            if (!Settings.IsLanguageManuallySelected && !string.IsNullOrWhiteSpace(Settings.Language)
                && (Settings.Language is "ru" or "uk" or "en"))
            {
                Settings.IsLanguageManuallySelected = true;
            }

            IsFirstRun = string.IsNullOrWhiteSpace(Settings.DefaultInstallPath);
            _lastSavedJson = json;
            _logger.LogInformation("Settings loaded from {Path}", PathHelper.SettingsFile);
        }

        public void Reset()
        {
            Settings = new AppSettings();
        }
    }
}
