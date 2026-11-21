using System;

namespace Nexora.Core.Services
{
    /// <summary>
    /// Minimal localization bridge so core services can produce localized user-facing
    /// text. The WPF app registers its LocalizationManager as the resolver on startup;
    /// until then (and in unit tests) the provided fallback is returned unchanged.
    /// </summary>
    public static class Localizer
    {
        private static Func<string, string>? _resolver;

        public static void SetResolver(Func<string, string>? resolver) => _resolver = resolver;

        /// <summary>Returns the localized string for <paramref name="key"/>, or <paramref name="fallback"/>.</summary>
        public static string T(string key, string fallback)
        {
            if (_resolver == null)
                return fallback;

            string? value;
            try
            {
                value = _resolver(key);
            }
            catch
            {
                value = null;
            }

            return string.IsNullOrEmpty(value) || string.Equals(value, key, StringComparison.Ordinal)
                ? fallback
                : value;
        }
    }
}
