using DotNetAgentSurface.Core;

namespace DotNetAgentSurface.CommandLine;

/// <summary>
/// A host-level flag accepted by <see cref="OperationCommandLineAdapter"/> that is not an operation parameter.
/// </summary>
/// <param name="Name">The flag, for example <c>--connection</c>. Matching ignores case, <c>-</c> and <c>_</c>.</param>
/// <param name="InjectAs">
/// The operation parameter that receives the value. The value is injected only into operations that declare
/// that parameter and not supplied explicitly; every other operation ignores the flag.
/// </param>
/// <param name="ConfigKey">
/// The configuration key the value maps to. Hosting applies it before the host is built (see
/// <see cref="OperationCommandLineAdapter.ReadConfigurationValues"/>); the adapter itself only accepts the flag.
/// </param>
/// <param name="Description">Text shown in help and generated skill documentation.</param>
public sealed record CommandLineGlobalOption(
    string Name,
    string? InjectAs = null,
    string? ConfigKey = null,
    string? Description = null)
{
    /// <summary>Gets the flag name without the leading dashes.</summary>
    public string FlagName => Name.TrimStart('-');
}

/// <summary>Optional behavior for <see cref="OperationCommandLineAdapter"/>.</summary>
public sealed class OperationCommandLineOptions
{
    /// <summary>Gets or sets the host-level flags accepted in addition to the adapter's own flags.</summary>
    public IReadOnlyList<CommandLineGlobalOption> GlobalOptions { get; set; } = [];

    /// <summary>
    /// Gets or sets whether multi-line operation errors are flattened to one line. Defaults to
    /// <see langword="false"/>: newlines are kept so structured error text survives.
    /// </summary>
    public bool FlattenOperationErrors { get; set; }

    /// <summary>
    /// Gets or sets a renderer that receives the raw (unprojected) value of non-string results. Return
    /// <see langword="null"/> to fall back to the default projection and renderer. An explicit
    /// <c>--output</c>/<c>--format</c> flag bypasses it.
    /// </summary>
    public Func<OperationDescriptor, object, string?>? ResultRenderer { get; set; }

    /// <summary>Gets or sets the reader used for <c>-</c> (standard input) values. Defaults to <see cref="Console.In"/>.</summary>
    public TextReader? Input { get; set; }
}
