// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System.CommandLine;
using System.CommandLine.Parsing;
using System.Globalization;
using System.Text.Json;
using XboxDevCLI.CommandLine;
using XboxDevCLI.Output;
using XboxDevCLI.Services;

namespace XboxDevCLI.Commands;

/// <summary>
/// Builds <c>xbox xbl config diff</c>: compares the draft, the published version, any version, or
/// local files, like <c>git diff</c>.
/// </summary>
internal static class ConfigDiffCommandFactory
{
    internal static readonly JsonSerializerOptions OutputJson = new();

    /// <summary>
    /// One row per change, or per document when its changes aren't described. The table renders
    /// a projection of the diff (<see cref="DiffTable"/>): its documents with their features,
    /// expanded by their changes.
    /// </summary>
    internal static TableView DiffTableView { get; } = new(
        Name(nameof(DiffTable.Documents)),
        [
            new CommandLine.TableColumn("SCOPE", Name(nameof(DiffTableDocument.Scope)), 7),
            new CommandLine.TableColumn("FEATURE", Name(nameof(DiffTableDocument.Feature)), 32),
            new CommandLine.TableColumn("DOCUMENT", Name(nameof(DiffTableDocument.Name)), 32),
            new CommandLine.TableColumn("STATE", Name(nameof(DiffTableDocument.State)), 7),
            new CommandLine.TableColumn(
                "CHANGE",
                FieldPath(nameof(DiffTableDocument.Changes), nameof(XboxLiveDocumentChange.Description)),
                60),
        ])
    {
        Expand = Name(nameof(DiffTableDocument.Changes)),
    };

    /// <summary>The documents that differ, flattened out of their features, as the table shows them.</summary>
    internal sealed record DiffTable(IReadOnlyList<DiffTableDocument> Documents);

    internal sealed record DiffTableDocument(
        string Scope,
        string Feature,
        string Name,
        string State,
        IReadOnlyList<XboxLiveDocumentChange> Changes);

