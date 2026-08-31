using System.Text.Json;
using DotNetAgentSurface.Core;
using DotNetAgentSurface.Samples.TaskTracker;

var builder = WebApplication.CreateBuilder(args);
builder.Services.AddSingleton<TaskTrackerService>();
builder.Services.AddSingleton(sp => OperationCatalog.Discover(typeof(TaskTrackerService)));
builder.Services.AddSingleton<OperationInvoker>();

var app = builder.Build();

app.MapGet("/", () => Results.Ok(new
{
    name = "DotNet Agent Surface ASP.NET Core sample",
    operations = new[] { "/operations", "/operations/{name}" }
}));

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
