namespace DotNetAgentSurface.CommandLine;

/// <summary>The outcome of one command-line execution.</summary>
/// <param name="ExitCode">0 success, 1 operation failure, 2 usage error, 130 cancellation.</param>
/// <param name="Output">Text for standard output.</param>
/// <param name="Error">Text for standard error.</param>
public sealed record CommandLineExecutionResult(int ExitCode, string Output, string? Error)
{
    /// <summary>
    /// Gets whether <see cref="Output"/> is a string result that must be written exactly as produced, without an
    /// appended line terminator when the output is redirected.
    /// </summary>
    public bool IsVerbatimOutput { get; init; }

    public static CommandLineExecutionResult Success(string output) => new(0, output, null);

    public static CommandLineExecutionResult Failure(string error, int exitCode = 1) => new(exitCode, string.Empty, error);
}

public sealed class CommandLineUsageException : Exception
{
    public CommandLineUsageException(string message)
        : base(message)
    {
    }
}
