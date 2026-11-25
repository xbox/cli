// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System.CommandLine;
using System.CommandLine.Parsing;
using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;
using XboxDevCLI.CommandLine;
using XboxDevCLI.Output;
using XboxDevCLI.Services;

namespace XboxDevCLI.Commands;

/// <summary>
/// Builds <c>xbox store-listing</c>: a game's Store listing text in Partner Center, one language at a time.
/// </summary>
internal static partial class StoreListingCommandFactory
{
    /// <summary>The problem <c>code</c> of a 404 for a language the game has no Store listing in.</summary>
    internal const string NotFoundCode = "store_listing_not_found";

    /// <summary>The problem <c>code</c> of a 422 for a game that doesn't use Partner Center's modular publishing.</summary>
    internal const string GameNotModularCode = "game_not_modular";

    internal const string LanguageFormat =
        "A language code is letters and digits in parts separated by hyphens, such as en-us or zh-hans-cn.";

    public static Command Create()
    {
        var command = new Command("store-listing", "Get the Store listing text of a game");
        command.Subcommands.Add(CreateGetCommand());
        return command.Requires(CommandRequirement.Authentication).Calls(ServiceEndpoints.XboxPartner);
    }

    /// <summary>The language code, trimmed and in lower case, or false when it isn't a language code's shape.</summary>
    internal static bool TryNormalizeLanguage(string? value, out string language)
    {
        string? trimmed = value?.Trim();
        language = trimmed is not null && LanguagePattern().IsMatch(trimmed) ? trimmed.ToLowerInvariant() : string.Empty;
        return language.Length > 0;
    }

    // ASCII only, so the code can't name another route, such as '..'.
    [GeneratedRegex("^[A-Za-z0-9]+(?:-[A-Za-z0-9]+)*$")]
    private static partial Regex LanguagePattern();

    private static Command CreateGetCommand()
    {
        var game = XboxPartnerGameResolver.CreateGameOptions();
        var language = new Option<string>("--language")
        {
            Description = "The listing's language code, such as en-us, in any case. It must match the listing's code "
                + "exactly: en doesn't read an en-us listing.",
            HelpName = "code",
            Required = true,
        };
        language.Validators.Add(result =>
        {
            if (result.Tokens.Count > 0 && !TryNormalizeLanguage(result.Tokens[0].Value, out _))
            {
                result.AddError($"Invalid language code '{result.Tokens[0].Value}'. {LanguageFormat}");
            }
        });
        var seller = XboxPartnerCommandSupport.CreateSellerIdOption();
        var baseUrl = XboxPartnerCommandSupport.CreateBaseUrlOption();
        var command = new Command("get", "Get a game's Store listing text in one language")
        {
            game.StoreId, game.TitleId, language, seller, baseUrl,
        };
        command.Validators.Add(result => XboxPartnerGameResolver.RequireOneGame(result, game));
        command.SetAction((ParseResult result, CancellationToken cancellationToken) =>
            GetAsync(result, game, language, seller, baseUrl, cancellationToken));
        return command.WithMetadata(new CommandMetadata(
            Safety: CommandSafety.ReadOnly,
            Scope: "The Store listing of the game chosen by --store-id or --xbox-title-id, in the language chosen by "
                + "--language, for the seller selected by --seller-id, XBOX_SELLER_ID, or 'context set'.",
            Output: "A JSON object with storeId, language, title (the product name), description, and features "
                + "(the product features). Text that isn't set is an empty string, or an empty features array, and "
                + "stderr names it.",
            Examples:
            [
                "xbox store-listing get --store-id 9N0000000000 --language en-us",
                "xbox store-listing get --xbox-title-id 1234ABCD --language fr-fr --seller-id 1234567",
            ],
            LearnMore:
            [
                "Reads the listing on the game's Main branch as it is in Partner Center now, which the Microsoft Store shows "
                    + "only after you publish a submission.",
                "Fails (HTTP 404) when the game has no Store listing in that language.",
                "Fails (HTTP 422) for a game that doesn't use Partner Center's modular publishing.",
                "Fails (HTTP 502) when Partner Center doesn't support the language code.",
                "If you belong to several sellers, Partner Center reads the game from the seller you last selected there, "
                    + "whatever --seller-id says.",
            ]));
    }

