using System.Text.RegularExpressions;

namespace OnCourse.ABConnect.Queries;

/// <summary>
/// One equality term of a standards filter: a dotted AB Connect property path and the GUID it must
/// equal.
/// </summary>
/// <remarks>
/// Terms are always spelled with <c>.guid</c>, never <c>.id</c>. The two are equivalent per AB
/// Connect's standards reference, and settling on one spelling removes a class of copy-paste defect.
/// </remarks>
/// <param name="Field">The dotted property path, for example <c>document.publication.guid</c>.</param>
/// <param name="Value">The GUID the property must equal. Already validated when the term was created.</param>
public sealed record StandardsFilterTerm(string Field, string Value);

/// <summary>
/// One set-membership term of a standards filter: a dotted AB Connect property path and the GUIDs it
/// may match. Renders as <c>(field IN ('g1','g2', ...))</c>.
/// </summary>
/// <remarks>
/// This is how a batched GUID probe asks for many standards in one request instead of one lookup
/// each. Every GUID is validated when the term is created, exactly as an equality term's value is, so
/// the rendered <c>IN</c> list can never carry an unvalidated fragment. The synthesized record
/// equality would compare <see cref="Values"/> by reference, so <see cref="Equals(StandardsFilterSetTerm)"/>
/// compares the GUIDs as values instead.
/// </remarks>
/// <param name="Field">The dotted property path, for example <c>guid</c>.</param>
/// <param name="Values">The GUIDs the property may equal, in order. Already validated and non-empty.</param>
public sealed record StandardsFilterSetTerm(string Field, IReadOnlyList<string> Values)
{
    /// <summary>Compares two set terms by field and by their GUIDs, in order.</summary>
    /// <param name="other">The term to compare with.</param>
    /// <returns><see langword="true"/> when both carry the same field and the same GUIDs in the same order.</returns>
    public bool Equals(StandardsFilterSetTerm? other)
        => other is not null
           && (ReferenceEquals(this, other)
               || (string.Equals(Field, other.Field, StringComparison.Ordinal)
                   && Values.SequenceEqual(other.Values, StringComparer.Ordinal)));

    /// <inheritdoc />
    public override int GetHashCode()
    {
        HashCode hash = new();
        hash.Add(Field, StringComparer.Ordinal);
        foreach (string value in Values)
        {
            hash.Add(value, StringComparer.Ordinal);
        }

        return hash.ToHashCode();
    }
}

/// <summary>
/// An immutable conjunction of standards filter terms, built only through named constructors so
/// that no caller ever concatenates a filter expression by hand.
/// </summary>
/// <remarks>
/// Every GUID argument is validated on the way in and rejected with <see cref="ArgumentException"/>
/// if it does not match <c>^[0-9A-Fa-f-]{32,36}$</c>, so a stray quote or an interpolated fragment
/// cannot reach a filter expression. The whole expression is escaped exactly once when it is
/// rendered into a query string.
/// </remarks>
public sealed partial record StandardsFilter
{
    /// <summary>The AB Connect property path for a standard's document.</summary>
    public const string DocumentGuidField = "document.guid";

    /// <summary>The AB Connect property path for a standard's publication.</summary>
    public const string PublicationGuidField = "document.publication.guid";

    /// <summary>The AB Connect property path for a publication's authorities.</summary>
    public const string AuthorityGuidField = "document.publication.authorities.guid";

    /// <summary>The AB Connect property path for a standard's section.</summary>
    public const string SectionGuidField = "section.guid";

    /// <summary>The AB Connect property path for a standard's own GUID.</summary>
    public const string StandardGuidField = "guid";

    /// <summary>
    /// The largest GUID set a single <c>IN</c> term may carry. The vendor caps a page at 100 objects,
    /// and a standard's GUID is unique, so a set of at most 100 GUIDs matches at most 100 rows, which
    /// fits one page. It also keeps the rendered request line well under the API gateway's line-length
    /// limit. A caller with more GUIDs than this batches them.
    /// </summary>
    public const int MaxGuidSetSize = 100;

