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
/// This is not a closed set; see the remarks on <see cref="ABChangeTypes"/>. Only <c>active</c> and
/// <c>deleted</c> are ever named in the documentation.
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
}
