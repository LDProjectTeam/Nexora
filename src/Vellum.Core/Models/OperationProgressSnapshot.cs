using System;

namespace Vellum.Core.Models
{
    /// <summary>Lifecycle state of a single long-running launcher operation.</summary>
    public enum OperationActivityState
    {
        Running,
        Paused,
        Error
    }

    /// <summary>
    /// Immutable progress snapshot for one operation (download, install, verification,
    /// update, unpacking). It intentionally carries no UI or Windows-specific types so
    /// loaders and installers can describe their work through a single contract.
    /// <para>
    /// <see cref="Priority"/> orders concurrent operations deterministically: lower
    /// values win ties (the download queue uses 1 for the first item). The taskbar
    /// still prefers the most advanced measurable operation on first selection, then
    /// keeps that operation stable.
    /// </para>
    /// </summary>
    public readonly record struct OperationProgressSnapshot(
        string OperationId,
        OperationActivityState State,
        bool IsDeterminate,
        long Completed,
        long Total,
        int Priority = 0);

    /// <summary>State that a single progress indicator should display.</summary>
    public enum OperationProgressDisplayState
    {
        None,
        Normal,
        Paused,
        Error,
        Indeterminate
    }

    /// <summary>Representative operation selected for the single progress indicator.</summary>
    public readonly record struct OperationProgressAggregate(
        OperationProgressDisplayState State,
        ulong Completed,
        ulong Total,
        string? OperationId = null);
}
