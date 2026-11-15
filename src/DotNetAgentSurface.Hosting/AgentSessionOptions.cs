namespace DotNetAgentSurface.Hosting;

/// <summary>Configures the file-backed session that <see cref="AgentSurfaceApplication"/> provides in CLI mode.</summary>
public sealed class AgentSessionOptions
{
    /// <summary>Default value of <see cref="TimeToLive"/>.</summary>
    public static readonly TimeSpan DefaultTimeToLive = TimeSpan.FromDays(30);

    /// <summary>
    /// Gets or sets the application name used for the state directory and the <c>&lt;APP&gt;_SESSION</c> environment
    /// variable. Defaults to the entry assembly name.
    /// </summary>
    public string? AppName { get; set; }

    /// <summary>
    /// Gets or sets the directory that replaces the per-user state root (<c>%LOCALAPPDATA%</c> on Windows,
    /// <c>$XDG_STATE_HOME</c> or <c>~/.local/state</c> elsewhere). Sessions live in <c>&lt;root&gt;/&lt;app&gt;/sessions</c>.
    /// </summary>
    public string? StateDirectory { get; set; }

    /// <summary>
    /// Gets or sets how long an unused session file is kept (opening a session in the CLI counts as use). Expired files are deleted when a CLI invocation opens a
    /// session. <see langword="null"/> (or a non-positive value) disables pruning. Defaults to <see cref="DefaultTimeToLive"/>.
    /// </summary>
    public TimeSpan? TimeToLive { get; set; } = DefaultTimeToLive;

    /// <summary>Gets or sets the directory hashed into the default session id. Defaults to the current directory.</summary>
    public string? WorkingDirectory { get; set; }

    /// <summary>
    /// Gets the session keys whose values <c>session show</c> masks, matched case-insensitively. The values are still stored
    /// in the session file, so this only keeps them out of terminal output and logs; it does not make a session a safe place
    /// for secrets.
    /// </summary>
    public ISet<string> SensitiveKeys { get; } = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
}
