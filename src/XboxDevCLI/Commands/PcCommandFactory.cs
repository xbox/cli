// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System.CommandLine;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using XboxDevCLI.CommandLine;
using XboxDevCLI.Output;
using XboxDevCLI.Presentation;
using XboxDevCLI.Services;
using XboxDevCLI.Telemetry;

namespace XboxDevCLI.Commands;

/// <summary>
/// Builds the <c>pc</c> namespace: settings of the machine the CLI runs on.
/// </summary>
/// <remarks>
/// <para>
/// The PC sandbox is deliberately not under <c>sandbox</c>. Those commands manage sandboxes in the
/// Partner Center account, so <c>sandbox get ABCDEF.1</c> reads an account resource; switching
/// this PC to a sandbox is a different resource with a different scope, platform, and permission.
/// </para>
/// <para>
/// Each <c>pc sandbox</c> command maps to one <c>XblPCSandbox.exe</c> command (<c>/get</c>,
/// <c>/set</c> or <c>/retail</c>, <c>/launchApps</c>) and behaves the same way, so the GDK
/// documentation and troubleshooting steps apply. <c>set</c> also accepts a sandbox number, which
/// it resolves to a sandbox ID before running the tool.
/// </para>
/// </remarks>
internal static class PcCommandFactory
{
    private const string RestartedServices =
        "XBOX services (XblAuthManager, GamingServices, and DiagTrack)";

    internal static TableView SandboxGetTableView { get; } = TableView.Record(
    [
        new TableColumn("SANDBOX", "sandbox"),
    ]);

    internal static TableView SandboxSetTableView { get; } = TableView.Record(
    [
        new TableColumn("SANDBOX", "sandbox"),
        new TableColumn("PREVIOUS SANDBOX", "previousSandbox"),
    ]);

    public static Command Create() => Create(PcSandboxDependencies.Default);

    /// <param name="dependencies">The PC and the tool.</param>
    /// <param name="clientFactory">Creates the client that resolves a sandbox number, for tests.</param>
    internal static Command Create(
        PcSandboxDependencies dependencies,
        Func<IConfiguration, XboxPartnerClient>? clientFactory = null)
    {
        var pc = new Command("pc", "Inspect and change settings on this PC");
        var sandbox = new Command("sandbox", "Get or switch the XBOX sandbox this PC uses");
        sandbox.Subcommands.Add(CreateGetCommand(dependencies));
        sandbox.Subcommands.Add(CreateSetCommand(dependencies, clientFactory));
        sandbox.Subcommands.Add(CreateLaunchAppsCommand(dependencies));
        pc.Subcommands.Add(sandbox);
        return pc.Requires(CommandRequirement.Configuration).Calls(ServiceEndpoints.None);
    }

    private static string ToolLocationNote =>
        $"Runs {PcSandbox.ToolFileName}, the PC Sandbox Switcher from the GDK and the XBOX services " +
        $"development tools, found through {PcSandbox.ToolPathConfigurationKey}, next to the CLI, " +
        "or on PATH.";

    private static Command CreateGetCommand(PcSandboxDependencies dependencies)
    {
        var command = new Command("get", "Get the XBOX sandbox this PC uses");
        command.WithMetadata(new CommandMetadata(
            Safety: CommandSafety.ReadOnly,
            Scope: "This PC. Windows only.",
            Output: "A JSON object with 'sandbox' (RETAIL when no development sandbox is set). " +
                "Table output has a SANDBOX column.",
            Examples:
            [
                "xbox pc sandbox get",
                "xbox pc sandbox get --output table",
            ],
            LearnMore:
            [
                $"Same as '{PcSandbox.ToolFileName} /get'. Asks Gaming Services, the XBOX " +
                    "component installed from the Microsoft Store, which sandbox is in use. Needs " +
                    "no sign-in or elevation.",
                ToolLocationNote,
                $"Troubleshooting: {PcSandbox.TroubleshootingUrl}",
            ],
            Table: SandboxGetTableView));
        command.SetAction((ParseResult parseResult, CancellationToken cancellationToken) =>
            GetAsync(parseResult, dependencies, cancellationToken));
        return command;
    }

