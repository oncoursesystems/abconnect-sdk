using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Options;
using OnCourse.ABConnect.Http;

namespace OnCourse.ABConnect.Handlers;

/// <summary>
/// Mints the AB Connect authentication query fragment
/// <c>partner.id=&lt;id&gt;&amp;auth.signature=&lt;sig&gt;&amp;auth.expires=&lt;epoch&gt;</c> and appends it
/// to the outgoing request URI.
/// </summary>
/// <remarks>
/// <para>
/// Innermost of the three handlers, so that a retry occurring after a signature expires gets a fresh
/// signature rather than replaying a stale one.
/// </para>
/// <para>
/// The signed message is <c>"{expires}\n\nGET"</c>, that is, expiry plus an empty user plus the
/// method, with no resource segment. AB Connect's rules are that if you specify a resource you must
/// also specify a method, and that each field can hold only one value, so a resource-scoped
/// signature would need one signature per endpoint for no benefit to a process that holds the
/// partner key anyway.
/// </para>
/// <para>
/// The signature value is percent-encoded before it is placed in the query, the expiry comes from an
/// injected <see cref="TimeProvider"/> so it is testable without sleeping, and the signature is
/// cached and reused until sixty seconds before its expiry.
/// </para>
/// <para>
/// The partner key never leaves this class. It is used as an HMAC key and is never placed in a URI,
/// a log line, an exception message, or a <see cref="object.ToString"/> result. The pre-signature
/// path is recorded on <see cref="HttpRequestMessage.Options"/> as an
/// <see cref="ABConnectRequestContext"/> before the credentials are appended, which is what lets the
/// response mapper report a request path that is redacted by construction rather than by filtering.
/// </para>
/// </remarks>
public sealed class ABConnectSigningHandler : DelegatingHandler
{
    /// <summary>
    /// How long before a signature's expiry it is re-minted. A cached signature is reused until this
    /// much of its lifetime remains, so that a request already in flight cannot be rejected for
    /// expiring between minting and arrival.
    /// </summary>
    private static readonly TimeSpan RenewalMargin = TimeSpan.FromSeconds(60);

    private readonly IOptions<ABConnectOptions> _options;
    private readonly TimeProvider _timeProvider;
    private readonly object _gate = new();

    private string? _cachedQueryFragment;
    private DateTimeOffset _cachedExpiresAt;

    /// <summary>Creates the signing handler.</summary>
    /// <param name="options">The SDK options supplying the partner credentials and signature lifetime.</param>
    /// <param name="timeProvider">The clock used to compute and expire signatures.</param>
    /// <exception cref="ArgumentNullException">Any argument is null.</exception>
    public ABConnectSigningHandler(IOptions<ABConnectOptions> options, TimeProvider timeProvider)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(timeProvider);

