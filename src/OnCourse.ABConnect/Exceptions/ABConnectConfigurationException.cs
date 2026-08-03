namespace OnCourse.ABConnect;

/// <summary>
/// Missing or invalid configuration. Thrown from options validation, from service registration,
/// from the signing handler when the partner id or partner key is absent, and from the query
/// builder when a wildcard field set is requested while
/// <see cref="ABConnectOptions.AllowWildcardFields"/> is false.
/// </summary>
/// <remarks>
/// No request has been made when this is thrown, which is why it derives from
/// <see cref="ABConnectException"/> directly rather than from <see cref="ABConnectRequestException"/>.
/// </remarks>
public sealed class ABConnectConfigurationException : ABConnectException
{
    /// <summary>Initializes a new instance with a default message.</summary>
    public ABConnectConfigurationException()
        : base("The AB Connect configuration is invalid.")
    {
    }

    /// <summary>Initializes a new instance with the specified message.</summary>
    /// <param name="message">
    /// A description of the configuration problem. Must never contain the partner key.
    /// </param>
    public ABConnectConfigurationException(string message)
        : base(message)
    {
    }

    /// <summary>Initializes a new instance with the specified message and underlying cause.</summary>
    /// <param name="message">
    /// A description of the configuration problem. Must never contain the partner key.
    /// </param>
    /// <param name="innerException">The exception that caused this failure, if any.</param>
    public ABConnectConfigurationException(string message, Exception? innerException)
        : base(message, innerException)
    {
    }
}
