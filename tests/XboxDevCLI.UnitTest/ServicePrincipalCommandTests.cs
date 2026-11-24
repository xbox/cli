// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System.CommandLine;
using System.Net;
using System.Text.Json;
using XboxDevCLI.CommandLine;
using XboxDevCLI.Output;
using XboxDevCLI.PlayFab.Commands;
using Xunit;

namespace XboxDevCLI.UnitTest;

/// <summary>
/// Coverage for <c>playfab service-principal</c>: required identifiers, request mapping, the
/// grants file, revoke confirmation, and the refusal of service-principal authentication modes.
/// </summary>
[Collection("ConsoleRedirect")]
public class ServicePrincipalCommandTests
{
    private const string StudioId = "1234567890ABCDEF";
    private const string TenantId = "22222222-3333-4444-5555-666666666666";
    private const string ObjectId = "33333333-4444-5555-6666-777777777777";
    private const string ClientId = "44444444-5555-6666-7777-888888888888";

    private const string AccessEnvelope =
        """{"code":200,"status":"OK","data":{"ServicePrincipal":{"TenantId":"t","ObjectId":"o","Version":2}}}""";

    private const string ValidGrants =
        """{"StudioPermissions":[],"TitleGrants":[{"TitleId":"ABC123","Permissions":["Admin"]}]}""";

    #region Command structure

    [Fact]
    public void Create_HasFourSubcommandsWithRequiredIdentifiers()
    {
        var command = ServicePrincipalCommandFactory.Create();

        Assert.Equal("service-principal", command.Name);
        Assert.Equal(["list", "get", "set", "revoke"], command.Subcommands.Select(c => c.Name));

        AssertRequired(command, "list", "--studio-id");
        AssertRequired(command, "get", "--studio-id", "--principal-tenant-id", "--principal-object-id");
        AssertRequired(command, "set", "--studio-id", "--principal-tenant-id", "--principal-object-id", "--display-name", "--grants-file");
        AssertRequired(command, "revoke", "--studio-id", "--principal-tenant-id", "--principal-object-id");
    }

    [Theory]
    [InlineData("list")]
    [InlineData("get", "--principal-tenant-id", TenantId, "--principal-object-id", ObjectId)]
    [InlineData("revoke", "--principal-tenant-id", TenantId, "--principal-object-id", ObjectId)]
    public void StudioId_IsNotTakenFromContext(params string[] args)
    {
        var parsed = Program.BuildRootCommand().Parse(
            ["playfab", "service-principal", .. args],
            Program.NoResponseFileConfig);

        Assert.Contains(parsed.Errors, e => e.Message.Contains("--studio-id", StringComparison.Ordinal));
    }

    [Fact]
    public void Commands_AreExperimentalDescribeReplacementAndTakeNoTitle()
    {
        var root = Program.BuildRootCommand();
        var family = root.Subcommands.Single(c => c.Name == "playfab").Subcommands.Single(c => c.Name == "service-principal");

        Assert.Equal(CommandStage.Experimental, CommandMetadataRegistry.GetMetadata(family).Stage);

        var set = family.Subcommands.Single(c => c.Name == "set");
        Assert.Contains("REPLACE", set.Description, StringComparison.Ordinal);
        Assert.Contains("removed", CommandMetadataRegistry.GetMetadata(set).SafetyDetails, StringComparison.Ordinal);

        foreach (var command in family.Subcommands)
        {
            Assert.False(CommandMetadataRegistry.CallsPlayFabTitle(command));
        }
    }

    #endregion

    #region Request mapping

