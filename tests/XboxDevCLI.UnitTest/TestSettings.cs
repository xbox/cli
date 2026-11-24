// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using Microsoft.Extensions.Configuration;
using XboxDevCLI.Services;

namespace XboxDevCLI.UnitTest;

/// <summary>The CLI's built-in settings, for tests.</summary>
internal static class TestSettings
{
    /// <summary>The built-in settings alone, as the CLI reads them before variables and options.</summary>
    internal static IConfiguration BuiltIn() =>
        new ConfigurationBuilder().AddJsonStream(BuiltInSettings.Open()).Build();

    /// <summary>The built-in value of <paramref name="key"/>.</summary>
    internal static string? BuiltIn(string key) => BuiltIn()[key];
}
