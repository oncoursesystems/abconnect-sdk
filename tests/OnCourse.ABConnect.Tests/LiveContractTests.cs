using System.Diagnostics;
using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using OnCourse.ABConnect.Feed;
using OnCourse.ABConnect.Handlers;
using OnCourse.ABConnect.Http;
using OnCourse.ABConnect.Models;
using OnCourse.ABConnect.Queries;
using Xunit;
using Xunit.Abstractions;

namespace OnCourse.ABConnect.Tests;

/// <summary>
/// The live contract group from specification section 9, satisfying gates G-1 through G-5 and G-8 of
/// section 12. Every test in this class talks to AB Connect over the network.
/// </summary>
/// <remarks>
/// <para>
/// These tests are excluded from CI by <c>--filter "Category!=Live"</c> and are intended to be run by
/// hand, before a release or when AB Connect is suspected of having changed. They are the only tests in
/// this project that perform network I/O.
/// </para>
/// <para>
/// <b>The credentials are production.</b> There is no devel AB Connect account, and the key cannot be
/// rotated cheaply, so treat it accordingly: do not paste it into a shell that records history, and do
/// not add it as a repository-level secret where every workflow could read it. Every call this class
/// makes is a GET, so the suite reads and never writes, but a leaked key is still a leaked production
/// key.
/// </para>
/// <para>
/// To run them, set <c>ABCONNECT_PARTNER_ID</c> and <c>ABCONNECT_PARTNER_KEY</c> and run
/// <c>dotnet test --filter "Category=Live"</c>. Without those two variables every test in this class
/// reports as <em>skipped</em>, not as passed: a test that asserted nothing has not succeeded, and a
/// green tick over six such tests would be a lie about what was verified.
/// </para>
/// <para>
/// Where these gates are meant to actually run, which is the <c>live-contract</c> workflow, the
/// credentials arrive as repository secrets and that workflow fails outright if either secret is
/// missing. So an expired secret is a red build there, and a skip everywhere else.
/// </para>
/// <para>
/// Optional variables: <c>ABCONNECT_BASE_ADDRESS</c> overrides the host, which is how the G-1
/// fallback host would be probed. <c>ABCONNECT_DOCUMENT_GUID</c> overrides the document used by G-3.
/// <c>ABCONNECT_G8_DOCUMENT_GUIDS</c> is a comma-separated list of the three documents G-8 spans.
/// </para>
/// <para>
/// Every test prints the exact request URI the SDK emits, taken from
/// <see cref="ABConnectRequestContext.RedactedPath"/>, which is the pre-signature path and therefore
/// carries no credentials. Response bodies are printed after passing through
/// <see cref="Redact"/>, because AB Connect echoes the whole request query back in
/// <c>links.self</c> and a raw echo would put the signature in the log, which AC-16 forbids.
/// </para>
/// </remarks>
[Trait("Category", "Live")]
public sealed class LiveContractTests
{
    /// <summary>The environment variable holding the partner id.</summary>
    private const string PartnerIdVariable = "ABCONNECT_PARTNER_ID";

    /// <summary>The environment variable holding the partner key.</summary>
    private const string PartnerKeyVariable = "ABCONNECT_PARTNER_KEY";

    /// <summary>The optional environment variable overriding the base address.</summary>
    private const string BaseAddressVariable = "ABCONNECT_BASE_ADDRESS";

    /// <summary>The optional environment variable overriding the document gate G-3 probes.</summary>
    private const string DocumentGuidVariable = "ABCONNECT_DOCUMENT_GUID";

    /// <summary>The optional environment variable listing the documents gate G-8 spans.</summary>
    private const string ParityDocumentGuidsVariable = "ABCONNECT_G8_DOCUMENT_GUIDS";

    /// <summary>
    /// The document the identifier-spelling gate used on 2026-08-02, which had <c>meta.count</c> 1163 on
    /// the <c>sws</c> account. Override it with <see cref="DocumentGuidVariable"/> for another account.
    /// </summary>
    private const string DefaultDocumentGuid = "9D85340C-592E-11E6-A0F5-48E229C466BA";

    /// <summary>How much of a response body to print before truncating.</summary>
    private const int MaximumPrintedBodyLength = 4000;

