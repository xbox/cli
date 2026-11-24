// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using XboxDevCLI.Commands;

namespace XboxDevCLI.Services;

/// <summary>
/// A seller account's relying parties and business partners. Reads come from the account's draft, so they
/// include changes not yet published, and writes save to it; an account publish makes them live.
/// </summary>
/// <remarks>
/// Each write is sent once and never retried. <c>onSending</c> is called immediately before the request is
/// handed to the network.
/// </remarks>
internal sealed partial class XboxPartnerClient
{
    // A setting that isn't sent is kept, and without a seller, the service uses the developer's only seller.
    private static readonly JsonSerializerOptions AccountRequestOptions = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    /// <summary>Reads the account's relying parties, by name, and returns them as a JSON array, each as the service sent it.</summary>
    public Task<string> ListRelyingPartiesAsync(long? sellerId, CancellationToken cancellationToken) =>
        ListAccountAsync("relyingparties", "relyingParties", "relying party list", sellerId, cancellationToken);

    /// <summary>Reads one relying party. Its name matches ignoring case, with or without <c>rp://</c> and the trailing <c>/</c>.</summary>
    public Task<XboxPartnerOperationResult<XboxLiveRelyingParty>> GetRelyingPartyAsync(
        long? sellerId, string name, CancellationToken cancellationToken) =>
        PostAccountAsync<XboxLiveRelyingParty>(
            "relyingparties/get", new XboxLiveRelyingPartyRequest(sellerId, name), "relying party", cancellationToken);

    public Task<XboxPartnerOperationResult<XboxLiveRelyingParty>> CreateRelyingPartyAsync(
        XboxLiveCreateRelyingPartyRequest request,
        string correlationId,
        CancellationToken cancellationToken,
        Action? onSending = null) =>
        PostAccountAsync<XboxLiveRelyingParty>(
            "relyingparties/create", request, "relying party", cancellationToken, correlationId, onSending);

    public Task<XboxPartnerOperationResult<XboxLiveRelyingParty>> UpdateRelyingPartyAsync(
        XboxLiveUpdateRelyingPartyRequest request,
        string correlationId,
        CancellationToken cancellationToken,
        Action? onSending = null) =>
        PostAccountAsync<XboxLiveRelyingParty>(
            "relyingparties/update", request, "relying party", cancellationToken, correlationId, onSending);

    /// <summary>Reads the account's business partners, the default one first, and returns them as a JSON array, each as the service sent it.</summary>
    public Task<string> ListBusinessPartnersAsync(long? sellerId, CancellationToken cancellationToken) =>
        ListAccountAsync("businesspartners", "businessPartners", "business partner list", sellerId, cancellationToken);

    public Task<XboxPartnerOperationResult<XboxLiveBusinessPartner>> GetBusinessPartnerAsync(
        long? sellerId, Guid businessPartnerId, CancellationToken cancellationToken) =>
        PostAccountAsync<XboxLiveBusinessPartner>(
            "businesspartners/get",
            new XboxLiveBusinessPartnerRequest(sellerId, businessPartnerId),
            "business partner",
            cancellationToken);

    public Task<XboxPartnerOperationResult<XboxLiveBusinessPartner>> CreateBusinessPartnerAsync(
        XboxLiveCreateBusinessPartnerRequest request,
        string correlationId,
        CancellationToken cancellationToken,
        Action? onSending = null) =>
        PostAccountAsync<XboxLiveBusinessPartner>(
            "businesspartners/create", request, "business partner", cancellationToken, correlationId, onSending);

    public Task<XboxPartnerOperationResult<XboxLiveBusinessPartner>> UpdateBusinessPartnerAsync(
        XboxLiveUpdateBusinessPartnerRequest request,
        string correlationId,
        CancellationToken cancellationToken,
        Action? onSending = null) =>
        PostAccountAsync<XboxLiveBusinessPartner>(
            "businesspartners/update", request, "business partner", cancellationToken, correlationId, onSending);

    // The service returns the whole list in one response, without a continuation token, so this reads one page.
    private async Task<string> ListAccountAsync(
        string route, string collection, string description, long? sellerId, CancellationToken cancellationToken) =>
        "[" + string.Join(',', await ReadEveryPageAsync(
            collection,
            description,
            _ => (
                HttpMethod.Post,
                $"xboxpartner/v1/xboxlive/{route}",
                JsonContent.Create(new XboxLiveAccountRequest(sellerId), options: AccountRequestOptions)),
            cancellationToken).ConfigureAwait(false)) + "]";

