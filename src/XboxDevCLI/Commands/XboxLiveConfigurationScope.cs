// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System.CommandLine;
using System.CommandLine.Parsing;
using XboxDevCLI.Output;
using XboxDevCLI.Services;

namespace XboxDevCLI.Commands;

/// <summary>Which XBOX services configuration an <c>xbl config</c> command works on.</summary>
internal enum XboxLiveScope
{
    /// <summary>One sandbox's configuration: its draft and what's live in it.</summary>
    Sandbox,

    /// <summary>The title-wide configuration every sandbox uses: drafted in ALL, live in HIDDEN.</summary>
    Title,

    /// <summary>The seller account's configuration, which needs no title or sandbox.</summary>
    Account,
}

/// <summary>
/// The <c>--scope</c>, <c>--scid</c>, <c>--title-id</c>, and <c>--sandbox-id</c> options every
/// <c>xbl config</c> command shares, their rules, and how a scope maps to where XBOX services keep it.
/// The title-wide configuration's draft is stored in the <c>ALL</c> sandbox and goes live through a
/// publish to <c>HIDDEN</c>. <c>--scope title</c> picks the right one for each command, and
/// <c>--sandbox-id ALL</c> or <c>HIDDEN</c> names one directly.
/// </summary>
internal sealed class XboxLiveConfigurationScope
{
    /// <summary>The sandbox that holds the title-wide draft.</summary>
    public const string TitleDraftSandbox = "ALL";

    /// <summary>The sandbox a title-wide publish goes live in.</summary>
    public const string TitleLiveSandbox = "HIDDEN";

    private static readonly string[] Names = ["sandbox", "title", "account"];

    private readonly Option<string> _scope;
    private readonly string[] _names;

    /// <param name="sandboxDescription">What <c>--sandbox-id</c> is, for Help.</param>
    /// <param name="account">Whether the command takes <c>--scope account</c>.</param>
    public XboxLiveConfigurationScope(string sandboxDescription, bool account)
    {
        string[] names = _names = account ? Names : ["sandbox", "title"];
        _scope = new Option<string>("--scope")
        {
            Description =
                "Configuration to use: 'sandbox', one sandbox's; 'title', the title-wide configuration " +
                "every sandbox uses, drafted in ALL and live in HIDDEN" +
                (account ? "; or 'account', the seller account's. Default: sandbox." : ". Default: sandbox."),
            DefaultValueFactory = _ => "sandbox",
        };
        _scope.Validators.Add(result => ValidateName(result, names));
        _scope.CompletionSources.Add(names);
        Target = new XboxLiveConfigurationTarget(result => GetScope(result) != XboxLiveScope.Account);
        SandboxOption = new Option<string?>("--sandbox-id")
        {
            HelpName = "id",
            Description = sandboxDescription + " Required with --scope sandbox, and not allowed otherwise.",
        }.AsContextValue(ContextEndpoint.XboxPartner, result => GetScope(result) == XboxLiveScope.Sandbox);
        SandboxOption.Validators.Add(ValidateSandbox);
    }

    /// <summary>The title, by <c>--scid</c> or <c>--title-id</c>, for the sandbox and title scopes.</summary>
    public XboxLiveConfigurationTarget Target { get; }

    /// <summary>The sandbox for the sandbox scope. A context value only in that scope.</summary>
    public Option<string?> SandboxOption { get; }

    /// <summary>
    /// The sandbox a validator sees: its token, because reading the value throws when
    /// <c>--sandbox-id</c> has none yet, as when it ends a line being typed or completed.
    /// </summary>
    public string? GetSandboxToken(SymbolResult result) =>
        result.GetResult(SandboxOption)?.Tokens.FirstOrDefault()?.Value;

    /// <summary>
    /// <c>config list</c>'s <c>--scope</c>, which filters the rows: no default, which lists every row
    /// including the seller account's, and no account scope.
    /// </summary>
    public static Option<string?> CreateFilterOption()
    {
        var option = new Option<string?>("--scope")
        {
            Description =
                "Show only 'sandbox' rows, one per sandbox, or the 'title' row, the title-wide configuration. " +
                "Default: every row, including the seller account's.",
        };
        option.Validators.Add(result => ValidateName(result, ["sandbox", "title"]));
        option.CompletionSources.Add("sandbox", "title");
        return option;
    }

    /// <summary>The scope a filter option selects, or <c>null</c> for every row.</summary>
    public static XboxLiveScope? GetFilter(ParseResult result, Option<string?> filter) =>
        result.GetValue(filter) is { } value ? Parse(value) : null;

