namespace OnCourse.ABConnect.Queries;

/// <summary>
/// The direction an events query is sorted by sequence number.
/// </summary>
public enum EventSequenceOrder
{
    /// <summary>
    /// Oldest first. The order every delta pull must use: reading ascending is what lets a pull
    /// advance its watermark one page at a time without ever stepping past an event it has not seen.
    /// </summary>
    Ascending,

    /// <summary>
    /// Newest first. Used only to read the head of the feed, the single highest sequence, in one
    /// request, which is how a cutover seeds its starting watermark without replaying history.
    /// </summary>
    Descending,
}
