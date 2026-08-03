namespace OnCourse.ABConnect;

/// <summary>
/// The service returned a page sequence that violates a documented or requested ordering guarantee.
/// Raised by the paging helpers on <see cref="IABConnectFeed"/>, never by a single-page call on
/// <see cref="IABConnectClient"/>.
/// </summary>
/// <remarks>
/// Examples are an event page whose sequence numbers are not strictly ascending after
/// <c>sort[events]=seq</c> was requested, or a document traversal whose accumulated row count does
/// not reconcile with the count the first page reported. Both mean a partial result would be
/// indistinguishable from a complete one, so the traversal fails instead of returning it.
/// </remarks>
public sealed class ABConnectPagingException : ABConnectException
{
    /// <summary>Initializes a new instance with a default message.</summary>
    public ABConnectPagingException()
        : base("The AB Connect page sequence violated an ordering or completeness guarantee.")
    {
    }

    /// <summary>Initializes a new instance with the specified message.</summary>
    /// <param name="message">
    /// A description of the guarantee that was violated, including the observed values that
    /// violated it. Must never contain the partner key.
    /// </param>
    public ABConnectPagingException(string message)
        : base(message)
    {
    }

    /// <summary>Initializes a new instance with the specified message and underlying cause.</summary>
    /// <param name="message">A description of the guarantee that was violated.</param>
    /// <param name="innerException">The exception that caused this failure, if any.</param>
    public ABConnectPagingException(string message, Exception? innerException)
        : base(message, innerException)
    {
    }
}
