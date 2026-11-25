// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

namespace XboxDevCLI.E2ETest;

/// <summary>
/// The settings of <see cref="MsaTests"/>, which run as a personal Microsoft account (MSA), read from
/// <c>XBOXDEVCLI_E2E_CLI</c>, <c>XBOXDEVCLI_E2E_USERNAME</c>, and <c>XBOXDEVCLI_E2E_MSA_</c> environment variables.
/// </summary>
/// <remarks>
/// The tests are skipped until every setting is set, unless <c>XBOXDEVCLI_E2E_REQUIRED</c> is <c>true</c>, which makes
/// them fail instead.
/// </remarks>
internal sealed class MsaSettings
{
    private const string Prefix = "XBOXDEVCLI_E2E_";

    private static readonly MsaSettings Instance = new();

    private readonly List<string> _missing = [];

    private MsaSettings()
    {
        Cli = Read("CLI");
        Username = Read("USERNAME");
        SellerId = Read("MSA_SELLER_ID");
        StoreId = Read("MSA_STORE_ID");
        TitleId = Read("MSA_TITLE_ID");
        OtherSellerId = Read("MSA_OTHER_SELLER_ID");
        OtherStoreId = Read("MSA_OTHER_STORE_ID");
    }

    /// <summary>The settings, once every one is set.</summary>
    public static MsaSettings Current =>
        Instance.Problem is { } problem ? throw new InvalidOperationException(problem) : Instance;

    /// <summary>Why the tests are skipped, or null when they run.</summary>
    public static string? SkipReason =>
        bool.TryParse(Environment.GetEnvironmentVariable(Prefix + "REQUIRED"), out bool required) && required
            ? null
            : Instance.Problem;

    /// <summary>The absolute path of the <c>xbox</c> executable under test.</summary>
    public string Cli { get; }

    /// <summary>The personal account <c>xbox auth login</c> signed in.</summary>
    public string Username { get; }

    /// <summary>The account's only Partner Center seller.</summary>
    public string SellerId { get; }

    /// <summary>The Store ID of a game on that seller.</summary>
    public string StoreId { get; }

    /// <summary>The game's XBOX title ID, as the CLI prints it: eight uppercase hexadecimal digits.</summary>
    public string TitleId { get; }

    /// <summary>A seller the account doesn't belong to.</summary>
    public string OtherSellerId { get; }

    /// <summary>The Store ID of a game on that other seller.</summary>
    public string OtherStoreId { get; }

    private string? Problem =>
        _missing.Count == 0
            ? null
            : $"End-to-end test settings for a personal Microsoft account aren't set: {string.Join(", ", _missing)}.";

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
