using System;
using System.IO;
using System.Threading;
using System.Windows;

namespace Nexora.App.Services
{
    /// <summary>
    /// Единая точка для неожиданных ошибок: пишет их в лог и один раз показывает
    /// пользователю диалог с текстом и путём к лог-файлу.
    /// Раньше такие исключения молча проглатывались (DispatcherUnhandledException
    /// ставил Handled = true), и поломка выглядела как «кнопка не работает».
    /// </summary>
    internal static class ErrorReporter
    {
        private static int _dialogOpen;
        private static long _lastShownTicks;

        /// <summary>Записать ошибку в лог и показать диалог с общим текстом.</summary>
        public static void Report(string source, Exception exception)
        {
            string message;
            try
            {
                message = string.Format(
                    LocalizationManager.GetString("UnexpectedErrorFormat", "The operation failed: {0}"),
                    exception.Message);
            }
            catch
            {
                message = exception.Message;
            }

            Report(source, message, exception);
        }

        /// <summary>Записать ошибку в лог и показать диалог пользователю.</summary>
        public static void Report(string source, string userMessage, Exception? exception)
        {
            AppLog.Error(source, userMessage, exception);
            ShowUser(userMessage);
        }

        /// <summary>Показать диалог без повторной записи в лог.</summary>
        public static void ShowUser(string userMessage)
        {
            var app = Application.Current;
            var dispatcher = app?.Dispatcher;
            if (dispatcher == null)
                return;

            // Пока открыт один диалог, остальные ошибки только логируются.
            if (Interlocked.CompareExchange(ref _dialogOpen, 1, 0) != 0)
                return;

            void Show()
            {
                try
                {
                    // Не даём пачке одинаковых ошибок открыть десяток окон подряд.
                    if (Environment.TickCount64 - Interlocked.Read(ref _lastShownTicks) < 1500)
                        return;

                    Interlocked.Exchange(ref _lastShownTicks, Environment.TickCount64);

                    var logPath = AppLog.CurrentFile;
                    var text = string.IsNullOrWhiteSpace(logPath) || !File.Exists(logPath)
                        ? userMessage
                        : userMessage + Environment.NewLine + Environment.NewLine +
                          string.Format(
                              LocalizationManager.GetString("ErrorLogLocationFormat", "Details were written to: {0}"),
                              logPath);

                    MessageBox.Show(
                        text,
                        LocalizationManager.GetString("CommonError", "Error"),
                        MessageBoxButton.OK,
                        MessageBoxImage.Error);
                }
                catch (Exception ex)
                {
                    AppLog.Error("ErrorReporter", "Could not show the error dialog.", ex);
                }
                finally
                {
                    Volatile.Write(ref _dialogOpen, 0);
                }
            }

            if (dispatcher.CheckAccess())
                Show();
            else
                dispatcher.BeginInvoke(new Action(Show));
        }
    }
}
