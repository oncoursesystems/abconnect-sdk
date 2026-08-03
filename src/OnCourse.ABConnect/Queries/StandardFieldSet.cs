namespace OnCourse.ABConnect.Queries;

/// <summary>
/// The explicit list of standard fields a query asks AB Connect for, rendered as a single
/// comma-separated <c>fields[standards]</c> value.
/// </summary>
/// <remarks>
/// Field sets are explicit by design. A wildcard request costs far more on AB Connect's side, is
/// throttled to two requests per second, and returns data no consumer asked for, so <c>*</c> is
/// reachable only through <see cref="Wildcard"/> and only when
/// <see cref="ABConnectOptions.AllowWildcardFields"/> is set.
/// </remarks>
/// <param name="Fields">The field names to request, in the order they are sent.</param>
public sealed record StandardFieldSet(IReadOnlyList<string> Fields)
{
    /// <summary>The token AB Connect interprets as "every field".</summary>
    public const string WildcardToken = "*";

    private static readonly StandardFieldSet SnapshotSet = new(
    [
        "guid",
        "seq",
        "level",
        "label",
        "status",
        "standard_type",
        "date_modified_utc",
        "date_deleted_utc",
        "number.raw",
        "number.enhanced",
        "number.prefix_enhanced",
        "number.root_enhanced",
        "statement.descr",
        "statement.combined_descr",
        "section.guid",
        "section.descr",
        "education_levels.grades",
        "document.guid",
        "document.descr",
        "document.adopt_year",
        "document.revision_year",
        "document.implementation_year",
        "document.assessment_year",
        "document.obsolete_year",
        "document.source_url",
        "document.date_modified_utc",
        "document.publication.guid",
        "document.publication.descr",
        "document.publication.acronym",
        "document.publication.source_url",
        "document.publication.publication_type",
        "document.publication.regions",
        "document.publication.authorities",
        "parent",
        "children",
    ]);

    private static readonly StandardFieldSet ProbeSet = new(["document", "document.publication"]);

    private static readonly StandardFieldSet IdentitySet = new(["guid", "status", "date_modified_utc"]);

    private static readonly StandardFieldSet WildcardSet = new([WildcardToken]);

    /// <summary>
    /// The full mirror set: every field a local copy of a standard needs, and nothing more. This is
    /// the default for both a standards query and a document snapshot.
    /// </summary>
    /// <remarks>
    /// <c>number.alternate</c> is deliberately excluded. It returns HTTP 200 when requested but was
    /// null on every one of 750 live samples, so it is dropped from both the model and this set.
    /// </remarks>
    public static StandardFieldSet Snapshot => SnapshotSet;

    /// <summary>
    /// <c>document</c> and <c>document.publication</c> only. Used by the one-row probes that exist
    /// purely to obtain a fully populated document or publication, which previously paid the
    /// wildcard penalty for the same information.
    /// </summary>
    public static StandardFieldSet Probe => ProbeSet;

    /// <summary>
    /// <c>guid</c>, <c>status</c>, and <c>date_modified_utc</c>. Enough to decide whether a local
    /// copy is stale without transferring the standard itself.
    /// </summary>
    public static StandardFieldSet Identity => IdentitySet;

    /// <summary>
    /// The wildcard set, <c>*</c>. Discovery only.
    /// </summary>
    /// <remarks>
    /// Using this set throws <see cref="ABConnectConfigurationException"/> at query-build time unless
    /// <see cref="ABConnectOptions.AllowWildcardFields"/> is true. When it is permitted, the request
    /// additionally acquires from the narrower wildcard token bucket, so a discovery run cannot
    /// drain the main bucket.
    /// </remarks>
    public static StandardFieldSet Wildcard => WildcardSet;

    /// <summary>Whether this set is the wildcard set.</summary>
    public bool IsWildcard => Fields.Count == 1 && Fields[0] == WildcardToken;

    /// <summary>Builds a field set from an explicit list of AB Connect field names.</summary>
    /// <param name="fields">The field names to request. Must contain at least one non-empty name.</param>
    /// <returns>A field set carrying exactly those names, in the order given.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="fields"/> is null.</exception>
    /// <exception cref="ArgumentException"><paramref name="fields"/> is empty or contains a null, empty, or whitespace name.</exception>
    public static StandardFieldSet Of(params string[] fields)
    {
        ArgumentNullException.ThrowIfNull(fields);

        if (fields.Length == 0)
        {
            throw new ArgumentException("A field set must contain at least one field name.", nameof(fields));
        }

        foreach (string field in fields)
        {
            if (string.IsNullOrWhiteSpace(field))
            {
                throw new ArgumentException("A field set must not contain a null or empty field name.", nameof(fields));
            }
        }

        return new StandardFieldSet([.. fields]);
    }

    /// <summary>
    /// Compares two field sets by their field names, in order.
    /// </summary>
    /// <remarks>
    /// The synthesized record equality would compare the <see cref="Fields"/> list by reference,
    /// so two sets built from the same names would not be equal. Field sets are values, so they
    /// compare as values.
    /// </remarks>
    /// <param name="other">The set to compare with.</param>
    /// <returns><see langword="true"/> when both sets carry the same names in the same order.</returns>
    public bool Equals(StandardFieldSet? other)
        => other is not null && (ReferenceEquals(this, other) || Fields.SequenceEqual(other.Fields, StringComparer.Ordinal));

    /// <inheritdoc />
    public override int GetHashCode()
    {
        HashCode hash = new();
        foreach (string field in Fields)
        {
            hash.Add(field, StringComparer.Ordinal);
        }

        return hash.ToHashCode();
    }
}
