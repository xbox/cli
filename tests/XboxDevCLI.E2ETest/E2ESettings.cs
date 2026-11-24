// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

namespace XboxDevCLI.E2ETest;

/// <summary>
/// The tests' settings, read from <c>XBOXDEVCLI_E2E_</c> environment variables. They don't start
/// with <c>XBOX_</c>, because the CLI reads every <c>XBOX_</c> variable as its own configuration.
/// </summary>
/// <remarks>
/// Each value names something that already exists for the signed-in account. The tests are
/// skipped until every setting is set, unless <c>XBOXDEVCLI_E2E_REQUIRED</c> is <c>true</c>, which
/// makes them fail instead. The tests that write also need <c>XBOXDEVCLI_E2E_WRITE</c> set to
/// <c>true</c>.
/// </remarks>
internal sealed class E2ESettings
{
    private const string Prefix = "XBOXDEVCLI_E2E_";

    private static readonly E2ESettings Instance = new();

    private readonly List<string> _missing = [];

    private E2ESettings()
    {
        Cli = Read("CLI");
        Username = Read("USERNAME");
        SellerId = Read("SELLER_ID");
        StoreId = Read("STORE_ID");
        TitleId = Read("TITLE_ID");
        Scid = Read("SCID");
        GameKeyword = Read("GAME_KEYWORD");
        GameJobId = Read("GAME_JOB_ID");
        Sandbox = Read("SANDBOX");
        AchievementImageId = Read("ACHIEVEMENT_IMAGE_ID");
        PlaytestId = Read("PLAYTEST_ID");
        AudienceId = Read("AUDIENCE_ID");
        BatchKeyword = Read("BATCH_KEYWORD");
    }

    /// <summary>The settings, once every one is set.</summary>
    public static E2ESettings Current =>
        Instance.Problem is { } problem ? throw new InvalidOperationException(problem) : Instance;

    /// <summary>Why the tests are skipped, or null when they run.</summary>
    public static string? SkipReason => IsTrue("REQUIRED") ? null : Instance.Problem;

    /// <summary>Why the tests that write are skipped, or null when they run.</summary>
    public static string? WriteSkipReason =>
        SkipReason ?? (IsTrue("WRITE")
            ? null
            : $"Set {Prefix}WRITE=true to run the tests that write. See docs/contributing/testing.md.");

    /// <summary>The absolute path of the <c>xbox</c> executable under test.</summary>
    public string Cli { get; }

    /// <summary>The account <c>xbox auth login</c> signed in.</summary>
    public string Username { get; }

    /// <summary>A Partner Center seller the account belongs to.</summary>
    public string SellerId { get; }

    /// <summary>The Store ID of a game on that seller.</summary>
    public string StoreId { get; }

    /// <summary>The game's XBOX title ID, as the CLI prints it: eight uppercase hexadecimal digits.</summary>
    public string TitleId { get; }

    /// <summary>The game's service configuration ID.</summary>
    public string Scid { get; }

    /// <summary>Text that, ignoring case, the game's name contains and no other game's name on the seller does.</summary>
    public string GameKeyword { get; }

    /// <summary>The job that created the game.</summary>
    public string GameJobId { get; }

    /// <summary>A development sandbox where the game has published XBOX services configuration.</summary>
    public string Sandbox { get; }

    /// <summary>One of the game's achievement image asset IDs.</summary>
    public string AchievementImageId { get; }

    /// <summary>A playtest of the game that stays, so not one that another test creates and deletes.</summary>
    public string PlaytestId { get; }

    /// <summary>An audience of the seller with at least one member.</summary>
    public string AudienceId { get; }

    /// <summary>A tag that exactly one of the account's test account batches has.</summary>
    public string BatchKeyword { get; }

    private string? Problem =>
        _missing.Count == 0
            ? null
            : $"End-to-end test settings aren't set: {string.Join(", ", _missing)}. "
                + "See docs/contributing/testing.md.";

    private static bool IsTrue(string name) =>
        bool.TryParse(Environment.GetEnvironmentVariable(Prefix + name), out bool value) && value;

    private string Read(string name)
    {
        string value = Environment.GetEnvironmentVariable(Prefix + name)?.Trim() ?? string.Empty;
        if (value.Length == 0)
        {
            _missing.Add(Prefix + name);
        }

        return value;
    }
}