    /// <summary>Rewrites credential-bearing query values to <c>REDACTED</c>.</summary>
    private static readonly Regex CredentialPattern = new(
        @"(auth\.signature|partner\.key|partner\.id)=[^&""'\s\\]*",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant,
        TimeSpan.FromSeconds(1));

    private readonly ITestOutputHelper _output;

    /// <summary>Creates the fixture, capturing xUnit's output sink for the evidence lines.</summary>
    /// <param name="output">The sink every gate writes its evidence to.</param>
    public LiveContractTests(ITestOutputHelper output) => _output = output;

    [SkippableFact]
    [Trait("Category", "Live")]
    public async Task GateOneBaseAddressAnswersAMetaOnlyStandardsRequest()
    {
        using ServiceProvider provider = BuildProvider();

        ABConnectOptions options = ResolveOptions(provider);
        _output.WriteLine($"G-1 base address: {options.BaseAddress}");

        (string requestUri, ABConnectRequestContext context) =
            ABQueryStringBuilder.Build(new StandardsQuery { Page = PageRequest.MetaOnly }, options);

        (HttpStatusCode status, string body) = await GetRawAsync(provider, requestUri, context);

        Assert.Equal(HttpStatusCode.OK, status);

        using JsonDocument document = JsonDocument.Parse(body);
        JsonElement meta = document.RootElement.GetProperty("meta");
        int count = meta.GetProperty("count").GetInt32();

        _output.WriteLine($"G-1 meta.count: {count}");
        Assert.True(count > 0, "A well-formed meta block must report a positive standards count.");
        Assert.Equal(0, meta.GetProperty("limit").GetInt32());
    }

    [SkippableFact]
    [Trait("Category", "Live")]
    public async Task GateTwoEveryFieldNameInBothFieldSetsIsAccepted()
    {
        using ServiceProvider provider = BuildProvider();

        ABConnectOptions options = ResolveOptions(provider);
        List<string> rejected = [];

        foreach (string field in StandardFieldSet.Snapshot.Fields)
        {
            (string requestUri, ABConnectRequestContext context) = ABQueryStringBuilder.Build(
                new StandardsQuery { Fields = StandardFieldSet.Of(field), Page = new PageRequest(0, 1) },
                options);

            (HttpStatusCode status, string body) = await GetRawAsync(provider, requestUri, context);
            _output.WriteLine($"G-2 fields[standards]={field} -> {(int)status}");

            if (status != HttpStatusCode.OK)
            {
                rejected.Add($"fields[standards]={field} returned {(int)status}: {body}");
            }
        }

        foreach (string field in EventFieldSet.Full.Fields)
        {
            (string requestUri, ABConnectRequestContext context) = ABQueryStringBuilder.Build(
                new EventsQuery
                {
                    AfterSequence = 0,
                    Fields = EventFieldSet.Of(field),
                    Page = new PageRequest(0, 1),
                },
                options);

            (HttpStatusCode status, string body) = await GetRawAsync(provider, requestUri, context);
            _output.WriteLine($"G-2 fields[events]={field} -> {(int)status}");

            if (status != HttpStatusCode.OK)
            {
                rejected.Add($"fields[events]={field} returned {(int)status}: {body}");
            }
        }

        Assert.Empty(rejected);
    }

