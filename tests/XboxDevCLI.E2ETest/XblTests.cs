// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System.Buffers.Binary;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Xml;
using System.Xml.Linq;
using Xunit.Abstractions;
using Xunit.Sdk;

namespace XboxDevCLI.E2ETest;

public class XblTests(ITestOutputHelper output)
{
    private const string SchemaType = "achievements2017";

    // The global storage lifecycle test names each file this, the UTC time it was made, and .json, so a later run can
    // find one it left. Other tests can use the same sandbox, so it keeps to a folder of its own.
    private const string StorageFolder = "xboxdevcli_e2e/";

    private const string StorageFileName = StorageFolder + "lifecycle-";

    private const string StorageFileTime = "yyyyMMdd'T'HHmmssfff'Z'";

    private static readonly XName XsdRoot = XName.Get("schema", "http://www.w3.org/2001/XMLSchema");

    private static readonly byte[] PngSignature = [0x89, (byte)'P', (byte)'N', (byte)'G', 0x0D, 0x0A, 0x1A, 0x0A];

    private static E2ESettings Settings => E2ESettings.Current;

    [LiveFact]
    public async Task SchemaList_ListsTheTypes()
    {
        CliResult result = await Cli.RunAsync("xbl", "schema", "list", "--output", "json-compact");

        result.Check(result.Json().Items().HasString(SchemaType), $"Expected the {SchemaType} type.");
    }

    [LiveFact]
    public async Task SchemaGet_DefaultsToTheNewestVersionOfAType()
    {
        long newest = await GetNewestSchemaVersionAsync();

        CliResult schema = await Cli.RunAsync("xbl", "schema", "get", "--type", SchemaType, "--output", "json-compact");

        JsonElement selected = schema.Json();
        schema.Check(selected.Is("type", SchemaType), $"Expected the {SchemaType} type.");
        schema.Check(selected.Number("version") == newest, $"Expected version {newest}.");
        schema.Check(!string.IsNullOrEmpty(selected.Text("namespace")), "Expected a namespace.");
    }

    [LiveFact]
    public async Task SchemaDownload_WritesOneXsdToStdout()
    {
        long newest = await GetNewestSchemaVersionAsync();

        CliResult schema = await Cli.RunAsync(
            "xbl", "schema", "download", "--type", SchemaType, "--version", newest.ToString(CultureInfo.InvariantCulture),
            "--dest", "-");

        schema.Check(schema.ExitCode == 0, "Expected exit code 0.");
        schema.Check(ParseXml(schema.Stdout)?.Name == XsdRoot, "Expected the XSD on stdout.");
    }

    private static async Task<long> GetNewestSchemaVersionAsync()
    {
        CliResult versions = await Cli.RunAsync("xbl", "schema", "list", "--type", SchemaType, "--output", "json-compact");
        long newest = versions.Json().Items().Select(version => version.Number("version") ?? 0).DefaultIfEmpty().Max();
        versions.Check(newest > 0, $"Expected versions of {SchemaType}.");
        return newest;
    }

    [LiveFact]
    public async Task ConfigDownload_SavesTheWorkingDocuments()
    {
        DirectoryInfo destination = Directory.CreateTempSubdirectory("xbox-e2e-");
        try
        {
            CliResult result = await Cli.RunForSellerAsync(
                "xbl", "config", "download", "--scid", Settings.Scid, "--sandbox-id", Settings.Sandbox,
                "--view", "draft", "--dest", destination.FullName);

            JsonElement download = result.Json();
            JsonElement[] files = download.Items("files");
            result.Check(download.Is("view", "draft"), "Expected the draft view.");
            result.Check(files.Length > 0, "Expected documents.");
            result.Check(
                files.All(file => file.ValueKind == JsonValueKind.String && File.Exists(file.GetString())),
                "Expected every listed document on disk.");
            result.Check(File.Exists(download.Text("etagFile")), "Expected the ETag file on disk.");
        }
        finally
        {
            destination.Delete(recursive: true);
        }
    }

