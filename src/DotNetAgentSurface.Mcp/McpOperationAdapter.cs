using System.Reflection;
using System.Security.Claims;
using System.Text.Json;
using DotNetAgentSurface.Core;
using Microsoft.Extensions.Logging;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace DotNetAgentSurface.Mcp;

/// <summary>Exposes an <see cref="OperationCatalog"/> as MCP tool descriptors and invocations.</summary>
public sealed class McpOperationAdapter
{
    private readonly OperationCatalog _catalog;
    private readonly OperationInvoker _invoker;
    private readonly OperationSchemaGenerator _schemaGenerator;
    private readonly OperationInvocationContext? _invocationContext;
    private readonly McpOperationOptions _options;
    private readonly IMcpResultRenderer _defaultRenderer;
    private readonly JsonSerializerOptions _jsonOptions;
    private readonly Func<OperationDescriptor, bool> _useStructuredContent;

    public McpOperationAdapter(
        OperationCatalog catalog,
        OperationInvoker invoker,
        OperationSchemaGenerator? schemaGenerator = null,
        OperationInvocationContext? invocationContext = null,
        McpOperationOptions? options = null)
    {
        _catalog = catalog ?? throw new ArgumentNullException(nameof(catalog));
        _invoker = invoker ?? throw new ArgumentNullException(nameof(invoker));
        _options = options ?? new McpOperationOptions();
        _jsonOptions = _options.JsonSerializerOptions ?? new JsonSerializerOptions(JsonSerializerDefaults.Web);
        _schemaGenerator = schemaGenerator ?? new OperationSchemaGenerator(_jsonOptions);
        _invocationContext = invocationContext;
        var userPredicate = _options.UseStructuredContent;
        _useStructuredContent = operation => operation.PolicyMetadata.OfType<McpStructuredContentHint>().Any() || userPredicate?.Invoke(operation) == true;
        _defaultRenderer = _options.ResultRenderer ?? new DefaultMcpResultRenderer(_jsonOptions, _useStructuredContent);
    }

    /// <summary>The operations of the catalog as SDK tools, ready for <c>McpServerOptions.ToolCollection</c>.</summary>
    public IReadOnlyList<McpServerTool> CreateServerTools() => _catalog.Operations
        .Select(operation => (McpServerTool)new McpOperationTool(this, operation))
        .ToArray();

    public IReadOnlyList<Tool> GetTools() => _catalog.Operations
        .Select(CreateTool)
        .ToArray();