    public static Command Create()
    {
        var sellerOption = new XdtsSellerOption();
        var oldArgument = VersionArgument(
            "old-version",
            "The old side, whose lines are marked '-'. Default: the layer below the new side: published under the draft, and the draft under --file files.");
        var newArgument = VersionArgument(
            "new-version",
            "The new side, whose lines are marked '+'. Default: the draft, or the --file files.");
        var scope = new XboxLiveConfigurationScope(
            "The case-sensitive sandbox ID: the sandbox whose draft, published version, and numbered " +
            "versions are compared. ALL and HIDDEN are the same as --scope title.",
            account: true);
        var filesOption = new Option<string[]>("--file")
        {
            Description =
                "A local file or filename glob pattern, as for config save. Repeat --file to select " +
                "more. The files are the new side, and only their documents are compared.",
            Arity = ArgumentArity.OneOrMore,
            AllowMultipleArgumentsPerToken = false,
        };
        filesOption.Validators.Add(result =>
        {
            if (result.Tokens.Count != result.IdentifierTokenCount ||
                result.Tokens.Any(token => string.IsNullOrWhiteSpace(token.Value)))
            {
                result.AddError("Each --file requires one non-empty path or glob pattern.");
            }
        });
        var featureOption = new Option<string[]>("--feature")
        {
            HelpName = "name",
            Description =
                "Compare only this feature: its name as the output shows it, a document name such as " +
                "achievements2017.xml, or a document type such as achievements2017. Repeat for more.",
            Arity = ArgumentArity.OneOrMore,
            AllowMultipleArgumentsPerToken = false,
        };
        var linesOption = new Option<bool>("--lines")
        {
            Description = "Add each differing document's line diff. Default: false.",
        };

        var command = new Command(
            "diff",
            "Compare the draft, the published version, any version, or local files");
        command.WithMetadata(new CommandMetadata(
            Safety: CommandSafety.ReadOnly,
            Scope: "One XBOX title's sandbox or title configuration, or the seller account's configuration.",
            Output:
                "One JSON object by default: the old and new sides, and each compared document by " +
                "feature, with its state (added, removed, changed, or same) and both hashes, and the documents " +
                "left out under notCompared. --lines " +
                "adds each differing document's unified line diff. Table output shows one row per " +
                "difference, writes the two sides to stderr, and with --lines writes the line diffs " +
                "after the table. Exit code 0 whenever the comparison runs.",
            Examples:
            [
                "xbox xbl config diff --scid aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee --sandbox-id XDKS.1",
                "xbox xbl config diff 52 --scid aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee --sandbox-id XDKS.1 --output table",
                "xbox xbl config diff 52 55 --scid aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee --sandbox-id XDKS.1 --feature achievements2017 --lines",
                "xbox xbl config diff --scid aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee --sandbox-id XDKS.1 --file \"nod-arena/*.xml\"",
            ],
            LearnMore:
            [
                "With no version, the draft is compared with the published version: what publishing would change.",
                "config status shows the draft and published version numbers. Both sides are versions of one sandbox: for another sandbox's versions, run config status and config diff with its --sandbox-id.",
                "Descriptions of each change, such as '1 achievements added', come with a sandbox's draft compared with its own published version.",
            ],
            Table: DiffTableView));
        command.Arguments.Add(oldArgument);
        command.Arguments.Add(newArgument);
        sellerOption.AddTo(command);
        scope.AddTo(command);
        command.Options.Add(filesOption);
        command.Options.Add(featureOption);
        command.Options.Add(linesOption);
        command.Validators.Add(result =>
        {
            if (result.GetResult(newArgument) is { Tokens.Count: > 0 } && result.GetResult(filesOption) is { Tokens.Count: > 0 })
            {
                result.AddError(
                    "--file can't be used with two versions: the files are the new side. " +
                    "Name one version, the old side, or none to compare with the draft.");
            }
        });
        command.SetAction(async (ParseResult parseResult, CancellationToken cancellationToken) =>
        {
            var output = OutputOptions.From(parseResult);
            var verbose = parseResult.GetValue(Program.VerboseOption);
            XboxLiveConfigurationScope.Selection selected = await scope.SelectAsync(parseResult, sellerOption.GetSellerId(parseResult));

            ulong? oldVersion = ParseVersion(parseResult.GetValue(oldArgument));
            ulong? newVersion = ParseVersion(parseResult.GetValue(newArgument));
            XboxLiveScope selectedScope = selected.Scope;
            string? sandbox = selected.Sandbox;
            if (selectedScope == XboxLiveScope.Sandbox)
            {
                if (XboxLiveConfigurationScope.IsTitleWide(sandbox))
                {
                    // As in config status: the title draft is kept in ALL and goes live through HIDDEN.
                    selectedScope = XboxLiveScope.Title;
                    sandbox = null;
                }
                else if (XboxLiveConfigurationScopes.IsTitleSandbox(sandbox!))
                {
                    string title = selected.Scid is { } givenScid ? $"--scid {givenScid:D}" : $"--title-id {selected.TitleId}";
                    Console.Error.WriteLine(
                        $"{sandbox} holds the title's configuration, not one sandbox's. Run " +
                        $"'{CliIdentity.CommandName} xbl config diff --scope title {title}'.");
                    return 1;
                }
            }

            string[] patterns = parseResult.GetValue(filesOption) ?? [];
            IReadOnlyList<string> paths = patterns.Length == 0 ? [] : CommitDocumentsCommandFactory.ResolveFiles(patterns);
            IReadOnlyList<XboxDocumentUpload> files = paths.Count == 0
                ? []
                : await CommitDocumentsCommandFactory.ReadDocumentsAsync(paths, cancellationToken).ConfigureAwait(false);
            var request = new ConfigDiffRequest(
                selectedScope,
                selected.Scid,
                sandbox,
                oldVersion,
                newVersion,
                paths,
                files,
                new XboxLiveComparisonOptions
                {
                    Features = parseResult.GetValue(featureOption) ?? [],
                    Lines = parseResult.GetValue(linesOption),
                })
            {
                TitleId = selected.Scid is null ? selected.TitleId : null,
            };

            var configuration = Program.GetConfiguration(parseResult);
            using var client = Program.XboxPartnerClientFactory is null
                ? new XboxPartnerClient(configuration, authenticationService: Program.GetAuthentication(parseResult))
                : Program.XboxPartnerClientFactory(configuration);
            _ = XboxPartnerCommandSupport.ResolveBaseUrl(configuration);
            Program.WriteVerbose(verbose, () => Describe(request));
            return await RunAsync(client, selected.SellerId, request, output, Console.Out, Console.Error, cancellationToken)
                .ConfigureAwait(false);
        });

        return command;
    }

