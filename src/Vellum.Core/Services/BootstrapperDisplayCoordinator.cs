using System;
using System.Threading;
using System.Threading.Tasks;
using Vellum.Core.Models;

namespace Vellum.Core.Services
{
    public enum BootstrapperPresentationOutcome
    {
        Running,
        Succeeded,
        Failed,
        Cancelled
    }

    /// <summary>
    /// Owns the presentation lifecycle of the bootstrapper window. The update and
    /// integrity workflow runs independently; this type only decides which progress
    /// events may reach the UI and how long a successful window stays visible.
    /// </summary>
    public sealed class BootstrapperDisplayCoordinator
    {
        public static readonly TimeSpan DefaultMinimumDisplayTime = TimeSpan.FromMilliseconds(1200);

        private readonly object _sync = new();
        private readonly TimeSpan _minimumDisplayTime;
        private readonly TimeProvider _timeProvider;
        private readonly Func<TimeSpan, CancellationToken, Task> _delay;

        private long? _uiReadyTimestamp;
        private long _completedTimestamp;
        private long _attempt;
        private BootstrapperPresentationOutcome _outcome = BootstrapperPresentationOutcome.Running;
        private BootstrapperStage _finalStage;
        private BootstrapperProgress? _lastProgress;
        private BootstrapperRunResult? _result;
        private string? _errorMessage;
        private bool _canRetry;
        private bool _canLaunchCurrent;

        public BootstrapperDisplayCoordinator(
            TimeSpan? minimumDisplayTime = null,
            TimeProvider? timeProvider = null,
            Func<TimeSpan, CancellationToken, Task>? delay = null)
        {
            _minimumDisplayTime = minimumDisplayTime ?? DefaultMinimumDisplayTime;
            if (_minimumDisplayTime < TimeSpan.Zero)
                throw new ArgumentOutOfRangeException(nameof(minimumDisplayTime));
            _timeProvider = timeProvider ?? TimeProvider.System;
            _delay = delay ?? ((duration, ct) => Task.Delay(duration, ct));
        }

        public TimeSpan MinimumDisplayTime => _minimumDisplayTime;

        public BootstrapperPresentationOutcome Outcome
        {
            get { lock (_sync) return _outcome; }
        }

        public bool IsFinalized
        {
            get { lock (_sync) return _outcome != BootstrapperPresentationOutcome.Running; }
        }

        public BootstrapperStage FinalStage
        {
            get { lock (_sync) return _finalStage; }
        }

        public BootstrapperProgress? LastProgress
        {
            get { lock (_sync) return _lastProgress; }
        }

        public BootstrapperRunResult? Result
        {
            get { lock (_sync) return _result; }
        }

        public string? ErrorMessage
        {
            get { lock (_sync) return _errorMessage; }
        }

        public bool CanRetry
        {
            get { lock (_sync) return _canRetry; }
        }

        public bool CanLaunchCurrent
        {
            get { lock (_sync) return _canLaunchCurrent; }
        }

        /// <summary>Starts the minimum-display clock when the window is ready.</summary>
        public void MarkUiReady()
        {
            lock (_sync)
            {
                if (_outcome != BootstrapperPresentationOutcome.Running)
                    return;
                _uiReadyTimestamp ??= _timeProvider.GetTimestamp();
            }
        }

        /// <summary>
        /// Resets a finished attempt for Retry without restarting the display clock.
        /// Returns the new attempt id; progress from older attempts is rejected.
        /// </summary>
        public long BeginAttempt()
        {
            lock (_sync)
            {
                _attempt++;
                _outcome = BootstrapperPresentationOutcome.Running;
                _finalStage = default;
                _lastProgress = null;
                _result = null;
                _errorMessage = null;
                _canRetry = false;
                _canLaunchCurrent = false;
                _completedTimestamp = 0;
                return _attempt;
            }
        }

        /// <summary>
        /// Accepts a progress event only while the attempt is running. Events queued
        /// after a final result are discarded instead of overwriting the final status.
        /// </summary>
        public bool TryApplyProgress(BootstrapperProgress progress)
        {
            ArgumentNullException.ThrowIfNull(progress);
            lock (_sync)
                return TryApplyProgressLocked(progress, _attempt);
        }

