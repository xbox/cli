// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System.Net.Http.Json;
using System.Text.Json.Serialization;
using XboxDevCLI.Commands;

namespace XboxDevCLI.Services;

internal sealed partial class XboxPartnerClient
{
    private const string WebServicesPath = "xboxpartner/v1/xboxlive/web-services";

    /// <summary>
    /// Reads every page of the seller's web services, ordered by ID, and returns them as a JSON array, as the
    /// service sent them.
    /// </summary>
    public async Task<string> ListWebServicesAsync(
        long? sellerId,
        CancellationToken cancellationToken,
        Action<int>? pageRead = null)
    {
        IReadOnlyList<string> services = await ReadEveryPageAsync(
            "webServices",
            "web service list",
            token => (
                HttpMethod.Post,
                WebServicesPath,
                // The service takes a page size or a continuation token, not both.
                JsonContent.Create(new XboxLiveWebServicesRequest(
                    sellerId, token is null ? ListPageSize : null, token))),
            cancellationToken,
            pageRead: pageRead).ConfigureAwait(false);
        return "[" + string.Join(',', services) + "]";
    }

    /// <summary>Reads one web service.</summary>
    public Task<XboxPartnerOperationResult<XboxLiveWebService>> GetWebServiceAsync(
        long? sellerId,
        Guid webServiceId,
        CancellationToken cancellationToken) =>
        SendWebServiceAsync(
            "/get", new XboxLiveWebServiceRequest(sellerId, webServiceId), Guid.NewGuid().ToString(), null,
            cancellationToken);

    /// <summary>Creates a web service under the account's default business partner.</summary>
    /// <remarks>
    /// The request is sent once and never retried. <paramref name="onSending"/> is called immediately
    /// before the request is handed to the network.
    /// </remarks>
    public Task<XboxPartnerOperationResult<XboxLiveWebService>> CreateWebServiceAsync(
        long? sellerId,
        string name,
        bool telemetryAccess,
        bool appChannelsAccess,
        string correlationId,
        CancellationToken cancellationToken,
        Action? onSending = null) =>
        SendWebServiceAsync(
            "/create",
            new XboxLiveCreateWebServiceRequest(sellerId, name, telemetryAccess, appChannelsAccess),
            correlationId,
            onSending,
            cancellationToken);

    /// <summary>
    /// Changes a web service's name, telemetry access, or app channels access. A null value keeps the
    /// web service's.
    /// </summary>
    /// <remarks>Sent once and never retried, as <see cref="CreateWebServiceAsync"/> is.</remarks>
    public Task<XboxPartnerOperationResult<XboxLiveWebService>> UpdateWebServiceAsync(
        long? sellerId,
        Guid webServiceId,
        string? name,
        bool? telemetryAccess,
        bool? appChannelsAccess,
        string correlationId,
        CancellationToken cancellationToken,
        Action? onSending = null) =>
        SendWebServiceAsync(
            "/update",
            new XboxLiveUpdateWebServiceRequest(sellerId, webServiceId, name, telemetryAccess, appChannelsAccess),
            correlationId,
            onSending,
            cancellationToken);

    /// <summary>Deletes a web service, and returns it as it was.</summary>
    /// <remarks>Sent once and never retried, as <see cref="CreateWebServiceAsync"/> is.</remarks>
    public Task<XboxPartnerOperationResult<XboxLiveWebService>> DeleteWebServiceAsync(
        long? sellerId,
        Guid webServiceId,
        string correlationId,
        CancellationToken cancellationToken,
        Action? onSending = null) =>
        SendWebServiceAsync(
            "/delete", new XboxLiveWebServiceRequest(sellerId, webServiceId), correlationId, onSending,
            cancellationToken);

