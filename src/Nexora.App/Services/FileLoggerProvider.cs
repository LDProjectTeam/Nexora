using System;
using Microsoft.Extensions.Logging;

namespace Nexora.App.Services
{
    /// <summary>
    /// Направляет логи Microsoft.Extensions.Logging (Core-сервисы и сам App)
    /// в <see cref="AppLog"/>, чтобы в релизе они не терялись.
    /// Debug/Trace отфильтровываются: в файл идут только Information и выше.
    /// </summary>
    internal sealed class FileLoggerProvider : ILoggerProvider
    {
        public ILogger CreateLogger(string categoryName) => new FileLogger(categoryName);

        public void Dispose()
        {
            AppLog.Flush();
        }

        private sealed class FileLogger : ILogger
        {
            private readonly string _category;

            public FileLogger(string categoryName)
            {
                var name = categoryName ?? string.Empty;
                var lastDot = name.LastIndexOf('.');
                _category = lastDot >= 0 && lastDot < name.Length - 1 ? name[(lastDot + 1)..] : name;
            }

            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

            public bool IsEnabled(LogLevel logLevel) => logLevel >= LogLevel.Information;

            public void Log<TState>(
                LogLevel logLevel,
                EventId eventId,
                TState state,
                Exception? exception,
                Func<TState, Exception?, string> formatter)
            {
                if (!IsEnabled(logLevel) || formatter == null)
                    return;

                var message = formatter(state, exception);
                switch (logLevel)
                {
                    case LogLevel.Warning:
                        AppLog.Warn(_category, message, exception);
                        break;
                    case LogLevel.Error:
                        AppLog.Error(_category, message, exception);
                        break;
                    case LogLevel.Critical:
                        AppLog.Fatal(_category, message, exception);
                        break;
                    default:
                        AppLog.Info(_category, message);
                        break;
                }
            }
        }
    }
}
