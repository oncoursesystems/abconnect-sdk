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

    private static readonly StandardsFilter EmptyFilter = new([]);

    private StandardsFilter(IReadOnlyList<StandardsFilterTerm> terms) => Terms = terms;

    /// <summary>
    /// The terms of the filter, combined with logical AND. Never null; empty for
    /// <see cref="None"/>. The query builder renders these into a single
    /// <c>filter[standards]</c> expression.
    /// </summary>
    public IReadOnlyList<StandardsFilterTerm> Terms { get; }

    /// <summary>Whether this filter constrains nothing.</summary>
    public bool IsEmpty => Terms.Count == 0;

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
        return new StandardsFilter(combined);
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
        => new([new StandardsFilterTerm(field, ValidateGuid(guid, parameterName))]);

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
        => other is not null && (ReferenceEquals(this, other) || Terms.SequenceEqual(other.Terms));

    /// <inheritdoc />
    public override int GetHashCode()
    {
        HashCode hash = new();
        foreach (StandardsFilterTerm item in Terms)
        {
            hash.Add(item);
        }

        return hash.ToHashCode();
    }
}
