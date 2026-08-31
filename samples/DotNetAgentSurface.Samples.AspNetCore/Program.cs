using System.Text.Json;
using DotNetAgentSurface.AspNetCore;
using DotNetAgentSurface.Core;
using DotNetAgentSurface.Samples.TaskTracker;
using Microsoft.AspNetCore.Mvc.ApiExplorer;
using Microsoft.AspNetCore.Routing;

var builder = WebApplication.CreateBuilder(args);
builder.Services.AddSingleton<TaskTrackerService>();
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddAuthorization();

// The catalog is resolved lazily on first request (from the /operations endpoints below), which happens
// after the routes mapped further down have been added to the application's EndpointDataSource. This lets
// a single catalog combine attribute-discovered TaskTracker operations with ApiExplorer-discovered minimal
// API routes without needing to start the host manually before building the catalog.
builder.Services.AddSingleton(sp => new OperationCatalogBuilder()
    .AddFromType<TaskTrackerService>()
    .AddFromApiExplorer(
        sp.GetRequiredService<IApiDescriptionGroupCollectionProvider>(),
        sp.GetServices<EndpointDataSource>(),
        sp)
    .Build());
builder.Services.AddSingleton<OperationInvoker>();

var app = builder.Build();

app.MapGet("/", () => Results.Ok(new
{
    name = "DotNet Agent Surface ASP.NET Core sample",
    operations = new[] { "/operations", "/operations/{name}" }
}));

// Demo endpoints that exercise the ApiExplorer discovery satellite: an anonymous endpoint that the catalog
// can invoke directly, and an authorization-protected endpoint that is cataloged but always denies invocation
// because the Core invocation pipeline does not carry an authenticated caller context (see development.md).
app.MapGet("/demo/ping", () => Results.Ok(new { message = "pong" }));
app.MapGet("/demo/secret", () => Results.Ok("secret")).RequireAuthorization();

app.MapGet("/operations", (OperationCatalog catalog) => Results.Ok(catalog.Operations.Select(ToResponse)));

app.MapGet("/operations/{name}", (string name, OperationCatalog catalog) =>
{
    var operation = catalog.Operations.SingleOrDefault(operation => string.Equals(operation.Name, name, StringComparison.OrdinalIgnoreCase));
    return operation is null ? Results.NotFound() : Results.Ok(ToResponse(operation));
});

app.MapPost("/operations/{name}", async (string name, JsonElement? inputs, OperationCatalog catalog, OperationInvoker invoker, CancellationToken cancellationToken) =>
{
    var operation = catalog.Operations.SingleOrDefault(operation => string.Equals(operation.Name, name, StringComparison.OrdinalIgnoreCase));
    if (operation is null)
    {
        return Results.NotFound();
    }

    var result = await invoker.InvokeAsync(operation, ToInputs(inputs), cancellationToken);
    return result.Succeeded
        ? Results.Ok(result.Value)
        : result.IsCancelled
            ? Results.StatusCode(StatusCodes.Status499ClientClosedRequest)
            : Results.BadRequest(new { error = result.Error });
});

app.Run();

static IReadOnlyDictionary<string, JsonElement>? ToInputs(JsonElement? inputs)
{
    if (inputs is not { ValueKind: JsonValueKind.Object } inputObject)
    {
        return null;
    }

    return inputObject.EnumerateObject().ToDictionary(property => property.Name, property => property.Value, StringComparer.OrdinalIgnoreCase);
}

static object ToResponse(OperationDescriptor operation) => new
{
    operation.Name,
    operation.Description,
    operation.Category,
    safetyLevel = operation.SafetyLevel.ToString(),
    operation.IsIdempotent,
    parameters = operation.Parameters.Select(parameter => new
    {
        parameter.Name,
        type = parameter.ParameterType.Name,
        parameter.IsOptional,
        parameter.IsNullable,
        parameter.DefaultValue
    })
};
