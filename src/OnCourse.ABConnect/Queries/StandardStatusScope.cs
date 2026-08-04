namespace OnCourse.ABConnect.Queries;

/// <summary>
/// Which lifecycle states a standards query covers, rendered as a <c>status</c> term in the filter
/// expression.
/// </summary>
public enum StandardStatusScope
{
    /// <summary>Only current standards. Emits <c>status EQ 'active'</c>.</summary>
    Active = 0,

    /// <summary>Only deleted standards. Emits <c>status EQ 'deleted'</c>.</summary>
    Deleted = 1,

    /// <summary>
    /// Both current and deleted standards. Emits <c>status IN ('active','deleted')</c>. This is the
    /// default, because a mirror that cannot see a deletion cannot apply it.
    /// </summary>
    ActiveAndDeleted = 2,
}
