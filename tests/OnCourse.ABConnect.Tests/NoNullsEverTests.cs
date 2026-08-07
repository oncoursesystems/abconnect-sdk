using System.Reflection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using OnCourse.ABConnect.Models;
using OnCourse.ABConnect.Queries;
using OnCourse.ABConnect.Tests.Fakes;
using OnCourse.ABConnect.Throttling;
using Xunit;

namespace OnCourse.ABConnect.Tests;

/// <summary>
/// Invariant E1 as a property of the public surface rather than of one code path. No method on
/// <see cref="IABConnectClient"/> or <see cref="IABConnectFeed"/> may declare a nullable result, with
/// exactly two named exceptions, and every method must produce a non-null result from a minimal valid
/// response.
/// </summary>
public sealed class NoNullsEverTests
{
    /// <summary>
    /// The only methods allowed to return null, named individually so the exclusion cannot drift into
    /// a blanket allowance. All are probes documented as "answered, and there is none": the two
    /// single-object lookups, and the feed-head read which returns null for an empty feed.
    /// </summary>
    private static readonly string[] DocumentedProbeMethods =
    [
        nameof(IABConnectFeed.ReadDocumentAsync),
        nameof(IABConnectFeed.ReadPublicationAsync),
        nameof(IABConnectFeed.ReadHeadSequenceAsync),
    ];

    private const string StandardGuid = "1B2C3D4D-592E-11E6-A0F5-48E229C466BA";
    private const string DocumentGuid = "9D85340C-592E-11E6-A0F5-48E229C466BA";
    private const string PublicationGuid = "5B0F2E10-592E-11E6-A0F5-48E229C466BA";

    /// <summary>A standards page reporting a successful match of nothing.</summary>
    private const string EmptyStandardsPage =
        """
        {"links":{"self":null,"first":null,"prev":null,"next":null,"last":null},
        "meta":{"limit":100,"offset":0,"count":0,"took":4},"data":[]}
        """;

    /// <summary>A standards page carrying one row, with the count that makes the walk complete.</summary>
    private const string OneStandardPage =
        """
        {"links":{"self":null,"first":null,"prev":null,"next":null,"last":null},
        "meta":{"limit":100,"offset":0,"count":1,"took":4},
        "data":[{"id":"1B2C3D4D-592E-11E6-A0F5-48E229C466BA","type":"standards",
        "attributes":{"guid":"1B2C3D4D-592E-11E6-A0F5-48E229C466BA","seq":32,"status":"active"}}]}
        """;

    /// <summary>A single standard, as the lookup endpoint returns it.</summary>
    private const string SingleStandard =
        """
        {"meta":{"took":9},"data":{"id":"1B2C3D4D-592E-11E6-A0F5-48E229C466BA","type":"standards",
        "attributes":{"guid":"1B2C3D4D-592E-11E6-A0F5-48E229C466BA","status":"active"}}}
        """;

    /// <summary>An events page reporting a successful match of nothing.</summary>
    private const string EmptyEventsPage =
        """
        {"links":{"self":null,"first":null,"prev":null,"next":null,"last":null},
        "meta":{"limit":100,"offset":0,"count":0,"took":4},"data":[]}
        """;

    /// <summary>An events page carrying one event.</summary>
    private const string OneEventPage =
        """
        {"links":{"self":null,"first":null,"prev":null,"next":null,"last":null},
        "meta":{"limit":100,"offset":0,"count":1,"took":4},
        "data":[{"id":"3e81b2f7-47ac-4d10-9f0b-5c6ad2938e71","type":"events",
        "attributes":{"seq":702955,"date_utc":"2017-11-12 00:00:00","change_type":"added",
        "target":"standard","guid":"1B2C3D4D-592E-11E6-A0F5-48E229C466BA"}}]}
        """;

    /// <summary>
    /// A probe response: one row carrying the embedded document and publication the probes read.
    /// </summary>
    private const string ProbeRow =
        """
        {"links":{"self":null,"first":null,"prev":null,"next":null,"last":null},
        "meta":{"limit":1,"offset":0,"count":1163,"took":4},
        "data":[{"id":"1B2C3D4D-592E-11E6-A0F5-48E229C466BA","type":"standards",
        "attributes":{"document":{"guid":"9D85340C-592E-11E6-A0F5-48E229C466BA",
        "descr":"West Virginia College and Career Readiness Standards",
        "publication":{"guid":"5B0F2E10-592E-11E6-A0F5-48E229C466BA","acronym":"WVCCRS",
        "descr":"West Virginia Board of Education"}}}}]}
        """;

