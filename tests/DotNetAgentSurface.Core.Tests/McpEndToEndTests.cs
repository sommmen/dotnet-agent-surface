using System.ComponentModel;
using System.IO.Pipelines;
using System.Security.Claims;
using System.Text.Json;
using DotNetAgentSurface.Mcp;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using ModelContextProtocol;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace DotNetAgentSurface.Core.Tests;

public sealed class McpEndToEndTests
{
    private static async Task<(IHost Host, McpClient Client)> StartAsync(
        Action<IServiceCollection> configureServices,
        Action<IMcpServerBuilder> configureServer,
        CancellationToken cancellationToken,
        McpClientOptions? clientOptions = null)
    {
        var clientToServer = new Pipe();
        var serverToClient = new Pipe();
        var builder = Host.CreateEmptyApplicationBuilder(new HostApplicationBuilderSettings());
        builder.Logging.SetMinimumLevel(LogLevel.None);
        configureServices(builder.Services);
        configureServer(builder.Services.AddMcpServer().WithStreamServerTransport(clientToServer.Reader.AsStream(), serverToClient.Writer.AsStream()));
        var host = builder.Build();
        await host.StartAsync(cancellationToken);

        var client = await McpClient.CreateAsync(
            new StreamClientTransport(clientToServer.Writer.AsStream(), serverToClient.Reader.AsStream()),
            clientOptions,
            cancellationToken: cancellationToken);
        return (host, client);
    }

    private static async Task<IList<Tool>> ListAsync(McpClient client, CancellationToken cancellationToken) =>
        (await client.ListToolsAsync(cancellationToken: cancellationToken)).Select(tool => tool.ProtocolTool).ToList();

    private static string Text(CallToolResult result) => Assert.IsType<TextContentBlock>(Assert.Single(result.Content)).Text;

    [Fact]
    public async Task Tools_list_matches_the_sdk_for_a_representative_tool_class()
    {
        var ct = TestContext.Current.CancellationToken;
        var (sdkHost, sdkClient) = await StartAsync(_ => { }, server => server.WithTools<ParityTools>(), ct);
        var (adapterHost, adapterClient) = await StartAsync(
            _ => { },
            server => server.WithOperations(catalog => catalog.AddMcpServerTools(
                typeof(ParityTools).Assembly,
                targetFactory: null,
                options =>
                {
                    options.Include = method => method.DeclaringType == typeof(ParityTools);
                    options.ResolveTargetsFromServices = true;
                })),
            ct);
        await using var _ = sdkHost as IAsyncDisposable;
        await using var __ = adapterHost as IAsyncDisposable;

        var differences = McpToolParity.Compare(await ListAsync(sdkClient, ct), await ListAsync(adapterClient, ct));

        Assert.Empty(differences);
        await sdkClient.DisposeAsync();
        await adapterClient.DisposeAsync();
    }

    [Fact]
    public async Task Parity_helper_reports_differences()
    {
        var ct = TestContext.Current.CancellationToken;
        var (sdkHost, sdkClient) = await StartAsync(_ => { }, server => server.WithTools<ParityTools>(), ct);
        await using var _ = sdkHost as IAsyncDisposable;
        var sdkTools = await ListAsync(sdkClient, ct);

        var adapterTools = new McpOperationAdapter(
            new OperationCatalogBuilder().Add("echo", "Different", (string text) => text).Build(),
            new OperationInvoker(new EmptyServices())).GetTools();
        var differences = McpToolParity.Compare(sdkTools, adapterTools);

        Assert.Contains(differences, d => d.Tool == "echo" && d.Field == "description");
        Assert.Contains(differences, d => d.Tool == "echo" && d.Field == "inputSchema");
        Assert.Contains(differences, d => d.Tool == "search" && d.Field == "presence");
        await sdkClient.DisposeAsync();
    }