    [LiveFact]
    public async Task ConfigList_IncludesTheSandboxAndTheTitle()
    {
        CliResult result = await Cli.RunForSellerAsync("xbl", "config", "list", "--scid", Settings.Scid);

        JsonElement[] sets = [.. result.Json().Items()];
        result.Check(
            sets.Any(set => set.Is("scope", "sandbox") && set.Is("sandbox", Settings.Sandbox)),
            $"Expected {Settings.Sandbox}.");
        result.Check(sets.Any(set => set.Is("scope", "title")), "Expected the title's configuration.");
    }

    [LiveFact]
    public async Task ConfigStatus_ShowsTheLiveVersionAndTheLastPublish()
    {
        CliResult result = await Cli.RunForSellerAsync(
            "xbl", "config", "status", "--scid", Settings.Scid, "--sandbox-id", Settings.Sandbox);

        JsonElement status = result.Json();
        result.Check(status.Is("sandbox", Settings.Sandbox), $"Expected {Settings.Sandbox}.");
        result.Check(
            status.TryGetProperty("published", out JsonElement published) &&
                published.ValueKind == JsonValueKind.Object &&
                published.Number("version") > 0,
            "Expected a published version.");
        result.Check(
            status.TryGetProperty("documents", out JsonElement documents) &&
                documents.ValueKind == JsonValueKind.Array &&
                documents.GetArrayLength() > 0,
            "Expected the sandbox's documents.");
        result.Check(
            status.TryGetProperty("lastPublish", out JsonElement lastPublish) &&
                lastPublish.ValueKind == JsonValueKind.Object &&
                !string.IsNullOrWhiteSpace(lastPublish.Text("status")),
            "Expected the last publish's status.");
    }

    [LiveFact]
    public async Task ConfigHistory_ListsEntriesNewestFirst()
    {
        CliResult result = await Cli.RunForSellerAsync(
            "xbl", "config", "history", "--scid", Settings.Scid, "--sandbox-id", Settings.Sandbox, "--limit", "5");

        JsonElement history = result.Json();
        result.Check(history.Is("sandbox", Settings.Sandbox), $"Expected {Settings.Sandbox}.");
        long?[] versions = [.. history.Items("entries").Select(entry => entry.Number("version"))];
        result.Check(versions.Length <= 5, "Expected at most 5 entries.");
        result.Check(versions.All(version => version > 0), "Expected each entry's version.");
        result.Check(versions.SequenceEqual(versions.OrderDescending()), "Expected the newest entry first.");
    }

    [LiveFact]
    public async Task AchievementImageList_IncludesTheImage()
    {
        CliResult result = await Cli.RunForSellerAsync("xbl", "achievement-image", "list", "--scid", Settings.Scid);

        result.Check(
            result.Json().Items().Any(image =>
                image.IsGuid("assetId", Settings.AchievementImageId) &&
                image.IsGuid("iconImageId", Settings.AchievementImageId)),
            $"Expected image {Settings.AchievementImageId}, with iconImageId set to it.");
    }

    [LiveFact]
    public async Task AchievementImageGet_ReturnsTheImage()
    {
        CliResult result = await Cli.RunForSellerAsync(
            "xbl", "achievement-image", "get", "--scid", Settings.Scid, "--asset-id", Settings.AchievementImageId);

        JsonElement image = result.Json();
        result.Check(
            image.IsGuid("assetId", Settings.AchievementImageId) && image.IsGuid("iconImageId", Settings.AchievementImageId),
            $"Expected image {Settings.AchievementImageId}, with iconImageId set to it.");
    }