    private async Task<XboxPartnerOperationResult<T>> PostAccountAsync<T>(
        string route,
        object request,
        string resource,
        CancellationToken cancellationToken,
        string? correlationId = null,
        Action? onSending = null)
    {
        GatewayResponse response = await SendAuthorizedAsync(
            BuildRequestUri(GetConfiguredBaseUrl(), $"xboxpartner/v1/xboxlive/{route}"),
            sellerId: null,
            cancellationToken,
            HttpMethod.Post,
            JsonSerializer.Serialize(request, request.GetType(), AccountRequestOptions),
            correlationId,
            onSending).ConfigureAwait(false);
        try
        {
            return new XboxPartnerOperationResult<T>(
                XboxPartnerCommandSupport.DeserializeResponse<T>(response.Body, resource), response.Body);
        }
        catch (InvalidDataException exception)
        {
            throw new XboxPartnerResponseException(response.Body, response.CorrelationId, exception);
        }
    }
}

internal sealed record XboxLiveAccountRequest(long? SellerId);

internal sealed record XboxLiveRelyingPartyRequest(long? SellerId, string Name);

/// <summary>
/// A new relying party: <c>EncryptionCertificate</c> is the public certificate's DER bytes, in base64, a null
/// <c>BusinessPartnerId</c> is the account's default business partner, and null claims are the ones Partner Center
/// selects for the business partner's type.
/// </summary>
internal sealed record XboxLiveCreateRelyingPartyRequest(
    long? SellerId,
    string Name,
    string EncryptionCertificate,
    Guid? BusinessPartnerId,
    int? LifetimeInHours,
    bool? JkuEnabled,
    IReadOnlyList<string>? Claims);

/// <summary>A change to a relying party's settings: a null setting is kept, and claims replace all of its claims.</summary>
internal sealed record XboxLiveUpdateRelyingPartyRequest(
    long? SellerId,
    string Name,
    string? EncryptionCertificate,
    int? LifetimeInHours,
    bool? JkuEnabled,
    IReadOnlyList<string>? Claims);

internal sealed record XboxLiveBusinessPartnerRequest(long? SellerId, Guid BusinessPartnerId);

/// <summary>A new business partner. A null type is the least privileged type the account's business partners have.</summary>
internal sealed record XboxLiveCreateBusinessPartnerRequest(long? SellerId, string Name, string? Type);

internal sealed record XboxLiveUpdateBusinessPartnerRequest(long? SellerId, Guid BusinessPartnerId, string Name);

/// <summary>A relying party: a token audience, and how XBOX services issue its tokens.</summary>
/// <param name="Name">Its name, such as <c>rp://api.nodpublishers.com/</c>.</param>
/// <param name="FileName">Its document in the account's configuration, <c>relyingparty-{guid}.xml</c>.</param>
/// <param name="BusinessPartnerId">The business partner that owns it.</param>
/// <param name="LifetimeInHours">How long its tokens last.</param>
/// <param name="EncryptionType">How its tokens are encrypted, or null until its settings are saved.</param>
/// <param name="JkuEnabled">Whether its tokens carry a <c>jku</c> header.</param>
/// <param name="Claims">The claim type URIs its tokens carry.</param>
/// <param name="EncryptionCertificate">The certificate its tokens are encrypted to.</param>
internal sealed record XboxLiveRelyingParty(
    [property: JsonPropertyName("name")] string? Name,
    [property: JsonPropertyName("fileName")] string? FileName,
    [property: JsonPropertyName("businessPartnerId")] string? BusinessPartnerId,
    [property: JsonPropertyName("lifetimeInHours")] int? LifetimeInHours,
    [property: JsonPropertyName("encryptionType")] string? EncryptionType,
    [property: JsonPropertyName("jkuEnabled")] bool? JkuEnabled,
    [property: JsonPropertyName("claims")] IReadOnlyList<string>? Claims,
    [property: JsonPropertyName("encryptionCertificate")] XboxLiveRelyingPartyCertificate? EncryptionCertificate);

/// <summary>A relying party's encryption certificate. Only its description is returned, never the certificate.</summary>
internal sealed record XboxLiveRelyingPartyCertificate(
    [property: JsonPropertyName("thumbprint")] string? Thumbprint,
    [property: JsonPropertyName("subject")] string? Subject,
    [property: JsonPropertyName("notBefore")] string? NotBefore,
    [property: JsonPropertyName("notAfter")] string? NotAfter);

/// <summary>
/// A business partner: an identity that owns a seller account's relying parties and web services. Its type is
/// <c>Partner</c>, <c>Legacy</c>, <c>LegacyAnonymized</c>, <c>LegacyPartner</c>, or <c>Internal</c>.
/// </summary>
internal sealed record XboxLiveBusinessPartner(
    [property: JsonPropertyName("businessPartnerId")] string? BusinessPartnerId,
    [property: JsonPropertyName("name")] string? Name,
    [property: JsonPropertyName("type")] string? Type,
    [property: JsonPropertyName("isDefault")] bool? IsDefault);
