using System.Net;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Runtime.CompilerServices;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using OnCourse.ABConnect.Handlers;
using OnCourse.ABConnect.Queries;
using OnCourse.ABConnect.Tests.Fakes;
using OnCourse.ABConnect.Throttling;
using Xunit;

namespace OnCourse.ABConnect.Tests;

/// <summary>
/// Every row of the status mapping in section 5.2, including the parsed JSON:API error list. The 401
/// and 404 bodies are the ones gate G-5 captured live from the devel environment and are read from
/// the fixture files verbatim rather than retyped here.
/// </summary>
public sealed class StatusMappingTests
{
    /// <summary>The guid used for lookup and filter tests. Well formed, so no argument guard fires.</summary>
    private const string DocumentGuid = "9D85340C-592E-11E6-A0F5-48E229C466BA";

    /// <summary>
    /// A 403 body in the shape AB Connect documents for an unlicensed but valid GUID. AB Connect does
    /// not publish a captured example of this one, so it is assembled from the documented shape rather
    /// than presented as a capture.
    /// </summary>
    private const string ForbiddenBody =
        """
        {"errors":[{"status":"403","title":"Forbidden",
        "detail":"Your license does not include this publication."}]}
        """;

    /// <summary>A 429 body. Bucket exhaustion, reported as a JSON:API error document.</summary>
    private const string ThrottledBody =
        """
        {"errors":[{"status":"429","title":"Too Many Requests","detail":"Rate limit exceeded."}]}
        """;

    /// <summary>A 5xx body.</summary>
    private const string ServerErrorBody =
        """
        {"errors":[{"status":"500","title":"Internal Server Error","detail":"Unexpected condition."}]}
        """;

    /// <summary>HTTP 401 is bad credentials or an unlicensed call. Both map to the same type.</summary>
    [Fact]
    public async Task UnauthorizedMapsToAuthenticationExceptionAndCarriesTheCapturedErrorBody()
    {
        using Harness harness = new();
        harness.Handler.EnqueueJson(HttpStatusCode.Unauthorized, Fixture("error-401-unauthorized.json"));

        ABConnectAuthenticationException failure = await Assert.ThrowsAsync<ABConnectAuthenticationException>(
            () => harness.Client.GetStandardsAsync(new StandardsQuery()));

        Assert.Equal(HttpStatusCode.Unauthorized, failure.StatusCode);
        Assert.Equal(1, failure.Attempts);
        Assert.StartsWith("standards", failure.RequestPath, StringComparison.Ordinal);

        ABConnectApiError error = Assert.Single(failure.Errors);
        Assert.Equal("401", error.Status);
        Assert.Equal("Unauthorized", error.Title);
        Assert.Equal("Signature is not authorized.", error.Detail);
        Assert.Null(error.SourcePointer);
        Assert.Null(error.SourceParameter);
        Assert.Contains("Signature is not authorized.", failure.Message, StringComparison.Ordinal);
    }

    /// <summary>HTTP 403 is a valid GUID outside the license.</summary>
    [Fact]
    public async Task ForbiddenMapsToNotLicensedExceptionAndCarriesTheDetailTheCallerMustInspect()
    {
        using Harness harness = new();
        harness.Handler.EnqueueJson(HttpStatusCode.Forbidden, ForbiddenBody);

        ABConnectNotLicensedException failure = await Assert.ThrowsAsync<ABConnectNotLicensedException>(
            () => harness.Client.GetStandardAsync(DocumentGuid));

        Assert.Equal(HttpStatusCode.Forbidden, failure.StatusCode);
        ABConnectApiError error = Assert.Single(failure.Errors);
        Assert.Equal("403", error.Status);
        Assert.Equal("Your license does not include this publication.", error.Detail);
    }

    /// <summary>
    /// HTTP 404, from the captured body whose <c>detail</c> is JSON null. A null detail must yield a
    /// parsed error with a null detail, not a dropped error.
    /// </summary>
    [Fact]
    public async Task NotFoundMapsToNotFoundExceptionAndKeepsAnErrorWhoseDetailIsJsonNull()
    {
        using Harness harness = new();
        harness.Handler.EnqueueJson(HttpStatusCode.NotFound, Fixture("error-404-not-found.json"));

        ABConnectNotFoundException failure = await Assert.ThrowsAsync<ABConnectNotFoundException>(
            () => harness.Client.GetStandardAsync(DocumentGuid));

        Assert.Equal(HttpStatusCode.NotFound, failure.StatusCode);
        ABConnectApiError error = Assert.Single(failure.Errors);
        Assert.Equal("404", error.Status);
        Assert.Equal("Not Found", error.Title);
        Assert.Null(error.Detail);
    }

