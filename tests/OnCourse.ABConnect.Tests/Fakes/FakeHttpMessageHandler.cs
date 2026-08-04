using System.Collections.ObjectModel;
using System.Net;
using System.Text;

namespace OnCourse.ABConnect.Tests.Fakes;

/// <summary>
/// A queue of canned responses that records every request it was asked to send, so a test can assert
/// on the emitted query string, the emitted headers, and the number of attempts the pipeline made.
/// </summary>
/// <remarks>
/// <para>
/// Nothing here touches a socket, a clock, or a thread pool timer. Every send is answered from the
/// queue synchronously, so a test that uses this handler with a
/// <c>FakeTimeProvider</c> is fully deterministic.
/// </para>
/// <para>
/// A queued entry is either a response or a failure to raise, which is how a DNS, TLS, or socket
/// failure is simulated. When the queue runs dry the handler either invokes
/// <see cref="Responder"/>, if one was set, or fails the test with an
/// <see cref="InvalidOperationException"/> that names the request it could not answer. Running dry is
/// never silently answered with a default response, because a test that accidentally sends a second
/// request must fail rather than pass.
/// </para>
/// </remarks>
public sealed class FakeHttpMessageHandler : HttpMessageHandler
{
    private readonly object _gate = new();
    private readonly Queue<Entry> _entries = new();
    private readonly List<HttpRequestMessage> _requests = [];
    private readonly List<string> _requestUris = [];
    private readonly List<IReadOnlyDictionary<string, string>> _requestHeaders = [];

    /// <summary>
    /// Answers a request the queue has no entry for. Left null so that an unexpected extra request
    /// fails the test instead of being absorbed.
    /// </summary>
    public Func<HttpRequestMessage, HttpResponseMessage>? Responder { get; set; }

    /// <summary>Every request the handler was asked to send, in order.</summary>
    /// <remarks>
    /// The messages are the live instances the pipeline built, so
    /// <c>ABConnectRequestContext.From(request)</c> can be asserted against them. The client disposes
    /// each request when its call returns, which disposes only the content, so the URI, the method,
    /// the headers, and the options remain readable afterwards.
    /// </remarks>
    public IReadOnlyList<HttpRequestMessage> Requests
    {
        get
        {
            lock (_gate)
            {
                return new ReadOnlyCollection<HttpRequestMessage>([.. _requests]);
            }
        }
    }

    /// <summary>The absolute URI of every request, in order, exactly as it went on the wire.</summary>
    public IReadOnlyList<string> RequestUris
    {
        get
        {
            lock (_gate)
            {
                return new ReadOnlyCollection<string>([.. _requestUris]);
            }
        }
    }

    /// <summary>
    /// A snapshot of the request headers of every request, in order, taken while the request was
    /// being sent.
    /// </summary>
    /// <remarks>
    /// A snapshot rather than a live view, because a handler that re-signs a request between attempts
    /// mutates the message in place and a later read would see only the final state. Header values
    /// are joined with <c>", "</c> in the order they were added.
    /// </remarks>
    public IReadOnlyList<IReadOnlyDictionary<string, string>> RequestHeaders
    {
        get
        {
            lock (_gate)
            {
                return new ReadOnlyCollection<IReadOnlyDictionary<string, string>>([.. _requestHeaders]);
            }
        }
    }

    /// <summary>How many requests the handler has been asked to send.</summary>
    public int SendCount
    {
        get
        {
            lock (_gate)
            {
                return _requestUris.Count;
            }
        }
    }

    /// <summary>How many queued entries have not been consumed yet.</summary>
    public int PendingCount
    {
        get
        {
            lock (_gate)
            {
                return _entries.Count;
            }
        }
    }

    /// <summary>The absolute URI of the most recent request.</summary>
    /// <exception cref="InvalidOperationException">No request has been sent.</exception>
    public string LastRequestUri
    {
        get
        {
            lock (_gate)
            {
                return _requestUris.Count > 0
                    ? _requestUris[^1]
                    : throw new InvalidOperationException("No request has been sent through this handler.");
            }
        }
    }

    /// <summary>Queues a response verbatim. Ownership of the response passes to the handler.</summary>
    /// <param name="response">The response to return for the next request.</param>
    /// <returns>The same handler, so queueing calls can be chained.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="response"/> is null.</exception>
    public FakeHttpMessageHandler Enqueue(HttpResponseMessage response)
    {
        ArgumentNullException.ThrowIfNull(response);

        lock (_gate)
        {
            _entries.Enqueue(new Entry(response, null));
        }

        return this;
    }

    /// <summary>Queues a response with a JSON body and the given status code.</summary>
    /// <param name="statusCode">The status code to return.</param>
    /// <param name="json">The response body, sent as <c>application/json</c>.</param>
    /// <returns>The same handler, so queueing calls can be chained.</returns>
    public FakeHttpMessageHandler EnqueueJson(HttpStatusCode statusCode, string json)
        => Enqueue(BuildResponse(statusCode, json, "application/json"));

