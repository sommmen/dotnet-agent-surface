namespace DotNetAgentSurface.Core;

/// <summary>Selects the adapter surfaces an operation is exposed on.</summary>
[Flags]
public enum AgentSurfaces
{
    /// <summary>The operation is not exposed on any surface.</summary>
    None = 0,

    /// <summary>The operation is exposed through the Model Context Protocol adapter.</summary>
    Mcp = 1,

    /// <summary>The operation is exposed through the command-line adapter.</summary>
    Cli = 2,

    /// <summary>The operation is exposed on every surface. This is the default.</summary>
    All = Mcp | Cli,
}