    [Fact]
    public async Task WithOperations_composes_with_WithTools_and_server_instructions()
    {
        var ct = TestContext.Current.CancellationToken;
        var (host, client) = await StartAsync(
            services => services.AddSingleton<NativeSdkTools>(),
            server =>
            {
                server.WithTools<NativeSdkTools>()
                    .WithOperations(catalog => catalog.AddFromType<GreetingOperations>());
                server.Services.Configure<McpServerOptions>(options => options.ServerInstructions = "Use greet.");
            },
            ct);
        await using var _ = host as IAsyncDisposable;

        var names = (await ListAsync(client, ct)).Select(tool => tool.Name).Order().ToArray();
        var native = await client.CallToolAsync("native", cancellationToken: ct);
        var greet = await client.CallToolAsync("greet", new Dictionary<string, object?> { ["name"] = "Ada" }, cancellationToken: ct);

        Assert.Contains("greet", names);
        Assert.Contains("native", names);
        Assert.Equal("Use greet.", client.ServerInstructions);
        Assert.Equal("native ok", Text(native));
        Assert.Equal("Hello, Ada!", Text(greet));
        await client.DisposeAsync();
    }

    [Fact]
    public async Task String_results_pass_through_and_other_results_use_web_json()
    {
        var ct = TestContext.Current.CancellationToken;
        var (host, client) = await StartAsync(_ => { }, server => server.WithOperations(catalog => catalog.AddFromType<GreetingOperations>()), ct);
        await using var _ = host as IAsyncDisposable;

        var text = await client.CallToolAsync("greet", new Dictionary<string, object?> { ["name"] = "Ada" }, cancellationToken: ct);
        var data = await client.CallToolAsync("data", cancellationToken: ct);
        var passthrough = await client.CallToolAsync("passthrough", cancellationToken: ct);

        Assert.Equal("Hello, Ada!", Text(text));
        Assert.Equal("{\"displayName\":\"Ada\",\"visitCount\":3}", Text(data));
        Assert.Equal("raw", Text(passthrough));
        Assert.True(passthrough.IsError);
        await client.DisposeAsync();
    }

    [Fact]
    public async Task Exceptions_keep_operation_messages_verbatim_and_redact_the_rest()
    {
        var ct = TestContext.Current.CancellationToken;
        var (host, client) = await StartAsync(_ => { }, server => server.WithOperations(catalog => catalog.AddFromType<GreetingOperations>()), ct);
        await using var _ = host as IAsyncDisposable;

        var verbatim = await client.CallToolAsync("verbatim", cancellationToken: ct);
        var mcp = await client.CallToolAsync("mcp-exception", cancellationToken: ct);
        var secret = await client.CallToolAsync("secret", cancellationToken: ct);

        Assert.True(verbatim.IsError);
        Assert.Equal("line one\nline two", Text(verbatim));
        Assert.Equal("from mcp\nsecond line", Text(mcp));
        Assert.True(secret.IsError);
        Assert.DoesNotContain("Server=db;Password=hunter2", Text(secret));
        Assert.Contains("Correlation id:", Text(secret));
        await client.DisposeAsync();
    }

    [Fact]
    public async Task Unexpected_exceptions_are_logged_with_the_correlation_id()
    {
        var ct = TestContext.Current.CancellationToken;
        var sink = new ListLoggerProvider();
        var (host, client) = await StartAsync(
            services => services.AddLogging(logging => logging.SetMinimumLevel(LogLevel.Trace).AddProvider(sink)),
            server => server.WithOperations(catalog => catalog.AddFromType<GreetingOperations>()),
            ct);
        await using var _ = host as IAsyncDisposable;

        var result = await client.CallToolAsync("secret", cancellationToken: ct);

        var id = Text(result).Split("Correlation id: ")[1].TrimEnd('.');
        Assert.Contains(sink.Messages, message => message.Contains(id, StringComparison.Ordinal) && message.Contains("Server=db;Password=hunter2", StringComparison.Ordinal) == false);
        Assert.Contains(sink.Exceptions, exception => exception.Message.Contains("hunter2", StringComparison.Ordinal));
        await client.DisposeAsync();
    }

