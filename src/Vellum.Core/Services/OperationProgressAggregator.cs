using System;
using System.Collections.Generic;
using System.Linq;
using Vellum.Core.Models;

namespace Vellum.Core.Services
{
    /// <summary>
    /// Reduces several concurrent operations to the one operation the taskbar should
    /// display. The indicator shows the real progress of a single, stable operation
    /// instead of averaging unrelated downloads (which used to make a 99%-complete
    /// download look like 50% just because a second one had started).
    /// <para>Selection order:</para>
    /// <list type="number">
    /// <item>any running operation (prefer measurable progress, then the most advanced);</item>
    /// <item>a failed operation when nothing is running;</item>
    /// <item>a paused operation when nothing is running or failed;</item>
    /// <item>otherwise no indicator.</item>
    /// </list>
    /// Once an operation is selected, callers pass its id as <c>preferredOperationId</c>.
    /// It stays selected while it runs, so the indicator does not flicker when another
    /// download temporarily reports a higher percentage.
    /// </summary>
    public static class OperationProgressAggregator
    {
        public static OperationProgressAggregate Aggregate(
            IEnumerable<OperationProgressSnapshot>? operations,
            string? preferredOperationId = null)
        {
            if (operations == null)
                return None();

            var active = new List<OperationProgressSnapshot>();
            foreach (var operation in operations)
            {
                // A default snapshot has no id. Cards that are not part of the queue
                // return one, so skipping it keeps callers free of null checks.
                if (!string.IsNullOrWhiteSpace(operation.OperationId))
                    active.Add(operation);
            }

            if (active.Count == 0)
                return None();

            // Stable ordering: queue position first, operation id as a deterministic
            // tie-breaker for cards that share a priority.
            var ordered = active
                .OrderBy(operation => operation.Priority)
                .ThenBy(operation => operation.OperationId, StringComparer.Ordinal)
                .ToList();

            var running = ordered
                .Where(operation => operation.State == OperationActivityState.Running)
                .ToList();
            if (running.Count > 0)
            {
                var chosen = Pick(running, preferredOperationId);
                return Build(
                    chosen,
                    IsDeterminate(chosen)
                        ? OperationProgressDisplayState.Normal
                        : OperationProgressDisplayState.Indeterminate);
            }

            var failed = ordered
                .Where(operation => operation.State == OperationActivityState.Error)
                .ToList();
            if (failed.Count > 0)
                return Build(Pick(failed, preferredOperationId), OperationProgressDisplayState.Error);

            var paused = ordered
                .Where(operation => operation.State == OperationActivityState.Paused)
                .ToList();
            if (paused.Count > 0)
                return Build(Pick(paused, preferredOperationId), OperationProgressDisplayState.Paused);

            return None();
        }

        private static OperationProgressSnapshot Pick(
            List<OperationProgressSnapshot> candidates,
            string? preferredOperationId)
        {
            var measurable = candidates.Where(IsDeterminate).ToList();

            // A measurable operation is always more useful than the Windows
            // indeterminate animation, so indeterminate peers never win over it.
            if (measurable.Count > 0)
            {
                if (!string.IsNullOrEmpty(preferredOperationId))
                {
                    var stable = measurable.FirstOrDefault(
                        operation => string.Equals(
                            operation.OperationId, preferredOperationId, StringComparison.Ordinal));
                    if (!string.IsNullOrEmpty(stable.OperationId))
                        return stable;
                }

                // First activation: show the operation that is actually furthest along.
                // Ties fall back to queue priority, then id, for a deterministic result.
                return measurable
                    .OrderByDescending(ProgressRatio)
                    .ThenBy(operation => operation.Priority)
                    .ThenBy(operation => operation.OperationId, StringComparer.Ordinal)
                    .First();
            }

            if (!string.IsNullOrEmpty(preferredOperationId))
            {
                var stable = candidates.FirstOrDefault(
                    operation => string.Equals(
                        operation.OperationId, preferredOperationId, StringComparison.Ordinal));
                if (!string.IsNullOrEmpty(stable.OperationId))
                    return stable;
            }

            return candidates[0];
        }

        private static double ProgressRatio(OperationProgressSnapshot operation)
        {
            if (operation.Total <= 0)
                return 0;

            var completed = operation.Completed <= 0
                ? 0
                : Math.Min(operation.Completed, operation.Total);
            return (double)completed / operation.Total;
        }

        private static bool IsDeterminate(OperationProgressSnapshot operation)
            => operation.IsDeterminate && operation.Total > 0;

        private static OperationProgressAggregate Build(
            OperationProgressSnapshot operation,
            OperationProgressDisplayState state)
        {
            if (!IsDeterminate(operation))
                return new OperationProgressAggregate(state, 0, 0, operation.OperationId);

            var total = (ulong)operation.Total;
            var completed = operation.Completed <= 0
                ? 0UL
                : (ulong)Math.Min(operation.Completed, operation.Total);

            return new OperationProgressAggregate(
                state, Math.Min(completed, total), total, operation.OperationId);
        }

        private static OperationProgressAggregate None()
            => new(OperationProgressDisplayState.None, 0, 0);
    }
}
