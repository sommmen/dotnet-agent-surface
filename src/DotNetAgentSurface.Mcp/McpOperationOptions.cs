using DotNetAgentSurface.Core;
using Microsoft.Extensions.Logging;

namespace DotNetAgentSurface.Mcp;

/// <summary>Settings for exposing an operation catalog over MCP.</summary>
public sealed class McpOperationOptions
{
    /// <summary>
    /// The options used to bind inputs, generate schemas and render results. When <see langword="null"/> the MCP SDK's own
    /// defaults (<c>McpJsonUtilities.DefaultOptions</c>) are used, which is what <c>WithTools&lt;T&gt;()</c> uses and what makes
    /// schemas match it. The adapter constructed directly over an <see cref="OperationInvoker"/> defaults to
    /// <see cref="System.Text.Json.JsonSerializerDefaults.Web"/>.
    /// </summary>
    public System.Text.Json.JsonSerializerOptions? JsonSerializerOptions { get; set; }

    /// <summary>The renderer for every operation that has no entry in <see cref="OperationRenderers"/>. Defaults to <see cref="DefaultMcpResultRenderer"/>.</summary>
    public IMcpResultRenderer? ResultRenderer { get; set; }

    /// <summary>Renderers for individual operations, keyed by operation name.</summary>
    public IDictionary<string, IMcpResultRenderer> OperationRenderers { get; } = new Dictionary<string, IMcpResultRenderer>(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Decides which operations also publish an output schema and structured content. Opt-in: by default only operations
    /// imported from <c>[McpServerTool(UseStructuredContent = true)]</c> do.
    /// </summary>
    public Func<OperationDescriptor, bool>? UseStructuredContent { get; set; }

    /// <summary>
    /// Asks the user through MCP elicitation to approve <see cref="AgentSafetyLevel.Confirm"/>/<see cref="AgentSafetyLevel.Dangerous"/>
    /// operations when the request carries no confirmation metadata and the client supports elicitation. Requires a
    /// confirmation-enforcing policy (or enables confirmation for operations that have one).
    /// </summary>
    public bool UseElicitationConfirmation { get; set; }

    /// <summary>Who enforces safety gating. Applies to the invoker the builder creates.</summary>
    public OperationConfirmationMode ConfirmationMode { get; set; }

    /// <summary>Policies for the invoker the builder creates, in addition to <see cref="IOperationInvocationPolicy"/> services in DI.</summary>
    public IList<IOperationInvocationPolicy> Policies { get; } = new List<IOperationInvocationPolicy>();

    /// <summary>Filters for the invoker the builder creates, in addition to <see cref="IOperationInvocationFilter"/> services in DI.</summary>
    public IList<IOperationInvocationFilter> Filters { get; } = new List<IOperationInvocationFilter>();

    /// <summary>Overrides the <see cref="IOperationExceptionMapper"/> (otherwise the DI service, then <see cref="DefaultOperationExceptionMapper"/>).</summary>
    public IOperationExceptionMapper? ExceptionMapper { get; set; }

    /// <summary>The logger for invocation scopes and unexpected exceptions. Defaults to one created from <see cref="ILoggerFactory"/> in DI.</summary>
    public ILogger? Logger { get; set; }
}

/// <summary>Marks an imported operation as publishing structured content (<c>[McpServerTool(UseStructuredContent = true)]</c>).</summary>
public sealed record McpStructuredContentHint;
