using System.Text;
using System.Text.Json;
using DotNetAgentSurface.Core;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Mvc.ApiExplorer;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;

namespace DotNetAgentSurface.AspNetCore;

/// <summary>Registers API Explorer endpoints as catalog operations.</summary>
public static class OperationCatalogBuilderExtensions
{
    /// <summary>
    /// Discovers MVC and Minimal API descriptions through ApiExplorer and registers invocations for their resolved route endpoints.
    /// Endpoints carrying authorization metadata are cataloged with their metadata so an authorization policy can
    /// evaluate them before invocation.
    /// </summary>
    public static OperationCatalogBuilder AddFromApiExplorer(
        this OperationCatalogBuilder builder,
        IApiDescriptionGroupCollectionProvider apiExplorer,
        IEnumerable<EndpointDataSource> endpointDataSources,
        IServiceProvider applicationServices)
    {
        if (builder is null)
        {
            throw new ArgumentNullException(nameof(builder));
        }

        if (apiExplorer is null)
        {
            throw new ArgumentNullException(nameof(apiExplorer));
        }

        if (endpointDataSources is null)
        {
            throw new ArgumentNullException(nameof(endpointDataSources));
        }

        if (applicationServices is null)
        {
            throw new ArgumentNullException(nameof(applicationServices));
        }

        var endpoints = endpointDataSources.SelectMany(static source => source.Endpoints).OfType<RouteEndpoint>().ToArray();
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var description in apiExplorer.ApiDescriptionGroups.Items.SelectMany(static group => group.Items)
                     .OrderBy(static description => description.HttpMethod, StringComparer.Ordinal)
                     .ThenBy(static description => description.RelativePath, StringComparer.Ordinal))
        {
            if (string.IsNullOrWhiteSpace(description.HttpMethod) || string.IsNullOrWhiteSpace(description.RelativePath))
            {
                continue;
            }

            var endpoint = FindEndpoint(description, endpoints);
            if (endpoint is null)
            {
                continue;
            }

            var name = CreateUniqueName(description.HttpMethod, description.RelativePath, names);
            var metadata = description.ActionDescriptor.EndpointMetadata.Concat(endpoint.Metadata).ToArray();
            var invocation = new ApiEndpointInvocation(endpoint, description.HttpMethod, applicationServices);
            builder.Add(name, $"Invokes ASP.NET Core {description.HttpMethod} /{description.RelativePath}.",
                (Func<JsonElement?, CancellationToken, Task<AspNetCoreEndpointResponse>>)invocation.InvokeAsync,
                options =>
                {
                    options.Category = "aspnetcore";
                    options.PolicyMetadata.AddRange(metadata);
                    options.InvocationPolicies.Add(new AspNetCoreEndpointAuthorizationPolicy(applicationServices));
                });
        }

        return builder;
    }

    private static RouteEndpoint? FindEndpoint(ApiDescription description, IEnumerable<RouteEndpoint> endpoints)
    {
        var path = NormalizePath(description.RelativePath!);
        return endpoints.FirstOrDefault(endpoint =>
            string.Equals(NormalizePath(endpoint.RoutePattern.RawText), path, StringComparison.OrdinalIgnoreCase) &&
            endpoint.Metadata.GetMetadata<IHttpMethodMetadata>()?.HttpMethods.Contains(description.HttpMethod!, StringComparer.OrdinalIgnoreCase) == true);
    }

    private static string CreateUniqueName(string method, string path, ISet<string> names)
    {
        var stem = $"aspnet_{method}_{path}";
        var name = new string(stem.Select(static character => char.IsLetterOrDigit(character) ? char.ToLowerInvariant(character) : '_').ToArray()).Trim('_');
        if (names.Add(name)) return name;
        for (var suffix = 2; ; suffix++)
        {
            var candidate = $"{name}_{suffix}";
            if (names.Add(candidate)) return candidate;
        }
    }

    private static string NormalizePath(string? path) => "/" + (path ?? string.Empty).Trim('/');
}

/// <summary>Response returned by an in-process anonymous endpoint invocation.</summary>
public sealed record AspNetCoreEndpointResponse(int StatusCode, string? ContentType, string Body);

internal sealed class ApiEndpointInvocation(RouteEndpoint endpoint, string method, IServiceProvider applicationServices)
{
    public async Task<AspNetCoreEndpointResponse> InvokeAsync(JsonElement? body = null, CancellationToken cancellationToken = default)
    {
        await using var scope = applicationServices.CreateAsyncScope();
        var context = new DefaultHttpContext { RequestServices = scope.ServiceProvider };
        context.Request.Method = method;
        context.Request.Path = endpoint.RoutePattern.RawText ?? "/";
        context.RequestAborted = cancellationToken;
        await using var responseBody = new MemoryStream();
        context.Response.Body = responseBody;

        if (body.HasValue && body.Value.ValueKind != JsonValueKind.Null && body.Value.ValueKind != JsonValueKind.Undefined)
        {
            var json = body.Value.GetRawText();
            var bytes = Encoding.UTF8.GetBytes(json);
            context.Request.ContentType = "application/json";
            context.Request.ContentLength = bytes.Length;
            context.Request.Body = new MemoryStream(bytes);
            context.Features.Set<IHttpRequestBodyDetectionFeature>(new RequestBodyDetectionFeature());
        }

        await endpoint.RequestDelegate!(context).ConfigureAwait(false);
        responseBody.Position = 0;
        using var reader = new StreamReader(responseBody, Encoding.UTF8, leaveOpen: true);
        return new AspNetCoreEndpointResponse(context.Response.StatusCode, context.Response.ContentType, await reader.ReadToEndAsync(cancellationToken).ConfigureAwait(false));
    }

    private sealed class RequestBodyDetectionFeature : IHttpRequestBodyDetectionFeature
    {
        public bool CanHaveBody => true;
    }}