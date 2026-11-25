// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System.Text.Json;

namespace XboxDevCLI.Services;

/// <summary>
/// The feature each XBOX services configuration document configures, so output can say
/// "Set definitions" rather than <c>sets.xml</c>. The names are in <c>Resources/xbl-feature-names.json</c>.
/// </summary>
internal static class XboxLiveFeatureNames
{
    internal const string ResourceName = "XboxDevCLI.Resources.xbl-feature-names.json";

    private static readonly Lazy<Dictionary<string, string>> Names = new(() =>
    {
        using Stream stream = typeof(XboxLiveFeatureNames).Assembly.GetManifestResourceStream(ResourceName)!;
        return new Dictionary<string, string>(
            JsonSerializer.Deserialize<Dictionary<string, string>>(stream)!,
            StringComparer.OrdinalIgnoreCase);
    });

    /// <summary>
    /// A document's type: its name without the extension, up to the first <c>-</c>. So
    /// <c>achievements2017.xml</c> is <c>achievements2017</c>, and <c>localization-en-US.xml</c>
    /// is <c>localization</c>.
    /// </summary>
    public static string TypeOf(string? name)
    {
        string withoutExtension = Path.GetFileNameWithoutExtension(name ?? string.Empty);
        int dash = withoutExtension.IndexOf('-', StringComparison.Ordinal);
        return dash > 0 ? withoutExtension[..dash] : withoutExtension;
    }

    /// <summary>The feature a document configures, or the document's name when its type isn't known.</summary>
    public static string NameOf(string? name) =>
        Names.Value.TryGetValue(TypeOf(name), out string? feature) ? feature : name ?? string.Empty;
}