    /// <summary>Whether <paramref name="sandbox"/> is where XBOX services keep the title-wide configuration.</summary>
    public static bool IsTitleWide(string? sandbox) =>
        string.Equals(sandbox, TitleDraftSandbox, StringComparison.OrdinalIgnoreCase) ||
        string.Equals(sandbox, TitleLiveSandbox, StringComparison.OrdinalIgnoreCase);

    /// <summary>The <c>--scope</c> value that names <paramref name="scope"/>.</summary>
    public static string Name(XboxLiveScope scope) => Names[(int)scope];

    /// <summary>
    /// The scope a document request works on, from its document type and sandbox: the title scope for the
    /// title-wide draft (<c>ALL</c>), and for <c>HIDDEN</c> when reading what's live there. <c>HIDDEN</c>'s
    /// own draft is a sandbox like any other.
    /// </summary>
    public static XboxLiveScope ScopeOf(string documentType, string? sandbox, bool published = false) =>
        string.Equals(documentType.Trim(), "account", StringComparison.OrdinalIgnoreCase) ? XboxLiveScope.Account
            : string.Equals(sandbox?.Trim(), TitleDraftSandbox, StringComparison.OrdinalIgnoreCase) ||
                (published && string.Equals(sandbox?.Trim(), TitleLiveSandbox, StringComparison.OrdinalIgnoreCase))
                ? XboxLiveScope.Title
            : XboxLiveScope.Sandbox;

    /// <summary>
    /// The file <c>config download</c> writes the ETag to, and <c>config save</c> reads it from, next to
    /// the documents: <c>Account.etag</c>, <c>Title.etag</c>, or the sandbox's name.
    /// </summary>
    public static string EtagFileName(XboxLiveScope scope, string? sandbox) => scope switch
    {
        XboxLiveScope.Account => "Account.etag",
        XboxLiveScope.Title => "Title.etag",
        _ => $"{sandbox?.Trim()}.etag",
    };

    public void AddTo(Command command)
    {
        command.Options.Add(_scope);
        Target.AddTo(command);
        command.Options.Add(SandboxOption);
        command.Validators.Add(result =>
        {
            XboxLiveScope scope = GetScope(result);
            if (scope == XboxLiveScope.Sandbox)
            {
                return;
            }

            string[] given =
            [
                .. scope == XboxLiveScope.Account && IsGiven(result, "--scid") ? ["--scid"] : Array.Empty<string>(),
                .. scope == XboxLiveScope.Account && IsGiven(result, "--title-id") ? ["--title-id"] : Array.Empty<string>(),
                .. result.GetResult(SandboxOption) is { Implicit: false } ? ["--sandbox-id"] : Array.Empty<string>(),
            ];
            if (given.Length > 0)
            {
                result.AddError(
                    $"--scope {Name(scope)} uses {Describe(scope)}, so it takes no {Join(given, "or")}. Remove {Join(given, "and")}.");
            }
        });
    }

    /// <summary>
    /// Refuses <paramref name="options"/>, such as a source sandbox or a version, on a title-wide publish:
    /// <c>--scope title</c> or <c>--sandbox-id HIDDEN</c>, which always publish the latest title-wide draft.
    /// </summary>
    public void RejectWithTitleScope(Command command, params Option[] options) =>
        command.Validators.Add(result =>
        {
            string? sandbox = GetSandboxToken(result)?.Trim();
            string? named = GetScope(result) switch
            {
                XboxLiveScope.Title => "--scope title",
                XboxLiveScope.Sandbox when string.Equals(sandbox, TitleLiveSandbox, StringComparison.OrdinalIgnoreCase) =>
                    $"--sandbox-id {sandbox}",
                _ => null,
            };
            if (named is null)
            {
                return;
            }

            string[] given = [.. options.Where(option => result.GetResult(option) is { Implicit: false }).Select(option => option.Name)];
            if (given.Length > 0)
            {
                result.AddError(
                    $"{named} always uses the latest title-wide draft, so it takes no source or version. " +
                    $"Remove {Join(given, "and")}.");
            }
        });

    /// <summary>Refuses <paramref name="options"/>, such as a source sandbox, with <c>--scope account</c>.</summary>
    public void RejectWithAccountScope(Command command, params Option[] options) =>
        command.Validators.Add(result =>
        {
            if (GetScope(result) != XboxLiveScope.Account)
            {
                return;
            }

            string[] given = [.. options.Where(option => result.GetResult(option) is { Implicit: false }).Select(option => option.Name)];
            if (given.Length > 0)
            {
                result.AddError(
                    $"--scope account uses {Describe(XboxLiveScope.Account)}, so it takes no {Join(given, "or")}. " +
                    $"Remove {Join(given, "and")}.");
            }
        });

    public XboxLiveScope GetScope(ParseResult result) => Parse(result.GetResult(_scope), _names);

