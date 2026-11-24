// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System.CommandLine;
using System.Runtime.CompilerServices;

namespace XboxDevCLI.CommandLine;

/// <summary>
/// What the invocation prepares before a command's action runs. Each level includes the ones
/// before it.
/// </summary>
/// <remarks>
/// Reading the token cache, signing in, and calling a service all prepare the same things: the
/// configuration and the sign-in service for the selected mode, validated. None of them reads the
/// token cache or acquires a token during preparation; the action does that, so they share
/// <see cref="Authentication"/>.
/// </remarks>
internal enum CommandRequirement
{
    /// <summary>Runs offline, without configuration, sign-in, or saved context.</summary>
    None,

    /// <summary>Reads configuration, such as a tool path, but never signs in.</summary>
    Configuration,

    /// <summary>
    /// Reads configuration and the sign-in settings, and may read the token cache, sign in, or
    /// acquire a token from its action.
    /// </summary>
    Authentication,
}

/// <summary>
/// Side table holding the <see cref="CommandRequirement"/> each command declares. A command
/// inherits its nearest ancestor's declaration, and one with none anywhere requires
/// <see cref="CommandRequirement.Authentication"/>, so a new command can't skip validation by
/// omission.
/// </summary>
internal static class CommandRequirements
{
    private static readonly ConditionalWeakTable<Command, StrongBox<CommandRequirement>> Declared = new();

    /// <summary>Declares what <paramref name="command"/> and its subcommands need prepared.</summary>
    internal static TCommand Requires<TCommand>(this TCommand command, CommandRequirement requirement)
        where TCommand : Command
    {
        Declared.AddOrUpdate(command, new StrongBox<CommandRequirement>(requirement));
        return command;
    }

    /// <summary>The requirement declared on <paramref name="command"/> or its nearest ancestor, if any.</summary>
    internal static CommandRequirement? FindDeclared(Command command)
    {
        for (Command? current = command; current is not null; current = current.Parents.OfType<Command>().FirstOrDefault())
        {
            if (Declared.TryGetValue(current, out var requirement))
            {
                return requirement.Value;
            }
        }

        return null;
    }

    /// <summary>What <paramref name="command"/> needs prepared.</summary>
    internal static CommandRequirement For(Command command) =>
        FindDeclared(command) ?? CommandRequirement.Authentication;

    private static readonly ConditionalWeakTable<Command, Conditional> DeclaredWhen = new();

    private sealed record Conditional(Func<ParseResult, bool> When, CommandRequirement Requirement);

    /// <summary>
    /// Declares that <paramref name="command"/> needs <paramref name="requirement"/> when
    /// <paramref name="when"/> holds for the parsed command line, such as when an argument needs a
    /// service to resolve it, and what it inherits otherwise. Unlike <c>Requires</c>, it applies
    /// only to <paramref name="command"/>, not to its subcommands.
    /// </summary>
    internal static TCommand RequiresWhen<TCommand>(
        this TCommand command, Func<ParseResult, bool> when, CommandRequirement requirement)
        where TCommand : Command
    {
        DeclaredWhen.AddOrUpdate(command, new Conditional(when, requirement));
        return command;
    }

    /// <summary>What <paramref name="parseResult"/>'s command needs prepared for its arguments.</summary>
    internal static CommandRequirement For(ParseResult parseResult)
    {
        var command = parseResult.CommandResult.Command;
        return DeclaredWhen.TryGetValue(command, out var conditional) && conditional.When(parseResult)
            ? conditional.Requirement
            : For(command);
    }
}
