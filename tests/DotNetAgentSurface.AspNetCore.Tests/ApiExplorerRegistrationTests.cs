using System.Text.Json;
using System.Security.Claims;
using DotNetAgentSurface.AspNetCore;
using DotNetAgentSurface.Core;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.ApiExplorer;
using Microsoft.Extensions.DependencyInjection;

namespace DotNetAgentSurface.AspNetCore.Tests;

public sealed class ApiExplorerRegistrationTests
{
    [Fact]
    public async Task Discovers_minimal_and_mvc_endpoints_and_invokes_anonymous_endpoint()
    {
        await using var app = CreateApplication();
        app.MapPost("/minimal", ([FromBody] Payload payload) => Results.Ok(new { value = payload.Value }));
        app.MapControllers();
        await app.StartAsync();

        var catalog = Register(app);
        Assert.Contains(catalog.Operations, operation => operation.Name == "aspnet_post_minimal");
        Assert.Contains(catalog.Operations, operation => operation.Name == "aspnet_get_controller");

        using var document = JsonDocument.Parse("{\"value\":\"ok\"}");
        var minimal = catalog.Operations.Single(operation => operation.Name == "aspnet_post_minimal");
        var result = await new OperationInvoker(app.Services).InvokeAsync(minimal, new Dictionary<string, JsonElement> { ["body"] = document.RootElement.Clone() });

        Assert.True(result.Succeeded, result.Error);
        var response = Assert.IsType<AspNetCoreEndpointResponse>(result.Value);
        Assert.True(response.StatusCode == 200, response.Body);
        Assert.Contains("ok", response.Body);
    }

    [Fact]
    public async Task Invokes_parameterized_minimal_api_endpoint_with_route_and_query_inputs()
    {
        await using var app = CreateApplication();
        app.MapGet("/items/{id}", (string id, [FromQuery] string q) => Results.Ok(new { id, q }));
        await app.StartAsync();
        var catalog = Register(app);

        var operation = Assert.Single(catalog.Operations);
        Assert.Collection(
            operation.Parameters,
            parameter => Assert.Equal("id", parameter.Name),
            parameter => Assert.Equal("q", parameter.Name),
            parameter => Assert.True(parameter.IsCancellationToken));

        var result = await new OperationInvoker(app.Services).InvokeAsync(
            operation,
            new Dictionary<string, JsonElement>
            {
                ["id"] = JsonSerializer.SerializeToElement("42"),
                ["q"] = JsonSerializer.SerializeToElement("hello world")
            });

        Assert.True(result.Succeeded, result.Error);
        var response = Assert.IsType<AspNetCoreEndpointResponse>(result.Value);
        Assert.Equal(200, response.StatusCode);
        Assert.Contains("\"id\":\"42\"", response.Body);
        Assert.Contains("\"q\":\"hello world\"", response.Body);
    }

    [Fact]
    public async Task Invokes_parameterized_mvc_endpoint_with_route_input()
    {
        await using var app = CreateApplication();
        app.MapControllers();
        await app.StartAsync();
        var catalog = Register(app);

        var operation = catalog.Operations.Single(o => o.Name == "aspnet_get_controller__id");
        Assert.Collection(
            operation.Parameters,
            parameter => Assert.Equal("id", parameter.Name),
            parameter => Assert.True(parameter.IsCancellationToken));

        var result = await new OperationInvoker(app.Services).InvokeAsync(
            operation,
            new Dictionary<string, JsonElement> { ["id"] = JsonSerializer.SerializeToElement("42") });

        Assert.True(result.Succeeded, result.Error);
        var response = Assert.IsType<AspNetCoreEndpointResponse>(result.Value);
        Assert.Equal(200, response.StatusCode);
        Assert.Contains("\"id\":\"42\"", response.Body);
    }

    [Fact]
    public async Task Invokes_mvc_controller_endpoint_whose_route_pattern_has_no_leading_slash()
    {
        // Controller route patterns (e.g. "controller") are reported by ApiExplorer/RouteEndpoint without a
        // leading '/', unlike minimal API routes. Assigning that raw text directly to HttpContext.Request.Path
        // previously threw ArgumentException ("The path in 'value' must start with '/'").
        await using var app = CreateApplication();
        app.MapControllers();
        await app.StartAsync();
        var catalog = Register(app);

        var operation = catalog.Operations.Single(candidate => candidate.Name == "aspnet_get_controller");
        var result = await new OperationInvoker(app.Services).InvokeAsync(operation);

        Assert.True(result.Succeeded, result.Error);
        Assert.Equal(200, Assert.IsType<AspNetCoreEndpointResponse>(result.Value).StatusCode);
    }

