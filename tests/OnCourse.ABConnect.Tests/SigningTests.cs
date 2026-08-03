using System.Globalization;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using OnCourse.ABConnect.Handlers;
using OnCourse.ABConnect.Http;
using OnCourse.ABConnect.Tests.Fakes;
using Xunit;

namespace OnCourse.ABConnect.Tests;

/// <summary>
/// Section 9 "Signing" test group, locking down defects 1, 2, and 3 and acceptance criterion AC-13.
/// </summary>
/// <remarks>
/// <para>
/// The worked example is AB Connect's own, from
/// <c>https://developerdocs.instructure.com/services/ab-connect/introduction/authentication.md</c>:
/// partner <c>test_account</c>, key <c>ajk84Hjk93h59skaAJ8732</c>, message <c>1512570029\n\nGET</c>,
/// signature <c>Sdcfa9xgRAUzQnlLik5nKj1ntqdB85jFYyFCkNxwD/M=</c>. It is asserted byte for byte rather
/// than by recomputing the HMAC the same way the implementation does, because recomputing would pass
/// for any self-consistent but wrong message shape.
/// </para>
/// <para>
/// Time is always a <see cref="FakeTimeProvider"/>. Nothing here sleeps, and nothing here touches a
/// socket: the innermost handler is <see cref="FakeHttpMessageHandler"/>.
/// </para>
/// </remarks>
public sealed class SigningTests
{
    private const string PartnerId = "test_account";
    private const string PartnerKey = "ajk84Hjk93h59skaAJ8732";

    /// <summary>The expiry AB Connect's published worked example signs.</summary>
    private const long WorkedExampleExpires = 1512570029L;

    /// <summary>The signature AB Connect publishes for <see cref="WorkedExampleExpires"/>.</summary>
    private const string WorkedExampleSignature = "Sdcfa9xgRAUzQnlLik5nKj1ntqdB85jFYyFCkNxwD/M=";

    /// <summary>
    /// The same signature as it must appear in the query string. Defect 1 is that the <c>/</c> and the
    /// <c>=</c> went on the wire raw.
    /// </summary>
    private const string WorkedExampleEncodedSignature = "Sdcfa9xgRAUzQnlLik5nKj1ntqdB85jFYyFCkNxwD%2FM%3D";

    /// <summary>
    /// An expiry whose signature under the same key contains <c>+</c>, <c>/</c>, and <c>=</c>, that is,
    /// all three base64 characters that are unsafe in a query string. Found by scanning expiries from
    /// the worked example forward.
    /// </summary>
    private const long PlusAndSlashExpires = 1512570033L;

    private const string PlusAndSlashSignature = "JgQvDnMm/NoVhLYxcQhrQIhN+wv3kDEIkGBrwfsmH2E=";

    private const string PlusAndSlashEncodedSignature = "JgQvDnMm%2FNoVhLYxcQhrQIhN%2Bwv3kDEIkGBrwfsmH2E%3D";

    private static readonly Uri BaseAddress = new("https://api.abconnect.instructure.com/rest/v4.1/");

    [Fact]
    public async Task WorkedExampleIsReproducedByteForByte()
    {
        // The clock is set so that now plus the default fifteen-minute lifetime is exactly the expiry
        // AB Connect's published example signs.
        FakeTimeProvider clock = ClockExpiringAt(WorkedExampleExpires, TimeSpan.FromMinutes(15));
        ABConnectOptions options = NewOptions();

        using SigningPipeline pipeline = new(options, clock);
        await pipeline.SendAsync("standards?limit=0");

        // The encoded constant below is the published signature and nothing else.
        Assert.Equal(WorkedExampleEncodedSignature, Uri.EscapeDataString(WorkedExampleSignature));

        Assert.Equal(
            "https://api.abconnect.instructure.com/rest/v4.1/standards?limit=0" +
            "&partner.id=test_account" +
            "&auth.signature=" + WorkedExampleEncodedSignature +
            "&auth.expires=1512570029",
            pipeline.Fake.LastRequestUri);
    }