    public XboxLiveScope GetScope(CommandResult result) => Parse(result.GetResult(_scope), _names);

    /// <summary>
    /// The scope, title, and sandbox the command works on. Fails, saying how to set it, when the sandbox
    /// scope has no sandbox or the sandbox and title scopes have no title.
    /// </summary>
    public async Task<Selection> SelectAsync(ParseResult result, long? sellerId)
    {
        XboxLiveScope scope = GetScope(result);
        if (scope == XboxLiveScope.Account)
        {
            return new Selection(scope, sellerId, null, null, null);
        }

        XboxLiveConfigurationTarget.SelectedTarget target = await Target.SelectAsync(result, sellerId);
        string? sandbox = null;
        if (scope == XboxLiveScope.Sandbox)
        {
            sandbox = result.GetValue(SandboxOption)?.Trim();
            if (string.IsNullOrEmpty(sandbox))
            {
                throw new InvalidOperationException(await ContextResolver.GetMissingMessageAsync(result, SandboxOption));
            }
        }

        return new Selection(scope, target.SellerId, target.Scid, target.TitleId, sandbox);
    }

    /// <summary>
    /// Refuses a sandbox ID that's empty or holds whitespace or a control character. Commands print the
    /// sandbox ID, so a control character would reach the terminal.
    /// </summary>
    public static void ValidateSandbox(OptionResult result)
    {
        if (result.Tokens.Count == 0)
        {
            return;
        }

        string value = result.Tokens[0].Value.Trim();
        if (value.Length == 0
            || value.Any(character => char.IsWhiteSpace(character) || TableFormatter.IsUnsafeCellCharacter(character)))
        {
            result.AddError($"{result.Option.Name} must be non-empty and cannot contain whitespace or control characters.");
        }
    }

    // Reads the token rather than the value, so a value that isn't a scope reads as the default; the
    // option's validator reports it.
    private static XboxLiveScope Parse(OptionResult? result, string[] names) =>
        Parse(result?.Tokens.Count > 0 && names.Contains(result.Tokens[0].Value.Trim(), StringComparer.OrdinalIgnoreCase)
            ? result.Tokens[0].Value
            : null);

    private static XboxLiveScope Parse(string? value) => value?.Trim().ToLowerInvariant() switch
    {
        "title" => XboxLiveScope.Title,
        "account" => XboxLiveScope.Account,
        _ => XboxLiveScope.Sandbox,
    };

    private static void ValidateName(OptionResult result, string[] names)
    {
        if (result.Tokens.Count > 0 && !names.Contains(result.Tokens[0].Value.Trim(), StringComparer.OrdinalIgnoreCase))
        {
            result.AddError(
                $"Invalid value '{result.Tokens[0].Value}' for option '{result.Option.Name}'. " +
                $"Allowed values: {string.Join(", ", names)}.");
        }
    }

    private static string Describe(XboxLiveScope scope) => scope switch
    {
        XboxLiveScope.Title => "the title-wide configuration",
        XboxLiveScope.Account => "the seller account's configuration",
        _ => "a sandbox's configuration",
    };

    private static bool IsGiven(CommandResult result, string name) =>
        result.GetResult(name) is OptionResult { Implicit: false };

    private static string Join(IReadOnlyList<string> names, string conjunction) =>
        names.Count switch
        {
            1 => names[0],
            2 => $"{names[0]} {conjunction} {names[1]}",
            _ => string.Join(", ", names.Take(names.Count - 1)) + $", {conjunction} {names[^1]}",
        };

    /// <summary>
    /// What a command works on: the scope, the seller, the title for the sandbox and title scopes, and
    /// the sandbox for the sandbox scope.
    /// </summary>
    internal sealed record Selection(XboxLiveScope Scope, long? SellerId, Guid? Scid, string? TitleId, string? Sandbox)
    {
        /// <summary>Where the draft is kept: the sandbox, <c>ALL</c>, or nothing for the account.</summary>
        public string? DraftSandbox => Scope switch
        {
            XboxLiveScope.Sandbox => Sandbox,
            XboxLiveScope.Title => TitleDraftSandbox,
            _ => null,
        };

        /// <summary>Where it goes live: the sandbox, <c>HIDDEN</c>, or nothing for the account.</summary>
        public string? LiveSandbox => Scope switch
        {
            XboxLiveScope.Sandbox => Sandbox,
            XboxLiveScope.Title => TitleLiveSandbox,
            _ => null,
        };

        public string Describe() =>
            new XboxLiveConfigurationTarget.SelectedTarget(SellerId, Scid, TitleId).Describe() +
            Scope switch
            {
                XboxLiveScope.Sandbox => $" in {Sandbox}",
                XboxLiveScope.Title => ", title-wide",
                _ => ", account",
            };
    }
}
