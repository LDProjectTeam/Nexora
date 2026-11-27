using System;
using System.IO;
using System.Threading;

namespace Vellum.Core.Services
{
    public sealed class BootstrapperFallbackAttempt
    {
        public bool Started { get; init; }
        public string? HealthFile { get; init; }
        public Func<bool>? HasExited { get; init; }
        public Action? Kill { get; init; }
        public string? ErrorMessage { get; init; }
    }

    public enum BootstrapperFallbackChoice
    {
        Retry,
        StartDirectly,
        Close
    }

    public enum BootstrapperFallbackOutcome
    {
        ExitToBootstrapper,
        StartDirectly,
        Close
    }

    /// <summary>
    /// Handles the direct-launch fallback. A health timeout never starts the launcher
    /// automatically: the user must explicitly choose Retry, Start directly or Close.
    /// </summary>
    public static class BootstrapperFallbackCoordinator
    {
        public static readonly TimeSpan DefaultHealthTimeout = TimeSpan.FromSeconds(20);

        public static BootstrapperFallbackOutcome Run(
            Func<BootstrapperFallbackAttempt> startBootstrapper,
            TimeSpan healthTimeout,
            Func<string, BootstrapperFallbackChoice> askUser,
            CancellationToken ct = default)
        {
            while (true)
            {
                ct.ThrowIfCancellationRequested();

                var attempt = startBootstrapper();
                if (attempt.Started
                    && !string.IsNullOrWhiteSpace(attempt.HealthFile)
                    && WaitForHealth(attempt, healthTimeout, ct))
                {
                    return BootstrapperFallbackOutcome.ExitToBootstrapper;
                }

                try { attempt.Kill?.Invoke(); } catch { }

                var message = string.IsNullOrWhiteSpace(attempt.ErrorMessage)
                    ? "Vellum Bootstrapper did not start correctly within the required time."
                    : attempt.ErrorMessage;
                var choice = askUser(message);
                switch (choice)
                {
                    case BootstrapperFallbackChoice.Retry:
                        continue;
                    case BootstrapperFallbackChoice.StartDirectly:
                        return BootstrapperFallbackOutcome.StartDirectly;
                    default:
                        return BootstrapperFallbackOutcome.Close;
                }
            }
        }

        private static bool WaitForHealth(BootstrapperFallbackAttempt attempt,
            TimeSpan timeout, CancellationToken ct)
        {
            var deadline = DateTime.UtcNow + timeout;
            while (DateTime.UtcNow < deadline)
            {
                ct.ThrowIfCancellationRequested();
                if (File.Exists(attempt.HealthFile))
                {
                    try { File.Delete(attempt.HealthFile!); } catch { }
                    return true;
                }
                if (attempt.HasExited?.Invoke() == true)
                    return false;
                Thread.Sleep(100);
            }
            return false;
        }
    }
}
