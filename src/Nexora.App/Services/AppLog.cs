using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using Nexora.Core.Helpers;

namespace Nexora.App.Services
{
    /// <summary>
    /// Минимальный, всегда доступный файловый лог. Работает даже до того, как
    /// построен DI-контейнер, поэтому глобальные обработчики необработанных
    /// исключений могут писать сюда без зависимостей.
    /// Файл: %APPDATA%\Nexora\logs\nexora-yyyy-MM-dd.log (хранится 10 последних).
    /// </summary>
    internal static class AppLog
    {
        private static readonly object Gate = new();
        private static StreamWriter? _writer;
        private static DateTime _writerDay;
        private static string? _currentFile;
        private static bool _writeFaulted;

        /// <summary>Путь к текущему лог-файлу (null, если писать некуда).</summary>
        public static string? CurrentFile
        {
            get
            {
                lock (Gate)
                {
                    EnsureWriter();
                    return _currentFile;
                }
            }
        }

        public static void Debug(string source, string message)
        {
            System.Diagnostics.Debug.WriteLine($"[DBG] {source}: {message}");
        }

        public static void Info(string source, string message) => Write("INF", source, message, null);
        public static void Warn(string source, string message, Exception? exception = null) => Write("WRN", source, message, exception);
        public static void Error(string source, string message, Exception? exception = null) => Write("ERR", source, message, exception);
        public static void Fatal(string source, string message, Exception? exception = null) => Write("FTL", source, message, exception);

        public static void Flush()
        {
            lock (Gate)
            {
                try { _writer?.Flush(); }
                catch { /* nothing we can do */ }
            }
        }

        private static void Write(string level, string source, string message, Exception? exception)
        {
            System.Diagnostics.Debug.WriteLine($"[{level}] {source}: {message}");

            try
            {
                lock (Gate)
                {
                    if (!EnsureWriter() || _writer == null)
                        return;

                    _writer.Write(DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff"));
                    _writer.Write(" [");
                    _writer.Write(level);
                    _writer.Write("] ");
                    _writer.Write(source);
                    _writer.Write(": ");
                    _writer.WriteLine((message ?? string.Empty).Replace('\r', ' ').Replace('\n', ' '));

                    if (exception != null)
                        _writer.WriteLine(exception.ToString());
                }
            }
            catch
            {
                // Диск отвалился/нет прав: отключаемся, чтобы логирование само не
                // стало источником исключений. В Debug-вывод сообщение уже ушло.
                _writeFaulted = true;
            }
        }

        private static bool EnsureWriter()
        {
            if (_writeFaulted)
                return false;

            var today = DateTime.Today;
            if (_writer != null && _writerDay == today)
                return true;

            try
            {
                _writer?.Dispose();
                _writer = null;

                Directory.CreateDirectory(PathHelper.LogsDir);
                PruneOldLogs();

                _currentFile = Path.Combine(PathHelper.LogsDir, $"nexora-{today:yyyy-MM-dd}.log");
                var stream = new FileStream(_currentFile, FileMode.Append, FileAccess.Write, FileShare.ReadWrite);
                _writer = new StreamWriter(stream, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false))
                {
                    AutoFlush = true
                };
                _writerDay = today;
                return true;
            }
            catch
            {
                _writer = null;
                _currentFile = null;
                return false;
            }
        }

        private static void PruneOldLogs()
        {
            try
            {
                var stale = new DirectoryInfo(PathHelper.LogsDir)
                    .GetFiles("nexora-*.log")
                    .OrderByDescending(file => file.LastWriteTimeUtc)
                    .Skip(10);

                foreach (var file in stale)
                {
                    try { file.Delete(); }
                    catch { /* keep the newest logs even if one cannot be removed */ }
                }
            }
            catch
            {
                // Retention is best-effort.
            }
        }
    }
}
