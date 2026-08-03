using System.Net;
using System.Net.Http.Headers;
using System.Text;
using OnCourse.ABConnect.Http;
using OnCourse.ABConnect.Models;
using Xunit;

namespace OnCourse.ABConnect.Tests;

/// <summary>
/// The parts of <see cref="ABConnectResponseMapper"/> that the client's own tests do not reach: the
/// <c>Retry-After</c> header in both documented forms, the error-document members that arrive as a
/// type JSON:API does not specify, and the argument guards. StatusMappingTests covers the status to
/// exception mapping itself.
/// </summary>
public sealed class ResponseMapperTests
{
    private const string Path = "standards?limit=100&offset=0";

    /// <summary>
    /// The delta-seconds form, which is what AB Connect sends in practice. It is honored verbatim and
    /// is deliberately not capped at the configured maximum delay: the specification says honor it.
    /// </summary>
    [Fact]
    public async Task TheDeltaSecondsFormOfRetryAfterIsHonoredVerbatim()
    {
        using HttpResponseMessage response = Throttled();
        response.Headers.RetryAfter = new RetryConditionHeaderValue(TimeSpan.FromSeconds(45));

        ABConnectThrottledException failure = await Map<ABConnectThrottledException>(response);

        Assert.Equal(TimeSpan.FromSeconds(45), failure.RetryAfter);
    }

    /// <summary>
    /// A retry instant already in the past yields a zero delay rather than a negative one, which
    /// would otherwise be handed to a timer as an immediate-but-nonsensical value.
    /// </summary>
    [Theory]
    [InlineData(0)]
    [InlineData(-30)]
    public async Task ARetryAfterThatHasAlreadyPassedYieldsAZeroDelay(int seconds)
    {
        using HttpResponseMessage response = Throttled();
        response.Headers.RetryAfter = new RetryConditionHeaderValue(TimeSpan.FromSeconds(seconds));

        ABConnectThrottledException failure = await Map<ABConnectThrottledException>(response);

        Assert.Equal(TimeSpan.Zero, failure.RetryAfter);
    }

    /// <summary>
    /// The HTTP-date form is measured against the response's own <c>Date</c> header, which is the
    /// sending clock the retry instant was expressed against. That is what makes the resulting delay
    /// reproducible without the static mapper needing an injected clock.
    /// </summary>
    [Fact]
    public async Task TheHttpDateFormOfRetryAfterIsMeasuredAgainstTheResponseDate()
    {
        DateTimeOffset sentAt = new(2026, 8, 3, 12, 0, 0, TimeSpan.Zero);

        using HttpResponseMessage response = Throttled();
        response.Headers.Date = sentAt;
        response.Headers.RetryAfter = new RetryConditionHeaderValue(sentAt.AddSeconds(90));

        ABConnectThrottledException failure = await Map<ABConnectThrottledException>(response);

        Assert.Equal(TimeSpan.FromSeconds(90), failure.RetryAfter);
    }

    [Fact]
    public async Task AnHttpDateRetryAfterInThePastYieldsAZeroDelay()
    {
        DateTimeOffset sentAt = new(2026, 8, 3, 12, 0, 0, TimeSpan.Zero);

        using HttpResponseMessage response = Throttled();
        response.Headers.Date = sentAt;
        response.Headers.RetryAfter = new RetryConditionHeaderValue(sentAt.AddSeconds(-5));

        ABConnectThrottledException failure = await Map<ABConnectThrottledException>(response);

        Assert.Equal(TimeSpan.Zero, failure.RetryAfter);
    }

    /// <summary>
    /// With no <c>Date</c> header the local clock stands in, so the delay can only be asserted as a
    /// bound. The instant is far enough out that no plausible clock skew makes the assertion flaky,
    /// and the point being locked down is that the value is positive and sane rather than null or
    /// negative.
    /// </summary>
    [Fact]
    public async Task AnHttpDateRetryAfterWithNoResponseDateFallsBackToTheSystemClock()
    {
        using HttpResponseMessage response = Throttled();
        response.Headers.RetryAfter =
            new RetryConditionHeaderValue(TimeProvider.System.GetUtcNow().AddHours(1));

        ABConnectThrottledException failure = await Map<ABConnectThrottledException>(response);

        TimeSpan retryAfter = Assert.IsType<TimeSpan>(failure.RetryAfter);
        Assert.InRange(retryAfter, TimeSpan.FromMinutes(58), TimeSpan.FromMinutes(61));
    }