    private static readonly StandardsFilter EmptyFilter = new([], []);

    private StandardsFilter(
        IReadOnlyList<StandardsFilterTerm> terms,
        IReadOnlyList<StandardsFilterSetTerm> setTerms)
    {
        Terms = terms;
        SetTerms = setTerms;
    }

    /// <summary>
    /// The equality terms of the filter, combined with logical AND. Never null; empty for
    /// <see cref="None"/>. The query builder renders these into a single
    /// <c>filter[standards]</c> expression.
    /// </summary>
    public IReadOnlyList<StandardsFilterTerm> Terms { get; }

    /// <summary>
    /// The set-membership terms of the filter, combined with logical AND alongside <see cref="Terms"/>.
    /// Never null; empty unless the filter was built with <see cref="ByStandardGuids"/>.
    /// </summary>
    public IReadOnlyList<StandardsFilterSetTerm> SetTerms { get; }

    /// <summary>Whether this filter constrains nothing.</summary>
    public bool IsEmpty => Terms.Count == 0 && SetTerms.Count == 0;

    /// <summary>The filter that constrains nothing, so the query matches every licensed standard.</summary>
    public static StandardsFilter None => EmptyFilter;

    /// <summary>Restricts the query to the standards of one document.</summary>
    /// <param name="documentGuid">The AB Connect GUID of the document.</param>
    /// <returns>A filter with a single <c>document.guid</c> term.</returns>
    /// <exception cref="ArgumentException"><paramref name="documentGuid"/> is empty or is not a well-formed GUID.</exception>
    public static StandardsFilter ByDocument(string documentGuid)
        => Single(DocumentGuidField, documentGuid, nameof(documentGuid));

    /// <summary>Restricts the query to the standards of one publication.</summary>
    /// <param name="publicationGuid">The AB Connect GUID of the publication.</param>
    /// <returns>A filter with a single <c>document.publication.guid</c> term.</returns>
    /// <exception cref="ArgumentException"><paramref name="publicationGuid"/> is empty or is not a well-formed GUID.</exception>
    public static StandardsFilter ByPublication(string publicationGuid)
        => Single(PublicationGuidField, publicationGuid, nameof(publicationGuid));

    /// <summary>Restricts the query to the standards owned by one authority.</summary>
    /// <param name="authorityGuid">The AB Connect GUID of the authority.</param>
    /// <returns>A filter with a single <c>document.publication.authorities.guid</c> term.</returns>
    /// <exception cref="ArgumentException"><paramref name="authorityGuid"/> is empty or is not a well-formed GUID.</exception>
    public static StandardsFilter ByAuthority(string authorityGuid)
        => Single(AuthorityGuidField, authorityGuid, nameof(authorityGuid));

    /// <summary>Restricts the query to the standards of one section.</summary>
    /// <param name="sectionGuid">The AB Connect GUID of the section.</param>
    /// <returns>A filter with a single <c>section.guid</c> term.</returns>
    /// <exception cref="ArgumentException"><paramref name="sectionGuid"/> is empty or is not a well-formed GUID.</exception>
    public static StandardsFilter BySection(string sectionGuid)
        => Single(SectionGuidField, sectionGuid, nameof(sectionGuid));

