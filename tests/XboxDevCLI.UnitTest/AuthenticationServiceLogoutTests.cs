// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using Microsoft.Extensions.Configuration;
using Microsoft.Identity.Client;
using Moq;
using XboxDevCLI.Services;
using Xunit;

namespace XboxDevCLI.UnitTest;

// On Linux, logout replaces the token cache file in the CLI's state directory, so these tests
// point that directory at one of their own.
[Collection(nameof(CliStateEnvironmentCollection))]
public sealed class AuthenticationServiceLogoutTests : IDisposable
{
    private readonly string? _originalDataHome = Environment.GetEnvironmentVariable("XDG_DATA_HOME");
    private readonly string _dataHome = Path.Combine(Path.GetTempPath(), $"xbox-logout-tests-{Guid.NewGuid():N}");

    public AuthenticationServiceLogoutTests() =>
        Environment.SetEnvironmentVariable("XDG_DATA_HOME", _dataHome);

    public void Dispose()
    {
        Environment.SetEnvironmentVariable("XDG_DATA_HOME", _originalDataHome);
        if (Directory.Exists(_dataHome))
        {
            Directory.Delete(_dataHome, recursive: true);
        }
    }

    [Fact]
    public async Task LogoutAsync_RemovesEveryCachedAccount()
    {
        var account1 = Mock.Of<IAccount>(account => account.Username == "first@example.com");
        var account2 = Mock.Of<IAccount>(account => account.Username == "second@example.com");
        var mockApp = CreateMockApplication(account1, account2);
        var auth = new AuthenticationService(CreateAuthConfig(), mockApp.Object);

        var result = await auth.LogoutAsync(CancellationToken.None);

        Assert.Equal(2, result.RemovedAccountCount);
        Assert.Equal(["first@example.com", "second@example.com"], result.Usernames);
        mockApp.Verify(app => app.RemoveAsync(account1), Times.Once);
        mockApp.Verify(app => app.RemoveAsync(account2), Times.Once);
        mockApp.Verify(app => app.RemoveAsync(It.IsAny<IAccount>()), Times.Exactly(2));
    }

    [Fact]
    public async Task LogoutAsync_WithNoCachedAccounts_ReturnsEmptyResult()
    {
        var mockApp = CreateMockApplication();
        var auth = new AuthenticationService(CreateAuthConfig(), mockApp.Object);

        var result = await auth.LogoutAsync(CancellationToken.None);

        Assert.Equal(0, result.RemovedAccountCount);
        Assert.Empty(result.Usernames);
        mockApp.Verify(app => app.RemoveAsync(It.IsAny<IAccount>()), Times.Never);
    }

    /// <summary>
    /// A sign-in without a keyring writes the tokens to this file in plain text, and a later
    /// sign-in and logout with the keyring don't overwrite it. A keyring sign-in that creates the
    /// file makes it readable by others, and a sign-in without a keyring keeps that.
    /// </summary>
    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    public async Task LogoutAsync_OnLinux_LeavesAnEmptyOwnerOnlyTokenCacheFile(int signedInAccounts)
    {
        // Logout replaces the file only on Linux, and only there does XDG_DATA_HOME keep this test
        // out of your real state directory.
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        var cacheFile = Path.Combine(CliState.GetDefaultDirectory(), "xboxdevcli_tokencache.bin");
        Directory.CreateDirectory(Path.GetDirectoryName(cacheFile)!);
        File.WriteAllText(cacheFile, "plain-text tokens");
        File.SetUnixFileMode(
            cacheFile,
            UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead | UnixFileMode.OtherRead);
        var accounts = Enumerable.Range(0, signedInAccounts)
            .Select(_ => Mock.Of<IAccount>(account => account.Username == "dev@nodpublishers.com"))
            .ToArray();
        var auth = new AuthenticationService(CreateAuthConfig(), CreateMockApplication(accounts).Object);

        await auth.LogoutAsync(CancellationToken.None);

        Assert.Empty(File.ReadAllBytes(cacheFile));
        Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(cacheFile));
    }

    private static Mock<IPublicClientApplication> CreateMockApplication(params IAccount[] accounts)
    {
        var tokenCache = new Mock<ITokenCache>();
        var mockApp = new Mock<IPublicClientApplication>();
        mockApp.SetupGet(app => app.UserTokenCache).Returns(tokenCache.Object);
        mockApp.Setup(app => app.GetAccountsAsync()).ReturnsAsync(accounts);
        mockApp.Setup(app => app.RemoveAsync(It.IsAny<IAccount>())).Returns(Task.CompletedTask);
        return mockApp;
    }

    private static IConfiguration CreateAuthConfig() => new ConfigurationBuilder()
        .AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["AzureAd:ClientId"] = "00000000-0000-0000-0000-000000000001",
            ["AzureAd:TenantId"] = "organizations",
            ["AzureAd:Scopes"] = "openid",
        })
        .Build();
}