    /// <summary>
    /// AB Connect does not document sending the header at all, so its absence is not a failure: the
    /// exception simply carries no retry hint and the caller falls back to the configured backoff.
    /// </summary>
    [Fact]
    public async Task AnAbsentRetryAfterLeavesTheHintNull()
    {
        using HttpResponseMessage response = Throttled();

        ABConnectThrottledException failure = await Map<ABConnectThrottledException>(response);

        Assert.Null(failure.RetryAfter);
    }

    /// <summary>
    /// JSON:API specifies <c>status</c> as a string, but the captured 404 body from gate G-5 carries a
    /// null <c>detail</c> and other deployments send a numeric <c>status</c>. Both are read rather
    /// than dropping the error, because a dropped error leaves the caller with a status code and no
    /// explanation.
    /// </summary>
    [Fact]
    public void ErrorMembersAreReadWhateverJsonTypeTheyArriveAs()
    {
        IReadOnlyList<ABConnectApiError> errors = ABConnectResponseMapper.ParseErrors(
            """
            {"errors":[
              {"status":400,"title":"Bad Request","detail":null},
              {"status":"429","title":"Too Many Requests","detail":"Slow down.",
               "source":{"pointer":"/data/attributes/seq","parameter":"filter[standards]"}}]}
            """);

        Assert.Equal(2, errors.Count);

        Assert.Equal("400", errors[0].Status);
        Assert.Equal("Bad Request", errors[0].Title);
        Assert.Null(errors[0].Detail);

        Assert.Equal("429", errors[1].Status);
        Assert.Equal("Slow down.", errors[1].Detail);
        Assert.Equal("/data/attributes/seq", errors[1].SourcePointer);
        Assert.Equal("filter[standards]", errors[1].SourceParameter);
    }

    /// <summary>
    /// An error member that is an object or an array is not a scalar the SDK can render, so it reads
    /// as null rather than as the raw JSON text of a nested structure.
    /// </summary>
    [Fact]
    public void AnErrorMemberThatIsNotAScalarReadsAsNull()
    {
        IReadOnlyList<ABConnectApiError> errors = ABConnectResponseMapper.ParseErrors(
            """{"errors":[{"status":{"code":400},"title":["Bad","Request"],"detail":"Real detail."}]}""");

        ABConnectApiError error = Assert.Single(errors);
        Assert.Null(error.Status);
        Assert.Null(error.Title);
        Assert.Equal("Real detail.", error.Detail);
    }