    private static Command CreateSetCommand(
        PcSandboxDependencies dependencies,
        Func<IConfiguration, XboxPartnerClient>? clientFactory)
    {
        var sandboxArgument = new Argument<string>("sandbox-id")
        {
            Description = "Sandbox to switch this PC to, such as ABCDEF.1, or RETAIL to switch back. " +
                "A sandbox number, such as 1, selects that sandbox in the seller's account.",
        };
        sandboxArgument.Validators.Add(result =>
        {
            if (result.Tokens.Count > 0
                && XboxSandboxId.IsNumber(result.Tokens[0].Value)
                && !XboxSandboxId.TryNormalizeNumber(result.Tokens[0].Value, out _))
            {
                result.AddError(
                    $"Invalid sandbox number '{result.Tokens[0].Value}'. {XboxSandboxId.NumberFormatDescription}");
            }
        });
        // Reads the token, not the value, because context resolution asks before parse errors are reported.
        bool IsNumber(ParseResult parseResult) =>
            XboxSandboxId.IsNumber(parseResult.GetResult(sandboxArgument)?.Tokens.FirstOrDefault()?.Value);

        // Only a number needs the seller, sign-in, and the service; a sandbox ID goes straight to the tool.
        var seller = XboxPartnerCommandSupport.CreateSellerIdOption()
            .AsContextValue(ContextEndpoint.XboxPartner, IsNumber);
        seller.Description = "Partner Center seller whose sandbox a sandbox number selects. Not used with a sandbox ID.";
        var confirm = new Option<bool>("--confirm", "--yes", "-y")
        {
            Description = "Confirm switching this PC's sandbox without prompting.",
        };
        var noApps = new Option<bool>("--no-apps")
        {
            Description = "Don't reopen the Microsoft Store and XBOX app. Same as the tool's /noApps.",
        };
        var command = new Command("set", "Switch this PC to an XBOX sandbox")
        {
            sandboxArgument,
            seller,
            confirm,
            noApps,
        };
        command.RequiresWhen(IsNumber, CommandRequirement.Authentication)
            .CallsWhen(IsNumber, ServiceEndpoints.XboxPartner);
        command.WithMetadata(new CommandMetadata(
            Safety: CommandSafety.MachineState,
            Scope: "This PC, for every user and game. Windows only.",
            Output: "A JSON object with 'sandbox' and 'previousSandbox'. Progress and " +
                $"{PcSandbox.ToolFileName} output go to stderr. Table output has SANDBOX and " +
                "PREVIOUS SANDBOX columns.",
            SafetyDetails: $"Restarts {RestartedServices}, reopens the Microsoft Store and XBOX " +
                "app if they are open, and shows a UAC prompt. Requires --confirm (--yes, -y) or " +
                "terminal confirmation. Agent approval is still required.",
            Examples:
            [
                "xbox pc sandbox set ABCDEF.1 --confirm",
                "xbox pc sandbox set 1 --seller-id 1234567 --confirm",
                "xbox pc sandbox set RETAIL --confirm",
                "xbox pc sandbox set ABCDEF.1 --confirm --no-apps",
            ],
            LearnMore:
            [
                $"Same as '{PcSandbox.ToolFileName} /set <sandbox-id>', or '/retail' for RETAIL. " +
                    "A sandbox number is resolved to its sandbox ID first.",
                ToolLocationNote,
                "A sandbox number from 0 to 999, without a leading zero, is joined to the seller's " +
                    "account moniker, such as ABCDEF.1 for 1. Only a number needs sign-in and a seller.",
                "Always switches, even to the current sandbox. Rerun it if sign-in uses the old sandbox.",
                "Switch to RETAIL to update the XBOX app or Gaming Services from the Microsoft Store.",
                $"Troubleshooting: {PcSandbox.TroubleshootingUrl}",
            ],
            Table: SandboxSetTableView));
        command.SetAction((ParseResult parseResult, CancellationToken cancellationToken) =>
        {
            var confirmed = parseResult.GetResult(confirm) is { Implicit: false }
                ? parseResult.GetValue(confirm)
                : parseResult.GetValue(Program.ConfirmOption);
            return SetAsync(
                parseResult,
                parseResult.GetValue(sandboxArgument)!,
                confirmed,
                parseResult.GetValue(noApps),
                dependencies,
                number => ResolveNumberAsync(parseResult, number, seller, clientFactory, cancellationToken),
                cancellationToken);
        });
        return command;
    }

