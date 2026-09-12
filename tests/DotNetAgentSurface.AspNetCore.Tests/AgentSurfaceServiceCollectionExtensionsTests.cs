using DotNetAgentSurface.AspNetCore;
using DotNetAgentSurface.Core;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;

namespace DotNetAgentSurface.AspNetCore.Tests;

public sealed class AgentSurfaceServiceCollectionExtensionsTests
{
    [Fact]
    public async Task Configure_with_service_provider_resolves_app_services_when_registering_operations()
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Services.AddEndpointsApiExplorer();
        builder.Services.AddSingleton(new GreetingService("hello from DI"));
        builder.Services.AddAgentSurfaceFromApiExplorer((catalog, serviceProvider) =>
        {
            var greetingService = serviceProvider.GetRequiredService<GreetingService>();
            catalog.Add(
                "greet",
                "Returns a greeting resolved from the application's service provider.",
                () => greetingService.Greet());
        });
        await using var app = builder.Build();

        var originalOut = Console.Out;
        using var output = new StringWriter();
        Console.SetOut(output);
        try
        {
            var exitCode = await app.RunAgentSurfaceCliAsync(["greet"]);

            Assert.Equal(0, exitCode);
            Assert.Contains("hello from DI", output.ToString(), StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            Console.SetOut(originalOut);
        }
    }

    private sealed class GreetingService(string greeting)
    {
        public string Greet() => greeting;
    }

    [Fact]
    public async Task Runs_cli_without_starting_server_and_sets_ambient_signal()
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Services.AddEndpointsApiExplorer();
        builder.Services.AddAgentSurfaceFromApiExplorer();
        await using var app = builder.Build();
        app.MapGet("/signal", () => Results.Ok(new { inProgress = AgentSurfaceCliInvocation.IsInProgress }));

        var originalOut = Console.Out;
        var originalError = Console.Error;
        using var output = new StringWriter();
        using var error = new StringWriter();
        Console.SetOut(output);
        Console.SetError(error);
        try
        {
            var exitCode = await app.RunAgentSurfaceCliAsync(["aspnetcore", "aspnet_get_signal"]);

            Assert.Equal(0, exitCode);
            Assert.Contains("inProgress", output.ToString(), StringComparison.OrdinalIgnoreCase);
            Assert.Empty(error.ToString());
            Assert.False(AgentSurfaceCliInvocation.IsInProgress);
        }
        finally
        {
            Console.SetOut(originalOut);
            Console.SetError(originalError);
        }
    }

}