    [Fact]
    public async Task RunListAsync_MapsPagingAndTrimsStudio()
    {
        var handler = new MockHttpMessageHandler(RoutingHttpHandler.Json(
            """{"code":200,"status":"OK","data":{"ServicePrincipals":[],"ContinuationToken":"next"}}"""));
        using var client = PlayFabTestClient.Create(handler);
        var stdout = new StringWriter();

        var exit = await ServicePrincipalCommandFactory.RunListAsync(
            client, $" {StudioId} ", 25, "token", OutputOptions.Json, stdout, new StringWriter(), CancellationToken.None);

        Assert.Equal(0, exit);
        Assert.EndsWith(ServicePrincipalCommandFactory.ListPath, handler.LastRequestUri);
        using var body = JsonDocument.Parse(handler.LastRequestBody!);
        Assert.Equal(StudioId, body.RootElement.GetProperty("StudioId").GetString());
        Assert.Equal(25, body.RootElement.GetProperty("PageSize").GetDouble());
        Assert.Equal("token", body.RootElement.GetProperty("ContinuationToken").GetString());
        Assert.Contains("next", stdout.ToString(), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(101)]
    public async Task RunListAsync_LimitOutOfRange_FailsWithoutCalling(int limit)
    {
        var handler = new MockHttpMessageHandler(RoutingHttpHandler.Json(AccessEnvelope));
        using var client = PlayFabTestClient.Create(handler);
        var stderr = new StringWriter();

        var exit = await ServicePrincipalCommandFactory.RunListAsync(
            client, StudioId, limit, null, OutputOptions.Json, new StringWriter(), stderr, CancellationToken.None);

        Assert.Equal(1, exit);
        Assert.Null(handler.LastRequest);
        Assert.Contains("--limit", stderr.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task RunGetAsync_MapsIdentifiers()
    {
        var handler = new MockHttpMessageHandler(RoutingHttpHandler.Json(AccessEnvelope));
        using var client = PlayFabTestClient.Create(handler);

        var exit = await ServicePrincipalCommandFactory.RunGetAsync(
            client, StudioId, TenantId, ObjectId, OutputOptions.Json, new StringWriter(), new StringWriter(), CancellationToken.None);

        Assert.Equal(0, exit);
        Assert.EndsWith(ServicePrincipalCommandFactory.GetPath, handler.LastRequestUri);
        AssertIdentifiers(handler.LastRequestBody!);
    }

    [Fact]
    public async Task RunSetAsync_SendsCompleteGrantSet()
    {
        var handler = new MockHttpMessageHandler(RoutingHttpHandler.Json(AccessEnvelope));
        using var client = PlayFabTestClient.Create(handler);
        using var file = new TempFile(ValidGrants);

        var exit = await ServicePrincipalCommandFactory.RunSetAsync(
            client, StudioId, TenantId, ObjectId, " Nod Publishers build ", file.Path, null,
            OutputOptions.Json, new StringWriter(), new StringWriter(), CancellationToken.None);

        Assert.Equal(0, exit);
        Assert.EndsWith(ServicePrincipalCommandFactory.SetPath, handler.LastRequestUri);
        AssertIdentifiers(handler.LastRequestBody!);
        using var body = JsonDocument.Parse(handler.LastRequestBody!);
        var root = body.RootElement;
        Assert.False(root.TryGetProperty("ClientId", out _));
        Assert.Equal("Nod Publishers build", root.GetProperty("DisplayName").GetString());
        Assert.Equal(0, root.GetProperty("StudioPermissions").GetArrayLength());
        var grant = Assert.Single(root.GetProperty("TitleGrants").EnumerateArray());
        Assert.Equal("ABC123", grant.GetProperty("TitleId").GetString());
        Assert.Equal("Admin", Assert.Single(grant.GetProperty("Permissions").EnumerateArray()).GetString());
        Assert.False(root.TryGetProperty("ExpectedVersion", out _));
    }

    [Theory]
    [InlineData("envelope")]
    [InlineData("data")]
    [InlineData("principal")]
    public async Task RunSetAsync_AcceptsGetOutputAndSendsExpectedVersion(string shape)
    {
        var principal =
            $$"""{"TenantId":"{{TenantId.ToUpperInvariant()}}","ObjectId":"{{ObjectId}}","ClientId":"{{ClientId}}","DisplayName":"Nod Publishers build","LastUpdated":"2026-01-01T00:00:00Z","Version":3,"StudioPermissions":["ManageUsers"],"TitleGrants":[{"TitleId":"ABC123","Permissions":["Admin"]}]}""";
        var json = shape switch
        {
            "envelope" => """{"code":200,"status":"OK","data":{"ServicePrincipal":""" + principal + "}}",
            "data" => """{"ServicePrincipal":""" + principal + "}",
            _ => principal,
        };
        var handler = new MockHttpMessageHandler(RoutingHttpHandler.Json(AccessEnvelope));
        using var client = PlayFabTestClient.Create(handler);
        using var file = new TempFile(json);

        var exit = await ServicePrincipalCommandFactory.RunSetAsync(
            client, StudioId, TenantId, ObjectId, "Nod Publishers build", file.Path, 3,
            OutputOptions.Json, new StringWriter(), new StringWriter(), CancellationToken.None);

        Assert.Equal(0, exit);
        using var body = JsonDocument.Parse(handler.LastRequestBody!);
        var root = body.RootElement;
        Assert.Equal(3, root.GetProperty("ExpectedVersion").GetDouble());
        Assert.Equal(TenantId, root.GetProperty("TenantId").GetString());
        Assert.Equal("ManageUsers", Assert.Single(root.GetProperty("StudioPermissions").EnumerateArray()).GetString());
        Assert.False(root.TryGetProperty("Version", out _));
        Assert.False(root.TryGetProperty("LastUpdated", out _));
        Assert.False(root.TryGetProperty("ClientId", out _));
    }

    [Fact]
    public async Task RunSetAsync_ExpectedVersionWithoutFileVersion_IsSent()
    {
        var handler = new MockHttpMessageHandler(RoutingHttpHandler.Json(AccessEnvelope));
        using var client = PlayFabTestClient.Create(handler);
        using var file = new TempFile(ValidGrants);

        var exit = await ServicePrincipalCommandFactory.RunSetAsync(
            client, StudioId, TenantId, ObjectId, "name", file.Path, 0,
            OutputOptions.Json, new StringWriter(), new StringWriter(), CancellationToken.None);

        Assert.Equal(0, exit);
        using var body = JsonDocument.Parse(handler.LastRequestBody!);
        Assert.Equal(0, body.RootElement.GetProperty("ExpectedVersion").GetDouble());
    }

    [Theory]
    [InlineData("\"Version\":3", null, "--expected-version 3")]
    [InlineData("\"Version\":3", 4.0, "--expected-version is 4")]
    [InlineData("\"TenantId\":\"99999999-0000-0000-0000-000000000000\"", null, "--principal-tenant-id")]
    [InlineData("\"ObjectId\":\"99999999-0000-0000-0000-000000000000\"", null, "--principal-object-id")]
    [InlineData("\"DisplayName\":\"Old name\"", null, "--display-name")]
    [InlineData("\"Version\":null", -1.0, "--expected-version must be")]
    public async Task RunSetAsync_FileDisagreesWithOptions_FailsWithoutCalling(string field, double? expectedVersion, string expected)
    {
        var handler = new MockHttpMessageHandler(RoutingHttpHandler.Json(AccessEnvelope));
        using var client = PlayFabTestClient.Create(handler);
        using var file = new TempFile($$"""{{{field}},"StudioPermissions":[],"TitleGrants":[]}""");
        var stderr = new StringWriter();

        var exit = await ServicePrincipalCommandFactory.RunSetAsync(
            client, StudioId, TenantId, ObjectId, "Nod Publishers build", file.Path, expectedVersion,
            OutputOptions.Json, new StringWriter(), stderr, CancellationToken.None);

        Assert.Equal(1, exit);
        Assert.Null(handler.LastRequest);
        Assert.Contains(expected, stderr.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task RunSetAsync_MismatchedDisplayName_StripsControlCharactersFromTheError()
    {
        var handler = new MockHttpMessageHandler(RoutingHttpHandler.Json(AccessEnvelope));
        using var client = PlayFabTestClient.Create(handler);
        using var file = new TempFile("""{"DisplayName":"Old\u001b[31m\nError: forged","StudioPermissions":[],"TitleGrants":[]}""");
        var stderr = new StringWriter();

        var exit = await ServicePrincipalCommandFactory.RunSetAsync(
            client, StudioId, TenantId, ObjectId, "Nod Publishers build", file.Path, null,
            OutputOptions.Json, new StringWriter(), stderr, CancellationToken.None);

        Assert.Equal(1, exit);
        Assert.Contains("'Old[31mError: forged'", stderr.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain('\u001b', stderr.ToString());
    }

    [Fact]
    public async Task RunSetAsync_MissingFile_FailsWithoutCalling()
    {
        var handler = new MockHttpMessageHandler(RoutingHttpHandler.Json(AccessEnvelope));
        using var client = PlayFabTestClient.Create(handler);
        var stderr = new StringWriter();
        var missing = Path.Combine(Path.GetTempPath(), $"xdc-missing-{Guid.NewGuid():N}", "grants.json");

        var exit = await ServicePrincipalCommandFactory.RunSetAsync(
            client, StudioId, TenantId, ObjectId, "name", missing, null,
            OutputOptions.Json, new StringWriter(), stderr, CancellationToken.None);

        Assert.Equal(1, exit);
        Assert.Null(handler.LastRequest);
        Assert.Contains("File not found", stderr.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task RunSetAsync_InvalidGrants_FailsWithoutCalling()
    {
        var handler = new MockHttpMessageHandler(RoutingHttpHandler.Json(AccessEnvelope));
        using var client = PlayFabTestClient.Create(handler);
        using var file = new TempFile("""{"StudioPermissions":[]}""");
        var stderr = new StringWriter();

        var exit = await ServicePrincipalCommandFactory.RunSetAsync(
            client, StudioId, TenantId, ObjectId, "name", file.Path, null,
            OutputOptions.Json, new StringWriter(), stderr, CancellationToken.None);

        Assert.Equal(1, exit);
        Assert.Null(handler.LastRequest);
        Assert.Contains("TitleGrants", stderr.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task RunSetAsync_ApiError_ReturnsOneOnStderr()
    {
        var handler = new MockHttpMessageHandler(RoutingHttpHandler.Json(
            """{"code":403,"status":"Forbidden","error":"NotAuthorized","errorCode":1089,"errorMessage":"denied"}""",
            HttpStatusCode.Forbidden));
        using var client = PlayFabTestClient.Create(handler);
        using var file = new TempFile(ValidGrants);
        var stdout = new StringWriter();
        var stderr = new StringWriter();

        var exit = await ServicePrincipalCommandFactory.RunSetAsync(
            client, StudioId, TenantId, ObjectId, "name", file.Path, null,
            OutputOptions.Json, stdout, stderr, CancellationToken.None);

        Assert.Equal(1, exit);
        Assert.Empty(stdout.ToString());
        Assert.Contains("denied", stderr.ToString(), StringComparison.Ordinal);
    }

    #endregion

    #region Grants file

    [Fact]
    public void TryParseGrants_AcceptsCaseInsensitiveNamesAndEmptyArrays()
    {
        Assert.True(ServicePrincipalCommandFactory.TryParseGrants(
            """{"studioPermissions":["ManageUsers"],"titleGrants":[]}""", out var grants, out var error));

        Assert.Null(error);
        Assert.Equal(["ManageUsers"], grants!.StudioPermissions);
        Assert.Empty(grants.TitleGrants!);
    }

    [Theory]
    [InlineData("", "empty")]
    [InlineData("[]", "JSON object")]
    [InlineData("{not json", "Invalid grants file")]
    [InlineData("""{"TitleGrants":[]}""", "both StudioPermissions and TitleGrants")]
    [InlineData("""{"StudioPermissions":null,"TitleGrants":[]}""", "both StudioPermissions and TitleGrants")]
    [InlineData("""{"StudioPermissions":[],"TitleGrant":[]}""", "TitleGrant")]
    [InlineData("""{"StudioPermissions":[" "],"TitleGrants":[]}""", "empty permission name")]
    [InlineData("""{"StudioPermissions":[],"TitleGrants":[{"Permissions":["Admin"]}]}""", "needs a TitleId")]
    [InlineData("""{"StudioPermissions":[],"TitleGrants":[{"TitleId":"ABC123"}]}""", "'ABC123' needs a non-empty Permissions array")]
    [InlineData("""{"StudioPermissions":[],"TitleGrants":[{"TitleId":"ABC123","Permissions":[]}]}""", "Omit the title")]
    [InlineData("""{"StudioPermissions":[],"TitleGrants":[{"TitleId":"ABC123","Permission":["Admin"]}]}""", "Permission")]
    [InlineData("""{"code":200,"data":{"ServicePrincipal":{"StudioPermissions":[],"TitleGrants":[]}},"extra":1}""", "'extra' next to 'data'")]
    [InlineData("""{"ServicePrincipal":{"StudioPermissions":[],"TitleGrants":[]},"StudioPermissions":[]}""", "'StudioPermissions' next to 'ServicePrincipal'")]
    [InlineData("""{"ServicePrincipal":[]}""", "'ServicePrincipal' must be a JSON object")]
    [InlineData("""{"ServicePrincipal":{"StudioPermissions":[],"TitleGrants":[],"Versoin":1}}""", "Versoin")]
    public void TryParseGrants_RejectsIncompleteOrUnknownInput(string json, string expected)
    {
        Assert.False(ServicePrincipalCommandFactory.TryParseGrants(json, out var grants, out var error));

        Assert.Null(grants);
        Assert.Contains(expected, error, StringComparison.Ordinal);
    }

    #endregion

    #region Revoke confirmation

    [Fact]
    public async Task RunRevokeAsync_Unconfirmed_IsRefusedBeforeSending()
    {
        var handler = new MockHttpMessageHandler(RoutingHttpHandler.Json("""{"code":200,"status":"OK","data":{}}"""));
        using var client = PlayFabTestClient.Create(handler);
        var stderr = new StringWriter();

        var exit = await ServicePrincipalCommandFactory.RunRevokeAsync(
            client, StudioId, TenantId, ObjectId, OutputOptions.Json, new StringWriter(), stderr,
            CancellationToken.None, confirmed: false);

        Assert.Equal(1, exit);
        Assert.Null(handler.LastRequest);
        Assert.Contains(ServicePrincipalCommandFactory.DeletePath, stderr.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task RunRevokeAsync_Confirmed_SendsDelete()
    {
        var handler = new MockHttpMessageHandler(RoutingHttpHandler.Json("""{"code":200,"status":"OK","data":{}}"""));
        using var client = PlayFabTestClient.Create(handler);

        var exit = await ServicePrincipalCommandFactory.RunRevokeAsync(
            client, StudioId, TenantId, ObjectId, OutputOptions.Json, new StringWriter(), new StringWriter(),
            CancellationToken.None, confirmed: true);

        Assert.Equal(0, exit);
        Assert.EndsWith(ServicePrincipalCommandFactory.DeletePath, handler.LastRequestUri);
        AssertIdentifiers(handler.LastRequestBody!);
    }

    #endregion

    #region Authentication mode

    [Theory]
    [InlineData("list")]
    [InlineData("get", "--principal-tenant-id", TenantId, "--principal-object-id", ObjectId)]
    [InlineData("revoke", "--principal-tenant-id", TenantId, "--principal-object-id", ObjectId, "--confirm")]
    public async Task Commands_RefuseServicePrincipalModes(params string[] args)
    {
        using var automation = TestEnvironment.UseAuthenticationMode("azurecli");
        var originalOut = Console.Out;
        var originalError = Console.Error;
        var stdout = new StringWriter();
        var stderr = new StringWriter();
        Console.SetOut(stdout);
        Console.SetError(stderr);
        try
        {
            var exit = await Program.Main(
                ["--disable-telemetry", "playfab", "service-principal", .. args, "--studio-id", StudioId]);

            Assert.Equal(1, exit);
            Assert.Empty(stdout.ToString());
            Assert.Contains(
                ServicePrincipalCommandFactory.GetAutomationRefusal("azurecli"),
                stderr.ToString(),
                StringComparison.Ordinal);
        }
        finally
        {
            Console.SetOut(originalOut);
            Console.SetError(originalError);
        }
    }

    #endregion

    #region Helpers

    private static void AssertRequired(Command family, string subcommand, params string[] options)
    {
        var command = family.Subcommands.Single(c => c.Name == subcommand);
        foreach (var option in options)
        {
            Assert.True(command.Options.Single(o => o.Name == option).Required, $"{subcommand} {option} should be required.");
        }
    }

    private static void AssertIdentifiers(string json)
    {
        using var body = JsonDocument.Parse(json);
        Assert.Equal(StudioId, body.RootElement.GetProperty("StudioId").GetString());
        Assert.Equal(TenantId, body.RootElement.GetProperty("TenantId").GetString());
        Assert.Equal(ObjectId, body.RootElement.GetProperty("ObjectId").GetString());
    }

    private sealed class TempFile : IDisposable
    {
        public TempFile(string content)
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"xdc-grants-{Guid.NewGuid():N}.json");
            File.WriteAllText(Path, content);
        }

        public string Path { get; }

        public void Dispose() => File.Delete(Path);
    }

    #endregion
}
