// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using XboxDevCLI.Commands;

namespace XboxDevCLI.Services;

internal sealed partial class XboxPartnerClient
{
    /// <summary>Reads the binding of the game with <paramref name="storeId"/>.</summary>
    public Task<XboxLiveBinding> GetXboxLiveBindingAsync(
        long? sellerId,
        string storeId,
        CancellationToken cancellationToken) =>
        SendBindingAsync(HttpMethod.Get, sellerId, storeId, null, null, null, cancellationToken);

    /// <summary>
    /// Binds the game with <paramref name="storeId"/> to the configuration of the game with
    /// <paramref name="primaryTitleId"/>, and returns the binding, whose status stays <c>Running</c>
    /// until the bind finishes.
    /// </summary>
    /// <remarks>
    /// The request is sent once and never retried. <paramref name="onSending"/> is called immediately
    /// before the request is handed to the network.
    /// </remarks>
    public Task<XboxLiveBinding> CreateXboxLiveBindingAsync(
        long? sellerId,
        string storeId,
        string primaryTitleId,
        string correlationId,
        CancellationToken cancellationToken,
        Action? onSending = null) =>
        SendBindingAsync(
            HttpMethod.Put, sellerId, storeId, JsonSerializer.Serialize(new { primaryTitleId }), correlationId,
            onSending, cancellationToken);

    /// <summary>
    /// Unbinds the game with <paramref name="storeId"/>, and returns the binding, whose
    /// <c>primaryTitleId</c> is null once the game is unbound.
    /// </summary>
    /// <remarks>
    /// The request is sent once and never retried. <paramref name="onSending"/> is called immediately
    /// before the request is handed to the network.
    /// </remarks>
    public Task<XboxLiveBinding> DeleteXboxLiveBindingAsync(
        long? sellerId,
        string storeId,
        string correlationId,
        CancellationToken cancellationToken,
        Action? onSending = null) =>
        SendBindingAsync(HttpMethod.Delete, sellerId, storeId, null, correlationId, onSending, cancellationToken);

    // Without a seller, the service uses the developer's only seller.
    private async Task<XboxLiveBinding> SendBindingAsync(
        HttpMethod method,
        long? sellerId,
        string storeId,
        string? body,
        string? correlationId,
        Action? onSending,
        CancellationToken cancellationToken)
    {
        string path = $"xboxpartner/v1/games/{Uri.EscapeDataString(storeId)}/xboxlive/binding"
            + (sellerId is { } seller ? $"?sellerId={seller.ToString(CultureInfo.InvariantCulture)}" : string.Empty);
        var response = await SendAuthorizedAsync(
            BuildRequestUri(GetConfiguredBaseUrl(), path), null, cancellationToken, method, body, correlationId, onSending)
            .ConfigureAwait(false);
        try
        {
            return XboxPartnerCommandSupport.DeserializeResponse<XboxLiveBinding>(response.Body, "binding")
                with { RawResponse = response.Body };
        }
        catch (InvalidDataException exception)
        {
            throw new XboxPartnerResponseException(response.Body, response.CorrelationId, exception);
        }
    }
}

/// <summary>
/// A game's binding, which makes it use another game's XBOX services configuration.
/// </summary>
/// <param name="StoreId">The game's Store ID.</param>
/// <param name="TitleId">The game's own XBOX title ID.</param>
/// <param name="PrimaryTitleId">The XBOX title ID of the game it's bound to, or null when it isn't bound.</param>
/// <param name="BoundStoreIds">The Store IDs of the games bound to it, when it's a primary game.</param>
/// <param name="Status">
/// The last bind or unbind: <c>Running</c>, <c>Success</c>, <c>Failed</c>, or null when the game isn't bound.
/// </param>
/// <param name="StatusMessage">Why the last bind or unbind failed.</param>
internal sealed record XboxLiveBinding(
    [property: JsonPropertyName("storeId")] string? StoreId,
    [property: JsonPropertyName("titleId")] string? TitleId,
    [property: JsonPropertyName("primaryTitleId")] string? PrimaryTitleId,
    [property: JsonPropertyName("boundStoreIds")] IReadOnlyList<string>? BoundStoreIds,
    [property: JsonPropertyName("status")] string? Status,
    [property: JsonPropertyName("statusMessage")] string? StatusMessage)
{
    /// <summary>The response as the service sent it, which output writes unchanged.</summary>
    [JsonIgnore]
    public string RawResponse { get; init; } = string.Empty;
}
