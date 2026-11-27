using System;
using System.Collections.Generic;
using System.Globalization;

namespace Vellum.Bootstrapper
{
    /// <summary>Small self-contained localization for the pre-launch window.</summary>
    internal static class BootstrapperText
    {
        private static string _language = "en";

        public static void SetLanguage(string? language)
        {
            _language = language is "ru" or "uk" or "en" ? language : "en";
        }

        public static string CurrentLanguage => _language;

        public static string Get(string key, params object[] args)
        {
            var table = _language switch
            {
                "ru" => Russian,
                "uk" => Ukrainian,
                _ => English
            };

            var text = table.TryGetValue(key, out var value) ? value : key;
            return args.Length > 0 ? string.Format(CultureInfo.CurrentCulture, text, args) : text;
        }

        private static readonly Dictionary<string, string> English = new()
        {
            ["Title"] = "Vellum",
            ["Subtitle"] = "Preparing your launcher",
            ["CheckingUpdates"] = "Checking for updates...",
            ["NoUpdates"] = "No updates available.",
            ["CheckingIntegrity"] = "Checking integrity...",
            ["AllChecksPassed"] = "All checks passed.",
            ["DownloadingUpdate"] = "Downloading update…",
            ["InstallingUpdate"] = "Installing update…",
            ["VerifyingFiles"] = "Verifying files…",
            ["StartingLauncher"] = "Starting Vellum...",
            ["UpdatingBootstrapper"] = "Updating the bootstrapper…",
            ["UpdateFailed"] = "Update failed",
            ["Retry"] = "Retry",
            ["StartPrevious"] = "Start previous version",
            ["StartAnyway"] = "Start Vellum",
            ["Close"] = "Close",
            ["Exit"] = "Close",
            ["DownloadedFormat"] = "{0} of {1}",
            ["SpeedFormat"] = "{0}/s",
            ["RemainingFormat"] = "{0} left"
        };

        private static readonly Dictionary<string, string> Russian = new()
        {
            ["Title"] = "Vellum",
            ["Subtitle"] = "Подготовка лаунчера",
            ["CheckingUpdates"] = "Проверка обновлений...",
            ["NoUpdates"] = "Обновлений нет.",
            ["CheckingIntegrity"] = "Проверка целостности...",
            ["AllChecksPassed"] = "Все проверки пройдены.",
            ["DownloadingUpdate"] = "Загрузка обновления…",
            ["InstallingUpdate"] = "Установка обновления…",
            ["VerifyingFiles"] = "Проверка файлов…",
            ["StartingLauncher"] = "Запуск Vellum...",
            ["UpdatingBootstrapper"] = "Обновление загрузчика…",
            ["UpdateFailed"] = "Не удалось обновить",
            ["Retry"] = "Повторить",
            ["StartPrevious"] = "Запустить предыдущую версию",
            ["StartAnyway"] = "Запустить Vellum",
            ["Close"] = "Закрыть",
            ["Exit"] = "Закрыть",
            ["DownloadedFormat"] = "{0} из {1}",
            ["SpeedFormat"] = "{0}/с",
            ["RemainingFormat"] = "осталось {0}"
        };

        private static readonly Dictionary<string, string> Ukrainian = new()
        {
            ["Title"] = "Vellum",
            ["Subtitle"] = "Підготовка лаунчера",
            ["CheckingUpdates"] = "Перевірка оновлень...",
            ["NoUpdates"] = "Оновлень немає.",
            ["CheckingIntegrity"] = "Перевірка цілісності...",
            ["AllChecksPassed"] = "Усі перевірки пройдено.",
            ["DownloadingUpdate"] = "Завантаження оновлення…",
            ["InstallingUpdate"] = "Встановлення оновлення…",
            ["VerifyingFiles"] = "Перевірка файлів…",
            ["StartingLauncher"] = "Запуск Vellum...",
            ["UpdatingBootstrapper"] = "Оновлення завантажувача…",
            ["UpdateFailed"] = "Не вдалося оновити",
            ["Retry"] = "Повторити",
            ["StartPrevious"] = "Запустити попередню версію",
            ["StartAnyway"] = "Запустити Vellum",
            ["Close"] = "Закрити",
            ["Exit"] = "Закрити",
            ["DownloadedFormat"] = "{0} з {1}",
            ["SpeedFormat"] = "{0}/с",
            ["RemainingFormat"] = "залишилось {0}"
        };
    }
}
