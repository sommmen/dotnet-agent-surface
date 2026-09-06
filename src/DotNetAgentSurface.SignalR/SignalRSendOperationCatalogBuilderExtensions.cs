using System.Text.Json;
using DotNetAgentSurface.Core;
using Microsoft.AspNetCore.SignalR;

namespace DotNetAgentSurface.SignalR;

/// <summary>Registers stable server-to-client SignalR send operations.</summary>
public static class SignalRSendOperationCatalogBuilderExtensions
{
    /// <summary>
    /// Adds operations for sending messages to all clients, a group, a user, or a connection through a hub context.
    /// </summary>
    public static OperationCatalogBuilder AddSignalRSendOperations<THub>(
        this OperationCatalogBuilder builder,
        IHubContext<THub> hubContext,
        Action<SignalROperationsOptions>? configure = null)
        where THub : Hub
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(hubContext);

        var options = new SignalROperationsOptions();
        configure?.Invoke(options);

        if (string.IsNullOrWhiteSpace(options.Category))
        {
            throw new ArgumentException("The SignalR operation category must not be empty.", nameof(configure));
        }

        builder.Add(
            "send-signalr-all",
            "Sends a client method invocation to every connected client.",
            async (string methodName, JsonElement[] arguments, CancellationToken cancellationToken) =>
                await SendAsync(() => hubContext.Clients.All, "all", methodName, arguments, cancellationToken).ConfigureAwait(false),
            registration => Configure(registration, options));

        builder.Add(
            "send-signalr-group",
            "Sends a client method invocation to every connection in a SignalR group.",
            async (string groupName, string methodName, JsonElement[] arguments, CancellationToken cancellationToken) =>
            {
                RequireValue(groupName, nameof(groupName));
                return await SendAsync(() => hubContext.Clients.Group(groupName), $"group:{groupName}", methodName, arguments, cancellationToken).ConfigureAwait(false);
            },
            registration => Configure(registration, options));

        builder.Add(
            "send-signalr-user",
            "Sends a client method invocation to every connection for a SignalR user.",
            async (string userId, string methodName, JsonElement[] arguments, CancellationToken cancellationToken) =>
            {
                RequireValue(userId, nameof(userId));
                return await SendAsync(() => hubContext.Clients.User(userId), $"user:{userId}", methodName, arguments, cancellationToken).ConfigureAwait(false);
            },
            registration => Configure(registration, options));

        builder.Add(
            "send-signalr-client",
            "Sends a client method invocation to a specific SignalR connection.",
            async (string connectionId, string methodName, JsonElement[] arguments, CancellationToken cancellationToken) =>
            {
                RequireValue(connectionId, nameof(connectionId));
                return await SendAsync(() => hubContext.Clients.Client(connectionId), $"client:{connectionId}", methodName, arguments, cancellationToken).ConfigureAwait(false);
            },
            registration => Configure(registration, options));

        return builder;
    }

    private static void Configure(OperationRegistrationOptions registration, SignalROperationsOptions options)
    {
        registration.Category = options.Category;
        registration.SafetyLevel = options.SendSafetyLevel;
    }

    /// <remarks>
    /// The <paramref name="clientFactory"/> resolves the <see cref="IClientProxy"/> lazily, after
    /// <paramref name="methodName"/> (and any target identifier, validated by the caller) has been checked. This
    /// avoids invoking hub-context routing (which may have observable side effects, such as group/user proxy
    /// resolution) for a request that fails input validation.
    /// </remarks>
    private static async Task<SignalRSendResult> SendAsync(
        Func<IClientProxy> clientFactory,
        string target,
        string methodName,
        JsonElement[] arguments,
        CancellationToken cancellationToken)
    {
        RequireValue(methodName, nameof(methodName));
        ArgumentNullException.ThrowIfNull(arguments);

        var client = clientFactory();
        await client.SendCoreAsync(methodName, arguments.Cast<object?>().ToArray(), cancellationToken).ConfigureAwait(false);
        return new SignalRSendResult(target, methodName, arguments.Length, DateTimeOffset.UtcNow);
    }

    private static string RequireValue(string value, string parameterName)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new ArgumentException($"A value is required for '{parameterName}'.", parameterName);
        }

        return value;
    }
}
