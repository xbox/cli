// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System.CommandLine;
using System.CommandLine.Parsing;
using System.Net;
using System.Text.Json;
using XboxDevCLI.CommandLine;
using XboxDevCLI.Output;
using XboxDevCLI.Services;

namespace XboxDevCLI.Commands;

/// <summary>
/// Builds <c>xbox xbl config list</c>: one row per configuration set a title has (each
/// accessible sandbox, the title, and the seller account) with whether its draft has changes.
/// </summary>
internal static class ListConfigurationsCommandFactory
{
    /// <summary>How many status reads run at once.</summary>
    internal const int MaxConcurrentReads = 4;

    public static Command Create()
    {
        var sellerOption = new XdtsSellerOption();
        var target = new XboxLiveConfigurationTarget();
        var scopeOption = XboxLiveConfigurationScope.CreateFilterOption();

        var command = new Command(
            "list",
            "List a title's configuration sets and whether each draft has changes that aren't published");
        command.WithMetadata(new CommandMetadata(
            Safety: CommandSafety.ReadOnly,
            Scope: "One XBOX title, by SCID or title ID: each accessible sandbox, the title, and the seller account.",
            Output:
                "A JSON array by default, one object per configuration set: its scope, sandbox, " +
                "draft and published versions, last publish, and hasChanges. Table output shows the same " +
                "with one row per set. " +
                ListProgress.HelpOutput,
            Examples:
            [
                "xbox xbl config list --scid aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee",
                "xbox xbl config list --scid aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee --output table",
                "xbox xbl config list --scid aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee --output json-compact",
                "xbox xbl config list --title-id 1234ABCD --scope sandbox --output table",
            ],
            LearnMore:
            [
                "Each row is the set's config status without its documents; run config status for what changed.",
                "A failed request prints no partial results, except that the account's row is left out when access to it is denied (HTTP 403).",
            ],
            Table: XboxPartnerCommandSupport.ConfigurationTableView));
        sellerOption.AddTo(command);
        target.AddTo(command);
        command.Options.Add(scopeOption);
        command.SetAction(async (ParseResult parseResult, CancellationToken cancellationToken) =>
        {
            var output = OutputOptions.From(parseResult);
            var verbose = parseResult.GetValue(Program.VerboseOption);
            XboxLiveConfigurationTarget.SelectedTarget selected = await target.SelectAsync(parseResult, sellerId: null);

            try
            {
                var configuration = Program.GetConfiguration(parseResult);
                using var client = Program.XboxPartnerClientFactory is null
                    ? new XboxPartnerClient(
                        configuration,
                        authenticationService: Program.GetAuthentication(parseResult))
                    : Program.XboxPartnerClientFactory(configuration);
                _ = XboxPartnerCommandSupport.ResolveBaseUrl(configuration);
                long? sellerId = sellerOption.GetSellerId(parseResult);

                Program.WriteVerbose(
                    verbose,
                    () => $"List XBOX services configuration sets for {(selected with { SellerId = sellerId }).Describe()}");
                return await RunAsync(
                    client,
                    sellerId,
                    selected.Scid,
                    output,
                    Console.Out,
                    Console.Error,
                    cancellationToken,
                    selected.TitleId,
                    verbose,
                    XboxLiveConfigurationScope.GetFilter(parseResult, scopeOption)).ConfigureAwait(false);
            }
            catch (Exception ex) when (ConfigStatusCommandFactory.IsReadFailure(ex, cancellationToken))
            {
                return ConfigStatusCommandFactory.WriteFailure(ex, Console.Error, output.IsCompact, cancellationToken);
            }
        });

        return command;
    }