    /// <summary>
    /// Has XBOX services sign <paramref name="publicKeyBlob"/>, a 2048-bit RSA public key as a 276-byte
    /// <c>PUBLICKEYBLOB</c>, into a certificate for the web service, limited to <paramref name="sandbox"/>
    /// unless it's null.
    /// </summary>
    /// <remarks>Sent once and never retried: each request creates another certificate.</remarks>
    public async Task<XboxLiveWebServiceCertificate> CreateWebServiceCertificateAsync(
        long? sellerId,
        Guid webServiceId,
        byte[] publicKeyBlob,
        string? sandbox,
        string correlationId,
        CancellationToken cancellationToken,
        Action? onSending = null)
    {
        string body = await SendAsync(
            HttpMethod.Post,
            WebServicesPath + "/certificates/create",
            JsonContent.Create(new XboxLiveCreateWebServiceCertificateRequest(
                sellerId, webServiceId, Convert.ToBase64String(publicKeyBlob), sandbox)),
            cancellationToken,
            onSending,
            correlationId).ConfigureAwait(false);
        try
        {
            return XboxPartnerCommandSupport.DeserializeResponse<XboxLiveWebServiceCertificate>(body, "certificate");
        }
        catch (InvalidDataException exception)
        {
            throw new XboxPartnerResponseException(body, correlationId, exception);
        }
    }

    private async Task<XboxPartnerOperationResult<XboxLiveWebService>> SendWebServiceAsync<TRequest>(
        string route,
        TRequest request,
        string correlationId,
        Action? onSending,
        CancellationToken cancellationToken)
    {
        string body = await SendAsync(
            HttpMethod.Post, WebServicesPath + route, JsonContent.Create(request), cancellationToken, onSending,
            correlationId).ConfigureAwait(false);
        try
        {
            return new(XboxPartnerCommandSupport.DeserializeResponse<XboxLiveWebService>(body, "web service"), body);
        }
        catch (InvalidDataException exception)
        {
            throw new XboxPartnerResponseException(body, correlationId, exception);
        }
    }
}

internal sealed record XboxLiveWebServicesRequest(
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] long? SellerId,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] int? PageSize,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? ContinuationToken);

internal sealed record XboxLiveWebServiceRequest(
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] long? SellerId,
    Guid WebServiceId);

internal sealed record XboxLiveCreateWebServiceRequest(
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] long? SellerId,
    string Name,
    bool TelemetryAccess,
    bool AppChannelsAccess);

internal sealed record XboxLiveUpdateWebServiceRequest(
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] long? SellerId,
    Guid WebServiceId,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Name,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] bool? TelemetryAccess,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] bool? AppChannelsAccess);

internal sealed record XboxLiveCreateWebServiceCertificateRequest(
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] long? SellerId,
    Guid WebServiceId,
    string PublicKey,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Sandbox);

/// <summary>A web service: an identity your own service uses to call XBOX services.</summary>
/// <param name="WebServiceId">Its ID, which its certificates carry.</param>
/// <param name="Name">Its name.</param>
/// <param name="BusinessPartnerId">
/// The business partner it acts for. The account's ID means the account's default business partner.
/// </param>
/// <param name="TelemetryAccess">Whether it can read telemetry for the account's games.</param>
/// <param name="AppChannelsAccess">Whether it can publish app channels.</param>
internal sealed record XboxLiveWebService(
    [property: JsonPropertyName("webServiceId")] Guid WebServiceId,
    [property: JsonPropertyName("name")] string? Name,
    [property: JsonPropertyName("businessPartnerId")] Guid BusinessPartnerId,
    [property: JsonPropertyName("telemetryAccess")] bool TelemetryAccess,
    [property: JsonPropertyName("appChannelsAccess")] bool AppChannelsAccess);

/// <summary>A certificate XBOX services issued for a web service.</summary>
/// <param name="WebServiceId">The web service it's for.</param>
/// <param name="Sandbox">The sandbox it's limited to, or null for every sandbox.</param>
/// <param name="Certificate">The certificate, DER encoded, without a private key.</param>
internal sealed record XboxLiveWebServiceCertificate(
    [property: JsonPropertyName("webServiceId")] Guid WebServiceId,
    [property: JsonPropertyName("sandbox")] string? Sandbox,
    [property: JsonPropertyName("certificate")] byte[] Certificate);
