using System.Text.Json;
using DotNetAgentSurface.Core;
using DotNetAgentSurface.SignalR;
using Microsoft.AspNetCore.SignalR;

namespace DotNetAgentSurface.SignalR.Tests;

public sealed class SignalRSendOperationCatalogBuilderExtensionsTests
{
    [Fact]
    public void AddSignalRSendOperations_registers_expected_metadata_without_sending()
    {
        var context = new RecordingHubContext();

        var catalog = new OperationCatalogBuilder().AddSignalRSendOperations(context).Build();

        Assert.Equal(
            ["send-signalr-all", "send-signalr-client", "send-signalr-group", "send-signalr-user"],
            catalog.Operations.Select(operation => operation.Name).Order());
        Assert.All(catalog.Operations, operation =>
        {
            Assert.Equal("SignalR", operation.Category);
            Assert.Equal(AgentSafetyLevel.Confirm, operation.SafetyLevel);
        });
        Assert.Empty(context.Proxies);
    }

    [Fact]
    public void AddSignalRSendOperations_applies_configured_metadata()
    {
        var catalog = new OperationCatalogBuilder()
            .AddSignalRSendOperations(new RecordingHubContext(), options =>
            {
                options.Category = "Notifications";
                options.SendSafetyLevel = AgentSafetyLevel.Safe;
            })
            .Build();

        Assert.All(catalog.Operations, operation =>
        {
            Assert.Equal("Notifications", operation.Category);
            Assert.Equal(AgentSafetyLevel.Safe, operation.SafetyLevel);
        });
    }

    [Theory]
    [InlineData("send-signalr-all", "all", null)]
    [InlineData("send-signalr-group", "group:operators", "groupName")]
    [InlineData("send-signalr-user", "user:alice", "userId")]
    [InlineData("send-signalr-client", "client:connection-1", "connectionId")]
    public async Task Send_operations_route_method_arguments_and_cancellation(
        string operationName,
        string expectedTarget,
        string? targetInputName)
    {
        var context = new RecordingHubContext();
        var catalog = new OperationCatalogBuilder().AddSignalRSendOperations(context).Build();
        using var cancellationSource = new CancellationTokenSource();
        var inputs = new Dictionary<string, JsonElement>
        {
            ["methodName"] = Json("notify"),
            ["arguments"] = JsonRaw("[{\"message\":\"ready\"},2]")
        };
        if (targetInputName is not null)
        {
            inputs[targetInputName] = Json(expectedTarget[(expectedTarget.IndexOf(':') + 1)..]);
        }

        var result = await InvokeAsync(catalog, operationName, inputs, cancellationSource.Token);

        Assert.True(result.Succeeded);
        var acknowledgement = Assert.IsType<SignalRSendResult>(result.Value);
        Assert.Equal(expectedTarget, acknowledgement.Target);
        Assert.Equal("notify", acknowledgement.MethodName);
        Assert.Equal(2, acknowledgement.ArgumentCount);
        Assert.Equal(TimeSpan.Zero, acknowledgement.AcceptedAt.Offset);
        var proxy = Assert.Single(context.Proxies);
        Assert.Equal("notify", proxy.Method);
        Assert.Equal(2, proxy.Arguments.Length);
        Assert.Equal(JsonValueKind.Object, Assert.IsType<JsonElement>(proxy.Arguments[0]).ValueKind);
        Assert.Equal(2, Assert.IsType<JsonElement>(proxy.Arguments[1]).GetInt32());
        Assert.Equal(cancellationSource.Token, proxy.CancellationToken);
    }

    [Theory]
    [InlineData("send-signalr-all", "methodName", "")]
    [InlineData("send-signalr-group", "groupName", " ")]
    [InlineData("send-signalr-user", "userId", "")]
    [InlineData("send-signalr-client", "connectionId", "")]
    public async Task Send_operations_reject_empty_required_values(string operationName, string inputName, string value)
    {
        var context = new RecordingHubContext();
        var catalog = new OperationCatalogBuilder().AddSignalRSendOperations(context).Build();
        var inputs = new Dictionary<string, JsonElement>
        {
            ["methodName"] = Json("notify"),
            ["arguments"] = JsonRaw("[]"),
            [inputName] = Json(value)
        };

        var result = await InvokeAsync(catalog, operationName, inputs);

        Assert.False(result.Succeeded);
        Assert.Empty(context.Proxies);
    }

    [Fact]
    public void AddSignalRSendOperations_rejects_empty_category()
    {
        Assert.Throws<ArgumentException>(() => new OperationCatalogBuilder().AddSignalRSendOperations(
            new RecordingHubContext(),
            options => options.Category = " "));
    }

    private static ValueTask<OperationInvocationResult> InvokeAsync(
        OperationCatalog catalog,
        string operationName,
        IReadOnlyDictionary<string, JsonElement> inputs,
        CancellationToken cancellationToken = default)
    {
        var operation = Assert.Single(catalog.Operations, operation => operation.Name == operationName);
        return new OperationInvoker(
            new NullServiceProvider(),
            policies: [new DangerousOperationConfirmationPolicy((_, _, _) => ValueTask.FromResult(true))])
            .InvokeAsync(operation, inputs, cancellationToken);
    }

    private static JsonElement Json(string value) => JsonSerializer.SerializeToElement(value);

    private static JsonElement JsonRaw(string rawJson) => JsonDocument.Parse(rawJson).RootElement.Clone();

    private sealed class TestHub : Hub;

    private sealed class RecordingHubContext : IHubContext<TestHub>
    {
        public List<RecordingClientProxy> Proxies { get; } = [];
        public IHubClients Clients => new RecordingHubClients(Proxies);
        public IGroupManager Groups { get; } = new NoOpGroupManager();
    }

    private sealed class RecordingHubClients(List<RecordingClientProxy> proxies) : IHubClients
    {
        public IClientProxy All => Create("all");
        public IClientProxy AllExcept(IReadOnlyList<string> excludedConnectionIds) => Create("all-except");
        public IClientProxy Client(string connectionId) => Create($"client:{connectionId}");
        public IClientProxy Clients(IReadOnlyList<string> connectionIds) => Create("clients");
        public IClientProxy Group(string groupName) => Create($"group:{groupName}");
        public IClientProxy GroupExcept(string groupName, IReadOnlyList<string> excludedConnectionIds) => Create("group-except");
        public IClientProxy Groups(IReadOnlyList<string> groupNames) => Create("groups");
        public IClientProxy User(string userId) => Create($"user:{userId}");
        public IClientProxy Users(IReadOnlyList<string> userIds) => Create("users");
        private IClientProxy Create(string target)
        {
            var proxy = new RecordingClientProxy(target);
            proxies.Add(proxy);
            return proxy;
        }
    }

    private sealed class RecordingClientProxy(string target) : IClientProxy
    {
        public string Target { get; } = target;
        public string? Method { get; private set; }
        public object[] Arguments { get; private set; } = [];
        public CancellationToken CancellationToken { get; private set; }
        public Task SendCoreAsync(string method, object?[] args, CancellationToken cancellationToken = default)
        {
            Method = method;
            Arguments = args!;
            CancellationToken = cancellationToken;
            return Task.CompletedTask;
        }
    }

    private sealed class NoOpGroupManager : IGroupManager
    {
        public Task AddToGroupAsync(string connectionId, string groupName, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task RemoveFromGroupAsync(string connectionId, string groupName, CancellationToken cancellationToken = default) => Task.CompletedTask;
    }

    private sealed class NullServiceProvider : IServiceProvider
    {
        public object? GetService(Type serviceType) => null;
    }
}
