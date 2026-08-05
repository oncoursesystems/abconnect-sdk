namespace OnCourse.ABConnect.Queries;

/// <summary>
/// A single request for one page of change events past a watermark. Immutable; build variants with
/// <c>with</c>.
/// </summary>
/// <remarks>
/// An events query always emits an explicit <c>sort[events]</c> on sequence, ascending by default
/// (<c>seq</c>) or descending when <see cref="Order"/> asks for it (<c>-seq</c>). AB Connect's
/// default ordering is by relevance, not by sequence, so the sort has to be requested every single
/// time; a delta pull that does not request ascending order will advance its watermark past events
/// it never saw.
/// </remarks>
public sealed record EventsQuery
{
    /// <summary>
    /// The watermark. Only events with a sequence strictly greater than this are returned, emitted
    /// as <c>filter[events]=(seq GT n)</c>.
    /// </summary>
    public required long AfterSequence { get; init; }

    /// <summary>Which fields to return. Defaults to every documented event field.</summary>
    public EventFieldSet Fields { get; init; } = EventFieldSet.Full;

    /// <summary>Which page window to return. Defaults to the first page.</summary>
    public PageRequest Page { get; init; } = PageRequest.First;

    /// <summary>
    /// The direction to sort by sequence. Defaults to ascending, which every delta pull requires.
    /// Descending is used only to read the head of the feed (its single highest sequence) in one
    /// request; it must never drive a delta pull, because reading newest-first cannot advance a
    /// watermark safely.
    /// </summary>
    public EventSequenceOrder Order { get; init; } = EventSequenceOrder.Ascending;

    /// <summary>
    /// An optional restriction on the standards the events must concern. AB Connect permits
    /// filtering events on the same subset of standard properties that a standards query supports.
    /// </summary>
    /// <remarks>
    /// Warning: a scoped feed produces a watermark that is only valid for that scope. Advancing a
    /// shared watermark from a scoped read silently discards every event outside the scope, and
    /// there is no way to recover them afterwards, because the feed only moves forward. A scheduled
    /// full pull must leave this null and keep any scoped watermark in a separate slot.
    /// </remarks>
    public StandardsFilter? StandardScope { get; init; }
}
