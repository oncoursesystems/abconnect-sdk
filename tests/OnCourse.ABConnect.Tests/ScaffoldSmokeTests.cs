using OnCourse.ABConnect.Queries;
using Xunit;

namespace OnCourse.ABConnect.Tests;

/// <summary>
/// Guards the shared contract the rest of the test suite is written against. If one of these fails,
/// a public surface every other phase builds on has moved.
/// </summary>
public sealed class ScaffoldSmokeTests
{
    [Fact]
    public void DefaultOptionsMatchTheSpecifiedDefaults()
    {
        ABConnectOptions options = new();

        Assert.Equal(new Uri("https://api.abconnect.instructure.com/rest/v4.1/"), options.BaseAddress);
        Assert.Equal(TimeSpan.FromMinutes(15), options.SignatureLifetime);
        Assert.Equal(TimeSpan.FromSeconds(100), options.RequestTimeout);
        Assert.Equal(100, options.PageSize);
        Assert.False(options.AllowWildcardFields);
        Assert.Equal(25, options.Throttle.BucketCapacity);
        Assert.Equal(5, options.Throttle.TokensPerSecond);
        Assert.Equal(5, options.Retry.MaxAttempts);
    }

    [Fact]
    public void SnapshotFieldSetCarriesTheThirtyFiveMirrorFields()
    {
        Assert.Equal(35, StandardFieldSet.Snapshot.Fields.Count);
        Assert.DoesNotContain("number.alternate", StandardFieldSet.Snapshot.Fields);
        Assert.Contains("number.root_enhanced", StandardFieldSet.Snapshot.Fields);
        Assert.False(StandardFieldSet.Snapshot.IsWildcard);
    }

    [Fact]
    public void EventFieldSetFullCarriesTheElevenDocumentedFields()
    {
        Assert.Equal(11, EventFieldSet.Full.Fields.Count);
    }
}
