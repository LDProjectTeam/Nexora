using System;
using System.Globalization;
using System.Linq;
using System.Windows;

namespace Vellum.App.Services
{
    public static class LocalizationManager
    {
        private static string _currentLanguage = string.Empty;

        public static event EventHandler? LanguageChanged;

        public static string CurrentLanguage => _currentLanguage;

        public static string GetSystemLanguage()
        {
            var language = CultureInfo.CurrentUICulture.TwoLetterISOLanguageName.ToLowerInvariant();
            return language switch
            {
                "ru" => "ru",
                "uk" or "ua" => "uk",
                _ => "en"
            };
        }

        public static void SetLanguage(string? language)
        {
            var code = language?.ToLowerInvariant() switch
            {
                "uk" or "ua" => "uk",
                "en" => "en",
                _ => "ru"
            };

            var changed = !string.Equals(_currentLanguage, code, StringComparison.Ordinal);
            _currentLanguage = code;

            var dictionaries = Application.Current?.Resources.MergedDictionaries;
            if (dictionaries != null)
            {
                var currentDictionary = dictionaries.FirstOrDefault(dictionary =>
                    dictionary.Source?.OriginalString.Contains("Localization/Strings.", StringComparison.OrdinalIgnoreCase) == true);
                var currentSource = currentDictionary?.Source?.OriginalString ?? string.Empty;
                var expectedSuffix = $"Strings.{code}.xaml";
                if (!currentSource.EndsWith(expectedSuffix, StringComparison.OrdinalIgnoreCase))
                {
                    var replacement = new ResourceDictionary
                    {
                        Source = new Uri($"/Vellum;component/Localization/{expectedSuffix}", UriKind.Relative)
                    };

                    if (currentDictionary != null)
                    {
                        var index = dictionaries.IndexOf(currentDictionary);
                        dictionaries[index] = replacement;
                    }
                    else
                    {
                        dictionaries.Add(replacement);
                    }
                }
            }

            if (changed)
            {
                var culture = code switch
                {
                    "uk" => CultureInfo.GetCultureInfo("uk-UA"),
                    "en" => CultureInfo.GetCultureInfo("en-US"),
                    _ => CultureInfo.GetCultureInfo("ru-RU")
                };
                CultureInfo.CurrentCulture = culture;
                CultureInfo.CurrentUICulture = culture;
                LanguageChanged?.Invoke(null, EventArgs.Empty);
            }
        }

        public static string GetString(string key, params object[] arguments)
        {
            var value = Application.Current?.TryFindResource(key)?.ToString() ?? key;
            if (arguments.Length == 0)
                return value;

            try
            {
                return string.Format(CultureInfo.CurrentCulture, value, arguments);
            }
            catch (FormatException)
            {
                // A translation with a mismatched placeholder count must not take down
                // the caller (an installer consent dialog used to disappear entirely).
                AppLog.Warn("Localization", $"Resource '{key}' has an invalid format string.");
                return value;
            }
        }

        /// <summary>
        /// Returns the localized value, or <paramref name="fallback"/> when the key is
        /// missing. Use this for text that is later formatted separately: passing a
        /// fallback through <see cref="GetString(string, object[])"/> would treat it as
        /// a format argument and fail when the resource contains placeholders.
        /// </summary>
        public static string GetStringOrFallback(string key, string fallback)
        {
            var value = Application.Current?.TryFindResource(key)?.ToString();
            return string.IsNullOrEmpty(value) || string.Equals(value, key, StringComparison.Ordinal)
                ? fallback
                : value;
        }
    }
}
