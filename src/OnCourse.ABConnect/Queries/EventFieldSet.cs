namespace OnCourse.ABConnect.Queries;

/// <summary>
/// The explicit list of event fields a query asks AB Connect for, rendered as a single
/// comma-separated <c>fields[events]</c> value.
/// </summary>
/// <param name="Fields">The field names to request, in the order they are sent.</param>
public sealed record EventFieldSet(IReadOnlyList<string> Fields)
{
    private static readonly EventFieldSet FullSet = new(
    [
        "seq",
        "date_utc",
        "change_type",
        "target",
        "guid",
        "document_guid",
        "section_guid",
        "affected_properties",
        "standard",
        "nondeliverable_standard",
        "deleted_standard",
    ]);

    /// <summary>
    /// Every documented event field. This is the default for an events query, because an event the
    /// SDK read with a narrower set cannot be re-read: the feed only moves forward.
    /// </summary>
    /// <remarks>
    /// <c>section_guid</c> and the three relationship fields come from AB Connect's documented event
    /// example. <c>guid</c> does not appear in that example but is returned and consumed in practice.
    /// </remarks>
    public static EventFieldSet Full => FullSet;

    /// <summary>Builds an event field set from an explicit list of AB Connect field names.</summary>
    /// <param name="fields">The field names to request. Must contain at least one non-empty name.</param>
    /// <returns>A field set carrying exactly those names, in the order given.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="fields"/> is null.</exception>
    /// <exception cref="ArgumentException"><paramref name="fields"/> is empty or contains a null, empty, or whitespace name.</exception>
    public static EventFieldSet Of(params string[] fields)
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

        return new EventFieldSet([.. fields]);
    }

    /// <summary>
    /// Compares two event field sets by their contents, in order.
    /// </summary>
    /// <remarks>
    /// The synthesized record equality would compare the <see cref="Fields"/> list by reference,
    /// so two instances built from the same contents would not be equal. These are values, so they
    /// compare as values.
    /// </remarks>
    /// <param name="other">The instance to compare with.</param>
    /// <returns><see langword="true"/> when both carry the same contents in the same order.</returns>
    public bool Equals(EventFieldSet? other)
        => other is not null && (ReferenceEquals(this, other) || Fields.SequenceEqual(other.Fields, StringComparer.Ordinal));

    /// <inheritdoc />
    public override int GetHashCode()
    {
        HashCode hash = new();
        foreach (string item in Fields)
        {
            hash.Add(item, StringComparer.Ordinal);
        }

        return hash.ToHashCode();
    }
}
