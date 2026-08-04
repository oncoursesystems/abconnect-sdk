using OnCourse.ABConnect.Models;
using OnCourse.ABConnect.Queries;
using Xunit;

namespace OnCourse.ABConnect.Tests;

/// <summary>
/// The query types are values, and each one overrides the record equality a positional record would
/// have synthesized, because the synthesized version compares the underlying collection by reference
/// and would report two sets built from identical names as different. A caller that memoizes a query,
/// keys a dictionary on a field set, or compares the query it built against the query it expected
/// depends on that override, so it is asserted rather than assumed.
/// </summary>
public sealed class QueryValueSemanticsTests
{
    [Fact]
    public void TwoStandardFieldSetsBuiltFromTheSameNamesAreEqual()
    {
        StandardFieldSet left = StandardFieldSet.Of("guid", "seq");
        StandardFieldSet right = StandardFieldSet.Of("guid", "seq");

        Assert.Equal(left, right);
        Assert.Equal(left.GetHashCode(), right.GetHashCode());
        Assert.True(left.Equals(left));
    }

    [Fact]
    public void StandardFieldSetsDifferingInOrderOrCaseAreNotEqual()
    {
        Assert.NotEqual(StandardFieldSet.Of("guid", "seq"), StandardFieldSet.Of("seq", "guid"));
        Assert.NotEqual(StandardFieldSet.Of("guid"), StandardFieldSet.Of("GUID"));
        Assert.NotEqual(StandardFieldSet.Of("guid"), StandardFieldSet.Of("guid", "seq"));
        Assert.False(StandardFieldSet.Of("guid").Equals(null));
    }

    [Fact]
    public void AStandardFieldSetNeedsAtLeastOneUsableName()
    {
        Assert.Throws<ArgumentNullException>(() => StandardFieldSet.Of(null!));
        Assert.Throws<ArgumentException>(() => StandardFieldSet.Of([]));
        Assert.Throws<ArgumentException>(() => StandardFieldSet.Of("guid", ""));
        Assert.Throws<ArgumentException>(() => StandardFieldSet.Of("guid", "   "));
        Assert.Throws<ArgumentException>(() => StandardFieldSet.Of("guid", null!));
    }

    [Fact]
    public void TwoEventFieldSetsBuiltFromTheSameNamesAreEqual()
    {
        EventFieldSet left = EventFieldSet.Of("seq", "change_type");
        EventFieldSet right = EventFieldSet.Of("seq", "change_type");

        Assert.Equal(left, right);
        Assert.Equal(left.GetHashCode(), right.GetHashCode());
        Assert.True(left.Equals(left));
        Assert.NotEqual(left, EventFieldSet.Of("change_type", "seq"));
        Assert.False(left.Equals(null));
    }

    [Fact]
    public void AnEventFieldSetNeedsAtLeastOneUsableName()
    {
        Assert.Throws<ArgumentNullException>(() => EventFieldSet.Of(null!));
        Assert.Throws<ArgumentException>(() => EventFieldSet.Of([]));
        Assert.Throws<ArgumentException>(() => EventFieldSet.Of("seq", ""));
        Assert.Throws<ArgumentException>(() => EventFieldSet.Of("seq", "  "));
    }

    /// <summary>
    /// The default event field set is every documented field, because an event read with a narrower
    /// set cannot be re-read: the feed only moves forward.
    /// </summary>
    [Fact]
    public void TheDefaultEventFieldSetIsEveryDocumentedField()
    {
        Assert.Contains("seq", EventFieldSet.Full.Fields);
        Assert.Contains("change_type", EventFieldSet.Full.Fields);
        Assert.Contains("affected_properties", EventFieldSet.Full.Fields);
        Assert.Contains("deleted_standard", EventFieldSet.Full.Fields);
        Assert.Same(EventFieldSet.Full, EventFieldSet.Full);
    }

    [Fact]
    public void TwoSortsBuiltFromTheSameKeysAreEqual()
    {
        StandardSort left = StandardSort.Of("-seq", "guid");
        StandardSort right = StandardSort.Of("-seq", "guid");

        Assert.Equal(left, right);
        Assert.Equal(left.GetHashCode(), right.GetHashCode());
        Assert.True(left.Equals(left));
        Assert.NotEqual(left, StandardSort.Of("guid", "-seq"));
        Assert.False(left.Equals(null));
    }

    [Fact]
    public void ASortNeedsAtLeastOneUsableKey()
    {
        Assert.Throws<ArgumentNullException>(() => StandardSort.Of(null!));
        Assert.Throws<ArgumentException>(() => StandardSort.Of([]));
        Assert.Throws<ArgumentException>(() => StandardSort.Of("seq", ""));
        Assert.Throws<ArgumentException>(() => StandardSort.Of("seq", " "));
    }

