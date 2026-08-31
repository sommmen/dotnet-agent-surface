using System.Text.Json;
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
    public async Task Denies_authorized_endpoint_without_allowing_direct_handler_execution()
    {
        await using var app = CreateApplication();
        app.MapGet("/protected", () => Results.Ok("secret")).RequireAuthorization();
        await app.StartAsync();
        var catalog = Register(app);

        var operation = Assert.Single(catalog.Operations);
        var result = await new OperationInvoker(app.Services).InvokeAsync(operation);

        Assert.False(result.Succeeded);
        Assert.Contains("requires authorization", result.Error!, StringComparison.OrdinalIgnoreCase);
    }

    private static WebApplication CreateApplication()
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Services.AddEndpointsApiExplorer();
        builder.Services.AddControllers().AddApplicationPart(typeof(ControllerEndpoints).Assembly);
        builder.Services.AddAuthorization();
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
}