    /// <summary>What to compare: the scope, and each side, by version number or local files.</summary>
    internal sealed record ConfigDiffRequest(
        XboxLiveScope Scope,
        Guid? Scid,
        string? Sandbox,
        ulong? OldVersion,
        ulong? NewVersion,
        IReadOnlyList<string> Paths,
        IReadOnlyList<XboxDocumentUpload> Files,
        XboxLiveComparisonOptions Options)
    {
        /// <summary>The title by its XBOX title ID, when <c>--title-id</c> named it instead of <c>--scid</c>.</summary>
        public string? TitleId { get; init; }
    }

    internal static async Task<int> RunAsync(
        XboxPartnerClient client,
        long? sellerId,
        ConfigDiffRequest request,
        OutputOptions output,
        TextWriter stdout,
        TextWriter stderr,
        CancellationToken cancellationToken)
    {
        XboxLiveComparisonResult result;
        try
        {
            using (RequestProgress.Start(Describe(request), stderr))
            {
                result = await CompareAsync(client, sellerId, request, cancellationToken).ConfigureAwait(false);
            }
        }
        catch (XboxLiveComparisonException ex)
        {
            XboxPartnerCommandSupport.WriteCorrelationId(ex.CorrelationId, stderr);
            stderr.WriteLine(ex.Message);
            return 1;
        }
        catch (Exception ex) when (ConfigStatusCommandFactory.IsReadFailure(ex, cancellationToken))
        {
            return ConfigStatusCommandFactory.WriteFailure(ex, stderr, output.IsCompact, cancellationToken);
        }

        foreach (string note in result.Notes)
        {
            stderr.WriteLine(TerminalText.SingleLine(note));
        }

        if (output.TableView is { } tableView)
        {
            (string table, IReadOnlyList<string> notes) = FormatTable(result.Diff, tableView);
            foreach (string note in notes)
            {
                stderr.WriteLine(note);
            }

            stdout.WriteLine(table);
            foreach (XboxLiveDocumentDiff document in result.Diff.Features.SelectMany(feature => feature.Documents))
            {
                if (document.Lines is { } lines)
                {
                    stdout.WriteLine();
                    foreach (string line in lines)
                    {
                        // A document's text reaches a terminal here, so it can't carry escape sequences.
                        stdout.WriteLine(TerminalText.MultiLine(line));
                    }
                }
            }
        }
        else
        {
            stdout.WriteLine(PlayFabApiClient.FormatJson(JsonSerializer.Serialize(result.Diff, OutputJson), output.IsCompact));
        }

        return 0;
    }