    /// <summary>Queues an HTTP 200 response with a JSON body.</summary>
    /// <param name="json">The response body, sent as <c>application/json</c>.</param>
    /// <returns>The same handler, so queueing calls can be chained.</returns>
    public FakeHttpMessageHandler EnqueueOk(string json)
        => EnqueueJson(HttpStatusCode.OK, json);

    /// <summary>Queues a response with an arbitrary body and content type.</summary>
    /// <param name="statusCode">The status code to return.</param>
    /// <param name="body">The response body.</param>
    /// <param name="contentType">The media type to send the body as.</param>
    /// <returns>The same handler, so queueing calls can be chained.</returns>
    public FakeHttpMessageHandler EnqueueBody(HttpStatusCode statusCode, string body, string contentType)
        => Enqueue(BuildResponse(statusCode, body, contentType));

    /// <summary>Queues the same JSON response several times, for exercising a retry ladder.</summary>
    /// <param name="count">How many copies to queue.</param>
    /// <param name="statusCode">The status code to return each time.</param>
    /// <param name="json">The response body to return each time.</param>
    /// <returns>The same handler, so queueing calls can be chained.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="count"/> is negative.</exception>
    public FakeHttpMessageHandler EnqueueJsonRepeated(int count, HttpStatusCode statusCode, string json)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(count);

        for (int index = 0; index < count; index++)
        {
            EnqueueJson(statusCode, json);
        }

        return this;
    }

    /// <summary>
    /// Queues a failure instead of a response, which is how a DNS, TLS, or socket failure is
    /// simulated. The exception is raised from the send, before any response exists.
    /// </summary>
    /// <param name="failure">The exception to raise for the next request.</param>
    /// <returns>The same handler, so queueing calls can be chained.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="failure"/> is null.</exception>
    public FakeHttpMessageHandler EnqueueFailure(Exception failure)
    {
        ArgumentNullException.ThrowIfNull(failure);

        lock (_gate)
        {
            _entries.Enqueue(new Entry(null, failure));
        }

        return this;
    }

    /// <summary>Records the request, then answers it from the queue.</summary>
    /// <param name="request">The request the pipeline produced.</param>
    /// <param name="cancellationToken">The token the pipeline passed down.</param>
    /// <returns>The queued response.</returns>
    /// <exception cref="InvalidOperationException">The queue is empty and no <see cref="Responder"/> is set.</exception>
    protected override Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();

        Entry? entry;
        lock (_gate)
        {
            _requests.Add(request);
            _requestUris.Add(request.RequestUri?.ToString() ?? "(no request uri)");
            _requestHeaders.Add(SnapshotHeaders(request));
            entry = _entries.Count > 0 ? _entries.Dequeue() : null;
        }

        if (entry is null)
        {
            HttpResponseMessage? improvised = Responder?.Invoke(request);
            if (improvised is not null)
            {
                improvised.RequestMessage = request;
                return Task.FromResult(improvised);
            }

            throw new InvalidOperationException(
                $"FakeHttpMessageHandler ran out of queued responses on request {SendCount} " +
                $"({request.Method} {request.RequestUri}). Queue one response per expected request.");
        }

        if (entry.Failure is { } failure)
        {
            return Task.FromException<HttpResponseMessage>(failure);
        }

        HttpResponseMessage response = entry.Response!;
        response.RequestMessage = request;
        return Task.FromResult(response);
    }

    /// <summary>Disposes every response still sitting in the queue.</summary>
    /// <param name="disposing">True when called from <see cref="IDisposable.Dispose"/>.</param>
    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            lock (_gate)
            {
                while (_entries.Count > 0)
                {
                    _entries.Dequeue().Response?.Dispose();
                }
            }
        }

        base.Dispose(disposing);
    }

    private static HttpResponseMessage BuildResponse(HttpStatusCode statusCode, string body, string contentType)
        => new(statusCode)
        {
            Content = new StringContent(body ?? string.Empty, Encoding.UTF8, contentType),
        };

    private static IReadOnlyDictionary<string, string> SnapshotHeaders(HttpRequestMessage request)
    {
        Dictionary<string, string> headers = new(StringComparer.OrdinalIgnoreCase);

        foreach (KeyValuePair<string, IEnumerable<string>> header in request.Headers)
        {
            headers[header.Key] = string.Join(", ", header.Value);
        }

        if (request.Content is { } content)
        {
            foreach (KeyValuePair<string, IEnumerable<string>> header in content.Headers)
            {
                headers[header.Key] = string.Join(", ", header.Value);
            }
        }

        return headers;
    }

    private sealed record Entry(HttpResponseMessage? Response, Exception? Failure);
}