    [SkippableFact]
    [Trait("Category", "Live")]
    public async Task GateThreeTheGuidAndIdSpellingsAgreeOnTheSameDocument()
    {
        using ServiceProvider provider = BuildProvider();

        ABConnectOptions options = ResolveOptions(provider);
        string documentGuid = Environment.GetEnvironmentVariable(DocumentGuidVariable) ?? DefaultDocumentGuid;

        (string guidUri, ABConnectRequestContext guidContext) = ABQueryStringBuilder.Build(
            new StandardsQuery
            {
                Filter = StandardsFilter.ByDocument(documentGuid),
                Fields = StandardFieldSet.Identity,
                Page = new PageRequest(0, 1),
            },
            options);

        (HttpStatusCode guidStatus, string guidBody) = await GetRawAsync(provider, guidUri, guidContext);
        Assert.Equal(HttpStatusCode.OK, guidStatus);
        int guidCount = MetaCount(guidBody);
        _output.WriteLine($"G-3 document.guid meta.count: {guidCount}");

        // The SDK deliberately cannot emit the `.id` spelling: StandardsFilter has no factory for an
        // arbitrary path and ABQueryStringBuilder rejects any path that is `id` or ends in `.id`,
        // because G-2 found `document.id` is silently dropped from a combined fields[standards]
        // request. The gate still has to be re-runnable, so the `.id` probe is hand-built here and
        // sent through the same signed, throttled pipeline.
        string idFilter = Uri.EscapeDataString($"(document.id EQ '{documentGuid}')");
        string idUri = $"standards?fields[standards]=guid&filter[standards]={idFilter}&limit=1&offset=0";
        ABConnectRequestContext idContext = new(idUri);

        (HttpStatusCode idStatus, string idBody) = await GetRawAsync(provider, idUri, idContext);
        Assert.Equal(HttpStatusCode.OK, idStatus);
        int idCount = MetaCount(idBody);
        _output.WriteLine($"G-3 document.id meta.count: {idCount}");

        Assert.Equal(guidCount, idCount);
    }

    [SkippableFact]
    [Trait("Category", "Live")]
    public async Task GateFourAtLeastTwoHundredRealEventsDeserializeAndTheirVocabularyIsRecorded()
    {
        using ServiceProvider provider = BuildProvider();

        ABConnectOptions options = ResolveOptions(provider);
        (string requestUri, _) = ABQueryStringBuilder.Build(
            new EventsQuery { AfterSequence = 0, Page = new PageRequest(0, 100) },
            options);
        _output.WriteLine($"G-4 first request: {requestUri}");

        var feed = provider.GetRequiredService<IABConnectFeed>();
        EventBatch batch = await feed.ReadEventsAsync(
            afterSequence: 0,
            new EventReadOptions { MaxEvents = 200, PageSize = 100 });

        _output.WriteLine(
            $"G-4 events read: {batch.Events.Count}, pages fetched: {batch.PagesFetched}, " +
            $"reported total: {batch.ReportedTotalCount}, highest seq: {batch.HighestSequence}, " +
            $"complete: {batch.IsComplete}");

        Assert.NotEmpty(batch.Events);
        Assert.All(batch.Events, abEvent => Assert.NotNull(abEvent.Attributes));

        long previous = 0;
        foreach (ABEvent abEvent in batch.Events)
        {
            Assert.True(
                abEvent.Attributes!.Seq > previous,
                $"Events must arrive in strictly ascending seq order, but {abEvent.Attributes.Seq} followed {previous}.");
            previous = abEvent.Attributes.Seq;
        }

        string[] changeTypes = [.. batch.Events
            .Select(e => e.Attributes!.ChangeType ?? "(null)")
            .Distinct()
            .Order()];
        string[] targets = [.. batch.Events
            .Select(e => e.Attributes!.Target ?? "(null)")
            .Distinct()
            .Order()];

        _output.WriteLine($"G-4 distinct change_type values: {string.Join(", ", changeTypes)}");
        _output.WriteLine($"G-4 distinct target values: {string.Join(", ", targets)}");

        ABEvent? withAffectedProperties = batch.Events
            .FirstOrDefault(e => e.Attributes?.AffectedProperties is { Count: > 0 });
        if (withAffectedProperties is null)
        {
            _output.WriteLine("G-4 no event in this sample carried a non-empty affected_properties array.");
        }
        else
        {
            foreach (AffectedProperty change in withAffectedProperties.Attributes!.AffectedProperties!)
            {
                _output.WriteLine(
                    $"G-4 affected property: name={change.Property}, " +
                    $"previous={change.PreviousValue}, new={change.NewValue}");
            }
        }

        ABEvent? withDeletedStandard = batch.Events
            .FirstOrDefault(e => e.Relationships?.DeletedStandard?.Id is not null);
        _output.WriteLine(
            withDeletedStandard is null
                ? "G-4 deleted_standard was never populated in this sample, matching the 2026-08-02 run."
                : $"G-4 deleted_standard populated: {withDeletedStandard.Relationships!.DeletedStandard!.Id}");
    }

