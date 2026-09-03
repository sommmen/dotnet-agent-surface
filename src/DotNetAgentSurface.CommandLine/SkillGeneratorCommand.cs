using DotNetAgentSurface.Core;

namespace DotNetAgentSurface.CommandLine;

/// <summary>
/// Standalone <c>generate</c>/<c>check</c> command surface wrapping <see cref="SkillReferenceGenerator"/>.
/// Independent of <see cref="OperationCommandLineAdapter"/> so any host can wire it in alongside its
/// operation-invocation command surface without coupling the two together.
/// </summary>
public static class SkillGeneratorCommand
{
    /// <summary>Relative directory used when a host does not pass an explicit <c>--output</c> value.</summary>
    public const string DefaultOutputDirectory = "skill";

    /// <summary>
    /// Executes a <c>generate</c> or <c>check</c> verb against <paramref name="catalog"/>.
    /// Follows the same exit code conventions as <see cref="OperationCommandLineAdapter"/>:
    /// 0 success, 1 failure, 2 usage error, 130 cancellation. Generated output is kept small
    /// with a root-level <c>SKILL.md</c> plus a <c>references/</c> directory for detailed command
    /// and schema data.
    /// </summary>
    public static ValueTask<CommandLineExecutionResult> ExecuteAsync(string[] args, OperationCatalog catalog, string outputDirectoryDefault = DefaultOutputDirectory, CancellationToken cancellationToken = default)
    {
        if (catalog is null)
        {
            throw new ArgumentNullException(nameof(catalog));
        }

        if (outputDirectoryDefault is null)
        {
            throw new ArgumentNullException(nameof(outputDirectoryDefault));
        }

        if (outputDirectoryDefault.Trim().Length == 0)
        {
            throw new ArgumentException("Value cannot be null or whitespace.", nameof(outputDirectoryDefault));
        }

        args ??= [];
        cancellationToken.ThrowIfCancellationRequested();

        try
        {
            if (args.Length == 0 || args[0] is "--help" or "-h")
            {
                return new ValueTask<CommandLineExecutionResult>(CommandLineExecutionResult.Success(RenderHelp()));
            }

            var rest = args.Skip(1).ToArray();
            var result = args[0] switch
            {
                "generate" => ExecuteGenerate(rest, catalog, outputDirectoryDefault),
                "check" => ExecuteCheck(rest, catalog, outputDirectoryDefault),
                _ => CommandLineExecutionResult.Failure($"Unknown skill command '{args[0]}'.\n\n{RenderHelp()}", 2),
            };
            return new ValueTask<CommandLineExecutionResult>(result);
        }
        catch (OperationCanceledException)
        {
            return new ValueTask<CommandLineExecutionResult>(CommandLineExecutionResult.Failure("Cancelled.", 130));
        }
        catch (CommandLineUsageException exception)
        {
            return new ValueTask<CommandLineExecutionResult>(CommandLineExecutionResult.Failure(exception.Message, 2));
        }
    }

    /// <summary>
    /// True when the first argument is a <c>generate</c> or <c>check</c> verb, useful for dispatch from a host's
    /// entry point before falling through to its own operation adapter. Deliberately excludes <c>--help</c>/<c>-h</c>
    /// so a host's own top-level help output is unaffected.
    /// </summary>
    public static bool CanHandle(string[] args) => args is ["generate" or "check", ..];

    private static CommandLineExecutionResult ExecuteGenerate(string[] rest, OperationCatalog catalog, string outputDirectoryDefault)
    {
        var (outputDirectory, force) = ParseGenerateOptions(rest, outputDirectoryDefault);
        var generator = new SkillReferenceGenerator();

        if (!force && generator.IsCurrent(catalog, outputDirectory))
        {
            return CommandLineExecutionResult.Success("Skill reference is already current.");
        }

        generator.Generate(catalog, outputDirectory);
        return CommandLineExecutionResult.Success($"Skill reference generated in '{outputDirectory}'.");
    }

    private static CommandLineExecutionResult ExecuteCheck(string[] rest, OperationCatalog catalog, string outputDirectoryDefault)
    {
        var outputDirectory = ParseCheckOptions(rest, outputDirectoryDefault);
        var generator = new SkillReferenceGenerator();

        return generator.IsCurrent(catalog, outputDirectory)
            ? CommandLineExecutionResult.Success("Skill reference is current.")
            : CommandLineExecutionResult.Failure($"Skill reference in '{outputDirectory}' is missing or stale. Run 'generate' to update it.");
    }

    private static (string OutputDirectory, bool Force) ParseGenerateOptions(string[] arguments, string outputDirectoryDefault)
    {
        string outputDirectory = outputDirectoryDefault;
        var force = false;

        for (var index = 0; index < arguments.Length; index++)
        {
            switch (arguments[index])
            {
                case "--output":
                    if (index + 1 >= arguments.Length)
                    {
                        throw new CommandLineUsageException("'--output' requires a directory value.");
                    }

                    outputDirectory = arguments[++index];
                    break;
                case "--force":
                    force = true;
                    break;
                default:
                    throw new CommandLineUsageException($"Unknown argument '{arguments[index]}' for 'generate'.\n\n{RenderHelp()}");
            }
        }

        return (outputDirectory, force);
    }

    private static string ParseCheckOptions(string[] arguments, string outputDirectoryDefault)
    {
        var outputDirectory = outputDirectoryDefault;

        for (var index = 0; index < arguments.Length; index++)
        {
            switch (arguments[index])
            {
                case "--output":
                    if (index + 1 >= arguments.Length)
                    {
                        throw new CommandLineUsageException("'--output' requires a directory value.");
                    }

                    outputDirectory = arguments[++index];
                    break;
                default:
                    throw new CommandLineUsageException($"Unknown argument '{arguments[index]}' for 'check'.\n\n{RenderHelp()}");
            }
        }

        return outputDirectory;
    }

    private static string RenderHelp() =>
        "Skill reference commands:\n" +
        "  generate [--output <dir>] [--force]   Generate SKILL.md plus references/commands.md and references/schemas.json (default output: 'skill').\n" +
        "  check [--output <dir>]                Verify the generated skill reference is current (exit 0 if current, 1 if stale or missing).";
}