    [Fact]
    public async Task Ambient_parameters_are_injected_and_excluded_from_the_schema()
    {
        var ct = TestContext.Current.CancellationToken;
        var (host, client) = await StartAsync(
            services => services.AddScoped<ScopedCounter>(),
            server => server.WithOperations(catalog => catalog.AddFromType<AmbientOperations>(), options => options.Filters.Add(new RecordingFilter())),
            ct);
        await using var _ = host as IAsyncDisposable;
        var progressValues = new List<ProgressNotificationValue>();

        var tool = Assert.Single(await ListAsync(client, ct), tool => tool.Name == "ambient");
        var properties = tool.InputSchema.GetProperty("properties");
        var result = await client.CallToolAsync(
            "ambient",
            new Dictionary<string, object?> { ["value"] = "x" },
            new InlineProgress(progressValues),
            cancellationToken: ct);

        Assert.Equal(["value"], properties.EnumerateObject().Select(property => property.Name));
        Assert.Equal("x|Mcp|server=True|ctx=True|scoped=1|id=True", Text(result));
        Assert.Contains(progressValues, value => value.Progress is 1f && value.Message == "half");
        await client.DisposeAsync();
    }

    [Fact]
    public async Task Filters_run_in_order_and_see_failures()
    {
        var ct = TestContext.Current.CancellationToken;
        var events = new List<string>();
        var (host, client) = await StartAsync(
            _ => { },
            server => server.WithOperations(
                catalog => catalog.AddFromType<GreetingOperations>(),
                options =>
                {
                    options.Filters.Add(new OrderFilter("outer", events));
                    options.Filters.Add(new OrderFilter("inner", events));
                }),
            ct);
        await using var _ = host as IAsyncDisposable;

        await client.CallToolAsync("greet", new Dictionary<string, object?> { ["name"] = "Ada" }, cancellationToken: ct);
        await client.CallToolAsync("secret", cancellationToken: ct);

        Assert.Equal(
            [
                "outer:before:greet", "inner:before:greet", "inner:after:greet", "outer:after:greet",
                "outer:before:secret", "inner:before:secret", "outer:error:secret", "inner:error:secret", "inner:after:secret", "outer:after:secret",
            ],
            events);
        await client.DisposeAsync();
    }

    [Fact]
    public async Task Filters_that_never_started_are_not_told_about_the_failure()
    {
        var events = new List<string>();
        var invoker = new OperationInvoker(
            new EmptyServices(),
            options: new OperationInvokerOptions { Filters = { new OrderFilter("first", events), new ThrowingFilter(), new OrderFilter("third", events) } });
        var operation = OperationCatalog.Discover(typeof(GreetingOperations)).Operations.Single(operation => operation.Name == "greet");

        var result = await invoker.InvokeAsync(operation, cancellationToken: TestContext.Current.CancellationToken);

        Assert.False(result.Succeeded);
        Assert.Equal(["first:before:greet", "first:error:greet", "first:after:greet"], events);
    }

    [Fact]
    public async Task Structured_content_is_opt_in_per_operation()
    {
        var ct = TestContext.Current.CancellationToken;
        var (host, client) = await StartAsync(
            _ => { },
            server => server.WithOperations(
                catalog => catalog.AddFromType<GreetingOperations>(),
                options => options.UseStructuredContent = operation => operation.Name == "data"),
            ct);
        await using var _ = host as IAsyncDisposable;

        var tools = await ListAsync(client, ct);
        var data = await client.CallToolAsync("data", cancellationToken: ct);

        Assert.NotNull(tools.Single(tool => tool.Name == "data").OutputSchema);
        Assert.Null(tools.Single(tool => tool.Name == "greet").OutputSchema);
        Assert.Equal("Ada", data.StructuredContent!.Value.GetProperty("displayName").GetString());
        await client.DisposeAsync();
    }

    [Fact]
    public async Task Operation_enforced_mode_runs_gated_operations_without_a_confirmation_policy()
    {
        var ct = TestContext.Current.CancellationToken;
        var (host, client) = await StartAsync(
            _ => { },
            server => server.WithOperations(catalog => catalog.AddFromType<GreetingOperations>(), options => options.ConfirmationMode = OperationConfirmationMode.OperationEnforced),
            ct);
        await using var _ = host as IAsyncDisposable;
        var (strictHost, strictClient) = await StartAsync(_ => { }, server => server.WithOperations(catalog => catalog.AddFromType<GreetingOperations>()), ct);
        await using var __ = strictHost as IAsyncDisposable;

        var allowed = await client.CallToolAsync("gated", cancellationToken: ct);
        var blocked = await strictClient.CallToolAsync("gated", cancellationToken: ct);

        Assert.Equal("done", Text(allowed));
        Assert.True(blocked.IsError);
        Assert.Contains("IConfirmationEnforcingPolicy", Text(blocked));
        await client.DisposeAsync();
        await strictClient.DisposeAsync();
    }

