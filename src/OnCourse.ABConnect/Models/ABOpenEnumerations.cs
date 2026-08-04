namespace OnCourse.ABConnect.Models;

/// <summary>
/// The <c>change_type</c> values AB Connect names in its published documentation.
/// </summary>
/// <remarks>
/// This is not a closed set. AB Connect has never published the complete value set, and live
/// sampling has observed values it does not document at all, including <c>deleted</c>,
/// <c>reordered</c>, <c>moved</c>, and several <c>updated ...</c> variants. That is why
/// <see cref="ABEventAttributes.ChangeType"/> is a <see cref="string"/> and not an enum: an enum
/// would throw or silently coerce on the first value AB Connect adds. Compare against these
/// constants and log-and-skip anything unrecognized.
/// </remarks>
public static class ABChangeTypes
{
    /// <summary>The subject was added.</summary>
    public const string Added = "added";

    /// <summary>The subject was removed.</summary>
    public const string Removed = "removed";
}

/// <summary>
/// The <c>target</c> values AB Connect names in its published documentation.
/// </summary>
/// <remarks>
/// This is not a closed set; see the remarks on <see cref="ABChangeTypes"/>. AB Connect names
/// <c>document</c> explicitly, describes section-level delivery events in prose, and refers
/// repeatedly to standard-level change events.
/// </remarks>
public static class ABEventTargets
{
    /// <summary>The event concerns a document.</summary>
    public const string Document = "document";

    /// <summary>The event concerns a section.</summary>
    public const string Section = "section";

    /// <summary>The event concerns an individual standard.</summary>
    public const string Standard = "standard";
}

/// <summary>
/// The <c>status</c> values AB Connect names for a standard.
/// </summary>
/// <remarks>
/// This is not a closed set; see the remarks on <see cref="ABChangeTypes"/>. The documentation names
/// <c>active</c> and <c>deleted</c>; live sampling confirmed a third value, <c>obsolete</c>, which the
/// vendor's no-status-filter default returns alongside <c>active</c> and which a
/// <c>status IN ('active','deleted')</c> filter silently excludes.
/// </remarks>
public static class ABStandardStatuses
{
    /// <summary>The standard is current.</summary>
    public const string Active = "active";

    /// <summary>
    /// The standard has been deleted. A deleted standard is still returned by a lookup by GUID, and
    /// is included in a list query when the status scope covers it.
    /// </summary>
    public const string Deleted = "deleted";

    /// <summary>
    /// The standard has been marked obsolete by the vendor: superseded but not deleted. AB Connect's
    /// no-status-filter default returns obsolete standards along with active ones, so a mirror that
    /// filters <c>status IN ('active','deleted')</c> drops every obsolete standard. Live sampling on
    /// 2026-08-04 found 424,565 obsolete standards feed-wide on partner <c>sws</c>, and a single
    /// document (<c>D4CA18A6-F66C-11E2-9582-EB419DFF4B22</c>) with 8,305 of them against 2,663 active.
    /// Include it through <see cref="OnCourse.ABConnect.Queries.StandardStatusScope"/> to mirror them.
    /// </summary>
    public const string Obsolete = "obsolete";
}
