using System.Text;
using DotNetAgentSurface.Core;
using Microsoft.Extensions.Logging;

namespace DotNetAgentSurface.Samples.CliAndMcp;

/// <summary>
/// A service that is only <see cref="IAsyncDisposable"/>. A host that disposes synchronously throws for it, so it
/// demonstrates why the CLI disposes the host asynchronously.
/// </summary>
public sealed class AuditLog : IAsyncDisposable
{
    private readonly object _gate = new();
    private readonly List<string> _entries = [];

    public int Count
    {
        get
        {
            lock (_gate)
            {
                return _entries.Count;
            }
        }
    }

    public void Record(string entry)
    {
        lock (_gate)
        {
            _entries.Add(entry);
        }
    }

    public ValueTask DisposeAsync()
    {
        lock (_gate)
        {
            _entries.Clear();
        }

        return ValueTask.CompletedTask;
    }
}

/// <summary>Operations that return pre-formatted text, which the CLI writes verbatim.</summary>
public sealed class ReportOperations(AuditLog audit, ILogger<ReportOperations> logger)
{
    /// <summary>Builds a markdown report with the requested number of lines.</summary>
    /// <param name="lines">How many lines to produce.</param>
    /// <param name="auditName">Label recorded in the audit log. Set it once with the global <c>--audit-name</c> flag.</param>
    [AgentOperation("export-report", Category = "reports", Examples = ["--lines 3"])]
    public string ExportReport(int lines = 5, string? auditName = null)
    {
        audit.Record(auditName ?? "unnamed");
        var builder = new StringBuilder("# Report\n\n");
        for (var line = 1; line <= lines; line++)
        {
            builder.Append("- line ").Append(line).Append(": \"quoted\" text with <markup> and unicode ✓\n");
        }

        return builder.ToString();
    }

    /// <summary>Fails with a multi-line message that the CLI prints unflattened.</summary>
    [AgentOperation("explain-failure", Category = "reports")]
    public string ExplainFailure() =>
        throw new OperationException("Could not resolve the target.\nCandidates:\n  - alpha\n  - beta");

    /// <summary>Logs a warning (written to stderr) and returns a short result (written to stdout).</summary>
    [AgentOperation("noisy-report", Category = "reports")]
    public string NoisyReport()
    {
        logger.LogWarning("diagnostic noise that must stay off stdout");
        return "done";
    }

    /// <summary>Observes cancellation the way a long-running operation would.</summary>
    [AgentOperation("cancel-me", Category = "reports")]
    public string CancelMe() => throw new OperationCanceledException();

    /// <summary>Only meaningful for MCP clients; hidden from the CLI.</summary>
    [AgentOperation("load-context", Category = "reports", Surfaces = AgentSurfaces.Mcp)]
    public string LoadContext(string name) => $"loaded {name}";
}
