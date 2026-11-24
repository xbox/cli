// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System.CommandLine;
using System.CommandLine.Parsing;

namespace XboxDevCLI.CommandLine;

/// <summary>
/// Reads a flag before the parser has reported its errors: in a command's validators, and in
/// <c>Program.Main</c> before it reports parse errors.
/// </summary>
internal static class FlagValues
{
    /// <summary>
    /// <paramref name="flag"/>'s value, or, when it was given more than one value, whether any of
    /// them is <c>true</c>, so <c>--disable-telemetry true --disable-telemetry false</c> still turns
    /// telemetry off. <c>GetValue</c> throws for more than one value, even under Help, and the parser
    /// reports it.
    /// </summary>
    internal static bool GetFlag(this SymbolResult result, Option<bool> flag) =>
        result.GetResult(flag) is { Tokens.Count: > 1 } given ? AnyTrue(given) : result.GetValue(flag);

    /// <inheritdoc cref="GetFlag(SymbolResult, Option{bool})"/>
    internal static bool GetFlag(this ParseResult parseResult, Option<bool> flag) =>
        parseResult.GetResult(flag) is { Tokens.Count: > 1 } given ? AnyTrue(given) : parseResult.GetValue(flag);

    private static bool AnyTrue(OptionResult given) =>
        given.Tokens.Any(token => bool.TryParse(token.Value, out var value) && value);
}
