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
    /// Both current and deleted standards. Emits <c>status IN ('active','deleted')</c>. Does NOT
    /// include obsolete standards; use <see cref="All"/> for a complete mirror.
    /// </summary>
    ActiveAndDeleted = 2,

    /// <summary>Only obsolete standards. Emits <c>status EQ 'obsolete'</c>.</summary>
    Obsolete = 3,

    /// <summary>
    /// Every documented lifecycle state: current, deleted, and obsolete. Emits
    /// <c>status IN ('active','deleted','obsolete')</c>. This is the default, because a complete
    /// mirror must carry every standard the vendor knows about: it cannot apply a deletion it cannot
    /// see, and it must not silently shed the obsolete standards the vendor's own default returns and
    /// that existing local links resolve against. If AB Connect ever names a fourth status, this
    /// enumerates the three it documents today and would need extending, which is deliberate: a new
    /// vendor status should be a conscious decision, not a silent inclusion.
    /// </summary>
    All = 4,
}