    // The CLI downloads the image and its thumbnail from the CDN itself.
    [LiveFact]
    public async Task AchievementImageDownload_SavesTheImage()
    {
        DirectoryInfo destination = Directory.CreateTempSubdirectory("xbox-e2e-");
        try
        {
            CliResult result = await Cli.RunForSellerAsync(
                "xbl", "achievement-image", "download", "--scid", Settings.Scid, "--asset-id", Settings.AchievementImageId,
                "--dest", destination.FullName);

            JsonElement download = result.Json();
            result.Check(download.IsGuid("assetId", Settings.AchievementImageId), $"Expected image {Settings.AchievementImageId}.");
            result.Check(File.Exists(download.Text("thumbnailPath")), "Expected the thumbnail on disk.");
            result.Check(
                download.Text("imagePath") is { } image && File.Exists(image) && ReadPngSize(image) == (1920, 1080),
                "Expected the image on disk, as a 1920x1080 PNG, the size XBOX services require.");
        }
        finally
        {
            destination.Delete(recursive: true);
        }
    }

    // XBOX services answer that the files haven't changed and store nothing, which the CLI counts as success.
    [LiveFact]
    public async Task ConfigSave_ValidateOnly_AcceptsTheDocumentsJustDownloaded()
    {
        DirectoryInfo destination = Directory.CreateTempSubdirectory("xbox-e2e-");
        try
        {
            CliResult download = await Cli.RunForSellerAsync(
                "xbl", "config", "download", "--scid", Settings.Scid, "--sandbox-id", Settings.Sandbox,
                "--view", "draft", "--dest", destination.FullName);
            string[] files = [.. download.Json().Items("files").Select(file => file.GetString()).OfType<string>()];
            download.Check(files.Length > 0, "Expected documents.");

            CliResult result = await Cli.RunForSellerAsync(
            [
                "xbl", "config", "save", "--validate-only", "--scid", Settings.Scid, "--sandbox-id", Settings.Sandbox,
                .. files.SelectMany(file => new[] { "--file", file }),
            ]);

            result.Check(result.Json().Field("result").Flag("Committed") == false, "Expected nothing committed.");
        }
        finally
        {
            destination.Delete(recursive: true);
        }
    }

    // XBOX services answer a save of documents identical to the latest ones with FilesNotChangedCommit, and store
    // nothing. The test saves the documents it just downloaded, at their ETag, only once a validation shows them
    // unchanged.
    [LiveWriteFact]
    public async Task ConfigSave_OfTheDocumentsJustDownloaded_SavesNothing()
    {
        DirectoryInfo destination = Directory.CreateTempSubdirectory("xbox-e2e-");
        try
        {
            CliResult download = await Cli.RunForSellerAsync(
                "xbl", "config", "download", "--scid", Settings.Scid, "--sandbox-id", Settings.Sandbox,
                "--view", "draft", "--dest", destination.FullName);
            JsonElement downloaded = download.Json();
            string[] files = [.. downloaded.Items("files").Select(file => file.GetString()).OfType<string>()];
            download.Check(files.Length > 0 && downloaded.Text("etag") is not null, "Expected documents and their ETag.");
            string[] save =
            [
                "xbl", "config", "save", "--scid", Settings.Scid, "--sandbox-id", Settings.Sandbox,
                .. files.SelectMany(file => new[] { "--file", file }),
            ];

            CheckSavedNothing(await Cli.RunForSellerAsync([.. save, "--validate-only"]), downloaded.Text("etag")!);
            CheckSavedNothing(await Cli.RunForSellerAsync(save), downloaded.Text("etag")!);
        }
        finally
        {
            destination.Delete(recursive: true);
        }
    }

    private static void CheckSavedNothing(CliResult save, string downloadedEtag)
    {
        // The CLI prints the service's response as it is, which names its fields in PascalCase.
        JsonElement result = save.Json().Field("result");
        save.Check(
            result.Flag("CanCommit") == false && result.Flag("Committed") == false &&
                result.Items("ValidationInfo").Select(message => message.Text("Error")).SequenceEqual(["FilesNotChangedCommit"]),
            "Expected nothing saved, because the documents haven't changed.");
        save.Check(result.Text("ETag") == downloadedEtag, $"Expected the configuration still at ETag {downloadedEtag}.");
    }