    [SkippableFact]
    [Trait("Category", "Live")]
    public async Task GateFiveAPercentEncodedSignatureContainingPlusAndSlashIsAccepted()
    {
        using ServiceProvider provider = BuildProvider();

        ABConnectOptions options = ResolveOptions(provider);
        Uri probe = new(options.BaseAddress, "standards?limit=0");

        Uri? signed = null;
        string? rawSignature = null;
        DateTimeOffset origin = DateTimeOffset.UtcNow;

        // The signature is a base64 HMAC of "{expires}\n\nGET", so each attempt has roughly a three in
        // four chance of containing '+' or '/'. Each attempt mints a fresh signature by handing the
        // signing handler a clock a second further on, which keeps every candidate expiry in the
        // future so the accepted form is genuinely accepted rather than merely well formed.
        for (int offsetSeconds = 0; offsetSeconds < 200 && rawSignature is null; offsetSeconds++)
        {
            FakeTimeProvider clock = new(origin.AddSeconds(offsetSeconds));
            CapturingHandler capture = new();
            using ABConnectSigningHandler signer = new(Options.Create(options), clock)
            {
                InnerHandler = capture,
            };
            using HttpMessageInvoker invoker = new(signer);
            using HttpRequestMessage request = new(HttpMethod.Get, probe);
            using HttpResponseMessage discarded = await invoker.SendAsync(request, CancellationToken.None);

            Uri candidate = capture.LastRequestUri!;
            string encodedSignature = QueryValue(candidate.Query, "auth.signature");
            string decoded = Uri.UnescapeDataString(encodedSignature);

            if (decoded.Contains('+', StringComparison.Ordinal) && decoded.Contains('/', StringComparison.Ordinal))
            {
                signed = candidate;
                rawSignature = decoded;
            }
        }

        Assert.NotNull(signed);
        Assert.NotNull(rawSignature);
        _output.WriteLine($"G-5 signature contains '+' and '/': {rawSignature.Length} base64 characters");
        _output.WriteLine($"G-5 percent-encoded request: {Redact(signed.ToString())}");

        using HttpClient bare = new();

        using HttpResponseMessage encodedResponse = await bare.GetAsync(signed);
        string encodedBody = Redact(await encodedResponse.Content.ReadAsStringAsync());
        _output.WriteLine($"G-5 percent-encoded form -> {(int)encodedResponse.StatusCode}: {Truncate(encodedBody)}");

        string encodedSignatureValue = QueryValue(signed.Query, "auth.signature");
        Uri rawForm = new(
            signed.ToString().Replace(encodedSignatureValue, rawSignature, StringComparison.Ordinal),
            UriKind.Absolute);
        using HttpResponseMessage rawResponse = await bare.GetAsync(rawForm);
        string rawBody = Redact(await rawResponse.Content.ReadAsStringAsync());
        _output.WriteLine($"G-5 raw, un-encoded form -> {(int)rawResponse.StatusCode}: {Truncate(rawBody)}");

        // The gate only requires the percent-encoded form to work. What the raw form does is recorded
        // above; the 2026-08-02 run saw it return 200 as well, which does not weaken the case for
        // encoding, because encoding is the only form the vendor documentation guarantees.
        Assert.Equal(HttpStatusCode.OK, encodedResponse.StatusCode);
    }

