// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System.Text.Json;

namespace XboxDevCLI.Services;

internal sealed partial class XboxPartnerClient
{
    /// <summary>
    /// Reads a game's Store listing text in one language: an object with <c>storeId</c>, <c>language</c>,
    /// <c>title</c>, <c>description</c>, and <c>features</c>. A language the game has no listing in fails with a 404
    /// whose <c>code</c> is <c>store_listing_not_found</c>, and a body that isn't JSON with an
    /// <see cref="XboxPartnerResponseException"/> that keeps the response's correlation ID.
    /// </summary>
    public async Task<string> GetStoreListingAsync(
        string baseUrl,
        string sellerId,
        string storeId,
        string language,
        CancellationToken cancellationToken = default)
    {
        var uri = BuildRequestUri(
            baseUrl,
            $"xboxpartner/v1/games/{Uri.EscapeDataString(storeId)}/store-listings/{Uri.EscapeDataString(language)}");
        return Require(
            await SendAuthorizedAsync(uri, sellerId, cancellationToken, HttpMethod.Get),
            static body => JsonDocument.Parse(body).Dispose());
    }
}