    // XBOX services' document hashes are SHA-1 of the bytes a download saves, so the draft's own files
    // compare as the same as the draft.
    [LiveFact]
    public async Task ConfigDiff_FindsTheDocumentsJustDownloadedTheSameAsTheDraft()
    {
        DirectoryInfo destination = Directory.CreateTempSubdirectory("xbox-e2e-");
        try
        {
            CliResult download = await Cli.RunForSellerAsync(
                "xbl", "config", "download", "--scid", Settings.Scid, "--sandbox-id", Settings.Sandbox,
                "--view", "draft", "--dest", destination.FullName);
            string[] files = [.. download.Json().Items("files").Select(file => file.GetString()).OfType<string>()];
            download.Check(files.Length > 0, "Expected documents.");

            CliResult result = await Cli.RunForSellerAsync(
            [
                "xbl", "config", "diff", "--scid", Settings.Scid, "--sandbox-id", Settings.Sandbox,
                .. files.SelectMany(file => new[] { "--file", file }),
            ]);

            JsonElement diff = result.Json();
            JsonElement[] documents = [.. diff.Items("features").SelectMany(feature => feature.Items("documents"))];
            result.Check(documents.Length > 0, "Expected compared documents.");
            result.Check(documents.All(document => document.Is("state", "same")), "Expected every document the same as the draft.");
        }
        finally
        {
            destination.Delete(recursive: true);
        }
    }

    // A failed request writes one JSON error to stderr, whose detail carries the service's reason and its
    // correlation vector (MS-CV), which traces the request.
    [LiveFact]
    public async Task ConfigSave_ValidateOnly_ShowsWhyABrokenDocumentIsRefused()
    {
        DirectoryInfo directory = Directory.CreateTempSubdirectory("xbox-e2e-");
        try
        {
            string document = Path.Combine(directory.FullName, "titlestorage.xml");
            await File.WriteAllTextAsync(document, "<{ not a configuration document");

            CliResult result = await Cli.RunForSellerAsync(
                "xbl", "config", "save", "--validate-only", "--scid", Settings.Scid, "--sandbox-id", Settings.Sandbox,
                "--file", document);

            result.Check(result.ExitCode == 1, "Expected exit code 1.");
            result.Check(string.IsNullOrWhiteSpace(result.Stdout), "Expected nothing on stdout.");
            result.Check(
                result.Stderr.Contains("\"status\":400", StringComparison.Ordinal) &&
                    result.Stderr.Contains("Invalid Document", StringComparison.OrdinalIgnoreCase) &&
                    result.Stderr.Contains("MS-CV:", StringComparison.Ordinal),
                "Expected a 400 on stderr, with the service's reason and its MS-CV.");
        }
        finally
        {
            directory.Delete(recursive: true);
        }
    }

    // With no --source-sandbox-id, XBOX services validate publishing the sandbox's own working configuration.
    [LiveFact]
    public async Task ConfigPublish_ValidateOnly_ValidatesTheSandbox()
    {
        CliResult result = await Cli.RunForSellerAsync(
            "xbl", "config", "publish", "--validate-only", "--scid", Settings.Scid, "--sandbox-id", Settings.Sandbox);

        // The CLI prints the service's response as it is, which names its fields in PascalCase.
        result.Check(!string.IsNullOrWhiteSpace(result.Json().Text("Status")), "Expected a status.");
    }

    // XBOX services validate the seller account's working configuration and start no publish. When that configuration is
    // already live, the service answers AlreadyPublished instead, without validating it.
    [LiveFact]
    public async Task ConfigPublish_ValidateOnly_ValidatesTheAccount()
    {
        CliResult result = await Cli.RunForSellerAsync("xbl", "config", "publish", "--scope", "account", "--validate-only");

        result.Check(
            result.Json().Text("status") is "Validated" or "AlreadyPublished",
            "Expected the account's configuration validated, or already published.");
    }