    [Fact]
    public async Task Fallback_registration_excludes_parameterized_minimal_api_endpoints()
    {
        // Parameterized endpoints like /items/{id} require route parameters from API Explorer descriptions.
        // The fallback mechanism only registers parameterless endpoints, so /items/{id} should not appear.
        // This test verifies that parameterized routes are omitted and don't mislead consumers.
        // We mark both endpoints ExcludeFromDescription() so ApiExplorer genuinely does not describe them,
        // forcing the test to exercise the fallback registration code path.
        await using var app = CreateApplication();
        app.MapGet("/items", () => Results.Ok(new { items = new[] { 1, 2, 3 } })).ExcludeFromDescription(); // Parameterless
        app.MapGet("/items/{id}", (string id) => Results.Ok(new { id })).ExcludeFromDescription(); // Parameterized (should be excluded)
        await app.StartAsync();

        var catalog = Register(app);

        // /items (parameterless) should be registered via fallback since it has no route parameters
        var parameterless = catalog.Operations.FirstOrDefault(op => op.Name == "aspnet_get_items");
        Assert.NotNull(parameterless);

        // /items/{id} should NOT be registered via fallback because it has a route parameter
        // Verify by checking that no operation with a parameterized-route-like name is present
        var operation = Assert.Single(catalog.Operations);
        Assert.Equal("aspnet_get_items", operation.Name);
    }

    [Fact]
    public async Task Denies_authorized_endpoint_without_allowing_direct_handler_execution()
    {
        await using var app = CreateApplication();
        app.MapGet("/protected", () => Results.Ok("secret")).RequireAuthorization();
        await app.StartAsync();
        var catalog = Register(app);

        var operation = Assert.Single(catalog.Operations);
        var result = await new OperationInvoker(app.Services).InvokeAsync(operation);

        Assert.False(result.Succeeded);
        Assert.Contains("authenticated caller", result.Error!, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Invokes_authorized_endpoint_with_authenticated_context()
    {
        await using var app = CreateApplication();
        app.MapGet("/protected", () => Results.Ok("secret")).RequireAuthorization();
        await app.StartAsync();
        var operation = Assert.Single(Register(app).Operations);
        var principal = new ClaimsPrincipal(new ClaimsIdentity("test"));

        var result = await new OperationInvoker(app.Services)
            .InvokeAsync(operation, invocationContext: new OperationInvocationContext(principal));

        Assert.True(result.Succeeded, result.Error);
        Assert.Contains("secret", Assert.IsType<AspNetCoreEndpointResponse>(result.Value).Body);
    }

    [Fact]
    public async Task Allows_anonymous_metadata_to_override_authorization()
    {
        await using var app = CreateApplication();
        app.MapGet("/public", () => Results.Ok("public")).RequireAuthorization().AllowAnonymous();
        await app.StartAsync();
        var operation = Assert.Single(Register(app).Operations);

        var result = await new OperationInvoker(app.Services).InvokeAsync(operation);

        Assert.True(result.Succeeded, result.Error);
        Assert.Contains("public", Assert.IsType<AspNetCoreEndpointResponse>(result.Value).Body);
    }

    [Fact]
    public async Task Evaluates_named_authorization_policy_against_forwarded_claims()
    {
        await using var app = CreateApplication();
        app.MapGet("/scoped", () => Results.Ok("scoped")).RequireAuthorization("scope");
        await app.StartAsync();
        var operation = Assert.Single(Register(app).Operations);
        var principal = new ClaimsPrincipal(new ClaimsIdentity(
            [new Claim("scope", "read")],
            "test"));

        var result = await new OperationInvoker(app.Services)
            .InvokeAsync(operation, invocationContext: new OperationInvocationContext(principal));

        Assert.True(result.Succeeded, result.Error);
        Assert.Contains("scoped", Assert.IsType<AspNetCoreEndpointResponse>(result.Value).Body);
    }

    private static WebApplication CreateApplication()
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Services.AddEndpointsApiExplorer();
        builder.Services.AddControllers().AddApplicationPart(typeof(ControllerEndpoints).Assembly);
        builder.Services.AddAuthorization(options =>
            options.AddPolicy("scope", policy => policy.RequireClaim("scope", "read")));
        return builder.Build();
    }

    private static OperationCatalog Register(WebApplication app) => new OperationCatalogBuilder()
        .AddFromApiExplorer(app.Services.GetRequiredService<IApiDescriptionGroupCollectionProvider>(), app.Services.GetServices<Microsoft.AspNetCore.Routing.EndpointDataSource>(), app.Services)
        .Build();

    private sealed record Payload(string Value);
}

[ApiController]
public sealed class ControllerEndpoints : ControllerBase
{
    [HttpGet("controller")]
    public IActionResult Get() => Ok();

    [HttpGet("controller/{id}")]
    public IActionResult GetById(string id) => Ok(new { id });
}