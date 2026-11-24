// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System.Globalization;
using System.Text.RegularExpressions;
using XboxDevCLI.Services;

namespace XboxDevCLI.Commands;

/// <summary>
/// The answer to "is this account's batch expired?": the account, the batch that holds it, and
/// whether the batch's access expiry has passed.
/// </summary>
/// <remarks>
/// Expired means the batch's sandbox access expiry passed, the only expiry the test account
/// service reports. Mirrors the JSON the command writes, so table columns can use <c>nameof</c>.
/// </remarks>
public sealed record TestAccountLookupResult(
    TestAccount Account,
    TestAccountBatch Batch,
    bool Expired,
    DateTimeOffset? ExpiredOn);

/// <summary>What a lookup found; see <see cref="TestAccountLookup.FindAsync"/>.</summary>
internal sealed record TestAccountLookupOutcome(
    IReadOnlyList<TestAccountLookupResult> Matches,
    int CandidateCount,
    int BatchesRead);

/// <summary>An email address and the batch email templates that could hold it.</summary>
internal sealed record TestAccountEmail(string Address, IReadOnlyList<string> Templates);

/// <summary>The account to find: its email address or its XUID, exactly one of them.</summary>
internal sealed record TestAccountKey(TestAccountEmail? Email, ulong? Xuid)
{
    public string Display => Email?.Address ?? $"XUID {Xuid!.Value.ToString(CultureInfo.InvariantCulture)}";
}

/// <summary>
/// Finds the batch that holds a test account. XBOX names accounts
/// <c>{prefix}-{template}{5 digit index}@xboxtest.com</c> and a batch's email template is
/// usually <c>{prefix}-{template}</c>, so the address leads to the batches that could hold it, and
/// each candidate batch's accounts are then read to confirm.
/// </summary>
/// <remarks>
/// Some batches store the template without the prefix (<c>-{template}</c>) although their accounts
/// carry it, so XboxPartner's <c>emailPrefix</c> filter cannot find them. The lookup therefore lists
/// every batch and matches both forms itself. A XUID goes as the <c>xuid</c> filter; a XUID says
/// nothing about the template, so without the filter only the newest batches are read.
/// </remarks>
internal static partial class TestAccountLookup
{
    internal const string Domain = "xboxtest.com";
    internal const int IndexDigits = 5;

    /// <summary>The most candidate batches read for one lookup.</summary>
    internal const int MaxCandidates = 10;

    /// <summary>The longest local part an email address may have.</summary>
    internal const int MaxLocalLength = 64;

    /// <summary>
    /// Microsoft account names: ASCII letters, digits, '_', '-' and single dots, starting with a
    /// letter or digit and not ending with a dot.
    /// </summary>
    [GeneratedRegex(@"^[A-Za-z0-9](?:[A-Za-z0-9_-]|\.(?!\.))*(?<!\.)$", RegexOptions.CultureInvariant)]
    private static partial Regex LocalPartPattern();

    /// <summary>
    /// Returns the problem with <paramref name="value"/>, or null and the account it names: a XUID
    /// when it's all digits, otherwise an email.
    /// </summary>
    internal static string? TryParseKey(string? value, out TestAccountKey? key)
    {
        key = null;
        string text = value?.Trim() ?? string.Empty;
        if (text.Length > 0 && text.All(char.IsAsciiDigit))
        {
            if (!ulong.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out ulong xuid) || xuid == 0)
            {
                return $"{text} isn't a valid XUID, such as 2814600000000001.";
            }

            key = new TestAccountKey(null, xuid);
            return null;
        }

        string? problem = TryParse(text, out TestAccountEmail? email);
        if (problem is null)
        {
            key = new TestAccountKey(email, null);
        }