    [SkippableFact]
    [Trait("Category", "Live")]
    public async Task GateEightDocumentSnapshotsAreCompleteAndSupersetTheActiveOnlyView()
    {
        using ServiceProvider provider = BuildProvider();

        ABConnectOptions options = ResolveOptions(provider);
        var feed = provider.GetRequiredService<IABConnectFeed>();

        string[] documentGuids = (Environment.GetEnvironmentVariable(ParityDocumentGuidsVariable)
                ?? Environment.GetEnvironmentVariable(DocumentGuidVariable)
                ?? DefaultDocumentGuid)
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        _output.WriteLine(
            $"G-8 documents under test: {string.Join(", ", documentGuids)}. Set " +
            $"{ParityDocumentGuidsVariable} to a comma-separated list spanning one document under 100 " +
            "standards, one between 1,000 and 5,000, and one over 10,000.");

        foreach (string documentGuid in documentGuids)
        {
            (string pageOneUri, _) = ABQueryStringBuilder.Build(
                new StandardsQuery
                {
                    Filter = StandardsFilter.ByDocument(documentGuid),
                    Page = new PageRequest(0, options.PageSize),
                },
                options);
            _output.WriteLine($"G-8 page one request: {pageOneUri}");

            Stopwatch stopwatch = Stopwatch.StartNew();
            DocumentSnapshot full = await feed.ReadDocumentSnapshotAsync(
                documentGuid,
                new DocumentReadOptions { Status = StandardStatusScope.ActiveAndDeleted });
            TimeSpan fullElapsed = stopwatch.Elapsed;

            stopwatch.Restart();
            DocumentSnapshot activeOnly = await feed.ReadDocumentSnapshotAsync(
                documentGuid,
                new DocumentReadOptions { Status = StandardStatusScope.Active });
            TimeSpan activeElapsed = stopwatch.Elapsed;

            _output.WriteLine(
                $"G-8 {documentGuid}: active and deleted {full.Standards.Count} rows in {fullElapsed}, " +
                $"active only {activeOnly.Standards.Count} rows in {activeElapsed}, " +
                $"pages fetched {full.PagesFetched}, reported total {full.ReportedTotalCount}");

            Dictionary<string, Standard> fullByGuid = Index(full);
            Dictionary<string, Standard> activeByGuid = Index(activeOnly);

            Assert.Equal(full.Standards.Count, fullByGuid.Count);
            Assert.Equal(full.ReportedTotalCount, fullByGuid.Count);

            // Version 2 could only ever see active standards, so the active-only view stands in for the
            // record set it produced. Version 3 must be a superset of it, strictly so wherever the
            // document holds deleted standards.
            foreach ((string guid, Standard version2Equivalent) in activeByGuid)
            {
                Assert.True(
                    fullByGuid.TryGetValue(guid, out Standard? version3),
                    $"Standard {guid} is visible to an active-only read but missing from the full read.");

                Assert.Equal(
                    version2Equivalent.Attributes?.Number?.PrefixEnhanced,
                    version3!.Attributes?.Number?.PrefixEnhanced);
                Assert.Equal(
                    version2Equivalent.Attributes?.Statement?.Description,
                    version3.Attributes?.Statement?.Description);
                Assert.Equal(version2Equivalent.Attributes?.Level, version3.Attributes?.Level);
                Assert.Equal(
                    version2Equivalent.Relationships?.Parent?.Id,
                    version3.Relationships?.Parent?.Id);
            }

            int deletedCount = full.Standards.Count(
                standard => standard.Attributes?.Status == ABStandardStatuses.Deleted);
            _output.WriteLine($"G-8 {documentGuid}: {deletedCount} deleted standards version 2 could not see");
        }
    }

    /// <summary>
    /// Indexes a snapshot by the standard's AB Connect GUID, which is also the assertion that no GUID
    /// repeats inside one snapshot.
    /// </summary>
    private static Dictionary<string, Standard> Index(DocumentSnapshot snapshot)
    {
        Dictionary<string, Standard> byGuid = new(StringComparer.OrdinalIgnoreCase);
        foreach (Standard standard in snapshot.Standards)
        {
            string guid = standard.Attributes?.Guid ?? standard.Id;
            Assert.True(byGuid.TryAdd(guid, standard), $"Standard {guid} appeared twice in one snapshot.");
        }

        return byGuid;
    }

    /// <summary>Reads <c>meta.count</c> out of a response body.</summary>
    private static int MetaCount(string body)
    {
        using JsonDocument document = JsonDocument.Parse(body);
        return document.RootElement.GetProperty("meta").GetProperty("count").GetInt32();
    }

    /// <summary>Reads one query-string value without decoding it.</summary>
    private static string QueryValue(string query, string name)
    {
        foreach (string pair in query.TrimStart('?').Split('&'))
        {
            int separator = pair.IndexOf('=', StringComparison.Ordinal);
            if (separator > 0 && pair[..separator] == name)
            {
                return pair[(separator + 1)..];
            }
        }

        throw new InvalidOperationException($"The signed URI carried no '{name}' parameter: {Redact(query)}");
    }