    /// <summary>
    /// HTTP 400, with the <c>source</c> member populated, which is where a caller learns which
    /// parameter it got wrong.
    /// </summary>
    [Fact]
    public async Task BadRequestMapsToInvalidRequestExceptionAndParsesEverySourceMember()
    {
        using Harness harness = new();
        harness.Handler.EnqueueJson(HttpStatusCode.BadRequest, Fixture("error-400-invalid-filter.json"));

        ABConnectInvalidRequestException failure = await Assert.ThrowsAsync<ABConnectInvalidRequestException>(
            () => harness.Client.GetStandardsAsync(new StandardsQuery()));

        Assert.Equal(HttpStatusCode.BadRequest, failure.StatusCode);
        Assert.Equal(2, failure.Errors.Count);

        Assert.Equal("400", failure.Errors[0].Status);
        Assert.Equal("Bad Request", failure.Errors[0].Title);
        Assert.Equal("Unknown property 'document.titel' in filter expression.", failure.Errors[0].Detail);
        Assert.Equal("/data/attributes/document.titel", failure.Errors[0].SourcePointer);
        Assert.Equal("filter[standards]", failure.Errors[0].SourceParameter);

        Assert.Equal("Unknown field 'number.alternat' requested.", failure.Errors[1].Detail);
        Assert.Null(failure.Errors[1].SourcePointer);
        Assert.Equal("fields[standards]", failure.Errors[1].SourceParameter);
    }

    /// <summary>
    /// A 4xx that section 5.2 does not name individually still maps to a typed failure rather than
    /// falling through to a server error.
    /// </summary>
    [Fact]
    public async Task AnUnnamedClientErrorMapsToInvalidRequestException()
    {
        using Harness harness = new();
        harness.Handler.EnqueueJson((HttpStatusCode)418, ServerErrorBody);

        ABConnectInvalidRequestException failure = await Assert.ThrowsAsync<ABConnectInvalidRequestException>(
            () => harness.Client.GetStandardsAsync(new StandardsQuery()));

        Assert.Equal(418, (int)failure.StatusCode!.Value);
    }

    /// <summary>
    /// HTTP 429 with the retry budget spent. <c>Retry-After</c> in its delta-seconds form is surfaced
    /// as the delay the caller should wait, to the exact second the header named.
    /// </summary>
    [Fact]
    public async Task ThrottledMapsToThrottledExceptionWithRetryAfterPopulated()
    {
        using Harness harness = new();
        HttpResponseMessage response = new(HttpStatusCode.TooManyRequests)
        {
            Content = new StringContent(ThrottledBody, System.Text.Encoding.UTF8, "application/json"),
        };
        response.Headers.RetryAfter = new RetryConditionHeaderValue(TimeSpan.FromSeconds(7));
        harness.Handler.Enqueue(response);

        ABConnectThrottledException failure = await Assert.ThrowsAsync<ABConnectThrottledException>(
            () => harness.Client.GetEventsAsync(new EventsQuery { AfterSequence = 1 }));

        Assert.Equal(HttpStatusCode.TooManyRequests, failure.StatusCode);
        Assert.Equal(TimeSpan.FromSeconds(7), failure.RetryAfter);
        ABConnectApiError error = Assert.Single(failure.Errors);
        Assert.Equal("429", error.Status);
    }

    /// <summary>
    /// AB Connect does not document sending <c>Retry-After</c> at all, so its absence must leave the
    /// property null rather than inventing a delay.
    /// </summary>
    [Fact]
    public async Task ThrottledWithoutARetryAfterHeaderLeavesTheDelayNull()
    {
        using Harness harness = new();
        harness.Handler.EnqueueJson(HttpStatusCode.TooManyRequests, ThrottledBody);

        ABConnectThrottledException failure = await Assert.ThrowsAsync<ABConnectThrottledException>(
            () => harness.Client.GetEventsAsync(new EventsQuery { AfterSequence = 1 }));

        Assert.Null(failure.RetryAfter);
    }