    /// <summary>
    /// Invokes a tool. Supply explicit <paramref name="confirmation"/> metadata for safety-gated tools; absent
    /// metadata fails closed when a confirmation policy is registered.
    /// </summary>
    public async ValueTask<CallToolResult> InvokeAsync(
        string name,
        IReadOnlyDictionary<string, JsonElement>? arguments = null,
        CancellationToken cancellationToken = default,
        OperationConfirmation? confirmation = null,
        OperationInvocationContext? invocationContext = null)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            throw new ArgumentException("Value cannot be null or whitespace.", nameof(name));
        }

        var operation = _catalog.Operations.FirstOrDefault(
            operation => string.Equals(operation.Name, name, StringComparison.OrdinalIgnoreCase)
                || operation.Aliases.Any(alias => string.Equals(alias, name, StringComparison.OrdinalIgnoreCase)));
        if (operation is null)
        {
            return Error($"Unknown operation '{name}'.");
        }

        return await InvokeOperationAsync(operation, arguments, cancellationToken, confirmation, WithSurface(invocationContext ?? _invocationContext)).ConfigureAwait(false);
    }

    internal async ValueTask<CallToolResult> InvokeToolAsync(
        OperationDescriptor operation,
        RequestContext<CallToolRequestParams> request,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var services = new McpRequestServices(request, request.Services ?? request.Server?.Services);
        request.Services = services;

        var baseContext = _invocationContext ?? new OperationInvocationContext();
        var context = baseContext with
        {
            Surface = AgentSurface.Mcp,
            Principal = request.User ?? baseContext.Principal,
            Services = services,
            RequestId = baseContext.RequestId ?? request.JsonRpcRequest?.Id.ToString() ?? Guid.NewGuid().ToString("N"),
        };

        var arguments = request.Params?.Arguments is { } raw ? new Dictionary<string, JsonElement>(raw) : null;
        var confirmation = McpOperationServer.GetConfirmation(request.Params?.Meta);
        if (!confirmation.IsConfirmed && _options.UseElicitationConfirmation)
        {
            confirmation = await ElicitConfirmationAsync(operation, request, cancellationToken).ConfigureAwait(false) ?? confirmation;
        }

        var result = await InvokeOperationAsync(operation, arguments, cancellationToken, confirmation, context).ConfigureAwait(false);
        await services.FlushProgressAsync().ConfigureAwait(false);
        return result;
    }

    private async ValueTask<CallToolResult> InvokeOperationAsync(
        OperationDescriptor operation,
        IReadOnlyDictionary<string, JsonElement>? arguments,
        CancellationToken cancellationToken,
        OperationConfirmation? confirmation,
        OperationInvocationContext context)
    {
        var invocation = await _invoker.InvokeAsync(operation, arguments, cancellationToken, confirmation, context).ConfigureAwait(false);
        if (invocation.IsCancelled)
        {
            return Error("Operation was cancelled.");
        }

        if (!invocation.Succeeded)
        {
            return Error(invocation.Error ?? "Operation failed.");
        }

        var renderer = _options.OperationRenderers.TryGetValue(operation.Name, out var specific) ? specific : _defaultRenderer;
        return renderer.Render(operation, invocation.Value);
    }

    private static async ValueTask<OperationConfirmation?> ElicitConfirmationAsync(
        OperationDescriptor operation,
        RequestContext<CallToolRequestParams> request,
        CancellationToken cancellationToken)
    {
        if (operation.SafetyLevel == AgentSafetyLevel.Safe || request.Server?.ClientCapabilities?.Elicitation is null)
        {
            return null;
        }

        bool approved;
        try
        {
            var result = await request.Server.ElicitAsync(
                new ElicitRequestParams
                {
                    Message = $"The operation '{operation.Name}' is marked '{operation.SafetyLevel}'. {operation.Description} Do you want to run it?",
                    RequestedSchema = new ElicitRequestParams.RequestSchema
                    {
                        Properties = { ["confirm"] = new ElicitRequestParams.BooleanSchema { Title = "Run this operation", Default = false } },
                    },
                },
                cancellationToken).ConfigureAwait(false);

            approved = result.IsAccepted
                && result.Content is { } content
                && content.TryGetValue("confirm", out var answer)
                && answer.ValueKind == JsonValueKind.True;
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            // Elicitation only supplements confirmation: a client that cannot answer leaves the policy to deny as usual.
            return null;
        }

        if (!approved)
        {
            return null;
        }

        return operation.SafetyLevel == AgentSafetyLevel.Dangerous ? OperationConfirmation.DangerousConfirmed : OperationConfirmation.Confirmed;
    }

    internal Tool CreateTool(OperationDescriptor operation)
    {
        using var schema = _schemaGenerator.GenerateInputSchema(operation);
        var hints = ResolveHints(operation);
        return new Tool
        {
            Name = operation.Name,
            Title = hints.Title,
            Description = operation.Description,
            InputSchema = schema.RootElement.Clone(),
            OutputSchema = _useStructuredContent(operation) ? McpOutputSchema.TryCreate(operation, _jsonOptions) : null,
            Annotations = hints.IsEmpty ? null : CreateAnnotations(hints)
        };
    }

    private static OperationInvocationContext WithSurface(OperationInvocationContext? context) =>
        context is null
            ? new OperationInvocationContext { Surface = AgentSurface.Mcp }
            : context with { Surface = AgentSurface.Mcp };

    private static OperationHints ResolveHints(OperationDescriptor operation)
    {
        var hints = operation.Hints;
        if (operation.PolicyMetadata.OfType<McpToolHints>().FirstOrDefault() is { } legacy)
        {
            hints = hints.WithFallback(new OperationHints(legacy.Title, legacy.ReadOnlyHint, legacy.DestructiveHint, legacy.IdempotentHint, legacy.OpenWorldHint));
        }

        return hints;
    }

    private static ToolAnnotations CreateAnnotations(OperationHints hints) => new()
    {
        Title = hints.Title,
        ReadOnlyHint = hints.ReadOnly,
        DestructiveHint = hints.Destructive,
        IdempotentHint = hints.Idempotent,
        OpenWorldHint = hints.OpenWorld
    };

    private static CallToolResult Error(string message) => new()
    {
        IsError = true,
        Content = [new TextContentBlock { Text = message }]
    };
}
