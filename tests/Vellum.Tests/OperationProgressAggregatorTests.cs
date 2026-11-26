using System;
using System.Collections.Generic;
using Vellum.Core.Models;
using Vellum.Core.Services;
using Xunit;

namespace Vellum.Tests
{
    public class OperationProgressAggregatorTests
    {
        private static OperationProgressSnapshot Snapshot(
            string id,
            OperationActivityState state,
            bool determinate,
            long completed,
            long total,
            int priority = 0)
            => new(id, state, determinate, completed, total, priority);

        [Fact]
        public void NoOperations_HidesIndicator()
        {
            Assert.Equal(
                new OperationProgressAggregate(OperationProgressDisplayState.None, 0, 0),
                OperationProgressAggregator.Aggregate(null));

            Assert.Equal(
                new OperationProgressAggregate(OperationProgressDisplayState.None, 0, 0),
                OperationProgressAggregator.Aggregate(Array.Empty<OperationProgressSnapshot>()));
        }

        [Fact]
        public void EmptyOperationId_IsIgnored()
        {
            var aggregate = OperationProgressAggregator.Aggregate(new[]
            {
                Snapshot(string.Empty, OperationActivityState.Running, true, 5, 10)
            });

            Assert.Equal(OperationProgressDisplayState.None, aggregate.State);
        }

        [Fact]
        public void SingleDeterminateRunning_ShowsExactProgress()
        {
            var aggregate = OperationProgressAggregator.Aggregate(new[]
            {
                Snapshot("game-a", OperationActivityState.Running, true, 25, 100)
            });

            Assert.Equal(OperationProgressDisplayState.Normal, aggregate.State);
            Assert.Equal(25UL, aggregate.Completed);
            Assert.Equal(100UL, aggregate.Total);
        }

        [Fact]
        public void SingleIndeterminateRunning_ShowsIndeterminate()
        {
            var aggregate = OperationProgressAggregator.Aggregate(new[]
            {
                Snapshot("game-a", OperationActivityState.Running, false, 0, 0)
            });

            Assert.Equal(OperationProgressDisplayState.Indeterminate, aggregate.State);
            Assert.Equal(0UL, aggregate.Completed);
            Assert.Equal(0UL, aggregate.Total);
        }

        [Fact]
        public void TwoDownloads_ShowOneOperationInsteadOfAverage()
        {
            // 99% complete game and a freshly started one. The indicator must show 99%,
            // not the 49.5% average that the old summing implementation produced.
            var aggregate = OperationProgressAggregator.Aggregate(new[]
            {
                Snapshot("game-a", OperationActivityState.Running, true, 99, 100, priority: 1),
                Snapshot("game-b", OperationActivityState.Running, true, 0, 100, priority: 2)
            });

            Assert.Equal(OperationProgressDisplayState.Normal, aggregate.State);
            Assert.Equal(99UL, aggregate.Completed);
            Assert.Equal(100UL, aggregate.Total);
        }

        [Fact]
        public void LowerPriority_StaysSelectedWhileProgressChanges()
        {
            var first = OperationProgressAggregator.Aggregate(new[]
            {
                Snapshot("game-a", OperationActivityState.Running, true, 40, 100, priority: 1),
                Snapshot("game-b", OperationActivityState.Running, true, 10, 100, priority: 2)
            });

            var later = OperationProgressAggregator.Aggregate(new[]
            {
                Snapshot("game-a", OperationActivityState.Running, true, 41, 100, priority: 1),
                Snapshot("game-b", OperationActivityState.Running, true, 90, 100, priority: 2)
            }, preferredOperationId: "game-a");

            Assert.Equal(40UL, first.Completed);
            Assert.Equal(41UL, later.Completed);
            Assert.Equal(100UL, later.Total);
            Assert.Equal("game-a", later.OperationId);
        }

        [Fact]
        public void PreferredOperation_IsKeptEvenWhenAnotherOvertakes()
        {
            var aggregate = OperationProgressAggregator.Aggregate(new[]
            {
                Snapshot("game-a", OperationActivityState.Running, true, 20, 100, priority: 1),
                Snapshot("game-b", OperationActivityState.Running, true, 95, 100, priority: 2)
            }, preferredOperationId: "game-a");

            Assert.Equal(OperationProgressDisplayState.Normal, aggregate.State);
            Assert.Equal("game-a", aggregate.OperationId);
            Assert.Equal(20UL, aggregate.Completed);
            Assert.Equal(100UL, aggregate.Total);
        }