    /// <summary>
    /// Joins a sandbox number to the seller's account moniker, as <c>sandbox get</c> does, or
    /// explains why it couldn't and returns <see langword="null"/>. Writes the sandbox ID, since
    /// the command changes the PC.
    /// </summary>
    private static async Task<string?> ResolveNumberAsync(
        ParseResult parseResult,
        string number,
        Option<string?> sellerOption,
        Func<IConfiguration, XboxPartnerClient>? clientFactory,
        CancellationToken cancellationToken)
    {
        string? id = null;
        await SandboxCommandFactory.ExecuteAsync(
            parseResult, sellerOption, baseUrlOption: null, clientFactory,
            async (client, baseUrl, sellerId) =>
            {
                id = await SandboxCommandFactory.ResolveSandboxIdAsync(
                    parseResult, number, forCreate: false, changes: true, client, baseUrl, sellerId, cancellationToken);
                return id is null ? 1 : 0;
            },
            cancellationToken,
            offline: $"Or pass the full sandbox ID, such as ABCDEF.{number}, which needs no sign-in or seller.")
            .ConfigureAwait(false);
        return id;
    }

    private static Command CreateLaunchAppsCommand(PcSandboxDependencies dependencies)
    {
        var command = new Command(
            "launch-apps",
            "Reopen the Microsoft Store and XBOX app in this PC's sandbox");
        command.WithMetadata(new CommandMetadata(
            Safety: CommandSafety.MachineState,
            Scope: "This PC, for the signed-in Windows user. Windows only.",
            Output: "A JSON object with 'sandbox', the sandbox the apps opened in. " +
                $"{PcSandbox.ToolFileName} output goes to stderr. Table output has a SANDBOX column.",
            SafetyDetails: "Closes and reopens the Microsoft Store and XBOX app, which interrupts " +
                "anything running in them. Needs no elevation.",
            Examples:
            [
                "xbox pc sandbox launch-apps",
            ],
            LearnMore:
            [
                $"Same as '{PcSandbox.ToolFileName} /launchApps'. Use it after " +
                    "'xbox pc sandbox set --no-apps'.",
                ToolLocationNote,
            ],
            Table: SandboxGetTableView));
        command.SetAction((ParseResult parseResult, CancellationToken cancellationToken) =>
            LaunchAppsAsync(parseResult, dependencies, cancellationToken));
        return command;
    }

    private static async Task<int> GetAsync(
        ParseResult parseResult,
        PcSandboxDependencies dependencies,
        CancellationToken cancellationToken)
    {
        if (!EnsureWindows(dependencies))
        {
            return 1;
        }

        try
        {
            var tool = LocateTool(parseResult, dependencies);
            var sandbox = await ReadSandboxAsync(parseResult, tool, dependencies, cancellationToken)
                .ConfigureAwait(false);
            if (sandbox is null)
            {
                return 1;
            }

            WriteResult(new { sandbox }, parseResult);
            return 0;
        }
        catch (Exception exception) when (IsLocalFailure(exception))
        {
            var exitCode = XboxPartnerCommandSupport.WriteLocalError(exception);
            TelemetryRecorder.RecordError(TelemetryErrorTypes.LocalTool);
            return exitCode;
        }
    }