        return problem;
    }

    /// <summary>Returns the problem with <paramref name="value"/>, or null and its parts.</summary>
    internal static string? TryParse(string? value, out TestAccountEmail? email)
    {
        email = null;
        string address = value?.Trim() ?? string.Empty;
        int at = address.IndexOf('@', StringComparison.Ordinal);
        if (at <= 0 || at != address.LastIndexOf('@') || address.Any(char.IsWhiteSpace))
        {
            return $"Pass an email such as ABCD-tester00000@{Domain}, or a XUID such as 2814600000000001.";
        }

        if (!string.Equals(address[(at + 1)..], Domain, StringComparison.OrdinalIgnoreCase))
        {
            return $"email must end with @{Domain}, the domain of XBOX test accounts.";
        }

        string local = address[..at];
        if (local.Length > MaxLocalLength || !LocalPartPattern().IsMatch(local))
        {
            return $"the part before @{Domain} may contain only letters, digits, '_', '-' and single " +
                "dots, must start with a letter or digit, must not end with a dot, and must be at most " +
                FormattableString.Invariant($"{MaxLocalLength} characters.");
        }

        var bases = new List<string>();
        if (local.Length > IndexDigits &&
            local[^IndexDigits..].All(char.IsAsciiDigit))
        {
            bases.Add(local[..^IndexDigits]);
        }

        bases.Add(local);
        var templates = new List<string>(bases);
        foreach (string template in bases)
        {
            int hyphen = template.IndexOf('-', StringComparison.Ordinal);
            if (hyphen > 0)
            {
                templates.Add(template[hyphen..]);
            }
        }

        email = new TestAccountEmail(address, templates);
        return null;
    }

    /// <summary>
    /// Lists the seller's batches that could hold the account, then reads each (newest first, up
    /// to <see cref="MaxCandidates"/>) for it. Every candidate is read, so an account found in more
    /// than one batch is reported rather than hidden.
    /// </summary>
    internal static async Task<TestAccountLookupOutcome> FindAsync(
        XboxPartnerClient client,
        long? sellerId,
        TestAccountKey key,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        string listJson = await client.ListTestAccountBatchesAsync(
            sellerId,
            new TestAccountBatchFilter(Xuid: key.Xuid?.ToString(CultureInfo.InvariantCulture)),
            cancellationToken).ConfigureAwait(false);
        var list = XboxPartnerCommandSupport.DeserializeResponse<TestAccountBatchesResponse>(
            listJson,
            "test account batch list",
            serviceName: null);
        if (list.Batches.Any(batch => batch is null))
        {
            throw new InvalidDataException("The test account batch list response contained a null batch.");
        }

        List<TestAccountBatch> candidates;
        Func<TestAccount, bool> isMatch;
        if (key.Email is { } email)
        {
            candidates = list.Batches
                .Where(batch => email.Templates.Contains(batch.EmailTemplate ?? string.Empty, StringComparer.OrdinalIgnoreCase))
                .ToList();
            isMatch = account => string.Equals(account.Email, email.Address, StringComparison.OrdinalIgnoreCase);
        }
        else
        {
            // A service with the xuid filter returns only the account's batch. One without it
            // returns every batch, and only the newest MaxCandidates of those are read.
            string xuid = key.Xuid!.Value.ToString(CultureInfo.InvariantCulture);
            candidates = [.. list.Batches];
            isMatch = account => string.Equals(account.Xuid, xuid, StringComparison.Ordinal);
        }

        var matches = new List<TestAccountLookupResult>();
        int read = 0;
        foreach (TestAccountBatch candidate in candidates.Take(MaxCandidates))
        {
            cancellationToken.ThrowIfCancellationRequested();
            string json = await client.GetTestAccountBatchAsync(sellerId, candidate.Id, cancellationToken)
                .ConfigureAwait(false);
            var detail = XboxPartnerCommandSupport.DeserializeResponse<TestAccountBatchResponse>(
                json,
                "test account batch",
                serviceName: null);
            if (detail.Batch is null || detail.Accounts is null || detail.Accounts.Any(account => account is null))
            {
                throw new InvalidDataException(
                    "The test account batch response must contain a 'batch' object and an " +
                    "'accounts' array.");
            }

            read++;
            foreach (TestAccount account in detail.Accounts.Where(isMatch))
            {
                matches.Add(Describe(account, detail.Batch, now));
            }
        }

        // A deleted account in one batch must not hide the live account in another.
        if (matches.Any(match => !match.Account.IsDeleted))
        {
            matches = [.. matches.Where(match => !match.Account.IsDeleted)];
        }

        return new TestAccountLookupOutcome(matches, candidates.Count, read);
    }

    internal static TestAccountLookupResult Describe(TestAccount account, TestAccountBatch batch, DateTimeOffset now)
    {
        bool expired = IsExpired(batch, now);
        return new TestAccountLookupResult(account, batch, expired, expired ? batch.AccessExpirationDate : null);
    }

    /// <summary>A batch with no expiry date is never expired.</summary>
    internal static bool IsExpired(TestAccountBatch batch, DateTimeOffset now) =>
        IsExpired(batch.AccessExpirationDate, now);

    internal static bool IsExpired(DateTimeOffset? expiry, DateTimeOffset now) =>
        expiry is { } date && date < now;
}
