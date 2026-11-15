using Microsoft.Extensions.Hosting;

namespace DotNetAgentSurface.Hosting;

/// <summary>
/// Marker for an <see cref="IHostedService"/> that must also run in CLI mode. <see cref="AgentSurfaceApplication"/>
/// never starts hosted services for a CLI invocation, because file watchers, process pools, and update checks
/// built for a long-running MCP server are noise or harm in a one-shot command. Implement this interface on a hosted
/// service that the CLI genuinely needs; it is started before the operation runs and stopped afterwards.
/// </summary>
public interface ICliHostedService : IHostedService
{
}
