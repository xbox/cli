// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System.Globalization;
using System.Net.Http.Headers;
using System.Text.Json.Serialization;
using XboxDevCLI.Commands;
using XboxDevCLI.Telemetry;

namespace XboxDevCLI.Services;

internal sealed partial class XboxPartnerClient
{
    /// <summary>The largest file the gateway takes in one upload: 4 MiB.</summary>
    internal const int MaxGlobalStorageUploadBytes = 4 * 1024 * 1024;

    /// <summary>Reads how much of its quota a sandbox's global storage uses.</summary>
    public async Task<XboxLiveGlobalStorageUsage> GetGlobalStorageUsageAsync(
        long? sellerId,
        Guid scid,
        string sandbox,
        CancellationToken cancellationToken)
    {
        string correlationId = Guid.NewGuid().ToString();
        string body = await SendAsync(
            HttpMethod.Get, GlobalStoragePath(sellerId, scid, sandbox, string.Empty), null, cancellationToken,
            correlationId: correlationId).ConfigureAwait(false);
        try
        {
            return XboxPartnerCommandSupport.DeserializeResponse<XboxLiveGlobalStorageUsage>(body, "global storage usage")
                with { RawResponse = body };
        }
        catch (InvalidDataException exception)
        {
            throw new XboxPartnerResponseException(body, correlationId, exception);
        }
    }

    /// <summary>
    /// Reads every page of a sandbox's global storage files, optionally only those under
    /// <paramref name="pathPrefix"/>, and returns them as a JSON array, as the service sent them.
    /// </summary>
    public async Task<string> ListGlobalStorageFilesAsync(
        long? sellerId,
        Guid scid,
        string sandbox,
        string? pathPrefix,
        CancellationToken cancellationToken,
        Action<int>? pageRead = null)
    {
        IReadOnlyList<string> files = await ReadEveryPageAsync(
            "files",
            "global storage file list",
            token => (
                HttpMethod.Get,
                GlobalStoragePath(
                    sellerId, scid, sandbox, "/files",
                    ("path", pathPrefix),
                    // The service takes a page size or a continuation token, not both.
                    token is null ? ("pageSize", ListPageSize.ToString(CultureInfo.InvariantCulture)) : ("continuationToken", token)),
                null),
            cancellationToken,
            pageRead: pageRead).ConfigureAwait(false);
        return "[" + string.Join(',', files) + "]";
    }

    /// <summary>Downloads one file into <paramref name="destination"/>, and returns how many bytes it wrote.</summary>
    /// <remarks>
    /// The gateway streams the file, so a failure after the first byte ends the response early, which
    /// throws here rather than returning a short file.
    /// </remarks>
    public async Task<long> DownloadGlobalStorageFileAsync(
        long? sellerId,
        Guid scid,
        string sandbox,
        string path,
        string type,
        Stream destination,
        CancellationToken cancellationToken)
    {
        string correlationId = Guid.NewGuid().ToString();
        using HttpResponseMessage response = await SendResponseAsync(
            HttpMethod.Get,
            GlobalStoragePath(sellerId, scid, sandbox, "/files/content", ("path", path), ("type", type)),
            null,
            "application/octet-stream",
            cancellationToken,
            correlationId).ConfigureAwait(false);
        using var bodyTimeout = CreateResponseBodyTimeout(cancellationToken);
        await using Stream body = await response.Content.ReadAsStreamAsync(bodyTimeout.Token).ConfigureAwait(false);
        byte[] buffer = new byte[81920];
        long written = 0;
        while (true)
        {
            int read;
            try
            {
                read = await body.ReadAsync(buffer, bodyTimeout.Token).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is OperationCanceledException or IOException or HttpRequestException
                && !cancellationToken.IsCancellationRequested)
            {
                TelemetryRecorder.RecordError(TelemetryErrorTypes.Transport);
                ex.Data[CorrelationIdHeader] = ReadCorrelationId(response) ?? correlationId;
                throw;
            }

            if (read == 0)
            {
                return written;
            }

            await destination.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
            written += read;
        }
    }