    [LiveFact]
    public async Task BindingGet_ReturnsTheGamesOwnTitleId()
    {
        CliResult result = await Cli.RunForSellerAsync("xbl", "binding", "get", "--store-id", Settings.StoreId);

        JsonElement binding = result.Json();
        result.Check(binding.Is("storeId", Settings.StoreId), $"Expected Store ID {Settings.StoreId}.");
        result.Check(binding.Is("titleId", Settings.TitleId), $"Expected XBOX title ID {Settings.TitleId}.");
    }

    [LiveFact]
    public async Task GlobalStorageGet_ReadsTheSandboxUsageByTitleId()
    {
        CliResult result = await Cli.RunForSellerAsync(
            "xbl", "global-storage", "get", "--title-id", Settings.TitleId, "--sandbox-id", Settings.Sandbox);

        result.Check(result.Json().Number("quotaBytes") > 0, "Expected the sandbox's quota.");
    }

    [LiveFact]
    public async Task GlobalStorageList_ListsTheSandboxFiles()
    {
        CliResult result = await Cli.RunForSellerAsync(
            "xbl", "global-storage", "list", "--scid", Settings.Scid, "--sandbox-id", Settings.Sandbox);

        result.Check(result.Json().ValueKind == JsonValueKind.Array, "Expected an array of files.");
    }

    [LiveFact]
    public async Task BusinessPartnerList_ListsTheAccountsBusinessPartners()
    {
        CliResult result = await Cli.RunForSellerAsync("xbl", "business-partner", "list");

        result.Check(result.Json().ValueKind == JsonValueKind.Array, "Expected an array of business partners.");
    }

    // Players in the sandbox get an uploaded file at once, so the test uploads to a folder of its own, and deletes the
    // file in the same test; XBOX services keep no copy of a deleted file. An upload without --overwrite never replaces a
    // file, and a delete removes only a file of that path and type, so the test changes only its own file and the ones
    // DeleteStorageLeftoversAsync finds.
    [LiveWriteFact]
    public async Task GlobalStorageUploadDownloadAndDelete_LeavesNoFile()
    {
        await DeleteStorageLeftoversAsync();

        string path = StorageFileName + DateTime.UtcNow.ToString(StorageFileTime, CultureInfo.InvariantCulture) + ".json";
        byte[] content = Encoding.UTF8.GetBytes("""{"createdBy":"The XBOX CLI's end-to-end tests"}""");
        DirectoryInfo directory = Directory.CreateTempSubdirectory("xbox-e2e-");
        string file = Path.Combine(directory.FullName, "upload.json");
        string downloaded = Path.Combine(directory.FullName, "download.json");
        await File.WriteAllBytesAsync(file, content);
        string[] upload =
        [
            "xbl", "global-storage", "upload", "--scid", Settings.Scid, "--sandbox-id", Settings.Sandbox,
            "--file", file, "--path", path, "--file-type", "json",
        ];
        bool mightBeLeft = false;
        try
        {
            CliResult uploaded = await Cli.RunForSellerAsync(upload);

            // Exit 1 means the upload was refused, so there's nothing to delete.
            mightBeLeft = uploaded.ExitCode != 1;
            uploaded.Check(uploaded.ExitCode == 0, "Expected exit code 0.");

            CliResult download = await Cli.RunForSellerAsync(
                "xbl", "global-storage", "download", "--scid", Settings.Scid, "--sandbox-id", Settings.Sandbox,
                "--path", path, "--file-type", "json", "--dest", downloaded);
            download.Check(
                download.ExitCode == 0 && File.Exists(downloaded) && File.ReadAllBytes(downloaded).AsSpan().SequenceEqual(content),
                "Expected the file as it was uploaded.");

            CliResult again = await Cli.RunForSellerAsync(upload);
            again.CheckServiceError(409);
            again.Check(
                again.Stderr.Contains("\"code\":\"global_storage_file_exists\"", StringComparison.Ordinal) &&
                    again.Stderr.Contains("Pass --overwrite to replace it.", StringComparison.Ordinal),
                "Expected the upload refused because the file exists, with the option that replaces it.");

            CliResult delete = await DeleteStorageFileAsync(path);
            delete.Check(delete.ExitCode == 0, "Expected exit code 0.");

            CliResult list = await ListStorageFolderAsync();
            list.Check(!list.Json().Items().Any(listed => listed.Is("path", path)), $"Expected {path} gone.");
            mightBeLeft = false;
        }
        finally
        {
            if (mightBeLeft && DescribeFailedDelete(await DeleteStorageFileAsync(path), path) is { } failure)
            {
                output.WriteLine(failure);
            }

            directory.Delete(recursive: true);
        }
    }

