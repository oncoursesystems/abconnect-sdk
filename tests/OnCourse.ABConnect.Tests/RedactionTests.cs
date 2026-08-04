using System.Globalization;
using System.Net;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using OnCourse.ABConnect.Handlers;
using OnCourse.ABConnect.Queries;
using OnCourse.ABConnect.Tests.Fakes;
using OnCourse.ABConnect.Throttling;
using Xunit;

namespace OnCourse.ABConnect.Tests;

/// <summary>
/// AC-16. No partner key and no unredacted signature appears in any exception, log message, or
/// <see cref="object.ToString"/> output. The partner key here is the one from AB Connect's own
/// documented worked example, so the scan has a concrete literal to look for.
/// </summary>
public sealed class RedactionTests
{
    /// <summary>
    /// The partner key from AB Connect's documented authentication example. It is the literal every
    /// assertion in this class scans for.
    /// </summary>
    private const string PartnerKey = "ajk84Hjk93h59skaAJ8732";

    private const string PartnerId = "test_account";

    /// <summary>
    /// A 500 body echoing the request query back in <c>links.self</c>, credential fragment included,
    /// which is what AB Connect actually does. A body excerpt is therefore not credential-free the way
    /// the recorded request path is.
    /// </summary>
    private const string BodyEchoingCredentials =
        """
        {"links":{"self":"https://api.abconnect.instructure.com/rest/v4.1/standards?limit=100&partner.id=test_account&auth.signature=Sdcfa9xgRAUzQnlLik5nKj1ntqdB85jFYyFCkNxwD%2FM%3D&auth.expires=1512570029&partner.key=ajk84Hjk93h59skaAJ8732"},
        "errors":[{"status":"500","title":"Internal Server Error","detail":"Unexpected condition."}]}
        """;

    /// <summary>
    /// The same echoed query, in a body that is not a JSON:API error document, so the mapper falls
    /// back to quoting an excerpt of the raw body in the message. This is the path where redaction
    /// visibly has to happen.
    /// </summary>
    private const string NonErrorBodyEchoingCredentials =
        """
        {"links":{"self":"https://api.abconnect.instructure.com/rest/v4.1/standards?limit=100&partner.id=test_account&auth.signature=Sdcfa9xgRAUzQnlLik5nKj1ntqdB85jFYyFCkNxwD%2FM%3D&auth.expires=1512570029&partner.key=ajk84Hjk93h59skaAJ8732"}}
        """;

    /// <summary>
    /// A 400 body whose error <c>detail</c> quotes the partner key back at the caller. The parsed
    /// error detail reaches the exception message, so it has to be redacted on that path too.
    /// </summary>
    private const string ErrorDetailEchoingCredentials =
        """
        {"errors":[{"status":"400","title":"Bad Request",
        "detail":"Unknown property in query 'partner.key=ajk84Hjk93h59skaAJ8732&auth.signature=Sdcfa9xgRAUzQnlLik5nKj1ntqdB85jFYyFCkNxwD/M='."}]}
        """;

    /// <summary>A standards page reporting a successful match of nothing.</summary>
    private const string EmptyStandardsPage =
        """
        {"links":{"self":null,"first":null,"prev":null,"next":null,"last":null},
        "meta":{"limit":100,"offset":0,"count":0,"took":4},"data":[]}
        """;

    /// <summary>
    /// Establishes that this suite is not vacuous. The credential fragment really is on the wire, so
    /// there really is something for the redaction to hide, and the partner key itself is never sent.
    /// </summary>
    [Fact]
    public async Task TheWireUriCarriesTheSignatureButNeverThePartnerKey()
    {
        using Harness harness = new();
        harness.Handler.EnqueueOk(EmptyStandardsPage);

        _ = await harness.Client.GetStandardsAsync(new StandardsQuery());

        string uri = harness.Handler.LastRequestUri;
        Assert.Contains("auth.signature=", uri, StringComparison.Ordinal);
        Assert.Contains("partner.id=test_account", uri, StringComparison.Ordinal);
        Assert.DoesNotContain(PartnerKey, uri, StringComparison.Ordinal);
        Assert.DoesNotContain("partner.key", uri, StringComparison.Ordinal);
    }