    /// <summary>
    /// Restricts the query to standards whose own GUID is in the given set, rendered as a single
    /// <c>guid IN (...)</c> term.
    /// </summary>
    /// <remarks>
    /// This is the batched-probe filter: one request answers for a whole set of GUIDs instead of one
    /// lookup each. A GUID the vendor no longer serves is simply absent from the result, which is the
    /// fact a resync's leftover reconciliation wants to record, so absence is not an error here.
    /// </remarks>
    /// <param name="standardGuids">The AB Connect GUIDs to match. Order is preserved.</param>
    /// <returns>A filter with a single <c>guid</c> set term.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="standardGuids"/> is null.</exception>
    /// <exception cref="ArgumentException">
    /// The set is empty, holds more than <see cref="MaxGuidSetSize"/> GUIDs, or contains a value that
    /// is not a well-formed GUID.
    /// </exception>
    public static StandardsFilter ByStandardGuids(IEnumerable<string> standardGuids)
    {
        ArgumentNullException.ThrowIfNull(standardGuids);

        List<string> guids = standardGuids as List<string> ?? [.. standardGuids];
        if (guids.Count == 0)
        {
            throw new ArgumentException(
                "A GUID set filter must contain at least one GUID.",
                nameof(standardGuids));
        }

        if (guids.Count > MaxGuidSetSize)
        {
            throw new ArgumentException(
                $"A GUID set filter carries at most {MaxGuidSetSize} GUIDs, but {guids.Count} were " +
                "supplied. Batch the GUIDs and issue one filter per batch.",
                nameof(standardGuids));
        }

        string[] validated = new string[guids.Count];
        for (int index = 0; index < guids.Count; index++)
        {
            validated[index] = ValidateGuid(guids[index], nameof(standardGuids));
        }

        return new StandardsFilter([], [new StandardsFilterSetTerm(StandardGuidField, validated)]);
    }

    /// <summary>
    /// Combines this filter with another using logical AND. Neither operand is modified.
    /// </summary>
    /// <param name="other">The filter to combine with. <see cref="None"/> is a no-op.</param>
    /// <returns>A filter carrying the terms of both operands, this one's first.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="other"/> is null.</exception>
    public StandardsFilter And(StandardsFilter other)
    {
        ArgumentNullException.ThrowIfNull(other);

        if (other.IsEmpty)
        {
            return this;
        }

        if (IsEmpty)
        {
            return other;
        }

        List<StandardsFilterTerm> combined = new(Terms.Count + other.Terms.Count);
        combined.AddRange(Terms);
        combined.AddRange(other.Terms);

        List<StandardsFilterSetTerm> combinedSets = new(SetTerms.Count + other.SetTerms.Count);
        combinedSets.AddRange(SetTerms);
        combinedSets.AddRange(other.SetTerms);

        return new StandardsFilter(combined, combinedSets);
    }

    /// <summary>
    /// Validates a GUID argument against AB Connect's GUID shape.
    /// </summary>
    /// <param name="value">The candidate GUID.</param>
    /// <param name="parameterName">The name of the caller's parameter, used in the exception message.</param>
    /// <returns>The validated GUID, unchanged.</returns>
    /// <exception cref="ArgumentException">The value is null, empty, or does not match <c>^[0-9A-Fa-f-]{32,36}$</c>.</exception>
    public static string ValidateGuid(string value, string parameterName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value, parameterName);

        if (!GuidPattern().IsMatch(value))
        {
            throw new ArgumentException(
                $"'{value}' is not a well-formed AB Connect GUID. Expected 32 to 36 hexadecimal digits and dashes.",
                parameterName);
        }

        return value;
    }

    private static StandardsFilter Single(string field, string guid, string parameterName)
        => new([new StandardsFilterTerm(field, ValidateGuid(guid, parameterName))], []);

    [GeneratedRegex("^[0-9A-Fa-f-]{32,36}$", RegexOptions.CultureInvariant)]
    private static partial Regex GuidPattern();

    /// <summary>
    /// Compares two filters by their contents, in order.
    /// </summary>
    /// <remarks>
    /// The synthesized record equality would compare the <see cref="Terms"/> list by reference,
    /// so two instances built from the same contents would not be equal. These are values, so they
    /// compare as values.
    /// </remarks>
    /// <param name="other">The instance to compare with.</param>
    /// <returns><see langword="true"/> when both carry the same contents in the same order.</returns>
    public bool Equals(StandardsFilter? other)
        => other is not null
           && (ReferenceEquals(this, other)
               || (Terms.SequenceEqual(other.Terms) && SetTerms.SequenceEqual(other.SetTerms)));

    /// <inheritdoc />
    public override int GetHashCode()
    {
        HashCode hash = new();
        foreach (StandardsFilterTerm item in Terms)
        {
            hash.Add(item);
        }

        foreach (StandardsFilterSetTerm item in SetTerms)
        {
            hash.Add(item);
        }

        return hash.ToHashCode();
    }
}