    /// <summary>
    /// Lists the configuration sets of the title named by <paramref name="scid"/> or
    /// <paramref name="titleId"/>. A title ID is resolved once, by the sandbox list, and every
    /// status read then uses the SCID and seller the service resolved it to.
    /// </summary>
    internal static async Task<int> RunAsync(
        XboxPartnerClient client,
        long? sellerId,
        Guid? scid,
        OutputOptions output,
        TextWriter stdout,
        TextWriter stderr,
        CancellationToken cancellationToken,
        string? titleId = null,
        bool verbose = false,
        XboxLiveScope? scope = null)
    {
        try
        {
            string response;
            XboxLiveResolvedConfigurationTarget? resolved = null;
            using (var progress = ListProgress.Start("Reading sandboxes", stderr))
            {
                response = await client
                    .GetXboxLiveConfigurationsAsync(
                        sellerId, scid, cancellationToken, titleId, target => resolved = target, progress.PageRead)
                    .ConfigureAwait(false);
            }

            if (verbose && resolved is not null)
            {
                XboxLiveConfigurationTarget.WriteResolved(resolved, sellerId, stderr);
            }

            Guid effectiveScid = resolved?.Scid ?? scid
                ?? throw new InvalidDataException("The configuration list response did not name the SCID it used.");

            // --scope sandbox or title shows only that scope's rows; without it, the account's too.
            IReadOnlyList<XboxLiveConfigurationSummary> sandboxes = scope == XboxLiveScope.Title ? [] : ReadSandboxes(response);
            bool includeTitle = scope != XboxLiveScope.Sandbox;
            bool includeAccount = scope is null;
            var notes = new List<string>();
            IReadOnlyList<XboxLiveConfigurationSetStatus> sets;
            using (RequestProgress.Start(
                FormattableString.Invariant(
                    $"Getting the status of {sandboxes.Count + (includeTitle ? 1 : 0) + (includeAccount ? 1 : 0)} configuration sets"),
                stderr))
            {
                sets = await ReadSetsAsync(
                        client, sellerId ?? resolved?.SellerId, effectiveScid, sandboxes, includeTitle, includeAccount, notes, cancellationToken)
                    .ConfigureAwait(false);
            }

            foreach (string note in notes)
            {
                stderr.WriteLine(note);
            }

            stdout.WriteLine(output.TableView is not null
                ? XboxPartnerTableFormatter.FormatConfigurations(sets)
                : PlayFabApiClient.FormatJson(
                    JsonSerializer.Serialize(sets, ConfigStatusCommandFactory.OutputJson),
                    output.IsCompact));
            return 0;
        }
        catch (Exception ex) when (ConfigStatusCommandFactory.IsReadFailure(ex, cancellationToken))
        {
            return ConfigStatusCommandFactory.WriteFailure(ex, stderr, output.IsCompact, cancellationToken);
        }
    }

    /// <summary>
    /// The accessible sandboxes, in the order listed, without the title's own sandboxes. Each
    /// has its versions and whether it has changes, from the same comparison as its status.
    /// </summary>
    private static IReadOnlyList<XboxLiveConfigurationSummary> ReadSandboxes(string response)
    {
        IReadOnlyList<XboxLiveConfigurationSummary> configurations =
            XboxPartnerCommandSupport.DeserializeResponse<IReadOnlyList<XboxLiveConfigurationSummary>>(
                response,
                "configuration list");
        if (configurations.Any(configuration => configuration is null || string.IsNullOrWhiteSpace(configuration.Sandbox)))
        {
            throw new InvalidDataException("The configuration list response contained an invalid sandbox.");
        }

        return
        [
            .. configurations
                .Select(configuration => configuration with { Sandbox = configuration.Sandbox.Trim() })
                .Where(configuration => !XboxLiveConfigurationScopes.IsTitleSandbox(configuration.Sandbox))
                .DistinctBy(configuration => configuration.Sandbox, StringComparer.Ordinal),
        ];
    }