        _options = options;
        _timeProvider = timeProvider;
    }

    /// <summary>Appends a fresh or cached signature to the request and forwards it.</summary>
    /// <param name="request">The outgoing request.</param>
    /// <param name="cancellationToken">Cancels the send.</param>
    /// <returns>The response from the inner handler.</returns>
    /// <exception cref="ABConnectConfigurationException">The partner id or partner key is null or empty.</exception>
    protected override Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        var options = _options.Value;

        if (string.IsNullOrWhiteSpace(options.PartnerId))
        {
            throw new ABConnectConfigurationException(
                $"AB Connect configuration section '{ABConnectOptions.SectionName}' is invalid: PartnerId is required.");
        }

        if (string.IsNullOrWhiteSpace(options.PartnerKey))
        {
            throw new ABConnectConfigurationException(
                $"AB Connect configuration section '{ABConnectOptions.SectionName}' is invalid: PartnerKey is required.");
        }

        var uri = request.RequestUri
            ?? throw new ABConnectConfigurationException(
                "An AB Connect request reached the signing handler with no request URI, which means neither the request nor the HttpClient supplied a base address.");

        // Stripping first makes signing idempotent. The retry handler re-sends this same request, so
        // an attempt that simply appended would accumulate one auth fragment per attempt; instead each
        // attempt is signed exactly once, with a fresh signature if the cached one has aged out.
        var (path, query) = Split(uri);
        query = StripAuthParameters(query);

        EnsureRequestContext(request, uri, query);

        var separator = query.Length == 0 ? '?' : '&';
        var signed = (query.Length == 0 ? path : path + "?" + query) + separator + GetOrMintQueryFragment(options);
        request.RequestUri = new Uri(signed, uri.IsAbsoluteUri ? UriKind.Absolute : UriKind.Relative);

        return base.SendAsync(request, cancellationToken);
    }

    /// <summary>
    /// Not supported. The AB Connect pipeline is asynchronous throughout.
    /// </summary>
    /// <remarks>
    /// <see cref="DelegatingHandler"/>'s synchronous path does not route through
    /// <see cref="SendAsync"/>, so allowing it would send an <em>unsigned</em> request and earn an
    /// opaque HTTP 401. Failing loudly is the only safe behavior.
    /// </remarks>
    /// <param name="request">The outgoing request.</param>
    /// <param name="cancellationToken">Unused.</param>
    /// <returns>Never returns.</returns>
    /// <exception cref="NotSupportedException">Always.</exception>
    protected override HttpResponseMessage Send(HttpRequestMessage request, CancellationToken cancellationToken) =>
        throw new NotSupportedException(
            "AB Connect requests must be sent asynchronously. The synchronous send path bypasses request signing, which would produce an unsigned request.");

    /// <summary>
    /// Computes the AB Connect signature for an expiry, base64-encoded and not yet percent-encoded.
    /// </summary>
    /// <remarks>
    /// The message is the expiry, an empty user field, and the method, joined by line feeds and with
    /// no trailing separator: <c>"{expires}\n\nGET"</c>. Both the key and the message are UTF-8. This
    /// reproduces AB Connect's published worked example byte for byte, which the signing tests assert:
    /// partner <c>test_account</c>, key <c>ajk84Hjk93h59skaAJ8732</c>, message
    /// <c>1512570029\n\nGET</c>, signature <c>Sdcfa9xgRAUzQnlLik5nKj1ntqdB85jFYyFCkNxwD/M=</c>.
    /// </remarks>
    /// <param name="partnerKey">The partner key, used as the HMAC-SHA256 key.</param>
    /// <param name="expiresAtEpochSeconds">The expiry, in seconds since the Unix epoch.</param>
    /// <returns>The base64-encoded signature.</returns>
    private static string ComputeSignature(string partnerKey, long expiresAtEpochSeconds)
    {
        var message = $"{expiresAtEpochSeconds}\n\nGET";
        var hash = HMACSHA256.HashData(
            Encoding.UTF8.GetBytes(partnerKey),
            Encoding.UTF8.GetBytes(message));

        return Convert.ToBase64String(hash);
    }

    /// <summary>
    /// Returns the cached authentication query fragment, minting a new one when none is cached or the
    /// cached one is within <see cref="RenewalMargin"/> of expiring.
    /// </summary>
    /// <remarks>
    /// The message being signed depends only on the expiry, so there is nothing per-request to vary
    /// and a single cached fragment guarded by a lock is sufficient. A
    /// <see cref="ABConnectOptions.SignatureLifetime"/> at or below the renewal margin re-mints on
    /// every request, which is correct but wasteful; validation keeps the lifetime at a minute or more.
    /// </remarks>
    /// <param name="options">The current options, already checked for credentials.</param>
    /// <returns>The <c>partner.id</c>, <c>auth.signature</c>, and <c>auth.expires</c> query fragment.</returns>
    private string GetOrMintQueryFragment(ABConnectOptions options)
    {
        var now = _timeProvider.GetUtcNow();

        lock (_gate)
        {
            if (_cachedQueryFragment is not null && now < _cachedExpiresAt - RenewalMargin)
            {
                return _cachedQueryFragment;
            }

            var expiresAt = now + options.SignatureLifetime;
            var expires = expiresAt.ToUnixTimeSeconds();
            var signature = ComputeSignature(options.PartnerKey!, expires);

            _cachedQueryFragment =
                $"partner.id={Uri.EscapeDataString(options.PartnerId!)}" +
                $"&auth.signature={Uri.EscapeDataString(signature)}" +
                $"&auth.expires={expires}";
            _cachedExpiresAt = expiresAt;

            return _cachedQueryFragment;
        }
    }

    /// <summary>
    /// Records the pre-signature path on the request when nothing upstream did.
    /// </summary>
    /// <remarks>
    /// The query builder normally attaches the context, and when it has, this method leaves it
    /// untouched: <see cref="ABConnectRequestContext.RedactedPath"/> is the path as the caller asked
    /// for it and this handler must not rewrite it. The fallback exists so that a request issued
    /// directly against the typed <see cref="HttpClient"/> still yields a redacted path rather than
    /// none, since the alternative is an exception message that reports the signed URI.
    /// </remarks>
    /// <param name="request">The outgoing request.</param>
    /// <param name="uri">The request URI, read before any credentials are appended.</param>
    /// <param name="strippedQuery">The query with any authentication parameters already removed.</param>
    private static void EnsureRequestContext(HttpRequestMessage request, Uri uri, string strippedQuery)
    {
        if (ABConnectRequestContext.From(request) is not null)
        {
            return;
        }

        var path = uri.IsAbsoluteUri ? uri.AbsolutePath : Split(uri).Path;
        var redacted = strippedQuery.Length == 0 ? path : path + "?" + strippedQuery;

        if (!string.IsNullOrWhiteSpace(redacted))
        {
            new ABConnectRequestContext(redacted).AttachTo(request);
        }
    }

    /// <summary>
    /// Splits a URI into everything up to and including the path, and the query without its leading
    /// question mark.
    /// </summary>
    /// <remarks>
    /// Absolute and relative URIs are both handled, because a handler sees whichever the caller and
    /// the <see cref="HttpClient"/> between them produced. Nothing this SDK builds carries a fragment.
    /// </remarks>
    /// <param name="uri">The URI to split.</param>
    /// <returns>The path part and the query part.</returns>
    private static (string Path, string Query) Split(Uri uri)
    {
        if (uri.IsAbsoluteUri)
        {
            var query = uri.Query;
            return (uri.GetLeftPart(UriPartial.Path), query.Length > 0 ? query[1..] : string.Empty);
        }

        var original = uri.OriginalString;
        var mark = original.IndexOf('?', StringComparison.Ordinal);

        return mark < 0 ? (original, string.Empty) : (original[..mark], original[(mark + 1)..]);
    }

    /// <summary>
    /// Removes the three authentication parameters from a query, leaving every other parameter, its
    /// order, and its encoding untouched.
    /// </summary>
    /// <param name="query">The query without its leading question mark.</param>
    /// <returns>The query with <c>partner.id</c>, <c>auth.signature</c>, and <c>auth.expires</c> removed.</returns>
    private static string StripAuthParameters(string query)
    {
        if (query.Length == 0 ||
            (!query.Contains("auth.", StringComparison.Ordinal) &&
             !query.Contains("partner.id=", StringComparison.Ordinal)))
        {
            return query;
        }

        var kept = query
            .Split('&')
            .Where(parameter =>
                !parameter.StartsWith("partner.id=", StringComparison.Ordinal) &&
                !parameter.StartsWith("auth.signature=", StringComparison.Ordinal) &&
                !parameter.StartsWith("auth.expires=", StringComparison.Ordinal));

        return string.Join('&', kept);
    }
}