        [Fact]
        public void PreferredOperation_ThatFinished_IsReplacedByTheNextOne()
        {
            var aggregate = OperationProgressAggregator.Aggregate(new[]
            {
                Snapshot("game-b", OperationActivityState.Running, true, 30, 100, priority: 2)
            }, preferredOperationId: "game-a");

            Assert.Equal(OperationProgressDisplayState.Normal, aggregate.State);
            Assert.Equal("game-b", aggregate.OperationId);
            Assert.Equal(30UL, aggregate.Completed);
            Assert.Equal(100UL, aggregate.Total);
        }

        [Fact]
        public void DeterminateOperation_IsPreferredOverIndeterminatePeer()
        {
            var aggregate = OperationProgressAggregator.Aggregate(new[]
            {
                Snapshot("game-a", OperationActivityState.Running, false, 0, 0, priority: 1),
                Snapshot("game-b", OperationActivityState.Running, true, 50, 200, priority: 2)
            });

            Assert.Equal(OperationProgressDisplayState.Normal, aggregate.State);
            Assert.Equal(50UL, aggregate.Completed);
            Assert.Equal(200UL, aggregate.Total);
        }

        [Fact]
        public void SeveralIndeterminateOperations_StayIndeterminate()
        {
            var aggregate = OperationProgressAggregator.Aggregate(new[]
            {
                Snapshot("game-a", OperationActivityState.Running, false, 0, 0, priority: 1),
                Snapshot("game-b", OperationActivityState.Running, false, 0, 0, priority: 2)
            });

            Assert.Equal(OperationProgressDisplayState.Indeterminate, aggregate.State);
            Assert.Equal(0UL, aggregate.Completed);
            Assert.Equal(0UL, aggregate.Total);
        }

        [Fact]
        public void DeterminateWithoutTotal_IsNotTreatedAsPercent()
        {
            var aggregate = OperationProgressAggregator.Aggregate(new[]
            {
                Snapshot("game-a", OperationActivityState.Running, true, 5, 0)
            });

            Assert.Equal(OperationProgressDisplayState.Indeterminate, aggregate.State);
        }

        [Fact]
        public void RunningOperation_WinsOverPausedPeer()
        {
            var aggregate = OperationProgressAggregator.Aggregate(new[]
            {
                Snapshot("game-a", OperationActivityState.Paused, true, 90, 100, priority: 1),
                Snapshot("game-b", OperationActivityState.Running, true, 15, 100, priority: 2)
            });

            Assert.Equal(OperationProgressDisplayState.Normal, aggregate.State);
            Assert.Equal(15UL, aggregate.Completed);
            Assert.Equal(100UL, aggregate.Total);
        }

        [Fact]
        public void AllPaused_ShowsPausedWithRealProgress()
        {
            var aggregate = OperationProgressAggregator.Aggregate(new[]
            {
                Snapshot("game-a", OperationActivityState.Paused, true, 10, 100, priority: 1),
                Snapshot("game-b", OperationActivityState.Paused, true, 50, 200, priority: 2)
            });

            Assert.Equal(OperationProgressDisplayState.Paused, aggregate.State);
            Assert.Equal(50UL, aggregate.Completed);
            Assert.Equal(200UL, aggregate.Total);
            Assert.Equal("game-b", aggregate.OperationId);
        }

        [Fact]
        public void PausedWithUnknownUnits_KeepsPausedWithoutCounter()
        {
            var aggregate = OperationProgressAggregator.Aggregate(new[]
            {
                Snapshot("game-a", OperationActivityState.Paused, false, 0, 0)
            });

            Assert.Equal(OperationProgressDisplayState.Paused, aggregate.State);
            Assert.Equal(0UL, aggregate.Completed);
            Assert.Equal(0UL, aggregate.Total);
        }

        [Fact]
        public void PausedDeterminate_IsPreferredOverPausedIndeterminate()
        {
            var aggregate = OperationProgressAggregator.Aggregate(new[]
            {
                Snapshot("game-a", OperationActivityState.Paused, false, 0, 0, priority: 1),
                Snapshot("game-b", OperationActivityState.Paused, true, 30, 100, priority: 2)
            });

            Assert.Equal(OperationProgressDisplayState.Paused, aggregate.State);
            Assert.Equal(30UL, aggregate.Completed);
            Assert.Equal(100UL, aggregate.Total);
        }