    /// <summary>
    /// A forced server failure whose body echoes the credential fragment and is quoted into the
    /// message as an excerpt. Neither the message, nor <see cref="object.ToString"/>, nor the recorded
    /// request path, nor any log line emitted during the call may contain the partner key or the
    /// signature that was minted for it, and the excerpt must visibly say so.
    /// </summary>
    [Fact]
    public async Task AServerFailureWhoseBodyExcerptEchoesCredentialsLeaksNothing()
    {
        using Harness harness = new();
        harness.Handler.EnqueueJson(HttpStatusCode.InternalServerError, NonErrorBodyEchoingCredentials);

        ABConnectServerException failure = await Assert.ThrowsAsync<ABConnectServerException>(
            () => harness.Client.GetStandardsAsync(new StandardsQuery()));

        harness.AssertNothingLeaked(failure);
        Assert.Contains("partner.key=REDACTED", failure.Message, StringComparison.Ordinal);
        Assert.Contains("auth.signature=REDACTED", failure.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// The same failure with a parsed JSON:API error document rather than a raw excerpt. The error
    /// details are what reach the message here, and nothing about the echoed query may survive.
    /// </summary>
    [Fact]
    public async Task AServerFailureWithAParsedErrorDocumentLeaksNothing()
    {
        using Harness harness = new();
        harness.Handler.EnqueueJson(HttpStatusCode.InternalServerError, BodyEchoingCredentials);

        ABConnectServerException failure = await Assert.ThrowsAsync<ABConnectServerException>(
            () => harness.Client.GetStandardsAsync(new StandardsQuery()));

        harness.AssertNothingLeaked(failure);
        Assert.Contains("Unexpected condition.", failure.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// A 2xx body that cannot be read as the expected envelope goes into an exception message twice:
    /// once as the deserializer's own complaint, which quotes the text it choked on, and once as an
    /// excerpt of the body. Both are text the SDK composes, so both have to be redacted.
    /// </summary>
    /// <remarks>
    /// <see cref="object.ToString"/> is not scanned, for the same reason as the transport case: the
    /// <see cref="System.Text.Json.JsonException"/> is preserved as the inner exception and its own
    /// text cannot be rewritten without discarding the cause.
    /// </remarks>
    [Fact]
    public async Task AFormatFailureWhoseBodyEchoesCredentialsLeaksNothing()
    {
        using Harness harness = new();
        harness.Handler.EnqueueBody(
            HttpStatusCode.OK,
            "not an envelope, and here is the query: " + NonErrorBodyEchoingCredentials,
            "text/plain");

        ABConnectResponseFormatException failure = await Assert.ThrowsAsync<ABConnectResponseFormatException>(
            () => harness.Client.GetStandardsAsync(new StandardsQuery()));

        harness.AssertNothingLeaked(failure, includeToString: false);
    }

    /// <summary>
    /// A transport failure quotes the underlying exception's own message, and a socket or TLS message
    /// can quote the full request URI, credential fragment included. The SDK's own message must be
    /// redacted.
    /// </summary>
    /// <remarks>
    /// <see cref="object.ToString"/> is deliberately not scanned here. Section 5.2 requires the
    /// original failure to be preserved as the inner exception, and
    /// <see cref="Exception.ToString"/> appends the inner exception's own text verbatim, which the SDK
    /// cannot rewrite without discarding the cause it is required to keep. In practice a .NET socket,
    /// DNS, or TLS message names the host and port rather than the query string, so the cause carries
    /// no credentials; the fabricated message here exists only to prove that the text the SDK itself
    /// composes is redacted.
    /// </remarks>
    [Fact]
    public async Task ATransportFailureWhoseCauseQuotesTheUriLeaksNothingFromItsOwnMessage()
    {
        using Harness harness = new();
        harness.Handler.EnqueueFailure(new HttpRequestException(
            "Connection refused while requesting " +
            "https://api.abconnect.instructure.com/rest/v4.1/standards?partner.id=test_account" +
            "&partner.key=ajk84Hjk93h59skaAJ8732" +
            "&auth.signature=Sdcfa9xgRAUzQnlLik5nKj1ntqdB85jFYyFCkNxwD%2FM%3D"));

        ABConnectTransportException failure = await Assert.ThrowsAsync<ABConnectTransportException>(
            () => harness.Client.GetStandardsAsync(new StandardsQuery()));

        harness.AssertNothingLeaked(failure, includeToString: false);
        Assert.Contains("partner.key=REDACTED", failure.Message, StringComparison.Ordinal);
        Assert.Contains("auth.signature=REDACTED", failure.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// The parsed JSON:API error detail is appended to the exception message verbatim, so a detail
    /// that quotes the query back leaks the credentials the body excerpt path is careful to redact.
    /// AC-16 is unconditional about where the text came from: no partner key and no unredacted
    /// signature in any exception message.
    /// </summary>
    [Fact]
    public async Task AnErrorDetailThatQuotesTheQueryLeaksNothing()
    {
        using Harness harness = new();
        harness.Handler.EnqueueJson(HttpStatusCode.BadRequest, ErrorDetailEchoingCredentials);

        ABConnectInvalidRequestException failure = await Assert.ThrowsAsync<ABConnectInvalidRequestException>(
            () => harness.Client.GetStandardsAsync(new StandardsQuery()));

        harness.AssertNothingLeaked(failure);
    }

    /// <summary>
    /// The recorded request path is the pre-signature path on every failure, so a caller that logs
    /// only <c>RequestPath</c> is safe by construction.
    /// </summary>
    [Theory]
    [InlineData(401)]
    [InlineData(403)]
    [InlineData(429)]
    [InlineData(500)]
    public async Task TheRecordedRequestPathNeverCarriesACredentialFragment(int statusCode)
    {
        using Harness harness = new();
        harness.Handler.EnqueueJson((HttpStatusCode)statusCode, BodyEchoingCredentials);

        ABConnectRequestException failure = await Assert.ThrowsAnyAsync<ABConnectRequestException>(
            () => harness.Client.GetStandardsAsync(new StandardsQuery()));

        Assert.StartsWith("standards?", failure.RequestPath, StringComparison.Ordinal);
        Assert.DoesNotContain("auth.signature", failure.RequestPath, StringComparison.Ordinal);
        Assert.DoesNotContain("auth.expires", failure.RequestPath, StringComparison.Ordinal);
        Assert.DoesNotContain("partner.", failure.RequestPath, StringComparison.Ordinal);
        harness.AssertNothingLeaked(failure);
    }

    /// <summary>
    /// A successful call still emits log lines, and they are held to the same standard: the debug
    /// diagnostics the client writes name the redacted path and nothing else.
    /// </summary>
    [Fact]
    public async Task LogLinesFromASuccessfulCallCarryOnlyTheRedactedPath()
    {
        using Harness harness = new();
        harness.Handler.EnqueueOk(EmptyStandardsPage);

        _ = await harness.Client.GetStandardsAsync(new StandardsQuery());

        Assert.NotEmpty(harness.LogLines);
        string signature = harness.SignatureFromLastRequest();

        foreach (string line in harness.LogLines)
        {
            Assert.DoesNotContain(PartnerKey, line, StringComparison.Ordinal);
            Assert.DoesNotContain(signature, line, StringComparison.Ordinal);
            Assert.DoesNotContain("auth.signature=", line, StringComparison.Ordinal);
        }
    }

    /// <summary>
    /// A client over the real retry, throttle, and signing handlers, so a real signature is minted and
    /// really goes on the wire, with every log line those components emit captured. The retry budget
    /// is one attempt, so no backoff is ever scheduled and no time has to pass.
    /// </summary>
    private sealed class Harness : IDisposable
    {
        private readonly HttpClient _httpClient;
        private readonly ABConnectRateLimiterProvider _rateLimiterProvider;
        private readonly ABConnectRetryHandler _retryHandler;
        private readonly ABConnectThrottleHandler _throttleHandler;
        private readonly ABConnectSigningHandler _signingHandler;
        private readonly List<string> _logLines = [];

        public Harness()
        {
            ABConnectOptions options = new()
            {
                PartnerId = PartnerId,
                PartnerKey = PartnerKey,
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
                new RecordingLogger<ABConnectThrottleHandler>(_logLines))
            {
                InnerHandler = _signingHandler,
            };
            _retryHandler = new ABConnectRetryHandler(
                wrapped,
                _rateLimiterProvider,
                timeProvider,
                new RecordingLogger<ABConnectRetryHandler>(_logLines))
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
                new RecordingLogger<ABConnectClient>(_logLines));
        }

        public FakeHttpMessageHandler Handler { get; }

        public IABConnectClient Client { get; }

        /// <summary>Every log line emitted through the SDK's loggers during this harness's lifetime.</summary>
        public IReadOnlyList<string> LogLines => _logLines;

        /// <summary>
        /// The percent-encoded signature the signing handler actually minted, read back off the wire so
        /// the scan looks for the real value rather than a value the test invented.
        /// </summary>
        public string SignatureFromLastRequest()
        {
            string uri = Handler.LastRequestUri;
            const string marker = "auth.signature=";
            int start = uri.IndexOf(marker, StringComparison.Ordinal);
            Assert.True(start >= 0, $"No signature was found in '{uri}'.");

            start += marker.Length;
            int end = uri.IndexOf('&', start);
            return end < 0 ? uri[start..] : uri[start..end];
        }

        /// <summary>
        /// Asserts that a failure leaked nothing: not through its message, not through
        /// <see cref="object.ToString"/>, not through the recorded request path, and not through any
        /// log line emitted while it was being produced. Both the percent-encoded signature that went
        /// on the wire and its decoded form are searched for, because a body can echo either.
        /// </summary>
        public void AssertNothingLeaked(ABConnectRequestException failure, bool includeToString = true)
        {
            ArgumentNullException.ThrowIfNull(failure);

            string encodedSignature = SignatureFromLastRequest();
            string decodedSignature = Uri.UnescapeDataString(encodedSignature);

            List<(string Label, string Text)> surfaces =
            [
                ("Message", failure.Message),
                ("RequestPath", failure.RequestPath),
            ];

            if (includeToString)
            {
                surfaces.Add(("ToString()", failure.ToString()));
            }

            for (int index = 0; index < _logLines.Count; index++)
            {
                surfaces.Add(($"log line {index}", _logLines[index]));
            }

            foreach ((string label, string text) in surfaces)
            {
                AssertClean(label, "the partner key", PartnerKey, text);
                AssertClean(label, "the minted signature", encodedSignature, text);
                AssertClean(label, "the decoded signature", decodedSignature, text);
            }
        }

        /// <summary>Fails with the offending surface named, so a leak is diagnosable at a glance.</summary>
        private static void AssertClean(string label, string what, string secret, string text)
            => Assert.False(
                text.Contains(secret, StringComparison.Ordinal),
                $"{label} contains {what}: {text}");

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

    /// <summary>
    /// A logger that records everything, at every level, so the scan sees the diagnostics a production
    /// host would write. Both the formatted message and every structured value are recorded, because a
    /// credential could leak through either.
    /// </summary>
    private sealed class RecordingLogger<TCategory>(List<string> sink) : ILogger<TCategory>
    {
        public IDisposable BeginScope<TState>(TState state)
            where TState : notnull
            => NullScope.Instance;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            ArgumentNullException.ThrowIfNull(formatter);

            lock (sink)
            {
                sink.Add(string.Format(
                    CultureInfo.InvariantCulture,
                    "[{0}] {1} {2}",
                    logLevel,
                    eventId,
                    formatter(state, exception)));

                if (state is IEnumerable<KeyValuePair<string, object?>> values)
                {
                    foreach (KeyValuePair<string, object?> value in values)
                    {
                        sink.Add(string.Format(
                            CultureInfo.InvariantCulture,
                            "  {0}={1}",
                            value.Key,
                            value.Value));
                    }
                }

                if (exception is not null)
                {
                    sink.Add(exception.ToString());
                }
            }
        }

        private sealed class NullScope : IDisposable
        {
            public static readonly NullScope Instance = new();

            public void Dispose()
            {
                // Nothing to release. Scopes exist only so the logging contract is satisfied.
            }
        }
    }
}
