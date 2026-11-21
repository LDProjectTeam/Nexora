using System;
using Nexora.Core.Models;

namespace Nexora.Core.Services
{
    /// <summary>
    /// Точка подключения UI для подтверждения запуска установщиков. Core не знает
    /// про WPF: приложение регистрирует обработчик, который показывает диалог.
    /// Без обработчика (тесты, консольный сценарий) поведение остаётся прежним.
    /// </summary>
    public static class InstallerConsent
    {
        private static Func<InstallerRunRequest, bool>? _handler;

        public static void SetHandler(Func<InstallerRunRequest, bool>? handler) => _handler = handler;

        /// <summary>True — запускать; false — пользователь отказался.</summary>
        public static bool Request(InstallerRunRequest request)
        {
            var handler = _handler;
            if (handler == null)
                return true;

            try
            {
                return handler(request);
            }
            catch
            {
                // Если диалог не смог показаться, безопаснее не запускать установщик.
                return false;
            }
        }
    }
}