    /// <summary>
    /// No method on either interface declares a nullable result, except the two documented probes,
    /// which must remain nullable so the exclusion list stays honest.
    /// </summary>
    [Fact]
    public void NoInterfaceMethodDeclaresANullableResultExceptTheTwoDocumentedProbes()
    {
        NullabilityInfoContext nullability = new();
        List<string> unexpectedlyNullable = [];
        List<string> unexpectedlyNotNullable = [];

        foreach (MethodInfo method in InterfaceMethods())
        {
            bool isNullable = PayloadNullability(nullability, method) != NullabilityState.NotNull;
            bool isDocumentedProbe = DocumentedProbeMethods.Contains(method.Name, StringComparer.Ordinal);

            if (isNullable && !isDocumentedProbe)
            {
                unexpectedlyNullable.Add(Describe(method));
            }

            if (!isNullable && isDocumentedProbe)
            {
                unexpectedlyNotNullable.Add(Describe(method));
            }
        }

        Assert.Empty(unexpectedlyNullable);
        Assert.Empty(unexpectedlyNotNullable);
    }

    /// <summary>
    /// The exclusion list names exactly the probe methods, all on the feed, each documented as
    /// answering "there is none" rather than reporting a failure. If any other nullable-returning
    /// method is ever added, the test above fails rather than the exclusion silently widening.
    /// </summary>
    [Fact]
    public void TheOnlyNullableReturningMethodsAreTheNamedProbes()
    {
        NullabilityInfoContext nullability = new();

        string[] nullableMethods = [.. InterfaceMethods()
            .Where(method => PayloadNullability(nullability, method) != NullabilityState.NotNull)
            .Select(static method => method.Name)
            .OrderBy(static name => name, StringComparer.Ordinal)];

        Assert.Equal(
            [
                nameof(IABConnectFeed.ReadDocumentAsync),
                nameof(IABConnectFeed.ReadHeadSequenceAsync),
                nameof(IABConnectFeed.ReadPublicationAsync),
            ],
            nullableMethods);
    }

    /// <summary>
    /// Every method on both interfaces, driven against a minimal valid response, produces a non-null
    /// result. The case names are asserted to cover the whole surface by
    /// <see cref="EveryInterfaceMethodHasACase"/>.
    /// </summary>
    [Theory]
    [MemberData(nameof(MethodCases))]
    public async Task EveryMethodProducesANonNullResultFromAMinimalValidResponse(string methodName)
    {
        using Harness harness = new();

        object result = await InvokeAsync(methodName, harness);

        Assert.NotNull(result);
        Assert.True(harness.Handler.SendCount >= 1, "The method under test issued no HTTP request.");
    }

    /// <summary>
    /// The data-driven test above covers every method on both interfaces. A method added to either
    /// interface without a case fails here.
    /// </summary>
    [Fact]
    public void EveryInterfaceMethodHasACase()
    {
        string[] declared = [.. InterfaceMethods()
            .Select(static method => method.Name)
            .Distinct(StringComparer.Ordinal)
            .OrderBy(static name => name, StringComparer.Ordinal)];

        string[] covered = [.. MethodCases()
            .Select(static row => (string)row[0]!)
            .Distinct(StringComparer.Ordinal)
            .OrderBy(static name => name, StringComparer.Ordinal)];

        Assert.Equal(declared, covered);
        Assert.Equal(17, declared.Length);
    }

    /// <summary>Every method name driven by the data-driven test.</summary>
    public static IEnumerable<object[]> MethodCases()
    {
        yield return [nameof(IABConnectClient.GetStandardsAsync)];
        yield return [nameof(IABConnectClient.GetStandardAsync)];
        yield return [nameof(IABConnectClient.GetEventsAsync)];
        yield return [nameof(IABConnectClient.GetFacetAsync)];
        yield return [nameof(IABConnectClient.GetRegionFacetAsync)];
        yield return [nameof(IABConnectClient.GetAuthorityFacetAsync)];
        yield return [nameof(IABConnectClient.GetPublicationFacetAsync)];
        yield return [nameof(IABConnectClient.GetDocumentFacetAsync)];
        yield return [nameof(IABConnectClient.GetSectionFacetAsync)];
        yield return [nameof(IABConnectFeed.ReadHeadSequenceAsync)];
        yield return [nameof(IABConnectFeed.ReadEventsAsync)];
        yield return [nameof(IABConnectFeed.ReadEventPagesAsync)];
        yield return [nameof(IABConnectFeed.ReadDocumentSnapshotAsync)];
        yield return [nameof(IABConnectFeed.ReadDocumentPagesAsync)];
        yield return [nameof(IABConnectFeed.ReadDocumentAsync)];
        yield return [nameof(IABConnectFeed.ReadPublicationAsync)];
        yield return [nameof(IABConnectFeed.ReadStandardsByGuidsAsync)];
    }