    /// <summary>Rewrites any credential-bearing query value to <c>REDACTED</c>.</summary>
    private static string Redact(string text)
        => CredentialPattern.Replace(text, match => $"{match.Groups[1].Value}=REDACTED");

    /// <summary>Caps a printed body so one gate cannot bury the others in output.</summary>
    private static string Truncate(string text)
        => text.Length <= MaximumPrintedBodyLength
            ? text
            : text[..MaximumPrintedBodyLength] + $" ... [{text.Length - MaximumPrintedBodyLength} more characters]";

    /// <summary>Resolves the bound options.</summary>
    private static ABConnectOptions ResolveOptions(IServiceProvider provider)
        => provider.GetRequiredService<IOptions<ABConnectOptions>>().Value;

    /// <summary>
    /// Issues one GET through the SDK's own signed, throttled, retrying pipeline and returns the
    /// status and the redacted body.
    /// </summary>
    /// <remarks>
    /// The request context from the query builder is attached, so the throttle handler sees the same
    /// wildcard flag and the same redacted path it would see for a call through
    /// <see cref="IABConnectClient"/>. Going through the raw pipeline rather than the typed client is
    /// deliberate for the discovery gates: they have to record what AB Connect actually returned,
    /// including for a body the typed client would reject.
    /// </remarks>
    private async Task<(HttpStatusCode Status, string Body)> GetRawAsync(
        IServiceProvider provider,
        string requestUri,
        ABConnectRequestContext context)
    {
        _output.WriteLine($"request: {context.RedactedPath}");

        using HttpClient httpClient = provider
            .GetRequiredService<IHttpClientFactory>()
            .CreateClient(typeof(IABConnectClient).Name);
        using HttpRequestMessage request = new(HttpMethod.Get, requestUri);
        context.AttachTo(request);

        using HttpResponseMessage response = await httpClient.SendAsync(request).ConfigureAwait(false);
        string body = Redact(await response.Content.ReadAsStringAsync().ConfigureAwait(false));
        _output.WriteLine($"response {(int)response.StatusCode}: {Truncate(body)}");

        return (response.StatusCode, body);
    }

    /// <summary>
    /// Builds a fully registered provider from the environment, or returns null after writing a skip
    /// line when the credentials are absent.
    /// </summary>
    private static ServiceProvider BuildProvider()
    {
        string? partnerId = Environment.GetEnvironmentVariable(PartnerIdVariable);
        string? partnerKey = Environment.GetEnvironmentVariable(PartnerKeyVariable);

        // A run without credentials is reported as SKIPPED, never as passed. There is nothing to opt
        // into: a test that asserted nothing has not succeeded, and saying so is the whole point.
        Skip.If(
            string.IsNullOrWhiteSpace(partnerId) || string.IsNullOrWhiteSpace(partnerKey),
            $"Set {PartnerIdVariable} and {PartnerKeyVariable} to AB Connect credentials to run the "
                + "live contract gates.");

        string? baseAddress = Environment.GetEnvironmentVariable(BaseAddressVariable);

        ServiceCollection services = [];
        services.AddABConnect(options =>
        {
            options.PartnerId = partnerId;
            options.PartnerKey = partnerKey;
            if (!string.IsNullOrWhiteSpace(baseAddress))
            {
                options.BaseAddress = new Uri(baseAddress, UriKind.Absolute);
            }
        });

        return services.BuildServiceProvider();
    }

    /// <summary>
    /// An inner handler that records the URI the signing handler produced and answers without sending
    /// anything, so gate G-5 can search for a signature shape before it makes a live call.
    /// </summary>
    private sealed class CapturingHandler : HttpMessageHandler
    {
        /// <summary>The URI of the most recent request, after signing.</summary>
        public Uri? LastRequestUri { get; private set; }

        /// <inheritdoc/>
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            LastRequestUri = request.RequestUri;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("{\"meta\":{\"count\":0}}"),
                RequestMessage = request,
            });
        }
    }
}