    // Deletes the files that runs which stopped early left, and fails if one is still there, so it's noticed.
    private static async Task DeleteStorageLeftoversAsync()
    {
        DateTimeOffset now = DateTimeOffset.UtcNow;
        List<string> failures = [];
        foreach (JsonElement file in (await ListStorageFolderAsync()).Json().Items())
        {
            if (IsStorageLeftover(file, now) &&
                DescribeFailedDelete(await DeleteStorageFileAsync(file.Text("path")!), file.Text("path")!) is { } failure)
            {
                failures.Add(failure);
            }
        }

        if (failures.Count > 0)
        {
            throw new XunitException(string.Join(Environment.NewLine, failures));
        }
    }

    // A json file named StorageFileName and a time over an hour ago, so a run that's still going keeps its own.
    private static bool IsStorageLeftover(JsonElement file, DateTimeOffset now) =>
        file.Is("type", "json") &&
        file.Text("path") is { } path &&
        path.StartsWith(StorageFileName, StringComparison.Ordinal) &&
        path.EndsWith(".json", StringComparison.Ordinal) &&
        DateTimeOffset.TryParseExact(
            path[StorageFileName.Length..^".json".Length], StorageFileTime, CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal, out DateTimeOffset made) &&
        now - made > TimeSpan.FromHours(1);

    private static Task<CliResult> ListStorageFolderAsync() =>
        Cli.RunForSellerAsync(
            "xbl", "global-storage", "list", "--scid", Settings.Scid, "--sandbox-id", Settings.Sandbox,
            "--path", StorageFolder);

    private static Task<CliResult> DeleteStorageFileAsync(string path) =>
        Cli.RunForSellerAsync(
            "xbl", "global-storage", "delete", "--scid", Settings.Scid, "--sandbox-id", Settings.Sandbox,
            "--path", path, "--file-type", "json", "--confirm");

    /// <summary>Why a delete failed, or null once the file is gone.</summary>
    private static string? DescribeFailedDelete(CliResult delete, string path) =>
        delete.ExitCode == 0 ||
        (delete.ExitCode == 1 && delete.Stderr.Contains("\"code\":\"global_storage_file_not_found\"", StringComparison.Ordinal))
            ? null
            : $"Deleting global storage file {path} failed.{Environment.NewLine}{delete}";

    /// <summary>The width and height in a PNG's header, or null if the file isn't a PNG.</summary>
    private static (int Width, int Height)? ReadPngSize(string path)
    {
        byte[] header = new byte[24];
        using (FileStream file = File.OpenRead(path))
        {
            if (file.ReadAtLeast(header, header.Length, throwOnEndOfStream: false) < header.Length)
            {
                return null;
            }
        }

        // The signature, then the IHDR chunk's length and type, then its width and height.
        return header.AsSpan(0, 8).SequenceEqual(PngSignature) && header.AsSpan(12, 4).SequenceEqual("IHDR"u8)
            ? (BinaryPrimitives.ReadInt32BigEndian(header.AsSpan(16)), BinaryPrimitives.ReadInt32BigEndian(header.AsSpan(20)))
            : null;
    }

    private static XElement? ParseXml(string text)
    {
        try
        {
            return XDocument.Parse(text).Root;
        }
        catch (XmlException)
        {
            return null;
        }
    }
}