    /// <summary>Every 5xx maps to the server exception, not just 500.</summary>
    [Theory]
    [InlineData(500)]
    [InlineData(502)]
    [InlineData(503)]
    [InlineData(599)]
    public async Task ServerErrorsMapToServerException(int statusCode)
    {
        using Harness harness = new();
        harness.Handler.EnqueueJson((HttpStatusCode)statusCode, ServerErrorBody);

        ABConnectServerException failure = await Assert.ThrowsAsync<ABConnectServerException>(
            () => harness.Client.GetStandardsAsync(new StandardsQuery()));

        Assert.Equal(statusCode, (int)failure.StatusCode!.Value);
        Assert.Equal(1, failure.Attempts);
    }

    /// <summary>
    /// A failure body that is not a JSON:API error document yields an empty error list and an excerpt
    /// of the raw body in the message, so the diagnosis is never lost.
    /// </summary>
    [Fact]
    public async Task AFailureBodyThatIsNotJsonYieldsNoErrorsAndAnExcerptOfTheBody()
    {
        using Harness harness = new();
        harness.Handler.EnqueueBody(
            HttpStatusCode.BadGateway,
            Fixture("error-non-json-body.txt"),
            "text/html");

        ABConnectServerException failure = await Assert.ThrowsAsync<ABConnectServerException>(
            () => harness.Client.GetStandardsAsync(new StandardsQuery()));

        Assert.Empty(failure.Errors);
        Assert.Contains("502 Bad Gateway", failure.Message, StringComparison.Ordinal);
    }

    /// <summary>A socket-level failure maps to the transport exception and keeps the cause.</summary>
    [Fact]
    public async Task ASocketFailureMapsToTransportExceptionAndPreservesTheCause()
    {
        using Harness harness = new();
        SocketException socketFailure = new((int)SocketError.ConnectionRefused);
        HttpRequestException transportFailure = new("Connection refused.", socketFailure);
        harness.Handler.EnqueueFailure(transportFailure);

        ABConnectTransportException failure = await Assert.ThrowsAsync<ABConnectTransportException>(
            () => harness.Client.GetStandardsAsync(new StandardsQuery()));

        Assert.Null(failure.StatusCode);
        Assert.Empty(failure.Errors);
        Assert.Same(transportFailure, failure.InnerException);
        Assert.StartsWith("standards", failure.RequestPath, StringComparison.Ordinal);
    }

    /// <summary>
    /// A timeout is a <see cref="TaskCanceledException"/> raised while the caller's own token is still
    /// uncancelled. It is a transport failure, not a cancellation.
    /// </summary>
    [Fact]
    public async Task ATimeoutMapsToTransportExceptionRatherThanSurfacingAsCancellation()
    {
        using Harness harness = new();
        TaskCanceledException timeout = new("The request timed out.");
        harness.Handler.EnqueueFailure(timeout);

        ABConnectTransportException failure = await Assert.ThrowsAsync<ABConnectTransportException>(
            () => harness.Client.GetStandardsAsync(new StandardsQuery()));

        Assert.Null(failure.StatusCode);
        Assert.Same(timeout, failure.InnerException);
    }

    /// <summary>
    /// Cancellation the caller asked for is not part of the mapping table. It surfaces as a bare
    /// <see cref="OperationCanceledException"/> and must not be an AB Connect exception, so that
    /// <c>catch (ABConnectException)</c> does not swallow a shutdown.
    /// </summary>
    [Fact]
    public async Task CallerCancellationSurfacesAsOperationCanceledAndNotAsAnAbConnectException()
    {
        using Harness harness = new();
        using CancellationTokenSource cancellation = new();
        cancellation.Cancel();

        OperationCanceledException failure = await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => harness.Client.GetStandardsAsync(new StandardsQuery(), cancellation.Token));

