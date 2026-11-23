using System;
using System.IO;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging.Abstractions;
using Vellum.Core.Helpers;
using Vellum.Core.Models;
using Vellum.Core.Services;
using Xunit;

namespace Vellum.Tests
{
    public class SettingsLanguageTests : IDisposable
    {
        private readonly string _settingsFilePath;
        private readonly string? _originalSettingsBackup;

        public SettingsLanguageTests()
        {
            _settingsFilePath = PathHelper.SettingsFile;
            if (File.Exists(_settingsFilePath))
            {
                _originalSettingsBackup = File.ReadAllText(_settingsFilePath);
            }
        }

        public void Dispose()
        {
            try
            {
                if (_originalSettingsBackup != null)
                {
                    File.WriteAllText(_settingsFilePath, _originalSettingsBackup);
                }
                else if (File.Exists(_settingsFilePath))
                {
                    File.Delete(_settingsFilePath);
                }
            }
            catch
            {
                // Best effort cleanup in test dispose
            }
        }

        [Theory]
        [InlineData("ru")]
        [InlineData("uk")]
        [InlineData("en")]
        public void Load_WhenLanguageSetInJsonWithoutManualFlag_SetsManualFlagToTrue(string langCode)
        {
            var json = $"{{\n  \"Language\": \"{langCode}\",\n  \"IsLanguageManuallySelected\": false\n}}";
            PathHelper.EnsureDirectory(PathHelper.AppDataDir);
            File.WriteAllText(_settingsFilePath, json);

            var service = new SettingsService(NullLogger<SettingsService>.Instance);
            service.Load();

            Assert.Equal(langCode, service.Settings.Language);
            Assert.True(service.Settings.IsLanguageManuallySelected);
        }

        [Theory]
        [InlineData("ru")]
        [InlineData("uk")]
        [InlineData("en")]
        public async Task LoadAsync_WhenLanguageSetInJsonWithoutManualFlag_SetsManualFlagToTrue(string langCode)
        {
            var json = $"{{\n  \"Language\": \"{langCode}\",\n  \"IsLanguageManuallySelected\": false\n}}";
            PathHelper.EnsureDirectory(PathHelper.AppDataDir);
            await File.WriteAllTextAsync(_settingsFilePath, json);

            var service = new SettingsService(NullLogger<SettingsService>.Instance);
            await service.LoadAsync();

            Assert.Equal(langCode, service.Settings.Language);
            Assert.True(service.Settings.IsLanguageManuallySelected);
        }

        [Fact]
        public void Save_ThenLoad_PreservesLanguageAndManualFlag()
        {
            var service = new SettingsService(NullLogger<SettingsService>.Instance);
            service.Settings.Language = "ru";
            service.Settings.IsLanguageManuallySelected = true;
            service.Save();

            var loadedService = new SettingsService(NullLogger<SettingsService>.Instance);
            loadedService.Load();

            Assert.Equal("ru", loadedService.Settings.Language);
            Assert.True(loadedService.Settings.IsLanguageManuallySelected);
        }
        [Fact]
        public async Task SaveAsync_WritesAtomically_LeavesNoTempFile_AndKeepsBackup()
        {
            var backupPath = _settingsFilePath + ".bak";
            if (File.Exists(backupPath))
                File.Delete(backupPath);

            var service = new SettingsService(NullLogger<SettingsService>.Instance);
            service.Settings.Language = "en";
            service.Settings.IsLanguageManuallySelected = true;
            await service.SaveAsync();

            Assert.True(File.Exists(_settingsFilePath));
            Assert.False(File.Exists(_settingsFilePath + ".tmp"));

            service.Settings.Language = "uk";
            await service.SaveAsync();

            Assert.True(File.Exists(backupPath), "The previous settings generation should be kept in .bak.");

            using var document = JsonDocument.Parse(await File.ReadAllTextAsync(_settingsFilePath));
            Assert.Equal("uk", document.RootElement.GetProperty("Language").GetString());
        }
    }
}