    [Fact]
    public async Task Elicitation_confirms_gated_operations_when_the_client_supports_it()
    {
        var ct = TestContext.Current.CancellationToken;
        var accepted = true;
        var clientOptions = new McpClientOptions
        {
            Capabilities = new ClientCapabilities { Elicitation = new ElicitationCapability { Form = new FormElicitationCapability() } },
            Handlers = new McpClientHandlers
            {
                ElicitationHandler = (request, _) => new ValueTask<ElicitResult>(new ElicitResult
                {
                    Action = accepted ? "accept" : "decline",
                    Content = accepted ? new Dictionary<string, JsonElement> { ["confirm"] = JsonSerializer.SerializeToElement(true) } : null,
                }),
            },
        };
        var (host, client) = await StartAsync(
            _ => { },
            server => server.WithOperations(
                catalog => catalog.AddFromType<GreetingOperations>(),
                options =>
                {
                    options.UseElicitationConfirmation = true;
                    options.Policies.Add(new DangerousOperationConfirmationPolicy());
                }),
            ct,
            clientOptions);
        await using var _ = host as IAsyncDisposable;

        var approved = await client.CallToolAsync("gated", cancellationToken: ct);
        accepted = false;
        var declined = await client.CallToolAsync("gated", cancellationToken: ct);

        Assert.Equal("done", Text(approved));
        Assert.True(declined.IsError);
        Assert.Contains("requires explicit confirmation", Text(declined));
        await client.DisposeAsync();
    }

    [Fact]
    public async Task McpOperationServer_options_serve_the_catalog_through_the_sdk_collection()
    {
        var ct = TestContext.Current.CancellationToken;
        var catalog = OperationCatalog.Discover(typeof(GreetingOperations));
        var adapter = new McpOperationAdapter(catalog, new OperationInvoker(new SingleServices(new GreetingOperations())));
        var options = new McpOperationServer(adapter, "hello there").CreateOptions();
        var clientToServer = new Pipe();
        var serverToClient = new Pipe();
        var transport = new StreamServerTransport(clientToServer.Reader.AsStream(), serverToClient.Writer.AsStream());
        await using var server = McpServer.Create(transport, options);
        var run = server.RunAsync(ct);

        await using var client = await McpClient.CreateAsync(
            new StreamClientTransport(clientToServer.Writer.AsStream(), serverToClient.Reader.AsStream()),
            cancellationToken: ct);
        var tools = await ListAsync(client, ct);
        var viaAlias = await client.CallToolAsync("hello", new Dictionary<string, object?> { ["name"] = "Ada" }, cancellationToken: ct);

        Assert.Contains(tools, tool => tool.Name == "greet");
        Assert.Equal("hello there", client.ServerInstructions);
        Assert.Equal("Hello, Ada!", Text(viaAlias));
        await client.DisposeAsync();
        await server.DisposeAsync();
        await run.WaitAsync(TimeSpan.FromSeconds(5), ct);
    }

    [McpServerToolType]
    public sealed class ParityTools
    {
        [McpServerTool(Name = "echo"), Description("Echoes text")]
        public Task<string> Echo([Description("Text to echo")] string text, CancellationToken cancellationToken) => Task.FromResult(text);

        [McpServerTool(Name = "search", ReadOnly = true, Destructive = false, Title = "Search"), Description("Searches")]
        public string Search(
            [Description("Tags to match")] IReadOnlyList<string> tags,
            Dictionary<string, JsonElement> filters,
            SearchFilter? filter,
            int limit = 10) => "ok";

        [McpServerTool, Description("Counts things")]
        public int CountItems(IList<int> values, bool includeZero = false) => values.Count;
    }

    public sealed record SearchFilter(string Name, int? Max, DateTimeOffset? Since);

    [McpServerToolType]
    public sealed class NativeSdkTools
    {
        [McpServerTool(Name = "native"), Description("Native tool")]
        public string Native() => "native ok";
    }

