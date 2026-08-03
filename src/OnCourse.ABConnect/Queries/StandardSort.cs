namespace OnCourse.ABConnect.Queries;

/// <summary>
/// The ordering applied to a standards query, rendered as a <c>sort[standards]</c> value.
/// </summary>
/// <remarks>
/// A deterministic sort is not optional for an offset-paged traversal. Without one, AB Connect
/// orders by relevance, and two pages taken at different offsets can overlap or skip rows without
/// any error being reported.
/// </remarks>
/// <param name="Keys">The sort keys, most significant first.</param>
public sealed record StandardSort(IReadOnlyList<string> Keys)
{
    private static readonly StandardSort DefaultSort = new(["seq", "guid"]);

    /// <summary>
    /// The two-key sort <c>seq,guid</c>. The first key is a documented attribute of a standard and
    /// the second is the entity's stable business key, so the ordering is total even where
    /// <c>seq</c> repeats. AB Connect recommends multi-key sorts for exactly this reason.
    /// </summary>
    public static StandardSort Default => DefaultSort;

    /// <summary>Builds a sort from an explicit list of AB Connect sort keys.</summary>
    /// <param name="keys">The sort keys, most significant first. Must contain at least one non-empty key.</param>
    /// <returns>A sort carrying exactly those keys, in the order given.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="keys"/> is null.</exception>
    /// <exception cref="ArgumentException"><paramref name="keys"/> is empty or contains a null, empty, or whitespace key.</exception>
    public static StandardSort Of(params string[] keys)
    {
        ArgumentNullException.ThrowIfNull(keys);

        if (keys.Length == 0)
        {
            throw new ArgumentException("A sort must contain at least one key.", nameof(keys));
        }

        foreach (string key in keys)
        {
            if (string.IsNullOrWhiteSpace(key))
            {
                throw new ArgumentException("A sort must not contain a null or empty key.", nameof(keys));
            }
        }

        return new StandardSort([.. keys]);
    }

    /// <summary>
    /// Compares two sorts by their contents, in order.
    /// </summary>
    /// <remarks>
    /// The synthesized record equality would compare the <see cref="Keys"/> list by reference,
    /// so two instances built from the same contents would not be equal. These are values, so they
    /// compare as values.
    /// </remarks>
    /// <param name="other">The instance to compare with.</param>
    /// <returns><see langword="true"/> when both carry the same contents in the same order.</returns>
    public bool Equals(StandardSort? other)
        => other is not null && (ReferenceEquals(this, other) || Keys.SequenceEqual(other.Keys, StringComparer.Ordinal));

    /// <inheritdoc />
    public override int GetHashCode()
    {
        HashCode hash = new();
        foreach (string item in Keys)
        {
            hash.Add(item, StringComparer.Ordinal);
        }

        return hash.ToHashCode();
    }
}
