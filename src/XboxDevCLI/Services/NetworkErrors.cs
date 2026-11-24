// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

namespace XboxDevCLI.Services;

/// <summary>
/// Adds the cause to network errors. .NET often reports a failed connection with a generic
/// message, such as "The SSL connection could not be established, see inner exception.",
/// and commands write only the message.
/// </summary>
internal static class NetworkErrors
{
    /// <summary>
    /// The message of <paramref name="exception"/>, followed by its innermost cause when it's a
    /// network error whose message doesn't already say it.
    /// </summary>
    internal static string Describe(Exception exception)
    {
        var cause = exception.GetBaseException().Message;
        return exception is HttpRequestException && !exception.Message.Contains(cause, StringComparison.Ordinal)
            ? $"{exception.Message} {cause.TrimEnd('.')}."
            : exception.Message;
    }

    /// <summary>
    /// Wraps <paramref name="exception"/> so its message says the cause, keeping its error and
    /// status code.
    /// </summary>
    internal static HttpRequestException WithCause(HttpRequestException exception) =>
        new(exception.HttpRequestError, Describe(exception), exception, exception.StatusCode);
}
