namespace OnCourse.ABConnect;

/// <summary>
/// One entry from an AB Connect JSON:API <c>errors[]</c> array. Every member is nullable because
/// AB Connect documents the shape but does not promise any particular member is present.
/// </summary>
/// <param name="Title">A short, human-readable summary of the problem.</param>
/// <param name="Detail">A human-readable explanation specific to this occurrence of the problem.</param>
/// <param name="Status">The HTTP status code for this error, as a string, as JSON:API specifies.</param>
/// <param name="SourcePointer">
/// A JSON Pointer to the member of the request body that caused the error, taken from
/// <c>source.pointer</c>.
/// </param>
/// <param name="SourceParameter">
/// The name of the query parameter that caused the error, taken from <c>source.parameter</c>. This
/// is the member that identifies which filter, field, or sort expression AB Connect rejected.
/// </param>
public sealed record ABConnectApiError(
    string? Title,
    string? Detail,
    string? Status,
    string? SourcePointer,
    string? SourceParameter);
