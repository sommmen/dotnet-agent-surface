namespace DotNetAgentSurface.Core;

/// <summary>Identifies the adapter surface that is invoking an operation.</summary>
public enum AgentSurface
{
    /// <summary>The surface is not known, for example when an operation is invoked directly through <see cref="OperationInvoker"/>.</summary>
    Unknown = 0,

    /// <summary>The operation is invoked through the Model Context Protocol adapter.</summary>
    Mcp,

    /// <summary>The operation is invoked through the command-line adapter.</summary>
    Cli,
}
