using System.Reflection;

namespace DotNetAgentSurface.Mcp;

/// <summary>How <see cref="McpOperationCatalogBuilderExtensions.AddMcpServerTools(DotNetAgentSurface.Core.OperationCatalogBuilder, Assembly, Func{Type, object?}?, Action{McpToolImportOptions}?)"/> derives <see cref="DotNetAgentSurface.Core.AgentSafetyLevel"/> from SDK tool attributes.</summary>
public enum McpToolSafetyMapping
{
    /// <summary>
    /// Imported tools are <see cref="DotNetAgentSurface.Core.AgentSafetyLevel.Safe"/>. SDK hint attributes such as
    /// <c>Destructive</c> only become MCP tool annotations and never gate invocation.
    /// </summary>
    None = 0,

    /// <summary>
    /// A tool whose <c>McpServerToolAttribute.Destructive</c> is <see langword="true"/> is imported as
    /// <see cref="DotNetAgentSurface.Core.AgentSafetyLevel.Dangerous"/>. The SDK default for that property is
    /// <see langword="true"/> and cannot be told apart from an explicit value, so every tool that does not set
    /// <c>Destructive = false</c> becomes dangerous. Invoking those tools requires a confirmation-enforcing policy.
    /// </summary>
    DestructiveIsDangerous,
}

/// <summary>Options for importing native MCP SDK tools into an operation catalog.</summary>
public sealed class McpToolImportOptions
{
    /// <summary>Gets or sets a predicate selecting which tool methods are imported. By default every tool method is imported.</summary>
    public Func<MethodInfo, bool>? Include { get; set; }

    /// <summary>Gets or sets how SDK hint attributes map to the operation safety level. Defaults to <see cref="McpToolSafetyMapping.None"/>.</summary>
    public McpToolSafetyMapping SafetyMapping { get; set; } = McpToolSafetyMapping.None;
}

/// <summary>
/// MCP tool annotation hints captured from an SDK tool attribute and carried on an imported operation's
/// <see cref="DotNetAgentSurface.Core.OperationDescriptor.PolicyMetadata"/>. A <see langword="null"/> hint is omitted from the tool descriptor.
/// </summary>
/// <param name="Title">The human-readable tool title.</param>
/// <param name="ReadOnlyHint">The read-only hint.</param>
/// <param name="DestructiveHint">The destructive hint.</param>
/// <param name="IdempotentHint">The idempotent hint.</param>
/// <param name="OpenWorldHint">The open-world hint.</param>
public sealed record McpToolHints(
    string? Title = null,
    bool? ReadOnlyHint = null,
    bool? DestructiveHint = null,
    bool? IdempotentHint = null,
    bool? OpenWorldHint = null);