        /// <summary>
        /// Accepts progress only from the current attempt. A queued event from a
        /// previous Retry cannot overwrite the new attempt's status.
        /// </summary>
        public bool TryApplyProgress(BootstrapperProgress progress, long attemptId)
        {
            ArgumentNullException.ThrowIfNull(progress);
            lock (_sync)
                return TryApplyProgressLocked(progress, attemptId);
        }

        private bool TryApplyProgressLocked(BootstrapperProgress progress, long attemptId)
        {
            if (_outcome != BootstrapperPresentationOutcome.Running || attemptId != _attempt)
                return false;
            _lastProgress = progress.Clone();
            return true;
        }

        public bool CompleteSuccess(BootstrapperRunResult result)
        {
            ArgumentNullException.ThrowIfNull(result);
            lock (_sync)
            {
                if (_outcome != BootstrapperPresentationOutcome.Running)
                    return false;

                _outcome = BootstrapperPresentationOutcome.Succeeded;
                _finalStage = BootstrapperStage.StartingLauncher;
                _result = result;
                _canLaunchCurrent = true;
                _completedTimestamp = _timeProvider.GetTimestamp();
                return true;
            }
        }

        public bool CompleteFailure(BootstrapperRunResult result)
        {
            ArgumentNullException.ThrowIfNull(result);
            lock (_sync)
            {
                if (_outcome != BootstrapperPresentationOutcome.Running)
                    return false;

                _outcome = BootstrapperPresentationOutcome.Failed;
                _finalStage = BootstrapperStage.UpdateFailed;
                _result = result;
                _errorMessage = result.ErrorMessage ?? "Unknown error";
                _canRetry = result.CanRetry;
                _canLaunchCurrent = result.CanLaunchCurrent;
                _completedTimestamp = _timeProvider.GetTimestamp();
                return true;
            }
        }

        public bool Fail(string message, bool canRetry, bool canLaunchCurrent)
        {
            lock (_sync)
            {
                if (_outcome != BootstrapperPresentationOutcome.Running)
                    return false;

                _outcome = BootstrapperPresentationOutcome.Failed;
                _finalStage = BootstrapperStage.UpdateFailed;
                _errorMessage = message;
                _canRetry = canRetry;
                _canLaunchCurrent = canLaunchCurrent;
                _completedTimestamp = _timeProvider.GetTimestamp();
                return true;
            }
        }

        public bool Cancel()
        {
            lock (_sync)
            {
                if (_outcome != BootstrapperPresentationOutcome.Running)
                    return false;

                _outcome = BootstrapperPresentationOutcome.Cancelled;
                _finalStage = BootstrapperStage.UpdateFailed;
                _errorMessage = "Cancelled.";
                _canRetry = true;
                _canLaunchCurrent = false;
                _completedTimestamp = _timeProvider.GetTimestamp();
                return true;
            }
        }

        public TimeSpan RemainingDisplayTime
        {
            get
            {
                lock (_sync)
                    return CalculateRemainingLocked(_timeProvider.GetTimestamp());
            }
        }

        /// <summary>
        /// Waits only for the unused part of the minimum display time after a success.
        /// Failures and cancellations are returned immediately.
        /// </summary>
        public async Task WaitForMinimumDisplayTimeAsync(CancellationToken ct = default)
        {
            while (true)
            {
                TimeSpan remaining;
                lock (_sync)
                {
                    if (_outcome != BootstrapperPresentationOutcome.Succeeded)
                        return;
                    remaining = CalculateRemainingLocked(_timeProvider.GetTimestamp());
                }

                if (remaining <= TimeSpan.Zero)
                    return;

                await _delay(remaining, ct).ConfigureAwait(false);
            }
        }

        private TimeSpan CalculateRemainingLocked(long now)
        {
            if (_outcome != BootstrapperPresentationOutcome.Succeeded)
                return TimeSpan.Zero;

            var start = _uiReadyTimestamp ?? _completedTimestamp;
            var elapsed = _timeProvider.GetElapsedTime(start, now);
            var remaining = _minimumDisplayTime - elapsed;
            return remaining > TimeSpan.Zero ? remaining : TimeSpan.Zero;
        }
    }
}