    /// <summary>
    /// Reads each sandbox's last publish, then the title's and the account's status, a few at a
    /// time. A sandbox's versions and changes come from the list. The account's row is left out,
    /// with a note, when the account's configuration can't be read.
    /// </summary>
    private static async Task<IReadOnlyList<XboxLiveConfigurationSetStatus>> ReadSetsAsync(
        XboxPartnerClient client,
        long? sellerId,
        Guid scid,
        IReadOnlyList<XboxLiveConfigurationSummary> sandboxes,
        bool includeTitle,
        bool includeAccount,
        List<string> notes,
        CancellationToken cancellationToken)
    {
        using var throttle = new SemaphoreSlim(MaxConcurrentReads);

        // The first failure cancels the reads still waiting or running, since nothing is printed.
        using var failed = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);

        async Task<XboxLiveConfigurationSetStatus> ReadAsync(XboxLiveScope scope, XboxLiveConfigurationSummary? sandbox)
        {
            await throttle.WaitAsync(failed.Token).ConfigureAwait(false);
            try
            {
                XboxLiveLastPublish? lastPublish = await ConfigStatusCommandFactory
                    .GetLastPublishAsync(client, sellerId, scope, scid, sandbox?.Sandbox, failed.Token)
                    .ConfigureAwait(false);
                if (sandbox is not null)
                {
                    return new XboxLiveConfigurationSetStatus
                    {
                        Scope = XboxLiveConfigurationScopes.Sandbox,
                        Sandbox = sandbox.Sandbox,
                        Draft = Version(sandbox.WorkingVersion, sandbox.WorkingCommitTime),
                        Published = Version(sandbox.PublishedVersion, sandbox.PublishedCommitTime),
                        LastPublish = lastPublish,
                        HasChanges = sandbox.HasUnpublishedChanges,
                    };
                }

                string response = await client
                    .GetXboxLiveConfigurationStatusAsync(
                        sellerId, scope, scope == XboxLiveScope.Account ? null : scid, null, failed.Token)
                    .ConfigureAwait(false);
                return XboxLiveConfigurationSetStatus.From(
                    ConfigStatusCommandFactory.ParseStatus(response, scid) with { LastPublish = lastPublish });
            }
            catch (Exception ex) when (!(ex is XboxPartnerApiException { StatusCode: HttpStatusCode.Forbidden } && scope == XboxLiveScope.Account))
            {
                failed.Cancel();
                throw;
            }
            finally
            {
                throttle.Release();
            }
        }

        async Task<XboxLiveConfigurationSetStatus?> ReadAccountAsync()
        {
            try
            {
                return await ReadAsync(XboxLiveScope.Account, null).ConfigureAwait(false);
            }
            catch (XboxPartnerApiException ex) when (ex.StatusCode == HttpStatusCode.Forbidden)
            {
                notes.Add("The seller account's configuration isn't listed: access to it was denied (HTTP 403).");
                return null;
            }
        }

        Task<XboxLiveConfigurationSetStatus>[] reads =
        [
            .. sandboxes.Select(sandbox => ReadAsync(XboxLiveScope.Sandbox, sandbox)),
            .. includeTitle ? [ReadAsync(XboxLiveScope.Title, null)] : Array.Empty<Task<XboxLiveConfigurationSetStatus>>(),
        ];
        Task<XboxLiveConfigurationSetStatus?> account = includeAccount
            ? ReadAccountAsync()
            : Task.FromResult<XboxLiveConfigurationSetStatus?>(null);
        Task[] all = [.. reads, account];
        try
        {
            await Task.WhenAll(all).ConfigureAwait(false);
        }
        catch (Exception) when (!cancellationToken.IsCancellationRequested)
        {
            // The failure that cancelled the others is reported, not the cancellations it caused.
            Task first = all.FirstOrDefault(read => read.IsFaulted) ?? all.First(read => read.IsCanceled);
            await first.ConfigureAwait(false);
        }

        return [.. reads.Select(read => read.Result), .. account.Result is { } accountSet ? [accountSet] : Array.Empty<XboxLiveConfigurationSetStatus>()];
    }

    private static XboxLiveConfigurationVersion? Version(ulong? version, DateTimeOffset? time) =>
        version is null ? null : new XboxLiveConfigurationVersion { Version = version, Time = time };
}