    /// <summary>
    /// The default standards sort is two keys, because <c>seq</c> alone is not unique within a
    /// document and a non-deterministic order across pages of an offset walk is how rows get read
    /// twice or missed entirely.
    /// </summary>
    [Fact]
    public void TheDefaultSortIsTwoKeys()
        => Assert.Equal(["seq", "guid"], StandardSort.Default.Keys);

    [Fact]
    public void TwoFiltersBuiltFromTheSameTermsAreEqual()
    {
        StandardsFilter left = StandardsFilter.ByDocument(Guid.Empty.ToString());
        StandardsFilter right = StandardsFilter.ByDocument(Guid.Empty.ToString());

        Assert.Equal(left, right);
        Assert.Equal(left.GetHashCode(), right.GetHashCode());
        Assert.True(left.Equals(left));
        Assert.False(left.Equals(null));
        Assert.NotEqual(left, StandardsFilter.BySection(Guid.Empty.ToString()));
    }

    /// <summary>
    /// Combining with an empty filter is the identity in both directions, so a caller building a
    /// filter up conditionally does not have to special-case the first term.
    /// </summary>
    [Fact]
    public void CombiningWithAnEmptyFilterIsTheIdentity()
    {
        StandardsFilter document = StandardsFilter.ByDocument(Guid.Empty.ToString());
        StandardsFilter empty = StandardsFilter.None;

        Assert.True(empty.IsEmpty);
        Assert.Same(document, document.And(empty));
        Assert.Same(document, empty.And(document));
        Assert.Equal(2, document.And(StandardsFilter.BySection(Guid.Empty.ToString())).Terms.Count);
        Assert.Throws<ArgumentNullException>(() => document.And(null!));
    }

    /// <summary>
    /// A page window advances by its own limit, which is the offset arithmetic AB Connect itself
    /// uses. The next window's limit is clamped like any other.
    /// </summary>
    [Fact]
    public void APageWindowAdvancesByItsOwnLimit()
    {
        PageRequest first = PageRequest.First;

        Assert.Equal(0, first.Offset);
        Assert.Equal(100, first.Limit);

        PageRequest second = first.Next(100);
        Assert.Equal(100, second.Offset);
        Assert.Equal(100, second.Limit);

        Assert.Equal(200, second.Next(50).Offset);
        Assert.Equal(100, second.Next(5000).Limit);
        Assert.Equal(0, PageRequest.MetaOnly.Limit);
        Assert.Throws<ArgumentOutOfRangeException>(() => first.Next(-1));
    }

    /// <summary>
    /// Grade extremes are chosen by AB Connect's own sequence rather than by array order, because the
    /// array arrives unordered and "lowest grade" read off position zero is how a K-12 document gets
    /// reported as starting at grade eleven. A grade with no sequence cannot be ordered, so the first
    /// entry stands in rather than the member reading as null.
    /// </summary>
    [Fact]
    public void GradeExtremesFollowTheReportedSequenceRatherThanArrayOrder()
    {
        EducationLevels levels = new()
        {
            Grades =
            [
                new Grade(11, "11", null, null),
                new Grade(9, "09", null, null),
                new Grade(12, "12", null, null),
                new Grade(10, "10", null, null),
            ],
        };

        Assert.Equal("09", levels.Lowest?.Code);
        Assert.Equal("12", levels.Highest?.Code);

        Assert.Null(new EducationLevels().Lowest);
        Assert.Null(new EducationLevels { Grades = [] }.Highest);

        EducationLevels unsequenced = new()
        {
            Grades = [new Grade(null, "KG", null, null), new Grade(null, "01", null, null)],
        };

        Assert.Equal("KG", unsequenced.Lowest?.Code);
        Assert.Equal("KG", unsequenced.Highest?.Code);
    }

    /// <summary>
    /// Each exception in the tree carries the three standard constructors, and the parameterless one
    /// still produces a message rather than the framework's default text naming the type.
    /// </summary>
    [Fact]
    public void EveryExceptionCarriesAUsableDefaultMessage()
    {
        Assert.NotEmpty(new ABConnectPagingException().Message);
        Assert.Equal("boom", new ABConnectPagingException("boom").Message);

        InvalidOperationException cause = new("cause");
        ABConnectPagingException withCause = new("boom", cause);
        Assert.Same(cause, withCause.InnerException);

        Assert.NotEmpty(new ABConnectConfigurationException().Message);
        Assert.Equal("boom", new ABConnectConfigurationException("boom").Message);
        Assert.Same(cause, new ABConnectConfigurationException("boom", cause).InnerException);
    }

    /// <summary>
    /// Every SDK failure is reachable through one catch of <see cref="ABConnectException"/>, which is
    /// the point of the tree: a caller that wants to handle everything can, and a caller that wants
    /// one case can name it.
    /// </summary>
    [Fact]
    public void EverySdkFailureIsAnABConnectException()
    {
        Assert.IsAssignableFrom<ABConnectException>(new ABConnectPagingException());
        Assert.IsAssignableFrom<ABConnectException>(new ABConnectConfigurationException());
    }
}