    /// <summary>
    /// Uploads <paramref name="content"/>, at most <see cref="MaxGlobalStorageUploadBytes"/>, as one file,
    /// replacing a file at its path only with <paramref name="overwrite"/>.
    /// </summary>
    /// <remarks>
    /// The request is sent once and never retried. <paramref name="onSending"/> is called immediately
    /// before the request is handed to the network.
    /// </remarks>
    public async Task UploadGlobalStorageFileAsync(
        long? sellerId,
        Guid scid,
        string sandbox,
        string path,
        string type,
        bool overwrite,
        ReadOnlyMemory<byte> content,
        string correlationId,
        CancellationToken cancellationToken,
        Action? onSending = null)
    {
        using var body = new ReadOnlyMemoryContent(content);
        body.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
        await SendGlobalStorageChangeAsync(
            HttpMethod.Put,
            GlobalStoragePath(
                sellerId, scid, sandbox, "/files/content",
                ("path", path), ("type", type), ("overwrite", overwrite ? "true" : null)),
            body,
            correlationId,
            onSending,
            cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Deletes one file, when its path holds a file of <paramref name="type"/>.</summary>
    /// <remarks>
    /// The request is sent once and never retried. <paramref name="onSending"/> is called immediately
    /// before the request is handed to the network.
    /// </remarks>
    public Task DeleteGlobalStorageFileAsync(
        long? sellerId,
        Guid scid,
        string sandbox,
        string path,
        string type,
        string correlationId,
        CancellationToken cancellationToken,
        Action? onSending = null) =>
        SendGlobalStorageChangeAsync(
            HttpMethod.Delete,
            GlobalStoragePath(sellerId, scid, sandbox, "/files", ("path", path), ("type", type)),
            null,
            correlationId,
            onSending,
            cancellationToken);

    // A success status is the outcome. The response's body only repeats the request's path and type.
    private async Task SendGlobalStorageChangeAsync(
        HttpMethod method,
        string path,
        HttpContent? content,
        string correlationId,
        Action? onSending,
        CancellationToken cancellationToken)
    {
        HttpResponseMessage response = await SendResponseAsync(
            method, path, content, "application/json", cancellationToken, correlationId, onSending)
            .ConfigureAwait(false);
        response.Dispose();
    }

    // The SCID and sandbox are in the path, and the file's path and type in the query, so a file path's
    // slashes never need path encoding.
    private static string GlobalStoragePath(
        long? sellerId,
        Guid scid,
        string sandbox,
        string resource,
        params (string Name, string? Value)[] query)
    {
        ValidateSellerId(sellerId);
        ValidateRequiredGuid(scid, nameof(scid));
        string validatedSandbox = ValidateSandbox(sandbox, nameof(sandbox));
        var parameters = new List<string>();
        AddQueryValue(parameters, "sellerId", sellerId?.ToString(CultureInfo.InvariantCulture));
        foreach ((string name, string? value) in query)
        {
            AddQueryValue(parameters, name, value);
        }

        return $"xboxpartner/v1/xboxlive/scids/{scid:D}/sandboxes/{Uri.EscapeDataString(validatedSandbox)}/global-storage" +
            resource + (parameters.Count == 0 ? string.Empty : "?" + string.Join('&', parameters));
    }
}

/// <summary>How much of its quota a sandbox's global storage uses.</summary>
/// <param name="UsedBytes">Bytes the sandbox's files use.</param>
/// <param name="QuotaBytes">
/// Once the files use this many bytes, uploads fail. The upload that crosses it succeeds, so
/// <paramref name="UsedBytes"/> can be higher.
/// </param>
internal sealed record XboxLiveGlobalStorageUsage(
    [property: JsonPropertyName("usedBytes")] long? UsedBytes,
    [property: JsonPropertyName("quotaBytes")] long? QuotaBytes)
{
    /// <summary>The response as the service sent it, which output writes unchanged.</summary>
    [JsonIgnore]
    public string RawResponse { get; init; } = string.Empty;
}

/// <summary>A global storage file, as listed.</summary>
/// <param name="Path">The file's path, such as <c>news/today.json</c>.</param>
/// <param name="Type"><c>binary</c>, <c>json</c>, or <c>config</c>.</param>
/// <param name="Size">Its size in bytes.</param>
/// <param name="ETag">Its entity tag.</param>
/// <param name="ClientFileTime">When it was uploaded, as the uploader reported it.</param>
internal sealed record XboxLiveGlobalStorageFile(
    [property: JsonPropertyName("path")] string? Path,
    [property: JsonPropertyName("type")] string? Type,
    [property: JsonPropertyName("size")] long? Size,
    [property: JsonPropertyName("etag")] string? ETag,
    [property: JsonPropertyName("clientFileTime")] string? ClientFileTime);