    /// <summary>
    /// Resolves each side as git does: NEW defaults to the draft, or the local files; OLD
    /// defaults to the layer below NEW, published under the draft and the draft under the files.
    /// </summary>
    internal static async Task<XboxLiveComparisonResult> CompareAsync(
        XboxPartnerClient client,
        long? sellerId,
        ConfigDiffRequest request,
        CancellationToken cancellationToken)
    {
        var comparer = new XboxLiveConfigurationComparer(client, sellerId, request.Scope, request.Scid, request.TitleId);
        bool hasFiles = request.Files.Count > 0;
        if (request.OldVersion is null && !hasFiles)
        {
            return await comparer.CompareDraftWithLiveAsync(request.Sandbox, request.Options, cancellationToken).ConfigureAwait(false);
        }

        XboxLiveConfigurationSnapshot old = request.OldVersion is { } oldVersion
            ? await comparer.GetVersionAsync(oldVersion, request.Sandbox, cancellationToken).ConfigureAwait(false)
            : await comparer.GetDraftAsync(request.Sandbox, cancellationToken).ConfigureAwait(false);
        XboxLiveConfigurationSnapshot @new = request.NewVersion is { } newVersion
            ? await comparer.GetVersionAsync(newVersion, request.Sandbox, cancellationToken).ConfigureAwait(false)
            : hasFiles
                ? comparer.FromFiles(request.Paths, request.Files)
                : await comparer.GetDraftAsync(request.Sandbox, cancellationToken).ConfigureAwait(false);
        return await comparer.CompareAsync(old, @new, request.Options, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// The table for stdout: a row for each change, or for each differing document whose changes
    /// aren't described. And notes for stderr naming the two sides.
    /// </summary>
    internal static (string Table, IReadOnlyList<string> Notes) FormatTable(XboxLiveConfigurationDiff diff, TableView view)
    {
        var rows = new List<DiffTableDocument>();
        int same = 0;
        foreach (XboxLiveFeatureDiff feature in diff.Features)
        {
            foreach (XboxLiveDocumentDiff document in feature.Documents)
            {
                if (document.State == XboxLiveDocumentDifferences.Same)
                {
                    same++;
                    continue;
                }

                IReadOnlyList<XboxLiveDocumentChange> changes = document.Changes ?? [];
                if (string.Equals(document.DescriptionStatus, XboxLiveDescriptionStatuses.Pending, StringComparison.Ordinal))
                {
                    changes = [.. changes, new XboxLiveDocumentChange { Description = "description pending" }];
                }

                rows.Add(new DiffTableDocument(document.Scope, feature.Name, document.Name, document.State, changes));
            }
        }

        int differing = rows.Count;
        var notes = new List<string>
        {
            $"Old: {DescribeSide(diff.Old, diff.Scope)}.",
            $"New: {DescribeSide(diff.New, diff.Scope)}.",
            differing == 0
                ? "No differences."
                : FormattableString.Invariant($"{differing} {(differing == 1 ? "document differs" : "documents differ")}."),
        };
        if (same > 0)
        {
            notes.Add(FormattableString.Invariant($"{same} {(same == 1 ? "document is" : "documents are")} the same."));
        }

        using JsonDocument projection = JsonDocument.Parse(JsonSerializer.Serialize(new DiffTable(rows), TableJson));
        return (TableFormatter.Format(projection.RootElement, view), notes);
    }

    private static readonly JsonSerializerOptions TableJson = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    private static string DescribeSide(XboxLiveDiffSide side, string scope)
    {
        if (side.View == XboxLiveSideViews.Files)
        {
            int count = side.Files?.Count ?? 0;
            return FormattableString.Invariant($"your files ({count} {(count == 1 ? "file" : "files")})");
        }

        if (side.Version is null)
        {
            string set = side.Sandbox ?? scope;
            return side.View == XboxLiveSideViews.Published ? $"{set}: never published" : $"{set}: no draft saved";
        }

        string text = side.Describe(scope);
        string who = XboxPartnerTableFormatter.NormalizeCell(side.UserName ?? side.User ?? string.Empty);
        string when = side.Time?.UtcDateTime.ToString("yyyy-MM-dd HH:mm:ss 'Z'", CultureInfo.InvariantCulture) ?? string.Empty;
        string verb = side.View == XboxLiveSideViews.Published ? "published" : "saved";
        return (when, who) switch
        {
            ("", "") => text,
            (_, "") => $"{text}, {verb} {when}",
            ("", _) => $"{text}, {verb} by {who}",
            _ => $"{text}, {verb} {when} by {who}",
        };
    }

    private static string Describe(ConfigDiffRequest request)
    {
        string target = request.Scope switch
        {
            XboxLiveScope.Sandbox when request.Sandbox is not null => request.Sandbox,
            XboxLiveScope.Sandbox => "sandbox",
            XboxLiveScope.Title => "title",
            _ => "account",
        };
        return $"Comparing the {target} configuration";
    }

    private static Argument<string?> VersionArgument(string name, string description)
    {
        var argument = new Argument<string?>(name)
        {
            Description = description,
            Arity = ArgumentArity.ZeroOrOne,
        };
        argument.Validators.Add(result =>
        {
            if (result.Tokens is [var token, ..] && ParseVersion(token.Value) is null)
            {
                result.AddError(
                    $"'{token.Value}' isn't a version number. Run '{CliIdentity.CommandName} xbl config status' " +
                    "to see the draft and published version numbers.");
            }
        });
        return argument;
    }

    private static ulong? ParseVersion(string? value) =>
        ulong.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out ulong version) && version > 0
            ? version
            : null;

    private static string Name(string property) => JsonNamingPolicy.CamelCase.ConvertName(property);

    private static string FieldPath(params string[] properties) => string.Join('.', properties.Select(Name));
}
