using OnCourse.ABConnect.Queries;

namespace OnCourse.ABConnect.Feed;

/// <summary>Options for reading the event feed.</summary>
public sealed record EventReadOptions
{
    /// <summary>
    /// A ceiling on how many events a single read returns, or null for no ceiling. When the ceiling
    /// stops a traversal, the result reports itself incomplete and the caller reads again from the
    /// highest sequence it received.
    /// </summary>
    /// <remarks>
    /// Must be at least one when it is set; a ceiling of zero is rejected with
    /// <see cref="ArgumentOutOfRangeException"/> rather than quietly reporting an empty feed. A read
    /// that reaches the ceiling always reports itself incomplete, including in the boundary case
    /// where the ceiling happened to coincide with the end of the feed, because whether more events
    /// exist cannot be known without another request. The caller's next read settles it and returns
    /// no events.
    /// </remarks>
    public int? MaxEvents { get; init; }

    /// <summary>Rows per page. Clamped to AB Connect's documented maximum of 100.</summary>
    /// <remarks>
    /// Must be at least one; zero is rejected with <see cref="ArgumentOutOfRangeException"/>, because
    /// a limit of zero returns the <c>meta</c> block and no rows and would report an empty feed
    /// rather than read it. The effective value is also lowered to
    /// <see cref="ABConnectOptions.PageSize"/> when the configured page size is smaller, since every
    /// request the SDK sends is subject to that ceiling.
    /// </remarks>
    public int PageSize { get; init; } = 100;

    /// <summary>
    /// An optional restriction on the standards the events must concern.
    /// </summary>
    /// <remarks>
    /// Warning: a scoped feed produces a watermark that is only valid for that scope. Advancing a
    /// shared watermark from a scoped read silently discards every event outside the scope, and the
    /// feed only moves forward, so they cannot be recovered afterwards.
    /// </remarks>
    public StandardsFilter? StandardScope { get; init; }
}