        Assert.IsNotAssignableFrom<ABConnectException>(failure);
    }

    /// <summary>
    /// A 2xx body that is not the expected envelope maps to the format exception, keeps the 2xx status
    /// code, and carries the deserializer's own complaint as the inner exception.
    /// </summary>
    [Fact]
    public async Task AMalformedSuccessBodyMapsToResponseFormatException()
    {
        using Harness harness = new();
        harness.Handler.EnqueueBody(HttpStatusCode.OK, "this is not json", "text/plain");

        ABConnectResponseFormatException failure = await Assert.ThrowsAsync<ABConnectResponseFormatException>(
            () => harness.Client.GetStandardsAsync(new StandardsQuery()));

        Assert.Equal(HttpStatusCode.OK, failure.StatusCode);
        Assert.Empty(failure.Errors);
        Assert.IsAssignableFrom<JsonException>(failure.InnerException);
        Assert.Contains("this is not json", failure.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// Every mapped failure carries the four things section 5.2 promises: the redacted path, the
    /// status code where one exists, a non-null error list, and an attempt count of at least one.
    /// </summary>
    [Theory]
    [InlineData(401)]
    [InlineData(403)]
    [InlineData(404)]
    [InlineData(400)]
    [InlineData(429)]
    [InlineData(500)]
    public async Task EveryMappedFailureCarriesThePathTheStatusTheErrorsAndTheAttemptCount(int statusCode)
    {
        using Harness harness = new();
        harness.Handler.EnqueueJson((HttpStatusCode)statusCode, Fixture("error-401-unauthorized.json"));

        ABConnectRequestException failure = await Assert.ThrowsAnyAsync<ABConnectRequestException>(
            () => harness.Client.GetStandardsAsync(new StandardsQuery()));

        Assert.Equal(statusCode, (int)failure.StatusCode!.Value);
        Assert.NotNull(failure.Errors);
        Assert.Single(failure.Errors);
        Assert.True(failure.Attempts >= 1);
        Assert.DoesNotContain("auth.signature", failure.RequestPath, StringComparison.Ordinal);
        Assert.DoesNotContain("partner.id", failure.RequestPath, StringComparison.Ordinal);
    }

    /// <summary>
    /// AC-3: every failure mode maps to a distinct type. Six statuses produce six different runtime
    /// types, all of them under <see cref="ABConnectRequestException"/>, so a caller can branch on the
    /// type rather than on a status code it would have to unpack.
    /// </summary>
    [Fact]
    public async Task EveryStatusMapsToADistinctExceptionType()
    {
        int[] statusCodes = [401, 403, 404, 400, 429, 500];
        List<Type> mapped = [];

        foreach (int statusCode in statusCodes)
        {
            using Harness harness = new();
            harness.Handler.EnqueueJson((HttpStatusCode)statusCode, ServerErrorBody);

            ABConnectRequestException failure = await Assert.ThrowsAnyAsync<ABConnectRequestException>(
                () => harness.Client.GetStandardsAsync(new StandardsQuery()));

            mapped.Add(failure.GetType());
        }

        Assert.Equal(statusCodes.Length, mapped.Distinct().Count());
        Assert.All(mapped, static type => Assert.True(
            typeof(ABConnectException).IsAssignableFrom(type),
            $"{type.Name} is not part of the ABConnectException hierarchy."));
    }

    /// <summary>
    /// The mapping is unchanged when the request goes through the real handler pipeline, and the
    /// request path on the exception is still the pre-signature path even though the wire URI carried
    /// the credential fragment.
    /// </summary>
    [Fact]
    public async Task TheMappingHoldsThroughTheRealHandlerPipeline()
    {
        using PipelineHarness harness = new();
        harness.Handler.EnqueueJson(HttpStatusCode.Unauthorized, Fixture("error-401-unauthorized.json"));

        ABConnectAuthenticationException failure = await Assert.ThrowsAsync<ABConnectAuthenticationException>(
            () => harness.Client.GetStandardsAsync(new StandardsQuery()));

        Assert.Equal(1, failure.Attempts);
        Assert.DoesNotContain("auth.signature", failure.RequestPath, StringComparison.Ordinal);
        Assert.Contains("auth.signature", harness.Handler.LastRequestUri, StringComparison.Ordinal);
        Assert.Single(failure.Errors);
    }

    /// <summary>
    /// Reads a fixture payload. Prefers a copy next to the test assembly, so the fixtures can later be
    /// declared as content in the test project, and falls back to the source tree, so the suite works
    /// today without that project change.
    /// </summary>
    private static string Fixture(string fileName)
    {
        string beside = Path.Combine(AppContext.BaseDirectory, "Fixtures", fileName);
        if (File.Exists(beside))
        {
            return File.ReadAllText(beside);
        }

        string inSourceTree = Path.Combine(SourceDirectory(), "Fixtures", fileName);
        Assert.True(File.Exists(inSourceTree), $"Fixture '{fileName}' was not found at '{inSourceTree}'.");
        return File.ReadAllText(inSourceTree);
    }

    /// <summary>The directory this test file lives in, resolved at compile time.</summary>
    private static string SourceDirectory([CallerFilePath] string callerFilePath = "")
        => Path.GetDirectoryName(callerFilePath)!;

    /// <summary>
    /// A client over the fake transport with no handlers installed, so exactly one send happens per
    /// call and the attempt count is always one.
    /// </summary>
    private sealed class Harness : IDisposable
    {
        private readonly HttpClient _httpClient;
        private readonly ABConnectRateLimiterProvider _rateLimiterProvider;

        public Harness()
        {
            ABConnectOptions options = new()
            {
                PartnerId = "test_account",
                PartnerKey = "ajk84Hjk93h59skaAJ8732",
                Retry = new ABConnectRetryOptions { MaxAttempts = 1 },
            };

            Handler = new FakeHttpMessageHandler();
            _httpClient = new HttpClient(Handler, disposeHandler: false)
            {
                BaseAddress = options.BaseAddress,
                Timeout = Timeout.InfiniteTimeSpan,
            };

            _rateLimiterProvider = new ABConnectRateLimiterProvider(
                Options.Create(options),
                new FakeTimeProvider());

            Client = new ABConnectClient(
                _httpClient,
                Options.Create(options),
                _rateLimiterProvider,
                NullLogger<ABConnectClient>.Instance);
        }

        public FakeHttpMessageHandler Handler { get; }

        public IABConnectClient Client { get; }

        public void Dispose()
        {
            _httpClient.Dispose();
            _rateLimiterProvider.Dispose();
            Handler.Dispose();
        }
    }

    /// <summary>
    /// A client over the real retry, throttle, and signing handlers. The retry budget is one attempt,
    /// so no backoff delay is ever scheduled and the test needs no time to pass, virtual or otherwise.
    /// </summary>
    private sealed class PipelineHarness : IDisposable
    {
        private readonly HttpClient _httpClient;
        private readonly ABConnectRateLimiterProvider _rateLimiterProvider;
        private readonly ABConnectRetryHandler _retryHandler;
        private readonly ABConnectThrottleHandler _throttleHandler;
        private readonly ABConnectSigningHandler _signingHandler;

        public PipelineHarness()
        {
            ABConnectOptions options = new()
            {
                PartnerId = "test_account",
                PartnerKey = "ajk84Hjk93h59skaAJ8732",
                Retry = new ABConnectRetryOptions { MaxAttempts = 1 },
            };

            IOptions<ABConnectOptions> wrapped = Options.Create(options);
            FakeTimeProvider timeProvider = new(new DateTimeOffset(2017, 12, 6, 13, 0, 29, TimeSpan.Zero));

            Handler = new FakeHttpMessageHandler();
            _rateLimiterProvider = new ABConnectRateLimiterProvider(wrapped, timeProvider);

            _signingHandler = new ABConnectSigningHandler(wrapped, timeProvider)
            {
                InnerHandler = Handler,
            };
            _throttleHandler = new ABConnectThrottleHandler(
                wrapped,
                _rateLimiterProvider,
                NullLogger<ABConnectThrottleHandler>.Instance)
            {
                InnerHandler = _signingHandler,
            };
            _retryHandler = new ABConnectRetryHandler(
                wrapped,
                _rateLimiterProvider,
                timeProvider,
                NullLogger<ABConnectRetryHandler>.Instance)
            {
                InnerHandler = _throttleHandler,
            };

            _httpClient = new HttpClient(_retryHandler, disposeHandler: false)
            {
                BaseAddress = options.BaseAddress,
                Timeout = Timeout.InfiniteTimeSpan,
            };

            Client = new ABConnectClient(
                _httpClient,
                wrapped,
                _rateLimiterProvider,
                NullLogger<ABConnectClient>.Instance);
        }

        public FakeHttpMessageHandler Handler { get; }

        public IABConnectClient Client { get; }

        public void Dispose()
        {
            _httpClient.Dispose();
            _retryHandler.Dispose();
            _throttleHandler.Dispose();
            _signingHandler.Dispose();
            _rateLimiterProvider.Dispose();
            Handler.Dispose();
        }
    }
}
