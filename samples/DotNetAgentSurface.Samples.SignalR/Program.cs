using System.Text.Json;
using DotNetAgentSurface.Core;
using DotNetAgentSurface.SignalR;
using Microsoft.AspNetCore.SignalR;

var builder = WebApplication.CreateBuilder(args);
builder.Services.AddSignalR();

var app = builder.Build();
var catalog = new OperationCatalogBuilder()
    .AddSignalRSendOperations(app.Services.GetRequiredService<IHubContext<NotificationsHub>>())
    .Build();
var invoker = new OperationInvoker(
    app.Services,
    policies: [new DangerousOperationConfirmationPolicy((_, _, _) => ValueTask.FromResult(true))]);

app.MapGet("/", () => Results.Ok(new
{
    name = "DotNet Agent Surface SignalR sample",
    hub = "/notifications",
    operations = catalog.Operations.Select(operation => operation.Name)
}));
app.MapHub<NotificationsHub>("/notifications");

app.MapGet("/operations", () => Results.Ok(catalog.Operations.Select(operation => new
{
    operation.Name,
    operation.Description,
    operation.Category,
    safetyLevel = operation.SafetyLevel.ToString(),
    parameters = operation.Parameters.Select(parameter => parameter.Name)
})));

app.MapPost("/operations/{name}", async (string name, JsonElement? inputs, CancellationToken cancellationToken) =>
{
    var operation = catalog.Operations.SingleOrDefault(operation => string.Equals(operation.Name, name, StringComparison.OrdinalIgnoreCase));
    if (operation is null)
    {
        return Results.NotFound();
    }

    var result = await invoker.InvokeAsync(operation, ToInputs(inputs), cancellationToken);
    return result.Succeeded ? Results.Ok(result.Value) : Results.BadRequest(result.Error);
});

app.Run();

static IReadOnlyDictionary<string, JsonElement>? ToInputs(JsonElement? value)
{
    if (value is not { ValueKind: JsonValueKind.Object } objectValue)
    {
        return null;
    }

    return objectValue.EnumerateObject().ToDictionary(property => property.Name, property => property.Value.Clone(), StringComparer.OrdinalIgnoreCase);
}

public sealed class NotificationsHub : Hub;
