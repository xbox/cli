// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

namespace XboxDevCLI.Services;

/// <summary>
/// The settings built into the CLI, the lowest configuration layer. They're an embedded resource,
/// not a file beside the executable, so the CLI runs the same from a symlink, a package manager's
/// shim, or a lone copy of the executable. <c>XBOX_</c> environment variables and command options
/// change them.
/// </summary>
internal static class BuiltInSettings
{
    internal const string ResourceName = "XboxDevCLI.Resources.defaults.json";

    /// <summary>Opens the built-in settings JSON.</summary>
    /// <exception cref="InvalidOperationException">The resource is missing from the build.</exception>
    internal static Stream Open() =>
        typeof(BuiltInSettings).Assembly.GetManifestResourceStream(ResourceName)
        ?? throw new InvalidOperationException(
            $"The built-in settings resource '{ResourceName}' is missing from this build of the XBOX CLI. Reinstall it.");
}