    /// <summary>
    /// A body that is not a JSON:API error document yields an empty list rather than throwing. An
    /// error body arrives exactly when something has already gone wrong, so parsing it must be total:
    /// a parse failure here would replace a useful status-mapped exception with a useless one.
    /// </summary>
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("not json at all")]
    [InlineData("{}")]
    [InlineData("null")]
    [InlineData("[]")]
    [InlineData("""{"errors":null}""")]
    [InlineData("""{"errors":{}}""")]
    [InlineData("""{"errors":[]}""")]
    [InlineData("""{"errors":["a string, not an error object"]}""")]
    public void ABodyThatIsNotAnErrorDocumentYieldsAnEmptyListRatherThanThrowing(string? body)
        => Assert.Empty(ABConnectResponseMapper.ParseErrors(body));

    /// <summary>
    /// An error object carrying none of the members the SDK renders is still an error the service
    /// reported, so it is kept rather than dropped. Dropping it would understate how many things went
    /// wrong, and the count of errors is one of the few things a caller can act on when every member
    /// is unfamiliar.
    /// </summary>
    [Fact]
    public void AnErrorObjectWithNoRecognizedMembersIsKeptRatherThanDropped()
    {
        ABConnectApiError error = Assert.Single(
            ABConnectResponseMapper.ParseErrors("""{"errors":[{"unrelated":"member"}]}"""));

        Assert.Null(error.Title);
        Assert.Null(error.Detail);
        Assert.Null(error.Status);
        Assert.Null(error.SourcePointer);
        Assert.Null(error.SourceParameter);
    }

    [Fact]
    public async Task TheMapperRefusesNullArguments()
    {
        using HttpResponseMessage response = Throttled();
        ABConnectRequestContext context = new(Path);

        await Assert.ThrowsAsync<ArgumentNullException>(
            () => ABConnectResponseMapper.CreateExceptionAsync(null!, context));
        await Assert.ThrowsAsync<ArgumentNullException>(
            () => ABConnectResponseMapper.CreateExceptionAsync(response, null!));

        await Assert.ThrowsAsync<ArgumentNullException>(
            () => ABConnectResponseMapper.ReadAsync<ABPageDocument<Standard>>(null!, context, ABConnectJson.Default));
        await Assert.ThrowsAsync<ArgumentNullException>(
            () => ABConnectResponseMapper.ReadAsync<ABPageDocument<Standard>>(response, null!, ABConnectJson.Default));
        await Assert.ThrowsAsync<ArgumentNullException>(
            () => ABConnectResponseMapper.ReadAsync<ABPageDocument<Standard>>(response, context, null!));

        Assert.Throws<ArgumentNullException>(
            () => ABConnectResponseMapper.CreateTransportException(null!, context));
        Assert.Throws<ArgumentNullException>(
            () => ABConnectResponseMapper.CreateTransportException(new HttpRequestException("boom"), null!));
    }

    /// <summary>
    /// A successful read returns the deserialized document, and a body that will not deserialize is a
    /// format failure that keeps the successful status code. That combination is Invariant E1: either
    /// a non-null value deserialized from a 2xx body, or a throw.
    /// </summary>
    [Fact]
    public async Task ASuccessfulBodyIsReadAndAnUnreadableOneIsAFormatFailure()
    {
        using HttpResponseMessage ok = Ok("""{"data":[],"meta":{"count":0,"offset":0,"took":1,"limit":100}}""");

        ABPageDocument<Standard> document = await ABConnectResponseMapper.ReadAsync<ABPageDocument<Standard>>(
            ok,
            new ABConnectRequestContext(Path),
            ABConnectJson.Default);

        Assert.NotNull(document.Meta);
        Assert.Equal(0, document.Meta.Count);

        using HttpResponseMessage garbage = Ok("this is not an envelope");

        ABConnectResponseFormatException failure = await Assert.ThrowsAsync<ABConnectResponseFormatException>(
            () => ABConnectResponseMapper.ReadAsync<ABPageDocument<Standard>>(
                garbage,
                new ABConnectRequestContext(Path),
                ABConnectJson.Default));

        Assert.Equal(HttpStatusCode.OK, failure.StatusCode);
        Assert.IsType<System.Text.Json.JsonException>(failure.InnerException);
    }

    /// <summary>
    /// A 2xx body of JSON <c>null</c> deserializes to null, which is a format failure rather than an
    /// empty result. Confusing the two is the defect this release exists to remove.
    /// </summary>
    [Fact]
    public async Task ABodyOfJsonNullIsAFormatFailureRatherThanAnEmptyResult()
    {
        using HttpResponseMessage response = Ok("null");

        ABConnectResponseFormatException failure = await Assert.ThrowsAsync<ABConnectResponseFormatException>(
            () => ABConnectResponseMapper.ReadAsync<ABPageDocument<Standard>>(
                response,
                new ABConnectRequestContext(Path),
                ABConnectJson.Default));

        Assert.Equal(HttpStatusCode.OK, failure.StatusCode);
        Assert.Contains("null", failure.Message, StringComparison.Ordinal);
    }

    private static HttpResponseMessage Throttled() => new(HttpStatusCode.TooManyRequests)
    {
        Content = new StringContent("""{"errors":[{"status":"429","title":"Too Many Requests"}]}""", Encoding.UTF8, "application/json"),
    };

    private static HttpResponseMessage Ok(string body) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(body, Encoding.UTF8, "application/json"),
    };

    private static async Task<T> Map<T>(HttpResponseMessage response)
        where T : ABConnectRequestException
    {
        ABConnectRequestException failure = await ABConnectResponseMapper.CreateExceptionAsync(
            response,
            new ABConnectRequestContext(Path));

        return Assert.IsType<T>(failure);
    }
}
