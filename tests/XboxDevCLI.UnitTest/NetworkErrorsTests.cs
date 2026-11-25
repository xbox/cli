// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using XboxDevCLI.Services;
using Xunit;

namespace XboxDevCLI.UnitTest;

public class NetworkErrorsTests
{
    /// <summary>
    /// The cause is added once, so a message that's described again, such as by the command that
    /// reports it, doesn't repeat it.
    /// </summary>
    [Fact]
    public void Describe_AddsTheCauseOnce()
    {
        var wrapped = NetworkErrors.WithCause(new HttpRequestException(
            HttpRequestError.SecureConnectionError,
            "The SSL connection could not be established, see inner exception.",
            new IOException("The remote certificate is invalid according to the validation procedure")));

        Assert.Equal(
            "The SSL connection could not be established, see inner exception. " +
            "The remote certificate is invalid according to the validation procedure.",
            NetworkErrors.Describe(wrapped));
    }

    /// <summary>Messages that already say the cause, and other errors, are written as they are.</summary>
    [Fact]
    public void Describe_KeepsMessagesThatAlreadySayTheCauseAndOtherErrors()
    {
        var refused = new HttpRequestException(
            HttpRequestError.ConnectionError, "Connection refused (localhost:443)", new IOException("Connection refused"));
        var other = new InvalidOperationException("Failed to upload block 0.", new IOException("Connection reset by peer"));

        Assert.Equal(refused.Message, NetworkErrors.Describe(refused));
        Assert.Equal(other.Message, NetworkErrors.Describe(other));
    }
}
