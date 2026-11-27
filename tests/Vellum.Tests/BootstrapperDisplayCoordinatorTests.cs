using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Vellum.Core.Models;
using Vellum.Core.Services;
using Xunit;

namespace Vellum.Tests
{
    public class BootstrapperDisplayCoordinatorTests
    {
        [Fact]
        public async Task Fast_success_waits_for_the_remaining_minimum_display_time()
        {
            var time = new ManualTimeProvider();
            var delays = new List<TimeSpan>();
            var coordinator = CreateCoordinator(time, delays);

            Assert.Equal(TimeSpan.FromMilliseconds(1200), coordinator.MinimumDisplayTime);
            coordinator.MarkUiReady();
            coordinator.BeginAttempt();
            time.Advance(TimeSpan.FromMilliseconds(150));

            Assert.True(coordinator.CompleteSuccess(BootstrapperRunResult.Ok(true)));
            await coordinator.WaitForMinimumDisplayTimeAsync();

            Assert.Single(delays);
            Assert.Equal(1050, Math.Round(delays[0].TotalMilliseconds));
            Assert.Equal(BootstrapperPresentationOutcome.Succeeded, coordinator.Outcome);
            Assert.Equal(BootstrapperStage.StartingLauncher, coordinator.FinalStage);
        }

        [Fact]
        public async Task Slow_workflow_is_not_delayed_artificially()
        {
            var time = new ManualTimeProvider();
            var delays = new List<TimeSpan>();
            var coordinator = CreateCoordinator(time, delays);

            coordinator.MarkUiReady();
            time.Advance(TimeSpan.FromSeconds(2.5));

            Assert.True(coordinator.CompleteSuccess(BootstrapperRunResult.Ok(true)));
            await coordinator.WaitForMinimumDisplayTimeAsync();

            Assert.Empty(delays);
            Assert.Equal(TimeSpan.Zero, coordinator.RemainingDisplayTime);
            Assert.Equal(BootstrapperPresentationOutcome.Succeeded, coordinator.Outcome);
        }

        [Fact]
        public async Task Final_success_status_is_starting_the_launcher()
        {
            var time = new ManualTimeProvider();
            var coordinator = CreateCoordinator(time, new List<TimeSpan>());

            coordinator.MarkUiReady();
            time.Advance(TimeSpan.FromMilliseconds(20));
            var result = BootstrapperRunResult.Ok(true);

            Assert.True(coordinator.CompleteSuccess(result));

            Assert.Same(result, coordinator.Result);
            Assert.Equal(BootstrapperStage.StartingLauncher, coordinator.FinalStage);
            Assert.Equal(BootstrapperPresentationOutcome.Succeeded, coordinator.Outcome);
            Assert.True(coordinator.CanLaunchCurrent);
            Assert.Null(coordinator.ErrorMessage);

            await coordinator.WaitForMinimumDisplayTimeAsync();
        }

        [Fact]
        public async Task Failure_transitions_to_error_immediately_without_waiting()
        {
            var time = new ManualTimeProvider();
            var delays = new List<TimeSpan>();
            var coordinator = CreateCoordinator(time, delays);

            coordinator.MarkUiReady();
            time.Advance(TimeSpan.FromMilliseconds(100));
            var result = BootstrapperRunResult.Failed("startup failed", true, true);

            Assert.True(coordinator.CompleteFailure(result));

            Assert.Equal(BootstrapperPresentationOutcome.Failed, coordinator.Outcome);
            Assert.Equal(BootstrapperStage.UpdateFailed, coordinator.FinalStage);
            Assert.Equal("startup failed", coordinator.ErrorMessage);
            Assert.True(coordinator.CanRetry);
            Assert.True(coordinator.CanLaunchCurrent);

            Assert.False(coordinator.TryApplyProgress(new BootstrapperProgress
            {
                Stage = BootstrapperStage.StartingLauncher,
                Message = "Starting Vellum..."
            }));
            Assert.Equal(BootstrapperStage.UpdateFailed, coordinator.FinalStage);

            await coordinator.WaitForMinimumDisplayTimeAsync();
            Assert.Empty(delays);
        }

        [Fact]
        public void Late_progress_after_success_cannot_overwrite_the_final_status()
        {
            var time = new ManualTimeProvider();
            var coordinator = CreateCoordinator(time, new List<TimeSpan>());

            coordinator.MarkUiReady();
            var before = new BootstrapperProgress
            {
                Stage = BootstrapperStage.CheckingIntegrity,
                Message = "Checking integrity..."
            };
            Assert.True(coordinator.TryApplyProgress(before));
            Assert.True(coordinator.CompleteSuccess(BootstrapperRunResult.Ok(true)));

            var late = new BootstrapperProgress
            {
                Stage = BootstrapperStage.UpdateFailed,
                Message = "Late failure"
            };
            Assert.False(coordinator.TryApplyProgress(late));

            Assert.Equal(BootstrapperStage.StartingLauncher, coordinator.FinalStage);
            Assert.Equal(BootstrapperStage.CheckingIntegrity, coordinator.LastProgress!.Stage);
            Assert.Equal(BootstrapperPresentationOutcome.Succeeded, coordinator.Outcome);
            Assert.Null(coordinator.ErrorMessage);
        }

        [Fact]
        public async Task Retry_resets_the_attempt_without_restarting_the_display_clock()
        {
            var time = new ManualTimeProvider();
            var delays = new List<TimeSpan>();
            var coordinator = CreateCoordinator(time, delays);

            var firstAttempt = coordinator.BeginAttempt();
            coordinator.MarkUiReady();
            time.Advance(TimeSpan.FromMilliseconds(200));
            Assert.True(coordinator.CompleteFailure(
                BootstrapperRunResult.Failed("first attempt failed", true, true)));

            var retryAttempt = coordinator.BeginAttempt();
            Assert.NotEqual(firstAttempt, retryAttempt);
            Assert.Equal(BootstrapperPresentationOutcome.Running, coordinator.Outcome);

            Assert.False(coordinator.TryApplyProgress(new BootstrapperProgress
            {
                Stage = BootstrapperStage.UpdateFailed,
                Message = "late event from the failed attempt"
            }, firstAttempt));
            Assert.True(coordinator.TryApplyProgress(new BootstrapperProgress
            {
                Stage = BootstrapperStage.CheckingUpdates,
                Message = "Checking for updates..."
            }, retryAttempt));

            time.Advance(TimeSpan.FromMilliseconds(1500));
            Assert.True(coordinator.CompleteSuccess(BootstrapperRunResult.Ok(true)));
            await coordinator.WaitForMinimumDisplayTimeAsync();

            Assert.Empty(delays);
        }

        private static BootstrapperDisplayCoordinator CreateCoordinator(
            ManualTimeProvider time, List<TimeSpan> delays)
        {
            return new BootstrapperDisplayCoordinator(
                timeProvider: time,
                delay: (duration, _) =>
                {
                    delays.Add(duration);
                    time.Advance(duration);
                    return Task.CompletedTask;
                });
        }

        private sealed class ManualTimeProvider : TimeProvider
        {
            private long _timestamp;

            public override long TimestampFrequency => 1000;

            public override long GetTimestamp() => _timestamp;

            public void Advance(TimeSpan duration)
                => _timestamp += (long)duration.TotalMilliseconds;
        }
    }
}