    /// <summary>
    /// Queues the minimal valid response the named method needs, calls it, and returns the result as
    /// an object so the caller can assert only that it is not null. The streaming methods are
    /// materialized, and their pages are checked individually, because a non-null enumerable that
    /// yields a null page would still violate the invariant.
    /// </summary>
    private static async Task<object> InvokeAsync(string methodName, Harness harness)
    {
        switch (methodName)
        {
            case nameof(IABConnectClient.GetStandardsAsync):
                harness.Handler.EnqueueOk(EmptyStandardsPage);
                return await harness.Client.GetStandardsAsync(new StandardsQuery()).ConfigureAwait(false);

            case nameof(IABConnectClient.GetStandardAsync):
                harness.Handler.EnqueueOk(SingleStandard);
                return await harness.Client.GetStandardAsync(StandardGuid).ConfigureAwait(false);

            case nameof(IABConnectClient.GetEventsAsync):
                harness.Handler.EnqueueOk(EmptyEventsPage);
                return await harness.Client
                    .GetEventsAsync(new EventsQuery { AfterSequence = 0 })
                    .ConfigureAwait(false);

            case nameof(IABConnectClient.GetFacetAsync):
                harness.Handler.EnqueueOk(FacetBody(ABFacetNames.Regions));
                return await harness.Client
                    .GetFacetAsync(new FacetQuery<Region> { FacetName = ABFacetNames.Regions })
                    .ConfigureAwait(false);

            case nameof(IABConnectClient.GetRegionFacetAsync):
                harness.Handler.EnqueueOk(FacetBody(ABFacetNames.Regions));
                return await harness.Client.GetRegionFacetAsync().ConfigureAwait(false);

            case nameof(IABConnectClient.GetAuthorityFacetAsync):
                harness.Handler.EnqueueOk(FacetBody(ABFacetNames.Authorities));
                return await harness.Client.GetAuthorityFacetAsync().ConfigureAwait(false);

            case nameof(IABConnectClient.GetPublicationFacetAsync):
                harness.Handler.EnqueueOk(FacetBody(ABFacetNames.Publications));
                return await harness.Client.GetPublicationFacetAsync().ConfigureAwait(false);

            case nameof(IABConnectClient.GetDocumentFacetAsync):
                harness.Handler.EnqueueOk(FacetBody(ABFacetNames.Documents));
                return await harness.Client.GetDocumentFacetAsync().ConfigureAwait(false);

            case nameof(IABConnectClient.GetSectionFacetAsync):
                harness.Handler.EnqueueOk(FacetBody(ABFacetNames.Sections));
                return await harness.Client.GetSectionFacetAsync().ConfigureAwait(false);

            case nameof(IABConnectFeed.ReadHeadSequenceAsync):
                harness.Handler.EnqueueOk(OneEventPage);
                return await harness.Feed.ReadHeadSequenceAsync().ConfigureAwait(false)
                    ?? throw new InvalidOperationException("The feed-head probe returned null.");

            case nameof(IABConnectFeed.ReadEventsAsync):
                harness.Handler.EnqueueOk(EmptyEventsPage);
                return await harness.Feed.ReadEventsAsync(afterSequence: 0).ConfigureAwait(false);

            case nameof(IABConnectFeed.ReadEventPagesAsync):
                harness.Handler.EnqueueOk(OneEventPage);
                harness.Handler.EnqueueOk(EmptyEventsPage);
                return await CollectAsync(harness.Feed.ReadEventPagesAsync(afterSequence: 0))
                    .ConfigureAwait(false);

            case nameof(IABConnectFeed.ReadDocumentSnapshotAsync):
                harness.Handler.EnqueueOk(OneStandardPage);
                return await harness.Feed.ReadDocumentSnapshotAsync(DocumentGuid).ConfigureAwait(false);

            case nameof(IABConnectFeed.ReadDocumentPagesAsync):
                harness.Handler.EnqueueOk(OneStandardPage);
                return await CollectAsync(harness.Feed.ReadDocumentPagesAsync(DocumentGuid))
                    .ConfigureAwait(false);

            case nameof(IABConnectFeed.ReadDocumentAsync):
                harness.Handler.EnqueueOk(ProbeRow);
                return await harness.Feed.ReadDocumentAsync(DocumentGuid).ConfigureAwait(false)
                    ?? throw new InvalidOperationException("The document probe returned null.");

            case nameof(IABConnectFeed.ReadPublicationAsync):
                harness.Handler.EnqueueOk(ProbeRow);
                return await harness.Feed.ReadPublicationAsync(PublicationGuid).ConfigureAwait(false)
                    ?? throw new InvalidOperationException("The publication probe returned null.");

            case nameof(IABConnectFeed.ReadStandardsByGuidsAsync):
                harness.Handler.EnqueueOk(OneStandardPage);
                return await harness.Feed.ReadStandardsByGuidsAsync([StandardGuid]).ConfigureAwait(false);

            default:
                throw new InvalidOperationException($"No case is defined for method '{methodName}'.");
        }
    }

