namespace OnCourse.ABConnect;

/// <summary>
/// Base type for every failure the AB Connect SDK reports.
/// </summary>
/// <remarks>
/// The SDK's failure model is a typed exception hierarchy, not a result type. For
/// every method on <see cref="IABConnectClient"/> and <see cref="IABConnectFeed"/>: the method
/// either returns a non-null value that was deserialized from a 2xx response body, or it throws. It
/// never returns null, and it never returns a default-constructed envelope. Catching this type
/// catches every SDK-originated failure; catch a derived type to branch on a specific outcome, for
/// example <see cref="ABConnectNotLicensedException"/> for the documented and normal case of an
/// event that references data outside the account's license.
/// </remarks>
public abstract class ABConnectException : Exception
{
    /// <summary>Initializes a new instance with a default message.</summary>
    protected ABConnectException()
        : base("The AB Connect request failed.")
    {
    }

    /// <summary>Initializes a new instance with the specified message.</summary>
    /// <param name="message">A description of the failure. Must never contain the partner key.</param>
    protected ABConnectException(string message)
        : base(message)
    {
    }

    /// <summary>Initializes a new instance with the specified message and underlying cause.</summary>
    /// <param name="message">A description of the failure. Must never contain the partner key.</param>
    /// <param name="innerException">The exception that caused this failure, if any.</param>
    protected ABConnectException(string message, Exception? innerException)
        : base(message, innerException)
    {
    }
}
