// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using Microsoft.Extensions.Configuration;
using XboxDevCLI.Safety;

namespace XboxDevCLI.CommandLine;

/// <summary>
/// An invocation's configuration as loaded: the layered settings, and the warnings about settings
/// a command may use. Loading never writes anything; whoever uses the configuration decides what
/// to do with the warnings.
/// </summary>
internal sealed class LoadedConfiguration
{
    private LoadedConfiguration(IConfiguration configuration, IReadOnlyDictionary<ServiceEndpoints, string> warnings)
    {
        Configuration = configuration;
        Warnings = warnings;
    }

    /// <summary>The layered configuration.</summary>
    internal IConfiguration Configuration { get; }

    /// <summary>
    /// The warning about each service endpoint that's been changed from the one shipped with the
    /// CLI, for a command that sends a token to it to write.
    /// </summary>
    internal IReadOnlyDictionary<ServiceEndpoints, string> Warnings { get; }

    /// <summary>
    /// This configuration with <paramref name="overrides"/> layered on top. The warnings are
    /// kept, so the overrides must not change an endpoint.
    /// </summary>
    internal LoadedConfiguration With(IDictionary<string, string?> overrides) =>
        new(new ConfigurationBuilder().AddConfiguration(Configuration).AddInMemoryCollection(overrides).Build(), Warnings);

    /// <summary>Keeps <paramref name="configuration"/> with the warning for each endpoint it changes.</summary>
    internal static LoadedConfiguration Load(IConfiguration configuration)
    {
        var warnings = new Dictionary<ServiceEndpoints, string>();
        if (EndpointOverrideWarning.GetWarning(configuration[EndpointOverrideWarning.ConfigurationKey]) is { } playFab)
        {
            warnings[ServiceEndpoints.PlayFab] = playFab;
        }

        if (EndpointOverrideWarning.GetStudioWarning(configuration[EndpointOverrideWarning.StudioConfigurationKey]) is { } studio)
        {
            warnings[ServiceEndpoints.PlayFabStudio] = studio;
        }

        // A missing or blank gateway is an error the command reports when it reads the URL, so
        // there's nothing to warn about.
        var gateway = configuration[EndpointOverrideWarning.XboxPartnerConfigurationKey];
        if (!string.IsNullOrWhiteSpace(gateway) && EndpointOverrideWarning.GetXboxPartnerWarning(gateway) is { } xboxPartner)
        {
            warnings[ServiceEndpoints.XboxPartner] = xboxPartner;
        }

        return new LoadedConfiguration(configuration, warnings);
    }
}