    internal static async Task<int> SetAsync(
        ParseResult parseResult,
        string requested,
        bool confirmed,
        bool noApps,
        PcSandboxDependencies dependencies,
        Func<string, Task<string?>> resolveNumber,
        CancellationToken cancellationToken)
    {
        if (!EnsureWindows(dependencies))
        {
            return 1;
        }

        var isNumber = XboxSandboxId.TryNormalizeNumber(requested, out var number);
        var target = requested.Trim().ToUpperInvariant();
        if (!isNumber && !PcSandbox.IsPlausibleSandboxId(target))
        {
            Console.Error.WriteLine(
                $"'{requested}' is not a valid sandbox ID. Use an ID such as ABCDEF.1, or RETAIL.");
            return 1;
        }

        try
        {
            // The tool reads the current sandbox before it switches, so a missing tool or a broken
            // Gaming Services fails here, before a number signs in, the confirmation, and the
            // administrator prompt.
            var tool = LocateTool(parseResult, dependencies);
            var previous = await ReadSandboxAsync(parseResult, tool, dependencies, cancellationToken)
                .ConfigureAwait(false);
            if (previous is null)
            {
                return 1;
            }

            // The resolver reports its own failures, so none reach the local-failure catch below.
            if (isNumber)
            {
                if (await resolveNumber(number).ConfigureAwait(false) is not { } resolved)
                {
                    return 1;
                }

                target = resolved;
            }

            var again = string.Equals(previous, target, StringComparison.OrdinalIgnoreCase);
            if (!Confirm(confirmed, DescribeSwitch(previous, target, again, noApps), dependencies))
            {
                return 1;
            }

            // Once launched, an approved elevated switch cannot be stopped, so honor Ctrl-C pressed
            // during the prompt before starting the tool.
            cancellationToken.ThrowIfCancellationRequested();

            var arguments = PcSandbox.BuildSetArguments(target, noApps);
            Console.Error.WriteLine(
                (again
                    ? $"Switching this PC to {target} again."
                    : $"Switching this PC from {previous} to {target}.") +
                " Approve the administrator prompt if Windows shows one; this usually takes " +
                "15-30 seconds.");

            using var output = new TeeWriter(Console.Error);
            int exitCode;
            try
            {
                exitCode = await RunToolAsync(parseResult, dependencies, tool, arguments, output, cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                Console.Error.WriteLine(
                    "Cancelled. If the administrator prompt was already accepted, the switch may " +
                    "still complete. Run 'xbox pc sandbox get' to check.");
                throw;
            }

            if (exitCode != 0)
            {
                TelemetryRecorder.RecordError(TelemetryErrorTypes.LocalTool);
                Console.Error.WriteLine(PcSandbox.DescribeToolFailure(exitCode, output.Lines));
                return 1;
            }

            // The tool prints the sandbox Gaming Services reports after the switch. Ask again only
            // when it could not, so the result is what the PC uses rather than an echo of the request.
            var actual = PcSandbox.ParseSandbox(output.Lines, PcSandbox.SandboxIsNowPrefix)
                ?? await ReadSandboxAsync(parseResult, tool, dependencies, cancellationToken).ConfigureAwait(false);
            if (actual is null)
            {
                return 1;
            }

            if (!string.Equals(actual, target, StringComparison.OrdinalIgnoreCase))
            {
                TelemetryRecorder.RecordError(TelemetryErrorTypes.LocalTool);
                Console.Error.WriteLine(
                    $"{PcSandbox.ToolFileName} reported success, but this PC uses {actual} rather " +
                    $"than {target}. Run the switch again, then restart the PC if it still fails. " +
                    PcSandbox.HelpHint);
                return 1;
            }

            WriteResult(new { sandbox = actual, previousSandbox = previous }, parseResult);
            return 0;
        }
        catch (Exception exception) when (IsLocalFailure(exception))
        {
            var exitCode = XboxPartnerCommandSupport.WriteLocalError(exception);
            TelemetryRecorder.RecordError(TelemetryErrorTypes.LocalTool);
            return exitCode;
        }
    }

    private static async Task<int> LaunchAppsAsync(
        ParseResult parseResult,
        PcSandboxDependencies dependencies,
        CancellationToken cancellationToken)
    {
        if (!EnsureWindows(dependencies))
        {
            return 1;
        }

        try
        {
            var tool = LocateTool(parseResult, dependencies);
            var sandbox = await ReadSandboxAsync(parseResult, tool, dependencies, cancellationToken)
                .ConfigureAwait(false);
            if (sandbox is null)
            {
                return 1;
            }

            Console.Error.WriteLine($"Reopening the Microsoft Store and XBOX app in {sandbox}.");
            using var output = new TeeWriter(Console.Error);
            var exitCode = await RunToolAsync(
                parseResult,
                dependencies,
                tool,
                PcSandbox.LaunchAppsArguments,
                output,
                cancellationToken).ConfigureAwait(false);
            if (exitCode != 0)
            {
                TelemetryRecorder.RecordError(TelemetryErrorTypes.LocalTool);
                Console.Error.WriteLine(PcSandbox.DescribeToolFailure(exitCode, output.Lines));
                return 1;
            }

            WriteResult(new { sandbox }, parseResult);
            return 0;
        }
        catch (Exception exception) when (IsLocalFailure(exception))
        {
            var exitCode = XboxPartnerCommandSupport.WriteLocalError(exception);
            TelemetryRecorder.RecordError(TelemetryErrorTypes.LocalTool);
            return exitCode;
        }
    }

    private static Task<int> RunToolAsync(
        ParseResult parseResult,
        PcSandboxDependencies dependencies,
        string tool,
        IReadOnlyList<string> arguments,
        TextWriter output,
        CancellationToken cancellationToken)
    {
        Program.WriteVerbose(
            parseResult.GetValue(Program.VerboseOption),
            () => $"Running {tool} {string.Join(' ', arguments)}");
        return dependencies.RunToolAsync(tool, arguments, output, cancellationToken);
    }

    /// <summary>
    /// Runs <c>XblPCSandbox.exe /get</c> and returns the sandbox it reports. On failure, shows the
    /// tool's output and the cause on stderr and returns null.
    /// </summary>
    private static async Task<string?> ReadSandboxAsync(
        ParseResult parseResult,
        string tool,
        PcSandboxDependencies dependencies,
        CancellationToken cancellationToken)
    {
        using var output = new TeeWriter(inner: null);
        var exitCode = await RunToolAsync(
            parseResult,
            dependencies,
            tool,
            PcSandbox.GetArguments,
            output,
            cancellationToken).ConfigureAwait(false);
        var sandbox = exitCode == 0
            ? PcSandbox.ParseSandbox(output.Lines, PcSandbox.CurrentSandboxPrefix)
            : null;
        if (sandbox is not null)
        {
            return sandbox;
        }

        // Quiet on success, but the tool's own explanation is the most useful part of a failure.
        foreach (var line in output.Lines)
        {
            Console.Error.WriteLine(line);
        }

        TelemetryRecorder.RecordError(TelemetryErrorTypes.LocalTool);
        Console.Error.WriteLine(exitCode != 0
            ? PcSandbox.DescribeToolFailure(exitCode, output.Lines)
            : $"{PcSandbox.ToolFileName} /get did not report a sandbox. {PcSandbox.HelpHintAfter(output.Lines)}");
        return null;
    }

    private static string LocateTool(ParseResult parseResult, PcSandboxDependencies dependencies) =>
        PcSandbox.LocateTool(
            Program.GetConfiguration(parseResult)[PcSandbox.ToolPathConfigurationKey],
            dependencies);

    private static bool IsLocalFailure(Exception exception) =>
        exception is FileNotFoundException or UnauthorizedAccessException
            or System.Security.SecurityException or IOException or InvalidOperationException
            or System.ComponentModel.Win32Exception;

    private static bool EnsureWindows(PcSandboxDependencies dependencies)
    {
        if (dependencies.IsWindows())
        {
            return true;
        }

        TelemetryRecorder.RecordError(TelemetryErrorTypes.Usage);
        Console.Error.WriteLine(
            "The PC sandbox can only be read or changed on Windows.");
        return false;
    }

    private static string DescribeSwitch(string previous, string target, bool again, bool noApps)
    {
        var description = again
            ? $"This PC already uses {target}. Switching again restarts {RestartedServices}, " +
                "which can fix sign-in that still uses the previous sandbox."
            : $"Switching this PC from {previous} to {target} restarts {RestartedServices}.";
        description += noApps
            ? " It affects every user and game on this PC."
            : " It reopens the Microsoft Store and XBOX app if they are open, and affects every " +
                "user and game on this PC.";
        return description;
    }

    private static bool Confirm(
        bool confirmed,
        string description,
        PcSandboxDependencies dependencies)
    {
        if (confirmed)
        {
            return true;
        }

        Console.Error.WriteLine(description);
        if (!dependencies.IsInteractive())
        {
            Console.Error.WriteLine(
                "Re-run with --confirm (--yes, -y) to proceed without an interactive prompt.");
            TelemetryRecorder.RecordError(TelemetryErrorTypes.NotConfirmed);
            return false;
        }

        ConfirmationPrompt.Write(Console.Error, "Switch the sandbox? [y/N]: ");
        var answer = dependencies.ReadLine()?.Trim();
        if (string.Equals(answer, "yes", StringComparison.OrdinalIgnoreCase)
            || string.Equals(answer, "y", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        Console.Error.WriteLine("Sandbox switch canceled.");
        TelemetryRecorder.RecordError(TelemetryErrorTypes.NotConfirmed);
        return false;
    }

    private static void WriteResult(object result, ParseResult parseResult)
    {
        var json = JsonSerializer.Serialize(result);
        var output = OutputOptions.From(parseResult);
        if (output.TableView is { } view)
        {
            using var document = JsonDocument.Parse(json);
            Console.WriteLine(TableFormatter.Format(document.RootElement, view));
            return;
        }

        Console.WriteLine(PlayFabApiClient.FormatJson(json, output.IsCompact));
    }

    /// <summary>
    /// Records each line the tool prints and, when <c>inner</c> is set, forwards it so the user
    /// sees progress as it happens.
    /// </summary>
    private sealed class TeeWriter(TextWriter? inner) : TextWriter
    {
        private readonly List<string> lines = [];

        public IReadOnlyList<string> Lines => lines;

        public override Encoding Encoding => Encoding.UTF8;

        public override void WriteLine(string? value)
        {
            lines.Add(value ?? string.Empty);
            inner?.WriteLine(value);
        }

        public override void Write(char value) => inner?.Write(value);
    }
}