        [Fact]
        public void ErrorWinsWhenNothingIsRunning()
        {
            var aggregate = OperationProgressAggregator.Aggregate(new[]
            {
                Snapshot("game-a", OperationActivityState.Paused, true, 10, 100, priority: 1),
                Snapshot("game-b", OperationActivityState.Error, true, 25, 50, priority: 2)
            });

            Assert.Equal(OperationProgressDisplayState.Error, aggregate.State);
            Assert.Equal(25UL, aggregate.Completed);
            Assert.Equal(50UL, aggregate.Total);
        }

        [Fact]
        public void RunningOperation_WinsOverFailedPeer()
        {
            var aggregate = OperationProgressAggregator.Aggregate(new[]
            {
                Snapshot("game-a", OperationActivityState.Error, false, 0, 0, priority: 1),
                Snapshot("game-b", OperationActivityState.Running, true, 10, 100, priority: 2)
            });

            Assert.Equal(OperationProgressDisplayState.Normal, aggregate.State);
            Assert.Equal(10UL, aggregate.Completed);
            Assert.Equal(100UL, aggregate.Total);
        }

        [Fact]
        public void OnlyError_ShowsErrorWithoutCounterWhenUnknown()
        {
            var aggregate = OperationProgressAggregator.Aggregate(new[]
            {
                Snapshot("game-a", OperationActivityState.Error, false, 0, 0)
            });

            Assert.Equal(OperationProgressDisplayState.Error, aggregate.State);
            Assert.Equal(0UL, aggregate.Completed);
            Assert.Equal(0UL, aggregate.Total);
        }

        [Fact]
        public void EqualPriority_IsResolvedDeterministically()
        {
            var aggregate = OperationProgressAggregator.Aggregate(new[]
            {
                Snapshot("game-b", OperationActivityState.Running, true, 20, 100, priority: 1),
                Snapshot("game-a", OperationActivityState.Running, true, 80, 100, priority: 1)
            });

            Assert.Equal(OperationProgressDisplayState.Normal, aggregate.State);
            Assert.Equal(80UL, aggregate.Completed);
            Assert.Equal(100UL, aggregate.Total);
        }

        [Fact]
        public void NegativeCompleted_IsTreatedAsZero()
        {
            var aggregate = OperationProgressAggregator.Aggregate(new[]
            {
                Snapshot("game-a", OperationActivityState.Running, true, -5, 100)
            });

            Assert.Equal(OperationProgressDisplayState.Normal, aggregate.State);
            Assert.Equal(0UL, aggregate.Completed);
            Assert.Equal(100UL, aggregate.Total);
        }

        [Fact]
        public void CompletedAboveTotal_IsClamped()
        {
            var aggregate = OperationProgressAggregator.Aggregate(new[]
            {
                Snapshot("game-a", OperationActivityState.Running, true, 500, 100)
            });

            Assert.Equal(OperationProgressDisplayState.Normal, aggregate.State);
            Assert.Equal(100UL, aggregate.Completed);
            Assert.Equal(100UL, aggregate.Total);
        }

        [Fact]
        public void RepeatedUpdates_ProduceStableResults()
        {
            var operations = new List<OperationProgressSnapshot>
            {
                Snapshot("game-a", OperationActivityState.Running, true, 10, 100, priority: 1)
            };

            var first = OperationProgressAggregator.Aggregate(operations);
            for (var i = 0; i < 10_000; i++)
            {
                operations[0] = Snapshot(
                    "game-a", OperationActivityState.Running, true, i % 100, 100, priority: 1);
                OperationProgressAggregator.Aggregate(operations);
            }

            var last = OperationProgressAggregator.Aggregate(new[]
            {
                Snapshot("game-a", OperationActivityState.Running, true, 99, 100, priority: 1)
            });

            Assert.Equal(OperationProgressDisplayState.Normal, first.State);
            Assert.Equal(99UL, last.Completed);
            Assert.Equal(100UL, last.Total);
        }

        [Fact]
        public void HugeTotals_DoNotOverflow()
        {
            var aggregate = OperationProgressAggregator.Aggregate(new[]
            {
                Snapshot("game-a", OperationActivityState.Running, true, long.MaxValue - 1, long.MaxValue)
            });

            Assert.Equal(OperationProgressDisplayState.Normal, aggregate.State);
            Assert.Equal((ulong)(long.MaxValue - 1), aggregate.Completed);
            Assert.Equal((ulong)long.MaxValue, aggregate.Total);
        }
    }
}