    [Fact]
    public async Task SignatureIsPercentEncodedWhenItsBase64ContainsPlusAndSlash()
    {
        FakeTimeProvider clock = ClockExpiringAt(PlusAndSlashExpires, TimeSpan.FromMinutes(15));

        using SigningPipeline pipeline = new(NewOptions(), clock);
        await pipeline.SendAsync("events?limit=100");

        string emitted = QueryValue(pipeline.Fake.LastRequestUri, "auth.signature");

        // The fixture is only worth anything if its base64 really does contain both characters.
        Assert.Contains("+", PlusAndSlashSignature, StringComparison.Ordinal);
        Assert.Contains("/", PlusAndSlashSignature, StringComparison.Ordinal);

        Assert.Equal(PlusAndSlashEncodedSignature, emitted);
        Assert.Equal(PlusAndSlashSignature, Uri.UnescapeDataString(emitted));

        // Defect 1 in its own terms: a raw '+' in a query is decoded as a space and a raw '=' splits
        // the parameter, so neither may survive into the emitted value.
        Assert.DoesNotContain("+", emitted, StringComparison.Ordinal);
        Assert.DoesNotContain("/", emitted, StringComparison.Ordinal);
        Assert.DoesNotContain("=", emitted, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SignedMessageIsTheExpiryThenTwoLineFeedsThenGet()
    {
        // A key that is not AB Connect's, so this test cannot pass by coincidence with the worked
        // example above.
        const string key = "OnCourse-signing-shape-fixture-key";
        FakeTimeProvider clock = ClockExpiringAt(1600000000L, TimeSpan.FromMinutes(15));

        ABConnectOptions options = NewOptions();
        options.PartnerKey = key;

        using SigningPipeline pipeline = new(options, clock);
        await pipeline.SendAsync("standards");

        string emitted = Uri.UnescapeDataString(QueryValue(pipeline.Fake.LastRequestUri, "auth.signature"));

        Assert.Equal(Hmac(key, "1600000000\n\nGET"), emitted);

        // Every near miss AB Connect's rules would allow you to reach for. If the handler ever signs
        // one of these instead, this test fails rather than silently signing the wrong thing.
        Assert.NotEqual(Hmac(key, "1600000000\nGET"), emitted);
        Assert.NotEqual(Hmac(key, "1600000000\n\nGET\n"), emitted);
        Assert.NotEqual(Hmac(key, "1600000000\n\n"), emitted);
        Assert.NotEqual(Hmac(key, "1600000000\n\nGET\n/standards"), emitted);
        Assert.NotEqual(Hmac(key, "1600000000\n\nget"), emitted);
    }

    [Fact]
    public async Task ExpiryIsNowPlusTheConfiguredSignatureLifetime()
    {
        FakeTimeProvider clock = new(new DateTimeOffset(2026, 8, 2, 12, 0, 0, TimeSpan.Zero));

        ABConnectOptions options = NewOptions();
        options.SignatureLifetime = TimeSpan.FromMinutes(42);

        using SigningPipeline pipeline = new(options, clock);
        await pipeline.SendAsync("standards");

        long expected = clock.GetUtcNow().AddMinutes(42).ToUnixTimeSeconds();

        Assert.Equal(expected, ExpiresOf(pipeline.Fake.LastRequestUri));
    }

    [Fact]
    public async Task DefaultSignatureLifetimeIsFifteenMinutes()
    {
        FakeTimeProvider clock = new(new DateTimeOffset(2026, 8, 2, 12, 0, 0, TimeSpan.Zero));

        using SigningPipeline pipeline = new(NewOptions(), clock);
        await pipeline.SendAsync("standards");

        Assert.Equal(
            900L,
            ExpiresOf(pipeline.Fake.LastRequestUri) - clock.GetUtcNow().ToUnixTimeSeconds());
    }

    [Fact]
    public async Task SignatureIsReusedUntilSixtySecondsBeforeExpiryAndReMintedAfter()
    {
        FakeTimeProvider clock = new(new DateTimeOffset(2026, 8, 2, 12, 0, 0, TimeSpan.Zero));
        DateTimeOffset start = clock.GetUtcNow();

        using SigningPipeline pipeline = new(NewOptions(), clock);

        await pipeline.SendAsync("standards?offset=0");

        // 839 seconds in, 61 seconds of the 900-second lifetime remain, so the cached signature stands.
        clock.Advance(TimeSpan.FromSeconds(839));
        await pipeline.SendAsync("standards?offset=100");

        // One second later exactly 60 seconds remain, which is the renewal margin, so it is re-minted.
        clock.Advance(TimeSpan.FromSeconds(1));
        await pipeline.SendAsync("standards?offset=200");

        IReadOnlyList<string> uris = pipeline.Fake.RequestUris;
        Assert.Equal(3, uris.Count);

        string first = QueryValue(uris[0], "auth.signature");
        string second = QueryValue(uris[1], "auth.signature");
        string third = QueryValue(uris[2], "auth.signature");

        Assert.Equal(first, second);
        Assert.NotEqual(second, third);

        Assert.Equal(start.AddSeconds(900).ToUnixTimeSeconds(), ExpiresOf(uris[0]));
        Assert.Equal(start.AddSeconds(900).ToUnixTimeSeconds(), ExpiresOf(uris[1]));
        Assert.Equal(start.AddSeconds(840 + 900).ToUnixTimeSeconds(), ExpiresOf(uris[2]));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public async Task MissingPartnerKeyThrowsAndSendsNothing(string? partnerKey)
    {
        ABConnectOptions options = NewOptions();
        options.PartnerKey = partnerKey;

        using SigningPipeline pipeline = new(options, new FakeTimeProvider());

        ABConnectConfigurationException failure =
            await Assert.ThrowsAsync<ABConnectConfigurationException>(() => pipeline.SendAsync("standards"));

        Assert.Contains("PartnerKey", failure.Message, StringComparison.Ordinal);
        Assert.Equal(0, pipeline.Fake.SendCount);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public async Task MissingPartnerIdThrowsAndSendsNothing(string? partnerId)
    {
        ABConnectOptions options = NewOptions();
        options.PartnerId = partnerId;

        using SigningPipeline pipeline = new(options, new FakeTimeProvider());

        ABConnectConfigurationException failure =
            await Assert.ThrowsAsync<ABConnectConfigurationException>(() => pipeline.SendAsync("standards"));

        Assert.Contains("PartnerId", failure.Message, StringComparison.Ordinal);
        Assert.Equal(0, pipeline.Fake.SendCount);
    }

    [Fact]
    public async Task MissingCredentialFailureNeverNamesThePartnerKey()
    {
        ABConnectOptions options = NewOptions();
        options.PartnerId = null;

        using SigningPipeline pipeline = new(options, new FakeTimeProvider());

        ABConnectConfigurationException failure =
            await Assert.ThrowsAsync<ABConnectConfigurationException>(() => pipeline.SendAsync("standards"));

        Assert.DoesNotContain(PartnerKey, failure.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(PartnerKey, failure.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task ReSigningAnAlreadySignedRequestLeavesExactlyOneAuthFragment()
    {
        // This is the retry path: the retry handler re-sends the same message, so an implementation
        // that appended rather than replaced would accumulate one fragment per attempt.
        FakeTimeProvider clock = ClockExpiringAt(WorkedExampleExpires, TimeSpan.FromMinutes(15));

        using SigningPipeline pipeline = new(NewOptions(), clock);
        await pipeline.SendAsync(
            "standards?limit=0&partner.id=stale_account&auth.signature=stale%2Fsignature&auth.expires=1");

        string uri = pipeline.Fake.LastRequestUri;

        Assert.Equal(1, Occurrences(uri, "partner.id="));
        Assert.Equal(1, Occurrences(uri, "auth.signature="));
        Assert.Equal(1, Occurrences(uri, "auth.expires="));
        Assert.DoesNotContain("stale", uri, StringComparison.Ordinal);
        Assert.Equal(WorkedExampleEncodedSignature, QueryValue(uri, "auth.signature"));
        Assert.Equal("0", QueryValue(uri, "limit"));
    }

    [Fact]
    public async Task RequestPathIsRecordedBeforeAnyCredentialIsAppended()
    {
        FakeTimeProvider clock = ClockExpiringAt(WorkedExampleExpires, TimeSpan.FromMinutes(15));

        using SigningPipeline pipeline = new(NewOptions(), clock);
        await pipeline.SendAsync("standards?limit=0");

        ABConnectRequestContext? context = ABConnectRequestContext.From(pipeline.Fake.Requests[0]);

        Assert.NotNull(context);
        Assert.Contains("standards?limit=0", context!.RedactedPath, StringComparison.Ordinal);
        Assert.DoesNotContain("auth.", context.RedactedPath, StringComparison.Ordinal);
        Assert.DoesNotContain("partner.id", context.RedactedPath, StringComparison.Ordinal);
        Assert.DoesNotContain(PartnerKey, context.RedactedPath, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AnExistingRequestContextIsLeftUntouched()
    {
        FakeTimeProvider clock = ClockExpiringAt(WorkedExampleExpires, TimeSpan.FromMinutes(15));
        ABConnectRequestContext supplied = new("events?filter[events]=(seq GT 7)") { IsWildcardRequest = true };

        using SigningPipeline pipeline = new(NewOptions(), clock);
        await pipeline.SendAsync("events?limit=100", supplied);

        ABConnectRequestContext? seen = ABConnectRequestContext.From(pipeline.Fake.Requests[0]);

        Assert.Same(supplied, seen);
        Assert.Equal("events?filter[events]=(seq GT 7)", seen!.RedactedPath);
        Assert.True(seen.IsWildcardRequest);
    }

    [Fact]
    public async Task CredentialsAreAppendedWithAQuestionMarkWhenTheRequestHasNoQuery()
    {
        FakeTimeProvider clock = ClockExpiringAt(WorkedExampleExpires, TimeSpan.FromMinutes(15));

        using SigningPipeline pipeline = new(NewOptions(), clock);
        await pipeline.SendAsync("standards/9D85340C-5EDF-11DF-9E2E-B4A6DFD72085/");

        Assert.Equal(
            "https://api.abconnect.instructure.com/rest/v4.1/standards/9D85340C-5EDF-11DF-9E2E-B4A6DFD72085/" +
            "?partner.id=test_account" +
            "&auth.signature=" + WorkedExampleEncodedSignature +
            "&auth.expires=1512570029",
            pipeline.Fake.LastRequestUri);
    }

    [Fact]
    public async Task ThePartnerKeyNeverAppearsInAnEmittedUri()
    {
        FakeTimeProvider clock = ClockExpiringAt(WorkedExampleExpires, TimeSpan.FromMinutes(15));

        using SigningPipeline pipeline = new(NewOptions(), clock);
        await pipeline.SendAsync("standards?limit=0");
        await pipeline.SendAsync("events?limit=100");

        foreach (string uri in pipeline.Fake.RequestUris)
        {
            Assert.DoesNotContain(PartnerKey, uri, StringComparison.Ordinal);
            Assert.DoesNotContain("partner.key", uri, StringComparison.OrdinalIgnoreCase);
        }
    }

    /// <summary>
    /// Builds options with the worked example's credentials and AB Connect's real base address, so an
    /// emitted URI can be asserted in full.
    /// </summary>
    private static ABConnectOptions NewOptions() => new()
    {
        BaseAddress = BaseAddress,
        PartnerId = PartnerId,
        PartnerKey = PartnerKey,
    };

    /// <summary>
    /// A clock positioned so that <c>now + lifetime</c> is exactly <paramref name="expiresAtEpochSeconds"/>.
    /// </summary>
    private static FakeTimeProvider ClockExpiringAt(long expiresAtEpochSeconds, TimeSpan lifetime) =>
        new(DateTimeOffset.FromUnixTimeSeconds(expiresAtEpochSeconds) - lifetime);

    /// <summary>Reads the <c>auth.expires</c> value from an emitted URI as epoch seconds.</summary>
    private static long ExpiresOf(string uri) =>
        long.Parse(QueryValue(uri, "auth.expires"), CultureInfo.InvariantCulture);

    private static string Hmac(string key, string message) =>
        Convert.ToBase64String(
            HMACSHA256.HashData(Encoding.UTF8.GetBytes(key), Encoding.UTF8.GetBytes(message)));

    /// <summary>
    /// Reads one query parameter's value from an emitted URI without decoding it, so that the encoding
    /// itself can be asserted.
    /// </summary>
    private static string QueryValue(string uri, string name)
    {
        string query = new Uri(uri, UriKind.Absolute).Query.TrimStart('?');

        foreach (string pair in query.Split('&'))
        {
            int mark = pair.IndexOf('=', StringComparison.Ordinal);

            if (mark > 0 && string.Equals(pair[..mark], name, StringComparison.Ordinal))
            {
                return pair[(mark + 1)..];
            }
        }

        Assert.Fail($"The emitted URI carries no '{name}' parameter: {uri}");
        return string.Empty;
    }

    private static int Occurrences(string text, string value)
    {
        int count = 0;

        for (int index = text.IndexOf(value, StringComparison.Ordinal); index >= 0;
             index = text.IndexOf(value, index + value.Length, StringComparison.Ordinal))
        {
            count++;
        }

        return count;
    }

    /// <summary>
    /// The signing handler with a <see cref="FakeHttpMessageHandler"/> underneath it, driven through an
    /// <see cref="HttpMessageInvoker"/> so that a configuration failure surfaces exactly as the handler
    /// threw it.
    /// </summary>
    private sealed class SigningPipeline : IDisposable
    {
        private readonly HttpMessageInvoker _invoker;
        private readonly Uri _baseAddress;

        public SigningPipeline(ABConnectOptions options, TimeProvider clock)
        {
            _baseAddress = options.BaseAddress;
            _invoker = new HttpMessageInvoker(
                new ABConnectSigningHandler(Options.Create(options), clock) { InnerHandler = Fake });
        }

        public FakeHttpMessageHandler Fake { get; } = new();

        public async Task SendAsync(string relativeUri, ABConnectRequestContext? context = null)
        {
            Fake.EnqueueJson(HttpStatusCode.OK, "{}");

            using HttpRequestMessage request = new(HttpMethod.Get, new Uri(_baseAddress, relativeUri));
            context?.AttachTo(request);

            using HttpResponseMessage response = await _invoker
                .SendAsync(request, CancellationToken.None)
                .ConfigureAwait(false);
        }

        public void Dispose() => _invoker.Dispose();
    }
}
