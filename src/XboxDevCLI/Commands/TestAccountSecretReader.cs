// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System.Text;

namespace XboxDevCLI.Commands;

/// <summary>
/// Seams for the parts of <c>test-account</c> commands that touch the terminal or the clock, so
/// tests can drive prompts, stdin, and waiting without a console.
/// </summary>
internal sealed record TestAccountCommandDependencies(
    Func<TimeSpan, CancellationToken, Task> DelayAsync,
    Func<DateTimeOffset> GetUtcNow,
    Func<bool> IsInteractive,
    Func<string, string?> ReadMaskedSecret,
    Func<string?> ReadConfirmation,
    Func<TextReader> GetStandardInput,
    Func<bool> IsStandardInputRedirected)
{
    internal static TestAccountCommandDependencies Default { get; } = new(
        Task.Delay,
        () => DateTimeOffset.UtcNow,
        () => XboxPartnerCommandSupport.IsInteractiveTerminal(),
        TestAccountSecretReader.ReadMaskedFromConsole,
        Console.ReadLine,
        // Console.In decodes with the console code page, which is not UTF-8 on Windows; a
        // non-ASCII secret piped in would be sent garbled.
        () => new StreamReader(Console.OpenStandardInput(), new UTF8Encoding(false)),
        () => Console.IsInputRedirected);
}

/// <summary>
/// Reads the create secrets from a file, from piped stdin, or from a masked prompt. Never from an
/// option value or an environment variable, which would leave them in shell history, process
/// listings, or logs.
/// </summary>
internal static class TestAccountSecretReader
{
    public const string StandardInput = "-";

    internal const string PasswordRequirements =
        "Password must be 8 to 16 characters and contain at least three of these four categories: " +
        "lowercase letters, uppercase letters, numbers, and symbols. Underscore does not count as a symbol.";

    public static Task<string> ReadPasswordAsync(
        string? file,
        TestAccountCommandDependencies dependencies,
        CancellationToken cancellationToken)
    {
        if (file is null && dependencies.IsInteractive())
        {
            Console.Error.WriteLine(PasswordRequirements);
        }

        return ReadAsync(file, "--password-file", "Password", dependencies, cancellationToken);
    }

    /// <summary>
    /// Throws <see cref="InvalidOperationException"/> when <see cref="ReadAsync"/> is bound to fail
    /// because the file is missing, <c>-</c> would read a terminal, or no terminal can prompt. Reads nothing.
    /// </summary>
    public static void EnsureSource(
        string? file,
        string fileOptionName,
        string label,
        TestAccountCommandDependencies dependencies)
    {
        if (file == StandardInput)
        {
            if (!dependencies.IsStandardInputRedirected())
            {
                throw StandardInputIsTerminal(fileOptionName, label, dependencies);
            }

            return;
        }

        if (file is not null)
        {
            if (!File.Exists(file))
            {
                throw new InvalidOperationException($"Could not read {fileOptionName} '{file}': the file does not exist.");
            }

            return;
        }

        if (!dependencies.IsInteractive())
        {
            throw NoTerminal(fileOptionName, label);
        }
    }

    /// <summary>
    /// Returns the secret, or throws <see cref="InvalidOperationException"/> with guidance, and
    /// <see cref="OperationCanceledException"/> when <paramref name="cancellationToken"/> is
    /// canceled while stdin is waiting for input. <paramref name="prompt"/> replaces
    /// <paramref name="label"/> in the terminal prompt.
    /// </summary>
    public static async Task<string> ReadAsync(
        string? file,
        string fileOptionName,
        string label,
        TestAccountCommandDependencies dependencies,
        CancellationToken cancellationToken,
        string? prompt = null)
    {
        string? value;
        if (file == StandardInput)
        {
            if (!dependencies.IsStandardInputRedirected())
            {
                throw StandardInputIsTerminal(fileOptionName, label, dependencies);
            }

            // A pipe read may ignore its token, so WaitAsync makes Ctrl+C stop the wait even when
            // the writer never closes the pipe.
            value = TrimOneNewline(await dependencies.GetStandardInput()
                .ReadToEndAsync(cancellationToken).WaitAsync(cancellationToken).ConfigureAwait(false));
        }
        else if (file is not null)
        {
            try
            {
                value = TrimOneNewline(File.ReadAllText(file));
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException
                or ArgumentException or NotSupportedException)
            {
                throw new InvalidOperationException(
                    $"Could not read {fileOptionName} '{file}': {exception.Message}", exception);
            }
        }
        else if (dependencies.IsInteractive())
        {
            value = dependencies.ReadMaskedSecret($"{prompt ?? label}: ");
            if (!string.IsNullOrEmpty(value) &&
                !string.Equals(value, dependencies.ReadMaskedSecret($"Confirm {label.ToLowerInvariant()}: "), StringComparison.Ordinal))
            {
                throw new InvalidOperationException($"The {label.ToLowerInvariant()} entries did not match.");
            }
        }
        else
        {
            throw NoTerminal(fileOptionName, label);
        }

        if (string.IsNullOrEmpty(value))
        {
            throw new InvalidOperationException($"The {label.ToLowerInvariant()} must not be empty.");
        }

        return value;
    }

    private static InvalidOperationException NoTerminal(string fileOptionName, string label) =>
        new($"No terminal is available to prompt for the {label.ToLowerInvariant()}. Pass {fileOptionName} " +
            $"with a file path, or {fileOptionName} - to read it from piped stdin.");

    // The prompt also needs stderr to be a terminal, so it's offered only when it can run.
    private static InvalidOperationException StandardInputIsTerminal(
        string fileOptionName, string label, TestAccountCommandDependencies dependencies) =>
        new($"{fileOptionName} - reads the {label.ToLowerInvariant()} from stdin, which is a terminal here, so " +
            "it would show on screen as you type it. " +
            (dependencies.IsInteractive()
                ? $"Leave out {fileOptionName} to type it at a prompt that doesn't show it, pipe it in, or pass "
                : "Pipe it in, or pass ") +
            $"{fileOptionName} with the path of a file that holds it.");

    internal static string TrimOneNewline(string value) =>
        value.EndsWith("\r\n", StringComparison.Ordinal) ? value[..^2]
        : value.EndsWith('\n') ? value[..^1]
        : value;

    /// <summary>Reads a line from the console without echoing it. The prompt goes to stderr.</summary>
    internal static string? ReadMaskedFromConsole(string prompt)
    {
        Console.Error.Write(prompt);
        Console.Error.Flush();
        var builder = new StringBuilder();
        while (true)
        {
            ConsoleKeyInfo key = Console.ReadKey(intercept: true);
            if (key.Key == ConsoleKey.Enter)
            {
                Console.Error.WriteLine();
                return builder.ToString();
            }

            if (key.Key == ConsoleKey.Backspace)
            {
                if (builder.Length > 0)
                {
                    builder.Length--;
                }
            }
            else if (!char.IsControl(key.KeyChar))
            {
                builder.Append(key.KeyChar);
            }
        }
    }
}
