![Instructure logo](https://raw.githubusercontent.com/oncoursesystems/abconnect-sdk/master/instructure.png)

# OnCourse.ABConnect

[![License: MIT](https://img.shields.io/badge/license-MIT-blue.svg)](https://opensource.org/licenses/MIT)
[![Build Status](https://github.com/oncoursesystems/abconnect-sdk/actions/workflows/ci.yml/badge.svg?branch=master)](https://github.com/oncoursesystems/abconnect-sdk/actions/workflows/ci.yml)
[![NuGet Version](https://img.shields.io/nuget/v/OnCourse.ABConnect)](https://www.nuget.org/packages/OnCourse.ABConnect/)

A .NET client for the [Academic Benchmarks AB Connect API](https://developerdocs.instructure.com/services/ab-connect),
version 4.1. It reads academic standards and the change-event feed that tracks them.

The SDK handles the parts of AB Connect that are easy to get wrong: HMAC request signing, the
per-account rate limit, retries with backoff, and paging that stays correct while the data underneath
it changes. You write queries and read models.

- **Reads only.** Every AB Connect Standards and Events call is a GET, and so is every method here.
- **Covers** the Standards and Events resources, including facets and the change feed.
- **Does not cover** Assets, Alignments, Topics, Concepts, Associations, Predictions, or Standard
  Collections.

Upgrading from 2.x? Start with [What changed in 3.0](#what-changed-in-30).

## Requirements

`net8.0` or later, and AB Connect partner credentials. Version 3 drops `net6.0` and `net7.0`; if you
need those, stay on 2.0.0.

```
dotnet add package OnCourse.ABConnect
```

## Quickstart

Add your credentials to configuration:

```json
{
  "ABConnect": {
    "PartnerId": "your-partner-id",
    "PartnerKey": "your-partner-key"
  }
}
```

Register the SDK and read a document:

```csharp
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using OnCourse.ABConnect;
using OnCourse.ABConnect.Feed;

internal static class Quickstart
{
    public static async Task RunAsync(string documentGuid, CancellationToken cancellationToken)
    {
        using IHost host = Host.CreateDefaultBuilder()
            .ConfigureServices((context, services) => services.AddABConnect(context.Configuration))
            .Build();

        IABConnectFeed feed = host.Services.GetRequiredService<IABConnectFeed>();

        DocumentSnapshot snapshot = await feed.ReadDocumentSnapshotAsync(
            documentGuid,
            cancellationToken: cancellationToken);

        Console.WriteLine($"{snapshot.Standards.Count} standards, read at {snapshot.FetchedAtUtc:O}");
    }
}
```

That call pages through the whole document, verifies it read every row exactly once, and returns when
it has all of them. Rate limiting, signing, and retries happen underneath without configuration.

`AddABConnect(configuration)` binds the `ABConnect` section. Two other overloads take an
`IConfigurationSection`, for settings under a different name, or an `Action<ABConnectOptions>` for
configuration in code:

```csharp
using Microsoft.Extensions.DependencyInjection;
using OnCourse.ABConnect;

var services = new ServiceCollection();

services.AddABConnect(options =>
{
    options.PartnerId = "your-partner-id";
    options.PartnerKey = "your-partner-key";
    options.PageSize = 100;
});
```

Configuration is validated when the host starts, and every problem is reported in one message rather
than one at a time.

## The two services

Registration provides two:

| Service | Use it for |
|---|---|
| `IABConnectFeed` | Reading a whole document or a whole delta. Owns paging, ordering, and completeness. Start here. |
| `IABConnectClient` | One request, one response, no paging. Use it when you want exact control over a single call. |

### Empty results and failures are never the same thing

Every method on both services either returns a value or throws. No method returns null, and no method
returns an empty object standing in for a failure.

So an `ABPage<T>` whose `Data.Count == 0` means AB Connect answered and matched nothing. An
`EventBatch` whose `Events.Count == 0` means the service answered and there is nothing past your
watermark. Neither is an error, and an error can never look like either of them. A loop that stops on
"no more data" cannot be tricked into stopping by an expired credential or a 500.

Two methods return a nullable type, `ReadDocumentAsync` and `ReadPublicationAsync`. They are existence
probes, so `null` from them means AB Connect answered and there is no such document or publication. It
never means the request failed.

## Configuration

`PartnerId` and `PartnerKey` are the only required keys. Every other key has a working default.

| Key | Type | Default | Meaning |
|---|---|---|---|
| `ABConnect:PartnerId` | string | none, **required** | AB Connect partner id. |
| `ABConnect:PartnerKey` | string | none, **required** | AB Connect partner key, used to sign every request. Never logged, never included in an exception message. |
| `ABConnect:BaseAddress` | absolute URI ending in `/` | `https://api.abconnect.instructure.com/rest/v4.1/` | API root. The former `api.abconnect.certicaconnect.com` host now resolves to the same backend. |
| `ABConnect:SignatureLifetime` | timespan, 1 minute to 24 hours | `00:15:00` | How long a minted signature stays valid. The signature is reused until 60 seconds before it expires, then re-minted. |
| `ABConnect:RequestTimeout` | timespan, positive | `00:01:40` | Timeout **per attempt**, not per logical call. A retried call may legitimately take longer in total. |
| `ABConnect:PageSize` | int, 1 to 100 | `100` | Page size for list calls. AB Connect caps this at 100 and the SDK will never send more. |
| `ABConnect:AllowWildcardFields` | bool | `false` | Permits `fields[...]=*` and `facet_summary=*`. Discovery only. With it off, a wildcard request throws `ABConnectConfigurationException` before any request is sent. |
| `ABConnect:Throttle:Enabled` | bool | `true` | Turns the client-side token bucket off. Do this only in tests. |
| `ABConnect:Throttle:BucketCapacity` | int, at least 1 | `25` | Burst size. AB Connect's documented burst allowance. |
| `ABConnect:Throttle:TokensPerSecond` | double, positive | `5` | Sustained rate. AB Connect's documented sustained allowance. |
| `ABConnect:Throttle:WildcardBucketCapacity` | int, at least 1 | `2` | Burst size of the separate, narrower bucket a wildcard request also spends from. |
| `ABConnect:Throttle:WildcardTokensPerSecond` | double, positive | `2` | Sustained rate of the wildcard bucket. AB Connect rate-limits wildcard field requests at 2 per second. |
| `ABConnect:Throttle:QueueLimit` | int, at least 0 | `1000` | How many callers may wait for a token. Exceeding it throws `ABConnectThrottledException` without sending a request. |
| `ABConnect:Retry:MaxAttempts` | int, at least 1 | `5` | Total attempts, not additional attempts. `1` disables retrying. |
| `ABConnect:Retry:BaseDelay` | timespan, at least zero | `00:00:01` | First backoff step. |
| `ABConnect:Retry:MaxDelay` | timespan, at least `BaseDelay` | `00:00:30` | Ceiling on computed backoff. A `Retry-After` header is honored verbatim and is not capped by this. |
| `ABConnect:Retry:HonorRetryAfter` | bool | `true` | Prefer a `Retry-After` header over computed backoff when the response carries one. |

## Handling errors

Every failure is a typed exception. There is no result type to inspect, no null to check, and no
status field to forget about.

```
ABConnectException                          abstract, the root of everything below
├── ABConnectConfigurationException         missing or invalid configuration, thrown before any request
├── ABConnectPagingException                the service answered but the answers do not compose
└── ABConnectRequestException               abstract; a request was made and did not produce a result
    ├── ABConnectAuthenticationException    401
    ├── ABConnectNotLicensedException       403
    ├── ABConnectNotFoundException          404
    ├── ABConnectInvalidRequestException    other 4xx, typically a rejected filter
    ├── ABConnectThrottledException         429 after retries were exhausted; carries RetryAfter
    ├── ABConnectServerException            5xx
    ├── ABConnectTransportException         socket failure or per-attempt timeout; the cause is the InnerException
    └── ABConnectResponseFormatException    2xx whose body could not be read as the documented shape
```

Every `ABConnectRequestException` carries:

- `RequestPath`, the request path **with the authentication query fragment removed**. `partner.id`,
  `auth.signature`, and `auth.expires` are never present. Any body excerpt in the message is
  redacted the same way, because AB Connect echoes your request query back in `links.self`.
- `StatusCode`, where one exists.
- `Errors`, the parsed JSON:API `errors[]` array. Never null; empty when the body carried none.
- `Attempts`, how many times the request was actually sent.

A cancellation you requested surfaces as a plain `OperationCanceledException`, per .NET convention.
It is never wrapped in an `ABConnect*` exception, and it is never retried.

```csharp
using OnCourse.ABConnect;
using OnCourse.ABConnect.Feed;

internal static class DeltaPull
{
    public static async Task<int> RunAsync(
        IABConnectFeed feed,
        long watermark,
        CancellationToken cancellationToken)
    {
        try
        {
            EventBatch batch = await feed.ReadEventsAsync(watermark, cancellationToken: cancellationToken);

            // Zero events is a successful answer, not a failure.
            return batch.Events.Count;
        }
        catch (ABConnectNotLicensedException)
        {
            // AB Connect documents that a change event may reference a standard the account can no
            // longer read, and that the correct response is to skip it: a "removed" event for it is
            // somewhere later in the queue.
            return 0;
        }
        catch (ABConnectThrottledException ex)
        {
            Console.WriteLine($"Throttled after {ex.Attempts} attempt(s). Retry after {ex.RetryAfter}.");
            throw;
        }
        catch (ABConnectRequestException ex)
        {
            Console.WriteLine($"{ex.StatusCode} on {ex.RequestPath}, {ex.Errors.Count} error(s).");
            throw;
        }
    }
}
```

## Throttling

AB Connect rate-limits per account, not per process, so the SDK's bucket is a singleton shared by
every client and every command in one registration. Two `IABConnectClient` instances resolved from the
same provider draw on the same bucket.

The defaults match AB Connect's documented limits: a burst of **25** requests and a sustained **5 per
second**. A request that asks for wildcard fields also spends from a second, narrower bucket, **2**
burst and **2 per second**, because AB Connect rate-limits those separately.

Behavior:

- A token is acquired **per attempt**, retries included. The throttle sits inside the retry loop
  precisely so that a retry storm cannot bypass the bucket and make a 429 storm worse.
- Waiting is asynchronous and in arrival order. No calling thread is ever blocked, and a late caller
  cannot take a token an earlier waiter is owed.
- An HTTP 429 drains the bucket as a self-adjusting penalty, on top of the backoff wait, so the next
  burst starts from empty rather than immediately re-offending. Two overlapping 429s never shorten each
  other's penalty.
- Exceeding `QueueLimit` throws `ABConnectThrottledException` **without sending the request**, rather
  than returning something that could be mistaken for an answer.

`IABConnectClient.Throttle` exposes the live counters, for a progress display:

```csharp
using OnCourse.ABConnect;

void ReportThrottle(IABConnectClient client)
{
    Console.WriteLine(
        $"tokens {client.Throttle.AvailableTokens}/{client.Throttle.BucketCapacity}, " +
        $"acquired {client.Throttle.AcquiredCount}, " +
        $"waited {client.Throttle.WaitCount} time(s) for {client.Throttle.TotalWaitTime}, " +
        $"429s seen {client.Throttle.ThrottleResponseCount}");
}
```

`BucketCapacity` is there so a status line can read `17/25` without hard-coding the denominator. A
non-zero `ThrottleResponseCount` means the local bucket is configured faster than the account's actual
limit, since a correctly configured bucket should keep requests inside it.

## Retries and the request pipeline

429 and 5xx are retried; every other 4xx returns immediately, because a 400, 401, 403 or 404 will not
become a different answer on a second attempt. Backoff is exponential with full jitter, bounded by
`MaxDelay`. When `HonorRetryAfter` is on and the response carries a `Retry-After` header, that wait
wins over computed backoff; both the delta-seconds and HTTP-date forms are read.

Transport failures and per-attempt timeouts are retried on the same ladder and surface as
`ABConnectTransportException` when the ladder is exhausted. An exhausted 429 ladder surfaces as
`ABConnectThrottledException`, an exhausted 5xx ladder as `ABConnectServerException`, each with
`Attempts` set to the number of requests actually sent.

The pipeline order is fixed:

```
HttpClient
  -> ABConnectRetryHandler        outermost: owns the attempt loop
       -> ABConnectThrottleHandler acquires a token per attempt, retries included
            -> ABConnectSigningHandler mints the auth query fragment per attempt
                 -> primary handler
```

Signing is innermost so that a retry occurring after the signature expires gets a fresh signature
rather than replaying a stale one. `HttpClient.Timeout` is deliberately infinite:
`ABConnectOptions.RequestTimeout` is a per-attempt budget enforced inside the retry handler, and a
timeout on the client itself would kill a call part-way up the ladder.

## Paging

### Events: re-anchored on sequence, never on offset

`ReadEventsAsync` buffers a whole delta; `ReadEventPagesAsync` streams it one page at a time and holds
no more than one page in memory.

```csharp
using OnCourse.ABConnect;
using OnCourse.ABConnect.Feed;

internal static class EventWalk
{
    public static async Task<long> ReadDeltaAsync(
        IABConnectFeed feed,
        long watermark,
        CancellationToken cancellationToken)
    {
        await foreach (EventPage page in feed.ReadEventPagesAsync(watermark, cancellationToken: cancellationToken))
        {
            Console.WriteLine($"page {page.PageNumber}: {page.Events.Count} of {page.ReportedTotalCount}");

            // Persist the events, then the watermark, in one transaction. Every page's
            // HighestSequence is the watermark to resume from.
            watermark = page.HighestSequence;
        }

        return watermark;
    }
}
```

Guarantees:

- Every request carries `sort[events]=seq` and `offset=0`, and filters on `seq GT <highest seen>`.
  The traversal re-anchors on the sequence it has already consumed rather than walking an offset, so
  events arriving mid-traversal cannot shift rows past the cursor.
- `links.next` is never read.
- Sequences must strictly ascend within a page, every page must raise the watermark, and no row may
  arrive at or below the watermark it was requested with. Any of those throws
  `ABConnectPagingException`, naming both sequences.
- A **gap** in sequence numbers is normal and produces no error and no warning. AB Connect assigns
  sequence numbers across all partners, so the ones you can see are not dense.
- The walk stops on a request that returns no events, not on a short page and not on `meta.count`. A
  completed traversal therefore costs one request more than the number of pages that carried events:
  `EventBatch.PagesFetched` for a three-page delta is 4, and for an empty feed it is 1. That extra
  round trip is deliberate: once a watermark advances past an event, that event is unrecoverable, so
  the walk pays one request per pull to make truncation impossible.
- `EventReadOptions.MaxEvents` truncates exactly and sets `IsComplete = false`, including when the
  ceiling happens to coincide with the end of the feed. Claiming completeness there would be a guess;
  the next read from `HighestSequence` returns zero events and settles it.
- `ReadEventPagesAsync` yields zero pages for an empty feed, not one empty page.

### Standards: offset paging with a deterministic sort and a completeness check

```csharp
using OnCourse.ABConnect;
using OnCourse.ABConnect.Feed;

internal static class DocumentRead
{
    public static async Task<int> SnapshotAsync(
        IABConnectFeed feed,
        string documentGuid,
        CancellationToken cancellationToken)
    {
        DocumentSnapshot snapshot = await feed.ReadDocumentSnapshotAsync(
            documentGuid,
            cancellationToken: cancellationToken);

        Console.WriteLine(
            $"{snapshot.Standards.Count} of {snapshot.ReportedTotalCount} standards in " +
            $"{snapshot.PagesFetched} page(s), read at {snapshot.FetchedAtUtc:O}");

        return snapshot.Standards.Count;
    }
}
```

Guarantees:

- Every request carries `sort[standards]=seq,guid`. Two keys, because `seq` alone is not unique and an
  unstable sort silently drops and duplicates rows across offset pages.
- Every request carries an explicit status term. The default scope is
  `status IN ('active','deleted','obsolete')`, so a complete mirror sees deleted and obsolete standards
  alike. AB Connect's own no-filter default returns active and obsolete but hides deleted, and a
  `status IN ('active','deleted')` filter hides obsolete; the default here (`StandardStatusScope.All`)
  hides neither. Narrow it with `Active`, `Deleted`, `Obsolete`, or `ActiveAndDeleted` when you want less.
- A GUID appearing on two pages throws `ABConnectPagingException` naming the GUID and the page.
- Before a snapshot is returned, the distinct GUID count must equal `meta.count` exactly. The
  tolerance is zero. A shortfall or an overshoot throws `ABConnectPagingException`; the usual cause is
  an edit to the document mid-traversal, and the fix is to refetch.
- Offsets advance by the `limit` AB Connect echoes back in `meta.limit`, which is the arithmetic AB
  Connect itself uses, not by the number of rows returned.
- The walk stops at the first short page or when the collected count reaches `meta.count`, and it is
  bounded by a request ceiling derived from the first page's reported count. A service that pages
  forever throws rather than looping.
- `ReadDocumentPagesAsync` keeps duplicate detection and the request ceiling but cannot perform the
  shortfall check, since that is only evaluable once the whole document has been read. That is the
  trade for streaming.

### Standards by GUID set: one batched request instead of one lookup each

When you already hold a set of standard GUIDs and want the standards behind them, ask for the whole
batch in a single request rather than probing each GUID on its own. A GUID the account no longer
licenses is simply absent from the result, so a shorter list than you asked for is the answer, not an
error: those are the GUIDs the vendor no longer serves.

```csharp
using OnCourse.ABConnect;
using OnCourse.ABConnect.Models;
using OnCourse.ABConnect.Queries;

internal static class GuidSetRead
{
    public static async Task<int> RecoverAsync(
        IABConnectFeed feed,
        IReadOnlyCollection<string> standardGuids,
        CancellationToken cancellationToken)
    {
        IReadOnlyList<Standard> served = await feed.ReadStandardsByGuidsAsync(
            standardGuids,
            cancellationToken: cancellationToken);

        Console.WriteLine($"{served.Count} of {standardGuids.Count} GUIDs are still served");
        return served.Count;
    }
}
```

Guarantees:

- The batch is capped at `StandardsFilter.MaxGuidSetSize` (100, the page size), so the whole set fits
  one page. A larger set throws `ArgumentException`; batch it and call once per batch.
- Every GUID is validated before any request is issued, so a malformed value fails fast and no stray
  value can reach the `guid IN (...)` filter.
- The default status scope is `ActiveAndDeleted`, so a deleted standard is distinguishable from one
  that is simply no longer served. Pass a different scope to narrow or widen it.
- The read still drains every page if the service applies a smaller limit than the batch, so the
  returned list is complete for the GUIDs the account can see.

### Facets are never paged

A facet is exactly one request with `limit=0`. AB Connect truncates facet values at 10,000 and does
not page them, so `ABFacet<T>.IsTruncated` tells you when you are looking at a partial list rather
than letting you believe it is complete.

```csharp
using OnCourse.ABConnect;
using OnCourse.ABConnect.Queries;

internal static class PublicationList
{
    public static async Task PrintAsync(
        IABConnectClient client,
        string authorityGuid,
        CancellationToken cancellationToken)
    {
        var facet = await client.GetPublicationFacetAsync(
            StandardsFilter.ByAuthority(authorityGuid),
            cancellationToken);

        if (facet.IsTruncated)
        {
            Console.WriteLine($"warning: AB Connect reported {facet.ReportedCount} values and returned {facet.Values.Count}.");
        }

        foreach (var value in facet.Values)
        {
            Console.WriteLine($"{value.Value.Acronym} ({value.Count})");
        }
    }
}
```

The five facet names AB Connect supports live inside the SDK, on `ABFacetNames`, so no caller has to
know the magic string `document.publication.authorities`.

## Single requests with IABConnectClient

Use `IABConnectClient` when you want one request and no paging behavior.

```csharp
using OnCourse.ABConnect;
using OnCourse.ABConnect.Models;
using OnCourse.ABConnect.Queries;

internal static class DeletedStandards
{
    public static async Task<int> CountAsync(
        IABConnectClient client,
        string documentGuid,
        CancellationToken cancellationToken)
    {
        ABPage<Standard> page = await client.GetStandardsAsync(
            new StandardsQuery
            {
                Filter = StandardsFilter.ByDocument(documentGuid),
                Fields = StandardFieldSet.Snapshot,
                Status = StandardStatusScope.Deleted,
                Page = new PageRequest(0, 100),
            },
            cancellationToken);

        // Data.Count == 0 here means the document has no deleted standards, not that anything failed.
        return page.Meta.Count;
    }
}
```

Queries are built through named factories rather than string concatenation, and they are validated
before anything is sent. A GUID containing a quote is rejected, a negative offset throws, a limit above
100 is clamped to 100 and then lowered to `PageSize`, a field name containing `&` or `=` is rejected
rather than escaped, and the whole filter expression is percent-encoded exactly once.

`GetStandardAsync(guid)` fetches a single standard by GUID and returns it whether it is active or
deleted, which is the way to resolve a GUID your local copy has lost track of.

## Working with the models

- Every date is `DateTimeOffset?` and parses AB Connect's `yyyy-MM-dd HH:mm:ss` form as UTC. JSON
  `null` yields `null` rather than throwing. A value that is neither yields a `JsonException` rather
  than being silently nulled.
- `change_type`, `target`, `status`, and `publication_type` are **strings**, not C# enums, with
  companion constant classes (`ABChangeTypes`, `ABEventTargets`, `ABStandardStatuses`). AB Connect
  documents two `change_type` values and actually emits at least twelve, so an unknown value must
  deserialize successfully rather than throwing or mapping to a default.
- A null collection means the member was **absent** from the payload and an empty collection means it
  was **present and empty**. The distinction is preserved rather than flattened, because "this standard
  has no children" and "we did not ask about children" are different facts. AB Connect also omits
  relationships your license does not cover while still returning 200.
- The four `number` variants (`Raw`, `Enhanced`, `PrefixEnhanced`, `RootEnhanced`) are all nullable and
  none of them is an identifier. AB Connect states that numbers are not unique and that some standards
  have none.
- Model records do **not** have deep equality: collection members compare by reference. Compare a
  `Standard` by `Attributes.Guid` and an `ABEvent` by `Attributes.Seq`, not with `==`.

## What changed in 3.0

Version 3 is a breaking release. It changes every entry point, so there is no shim and no
compatibility mode; the compiler will find every call site for you.

### Why it breaks

In version 2, every method caught its own exceptions, logged them, and returned a new empty object. For
the events feed that empty object was indistinguishable from a successful response that found no new
events: same empty list, same zero count. A network blip, an expired signature, a 429, or a JSON parse
failure all produced a clean, silent, successful-looking run that processed nothing.

Version 3's rule is that a method either returns a value or throws, which is the one thing a caller
cannot accidentally read as data. Everything else here follows from that.

### Registration

Version 2:

```text
services.UseABConnect(hostContext.Configuration, null);

services.UseABConnect(configuration, p => p.WaitAndRetryAsync(new[]
{
    TimeSpan.FromSeconds(1),
    TimeSpan.FromSeconds(5),
    TimeSpan.FromSeconds(10)
}));
```

Version 3:

```csharp
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using OnCourse.ABConnect;

internal static class Registration
{
    public static void Register(IServiceCollection services, IConfiguration configuration)
    {
        services.AddABConnect(configuration);
    }
}
```

`UseABConnect` is deleted. So is the Polly policy parameter: retrying, throttling, and timeouts are
part of the pipeline now, and configuring them through `ABConnectRetryOptions` and
`ABConnectThrottleOptions` means a retry cannot bypass the rate limiter, which is exactly what the
version 2 arrangement allowed.

### API surface

| Version 2 | Version 3 |
|---|---|
| `ABConnectClient` as the only entry point | `IABConnectClient` for single requests, `IABConnectFeed` for paging |
| `api.GetAuthorities()` then digging into `Meta.Facets.Find(f => f.FacetType == "...")?.Details` | `client.GetAuthorityFacetAsync(cancellationToken: ct)`, then iterate `facet.Values`. The magic facet string is inside the SDK. |
| `api.GetPublications(guid)`, `api.GetDocuments(guid)` | `client.GetPublicationFacetAsync(StandardsFilter.ByAuthority(guid), ct)`, `client.GetDocumentFacetAsync(StandardsFilter.ByPublication(guid), ct)` |
| `api.GetStandardsByPublication(guid, 0, 1)` with `fields[standards]=*` to probe a publication | `feed.ReadPublicationAsync(guid, ct)`, returning `StandardPublication?`. Same one-row probe, two fields instead of the wildcard. |
| `api.GetStandards(guid, 0, 1)` with `fields[standards]=*` to probe a document | `feed.ReadDocumentAsync(guid, ct)`, returning `StandardDocument?` |
| Hand-rolled `do { ... } while` over `api.GetEvents(seq, offset)` plus `api.ParseOffset(links.next)` | `await foreach (var page in feed.ReadEventPagesAsync(watermark, cancellationToken: ct))`. `ParseOffset` no longer exists. |
| Hand-rolled offset loop to read a whole document | `feed.ReadDocumentSnapshotAsync(guid, ct)`, with duplicate and shortfall verification |
| No way to fetch one standard by GUID | `client.GetStandardAsync(guid, ct)` |
| Deleted standards invisible; obsolete standards silently included by the no-filter default | `StandardStatusScope.All` (active + deleted + obsolete) is the default scope, so nothing is hidden and nothing is silently dropped |
| `Guard.Against.Null(events, "events")` after every call | Deleted. Version 3 cannot return null. |
| `if (events.Data is null \|\| events.Data.Count == 0 \|\| events.Meta is null) break;` | Deleted. Loop termination belongs to the SDK, and a failure throws instead of looking empty. |
| `events.Meta.Count`, available only on the first page | `page.ReportedTotalCount`, on every page |
| A caller-side rate limiter plus `Thread.Sleep(500)` between calls | Deleted. Throttling is in the pipeline. A progress display reads `client.Throttle.AvailableTokens` and `client.Throttle.BucketCapacity`, so the hard-coded `/25` goes too. |

### Models

| Version 2 | Version 3 |
|---|---|
| `standard.Relationships?.Parent?.Data?.Id` | `standard.Relationships?.Parent?.Id`. The JSON:API `Data` wrapper is flattened. |
| `standard.Relationships?.Children?.Data?.Count` | `standard.Relationships?.Children?.Count` |
| `EducationLevels?.Grades?.First().Code`, which throws on a non-null empty list | `EducationLevels?.Lowest?.Code` and `?.Highest?.Code`, ordered by AB Connect's documented `seq` and null-safe |
| `StandardDocumentPublication` | `StandardPublication`, with the same `Guid`, `Description`, `Acronym`, `Regions`, `Authorities`, and `SourceUrl` members |
| `EventAttributes.Sequence`, an `int` | `ABEventAttributes.Seq`, a `long`. Widen any persisted watermark column to match. |
| `EventAttributes`, `Events`, `Documents`, `Standards` wrapper types | `ABEventAttributes`, `ABPage<ABEvent>`, `ABPage<Standard>` |
| `StandardDocument.DateModifiedUtc`, a `DateTime` | `DateTimeOffset?`. Convert at your persistence boundary. |
| `Number.PrefixEnhanced` only | `Number.Raw`, `Number.Enhanced`, `Number.PrefixEnhanced`, `Number.RootEnhanced` |
| Event `affected_properties` and `section_guid` discarded | `ABEventAttributes.AffectedProperties`, `.SectionGuid`, and `ABEventRelationships` are all exposed |

### Dependencies

Version 3 drops `Newtonsoft.Json`, `Microsoft.AspNetCore.WebUtilities`, `Ardalis.GuardClauses`, and
`Microsoft.Extensions.Http.Polly`. Serialization is `System.Text.Json` with source generation. If you
were relying on the SDK to drag `Newtonsoft.Json` into your project, reference it yourself.

## Contributing

```
dotnet build OnCourse.ABConnect.sln
dotnet test OnCourse.ABConnect.sln
```

The test suite performs no network I/O and runs in a few seconds. It includes a test that compiles
every C# sample in this file against the built assembly, so a sample that drifts from the real API
fails the build rather than somebody's first afternoon with the package.

There is also a small set of live contract tests, excluded by default, that read from a real AB Connect
account. To run them, set `ABCONNECT_PARTNER_ID` and `ABCONNECT_PARTNER_KEY` and run
`dotnet test --filter "Category=Live"`. Without credentials they report as skipped rather than passing.

## License

[MIT](https://opensource.org/licenses/MIT). Copyright (c) 2023-2026 OnCourse Systems For Education.
The full text is in [LICENSE](LICENSE).