    public sealed class GreetingOperations
    {
        [AgentOperation("greet", "Greets", Aliases = ["hello"])]
        public string Greet(string name) => $"Hello, {name}!";

        [AgentOperation("data", "Returns data")]
        public DisplayData Data() => new("Ada", 3);

        [AgentOperation("passthrough", "Returns a call result")]
        public CallToolResult Passthrough() => new() { IsError = true, Content = [new TextContentBlock { Text = "raw" }] };

        [AgentOperation("verbatim", "Throws OperationException")]
        public void Verbatim() => throw new OperationException("line one\nline two");

        [AgentOperation("mcp-exception", "Throws McpException")]
        public void McpFailure() => throw new McpException("from mcp\nsecond line");

        [AgentOperation("secret", "Throws a leaky exception")]
        public void Secret() => throw new InvalidOperationException("Server=db;Password=hunter2");

        [AgentOperation("gated", "Gated operation", SafetyLevel = AgentSafetyLevel.Dangerous)]
        public string Gated() => "done";
    }

    public sealed record DisplayData(string DisplayName, int VisitCount);

    public sealed class ScopedCounter
    {
        public int Value { get; set; } = 1;
    }

    public sealed class AmbientOperations
    {
        [AgentOperation("ambient", "Uses ambient parameters")]
        public string Ambient(
            string value,
            McpServer server,
            RequestContext<CallToolRequestParams> context,
            IProgress<ProgressNotificationValue> progress,
            IOperationContext operationContext,
            IServiceProvider services,
            ClaimsPrincipal? principal,
            CancellationToken cancellationToken)
        {
            progress.Report(new ProgressNotificationValue { Progress = 1, Total = 2, Message = "half" });
            var scoped = services.GetRequiredService<ScopedCounter>().Value;
            return $"{value}|{operationContext.Surface}|server={!ReferenceEquals(server, null)}|ctx={!ReferenceEquals(context, null)}|scoped={scoped}|id={operationContext.RequestId.Length > 0}";
        }
    }

    private sealed class InlineProgress(List<ProgressNotificationValue> values) : IProgress<ProgressNotificationValue>
    {
        public void Report(ProgressNotificationValue value)
        {
            lock (values)
            {
                values.Add(value);
            }
        }
    }

    private sealed class ThrowingFilter : OperationInvocationFilter
    {
        public override ValueTask OnInvokingAsync(OperationInvocationFilterContext context) => throw new InvalidOperationException("filter failed");
    }

    private sealed class RecordingFilter : OperationInvocationFilter;

    private sealed class OrderFilter(string name, List<string> events) : IOperationInvocationFilter
    {
        public ValueTask OnInvokingAsync(OperationInvocationFilterContext context)
        {
            events.Add($"{name}:before:{context.Operation.Name}");
            return default;
        }

        public ValueTask<OperationInvocationResult> OnInvokedAsync(OperationInvocationFilterContext context, OperationInvocationResult result)
        {
            events.Add($"{name}:after:{context.Operation.Name}");
            return new(result);
        }

        public ValueTask OnExceptionAsync(OperationInvocationFilterContext context, Exception exception)
        {
            events.Add($"{name}:error:{context.Operation.Name}");
            return default;
        }
    }

    private sealed class EmptyServices : IServiceProvider
    {
        public object? GetService(Type serviceType) => null;
    }

    private sealed class SingleServices(object service) : IServiceProvider
    {
        public object? GetService(Type serviceType) => serviceType.IsInstanceOfType(service) ? service : null;
    }

    private sealed class ListLoggerProvider : ILoggerProvider
    {
        public List<string> Messages { get; } = [];

        public List<Exception> Exceptions { get; } = [];

        public ILogger CreateLogger(string categoryName) => new Sink(this);

        public void Dispose()
        {
        }

        private sealed class Sink(ListLoggerProvider owner) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

            public bool IsEnabled(LogLevel logLevel) => true;

            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
            {
                lock (owner)
                {
                    owner.Messages.Add(formatter(state, exception));
                    if (exception is not null)
                    {
                        owner.Exceptions.Add(exception);
                    }
                }
            }
        }
    }
}