    private static async Task<int> GetAsync(
        ParseResult result,
        XboxPartnerGameResolver.GameOptions game,
        Option<string> languageOption,
        Option<string?> sellerOption,
        Option<string?> baseUrlOption,
        CancellationToken cancellationToken)
    {
        TryNormalizeLanguage(result.GetRequiredValue(languageOption), out string language);
        string? storeId = null;
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            var configuration = Program.GetConfiguration(result);
            var baseUrl = XboxPartnerCommandSupport.ResolveBaseUrl(result, baseUrlOption, configuration);
            using var client = Program.XboxPartnerClientFactory is null
                ? new XboxPartnerClient(configuration, authenticationService: Program.GetAuthentication(result))
                : Program.XboxPartnerClientFactory(configuration);
            var sellerId = await XboxPartnerCommandSupport.GetSellerIdAsync(result, sellerOption)
                .WaitAsync(cancellationToken);
            storeId = await XboxPartnerGameResolver.ResolveStoreIdAsync(
                result, game, client, baseUrl, sellerId, cancellationToken);

            string json;
            using (RequestProgress.Start("Reading Store listing"))
            {
                json = await client.GetStoreListingAsync(baseUrl, sellerId, storeId, language, cancellationToken);
            }

            WriteUnsetText(json, language);
            Console.WriteLine(Program.FormatOutput(json, OutputOptions.From(result).IsCompact, dataOnly: false));
            return 0;
        }
        catch (XboxPartnerApiException exception) when (DescribeFailure(exception, storeId, language) is { } diagnosis)
        {
            XboxPartnerCommandSupport.WriteCorrelationId(exception.CorrelationId, Console.Error);
            XboxPartnerCommandSupport.WriteGatewayError(exception, result, diagnosis);
            return 1;
        }
        // Only the service's failures, a response that couldn't be read, and a request timeout, which cancels without the
        // caller, are reported here. Everything else, including the caller's cancellation, reaches the shared error boundary.
        catch (Exception exception) when (exception is XboxPartnerApiException or XboxPartnerResponseException
            || (exception is OperationCanceledException && !cancellationToken.IsCancellationRequested))
        {
            XboxPartnerCommandSupport.WriteRequestError(exception, result, cancellationToken);
            return 1;
        }
    }

    /// <summary>
    /// Explains a missing listing, a game that doesn't use modular publishing, and a failure to read a listing, which is
    /// most often a language code Partner Center doesn't support. Other failures keep the general guidance.
    /// </summary>
    internal static string? DescribeFailure(XboxPartnerApiException exception, string? storeId, string language) =>
        (exception.StatusCode, TestAccountCommandFactory.ReadProblemString(exception.ResponseBody, "code")) switch
        {
            (HttpStatusCode.NotFound, NotFoundCode) =>
                $"Game {storeId} has no Store listing in {language}. Partner Center matches the language code exactly: "
                + "en doesn't read an en-us listing, nor the reverse.",
            (HttpStatusCode.UnprocessableEntity, GameNotModularCode) =>
                $"Game {storeId} doesn't use Partner Center's modular publishing, which reading its Store listing needs. "
                + "Read the listing in Partner Center instead.",
            (HttpStatusCode.BadGateway, _) when storeId is not null =>
                "The Store listing couldn't be read. Partner Center fails this way for a language code it doesn't support. "
                + "Otherwise, try again later.",
            _ => null,
        };

    /// <summary>Names on stderr the listing's text that isn't set, so an incomplete listing stands out.</summary>
    private static void WriteUnsetText(string json, string language)
    {
        using var document = JsonDocument.Parse(json);
        JsonElement listing = document.RootElement;
        var unset = new List<string>();
        foreach (string field in new[] { "title", "description" })
        {
            if (XboxPartnerCommandSupport.ReadString(listing, field).Length == 0)
            {
                unset.Add(field);
            }
        }

        if (!listing.TryGetProperty("features", out JsonElement features)
            || features.ValueKind != JsonValueKind.Array
            || features.GetArrayLength() == 0)
        {
            unset.Add("features");
        }

        if (unset.Count > 0)
        {
            Console.Error.WriteLine($"Not set in the {language} listing: {string.Join(", ", unset)}.");
        }
    }
}