    /// <summary>
    /// Materializes a page stream, asserting that every page it yielded is itself non-null, and
    /// returns the collected pages.
    /// </summary>
    private static async Task<List<TPage>> CollectAsync<TPage>(IAsyncEnumerable<TPage> pages)
    {
        Assert.NotNull(pages);

        List<TPage> collected = [];
        await foreach (TPage page in pages.ConfigureAwait(false))
        {
            Assert.NotNull(page);
            collected.Add(page);
        }

        Assert.NotEmpty(collected);
        return collected;
    }

    /// <summary>A facet body carrying one value, with the facet name substituted in.</summary>
    private const string FacetTemplate =
        """
        {"meta":{"count":1163,"took":38,"facets":[{"facet":"FACET_NAME","count":1,
        "details":[{"count":1163,"data":{"guid":"5B0F2E10-592E-11E6-A0F5-48E229C466BA",
        "type":"state","code":"WV","acronym":"WVCCRS","adopt_year":"2015",
        "descr":"West Virginia"}}]}]},"data":[]}
        """;

    /// <summary>A facet body carrying one value for the named facet.</summary>
    private static string FacetBody(string facetName)
        => FacetTemplate.Replace("FACET_NAME", facetName, StringComparison.Ordinal);

    /// <summary>Every method declared on the two public interfaces.</summary>
    private static IEnumerable<MethodInfo> InterfaceMethods()
        => typeof(IABConnectClient).GetMethods()
            .Concat(typeof(IABConnectFeed).GetMethods())
            .Where(static method => !method.IsSpecialName);

    /// <summary>
    /// The nullability of what a method actually hands back: the payload inside
    /// <see cref="Task{TResult}"/> or <see cref="IAsyncEnumerable{T}"/>, not the wrapper.
    /// </summary>
    private static NullabilityState PayloadNullability(NullabilityInfoContext nullability, MethodInfo method)
    {
        NullabilityInfo info = nullability.Create(method.ReturnParameter);

        return info.GenericTypeArguments.Length == 1 && IsWrapper(method.ReturnType)
            ? info.GenericTypeArguments[0].ReadState
            : info.ReadState;
    }

    /// <summary>True when the return type is an asynchronous wrapper around a payload.</summary>
    private static bool IsWrapper(Type returnType)
    {
        if (!returnType.IsGenericType)
        {
            return false;
        }

        Type definition = returnType.GetGenericTypeDefinition();
        return definition == typeof(Task<>) || definition == typeof(IAsyncEnumerable<>);
    }

    /// <summary>Names a method for an assertion failure message.</summary>
    private static string Describe(MethodInfo method)
        => $"{method.DeclaringType?.Name}.{method.Name}";

    /// <summary>
    /// A client and a feed over the fake transport, with no handlers installed, so exactly one send
    /// happens per client call.
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

            FakeTimeProvider timeProvider = new();
            Handler = new FakeHttpMessageHandler();
            _httpClient = new HttpClient(Handler, disposeHandler: false)
            {
                BaseAddress = options.BaseAddress,
                Timeout = Timeout.InfiniteTimeSpan,
            };

            _rateLimiterProvider = new ABConnectRateLimiterProvider(Options.Create(options), timeProvider);

            Client = new ABConnectClient(
                _httpClient,
                Options.Create(options),
                _rateLimiterProvider,
                NullLogger<ABConnectClient>.Instance);

            Feed = new ABConnectFeed(
                Client,
                Options.Create(options),
                timeProvider,
                NullLogger<ABConnectFeed>.Instance);
        }

        public FakeHttpMessageHandler Handler { get; }

        public IABConnectClient Client { get; }

        public IABConnectFeed Feed { get; }

        public void Dispose()
        {
            _httpClient.Dispose();
            _rateLimiterProvider.Dispose();
            Handler.Dispose();
        }
    }
}
