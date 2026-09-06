using System.Linq.Expressions;
using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using DotNetAgentSurface.Core;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Http.Extensions;
using Microsoft.AspNetCore.Mvc.ApiExplorer;
using Microsoft.AspNetCore.Mvc.ModelBinding;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;

[assembly: InternalsVisibleTo("DotNetAgentSurface.AspNetCore.Tests")]
[assembly: InternalsVisibleTo("DotNetAgentSurface.AspNetCore.Invocation.*")]

namespace DotNetAgentSurface.AspNetCore;

/// <summary>Registers API Explorer endpoints as catalog operations.</summary>
public static class OperationCatalogBuilderExtensions
{
    /// <summary>
    /// Discovers MVC and Minimal API descriptions through ApiExplorer and registers invocations for their resolved route endpoints.
    /// Endpoints carrying authorization metadata are cataloged with their metadata so an authorization policy can
    /// evaluate them before invocation. Map all routes before calling this method, because it reads the current
    /// <paramref name="endpointDataSources"/> immediately. For DI registration, prefer
    /// <see cref="AgentSurfaceServiceCollectionExtensions.AddAgentSurfaceFromApiExplorer(IServiceCollection, Action{OperationCatalogBuilder}?)"/>,
    /// which defers construction until the catalog is resolved.
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
        var discoveredEndpoints = new HashSet<RouteEndpoint>();

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

            discoveredEndpoints.Add(endpoint);
            var name = CreateUniqueName(description.HttpMethod, description.RelativePath, names);
            var metadata = description.ActionDescriptor.EndpointMetadata.Concat(endpoint.Metadata).ToArray();
            var invocation = new ApiEndpointInvocation(endpoint, description.HttpMethod, applicationServices);
            builder.Add(name, $"Invokes ASP.NET Core {description.HttpMethod} /{description.RelativePath}.",
                invocation.CreateDelegate(description.ParameterDescriptions),
                options =>
                {
                    options.Category = "aspnetcore";
                    options.PolicyMetadata.AddRange(metadata);
                    options.InvocationPolicies.Add(new AspNetCoreEndpointAuthorizationPolicy(applicationServices));
                });
        }

        // Minimal API descriptions are populated when the host starts. The in-process CLI deliberately
        // avoids starting Kestrel, so register simple endpoint fallbacks that API Explorer has not yet exposed.
        // Only register parameterless endpoints; parameterized routes require API Explorer descriptions.
        foreach (var endpoint in endpoints.Where(endpoint => !discoveredEndpoints.Contains(endpoint) && !endpoint.RoutePattern.Parameters.Any()))
        {
            var methods = endpoint.Metadata.GetMetadata<IHttpMethodMetadata>()?.HttpMethods;
            if (methods is null)
            {
                continue;
            }

            foreach (var httpMethod in methods.Where(static method => !string.Equals(method, "OPTIONS", StringComparison.OrdinalIgnoreCase)))
            {
                var path = endpoint.RoutePattern.RawText?.Trim('/');
                if (string.IsNullOrWhiteSpace(path))
                {
                    continue;
                }

                var invocation = new ApiEndpointInvocation(endpoint, httpMethod, applicationServices);
                builder.Add(CreateUniqueName(httpMethod, path, names), $"Invokes ASP.NET Core {httpMethod} /{path}.",
                    invocation.CreateDelegate([]), options =>
                    {
                        options.Category = "aspnetcore";
                        options.PolicyMetadata.AddRange(endpoint.Metadata);
                        options.InvocationPolicies.Add(new AspNetCoreEndpointAuthorizationPolicy(applicationServices));
                    });
            }
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
    private IReadOnlyList<InvocationInput> _inputs = [];

   /// <summary>Creates a wrapper delegate that can be called from dynamically generated code to invoke this endpoint.</summary>
    internal Func<object?[], CancellationToken, Task<AspNetCoreEndpointResponse>> CreateInvocationWrapper()
    {
        return async (inputs, ct) => await InvokeAsync(inputs, ct);
    }

    /// <summary>Creates a delegate for this endpoint that accepts JsonElement parameters and returns AspNetCoreEndpointResponse.</summary>
    internal Delegate CreateDelegate(IList<ApiParameterDescription> parameters)
    {
        if (!RuntimeFeature.IsDynamicCodeSupported)
        {
            throw new PlatformNotSupportedException(
                $"Cannot create delegate for endpoint '{endpoint.DisplayName}': dynamic code generation is not supported in this runtime environment (e.g., Native AOT, constrained hosts). " +
                "Ensure the application runs in an environment that supports System.Reflection.Emit.");
        }

        var inputs = parameters
            .Where(static parameter => !string.IsNullOrWhiteSpace(parameter.Name))
            .Select(static parameter => InvocationInput.Create(parameter))
            .Where(static input => input is not null)
            .Cast<InvocationInput>()
            .ToArray();

        if (inputs.Count(static input => input.Source == InvocationInputSource.Body) > 1)
        {
            throw new InvalidOperationException($"Endpoint '{endpoint.DisplayName}' has more than one request body parameter.");
        }

        var parameterNames = inputs.Select(static input => input.Name).ToArray();
        if (parameterNames.Distinct(StringComparer.OrdinalIgnoreCase).Count() != parameterNames.Length)
        {
            throw new InvalidOperationException($"Endpoint '{endpoint.DisplayName}' has duplicate API parameter names.");
        }

        _inputs = inputs;
        var parameterTypes = inputs
            .Select(static _ => typeof(JsonElement?))
            .Append(typeof(CancellationToken))
            .ToArray();
        var returnType = typeof(Task<AspNetCoreEndpointResponse>);
        var delegateType = Expression.GetDelegateType([.. parameterTypes, returnType]);

        var assembly = AssemblyBuilder.DefineDynamicAssembly(
            new AssemblyName($"DotNetAgentSurface.AspNetCore.Invocation.{Guid.NewGuid():N}"),
            AssemblyBuilderAccess.Run);
        var module = assembly.DefineDynamicModule("Invocation");
        var typeBuilder = module.DefineType(
            "EndpointInvocation",
            TypeAttributes.Public | TypeAttributes.Sealed);
        var wrapperDelegateField = typeBuilder.DefineField(
            "_wrapperDelegate",
            typeof(Func<object?[], CancellationToken, Task<AspNetCoreEndpointResponse>>),
            FieldAttributes.Private | FieldAttributes.InitOnly);
        var constructor = typeBuilder.DefineConstructor(
            MethodAttributes.Public,
            CallingConventions.Standard,
            [typeof(Func<object?[], CancellationToken, Task<AspNetCoreEndpointResponse>>)]);
        var constructorIl = constructor.GetILGenerator();
        constructorIl.Emit(OpCodes.Ldarg_0);
        constructorIl.Emit(OpCodes.Call, typeof(object).GetConstructor(Type.EmptyTypes)!);
        constructorIl.Emit(OpCodes.Ldarg_0);
        constructorIl.Emit(OpCodes.Ldarg_1);
        constructorIl.Emit(OpCodes.Stfld, wrapperDelegateField);
        constructorIl.Emit(OpCodes.Ret);

        var methodBuilder = typeBuilder.DefineMethod(
            "InvokeAsync",
            MethodAttributes.Public,
            returnType,
            parameterTypes);
        for (var index = 0; index < parameterNames.Length; index++)
        {
            methodBuilder.DefineParameter(index + 1, ParameterAttributes.None, parameterNames[index]);
        }

        methodBuilder.DefineParameter(parameterTypes.Length, ParameterAttributes.None, "cancellationToken");
        var il = methodBuilder.GetILGenerator();

        // Load the delegate from the field
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Ldfld, wrapperDelegateField);

        // Build the inputs array
        il.Emit(OpCodes.Ldc_I4, inputs.Length);
        il.Emit(OpCodes.Newarr, typeof(object));
        for (var index = 0; index < inputs.Length; index++)
        {
            il.Emit(OpCodes.Dup);
            il.Emit(OpCodes.Ldc_I4, index);
            il.Emit(OpCodes.Ldarg, index + 1);
            il.Emit(OpCodes.Box, typeof(JsonElement?));
            il.Emit(OpCodes.Stelem_Ref);
        }

        // Load the cancellation token
        il.Emit(OpCodes.Ldarg, parameterTypes.Length);

        // Call the wrapper delegate's Invoke method
        // Stack: delegate, object[], CancellationToken -> awaitable Task<AspNetCoreEndpointResponse>
        il.Emit(OpCodes.Callvirt, typeof(Func<object?[], CancellationToken, Task<AspNetCoreEndpointResponse>>).GetMethod("Invoke")!);
        il.Emit(OpCodes.Ret);

        var wrapperType = typeBuilder.CreateType()!;
        var invocationWrapper = CreateInvocationWrapper();
        var wrapper = Activator.CreateInstance(wrapperType, invocationWrapper)!;
        return Delegate.CreateDelegate(delegateType, wrapper, wrapperType.GetMethod("InvokeAsync")!);
    }

    /// <summary>Invokes the endpoint with the provided parameter values.</summary>
    /// <remarks>
    /// This method is public to allow dynamically generated wrapper delegates (created via Reflection.Emit in separate assemblies)
    /// to call it via reflection. It is not intended as a public API contract and should not be called directly by consumers.
    /// </remarks>
    public async Task<AspNetCoreEndpointResponse> InvokeAsync(object?[] inputs, CancellationToken cancellationToken)
    {
        await using var scope = applicationServices.CreateAsyncScope();
        var context = new DefaultHttpContext { RequestServices = scope.ServiceProvider };
        context.Request.Method = method;
        // RouteEndpoint.RoutePattern.RawText is the raw route template (e.g. "Development/autologin"),
        // which is not guaranteed to have a leading '/' and would otherwise throw when assigned to PathString.
        context.Request.Path = "/" + (endpoint.RoutePattern.RawText ?? string.Empty).TrimStart('/');
        context.RequestAborted = cancellationToken;
        await using var responseBody = new MemoryStream();
        context.Response.Body = responseBody;

        var query = new QueryBuilder();
        for (var index = 0; index < _inputs.Count; index++)
        {
            var value = (JsonElement?)inputs[index];
            if (!HasValue(value))
            {
                continue;
            }

            var input = _inputs[index];
            switch (input.Source)
            {
                case InvocationInputSource.Path:
                    context.Request.RouteValues[input.Name] = ToRequestValue(value!.Value);
                    break;
                case InvocationInputSource.Query:
                    foreach (var requestValue in ToRequestValues(value!.Value))
                    {
                        query.Add(input.Name, requestValue);
                    }

                    break;
                case InvocationInputSource.Body:
                    SetRequestBody(context, value!.Value);
                    break;
            }
        }

        context.Request.QueryString = query.ToQueryString();
        await endpoint.RequestDelegate!(context).ConfigureAwait(false);
        responseBody.Position = 0;
        using var reader = new StreamReader(responseBody, Encoding.UTF8, leaveOpen: true);
        return new AspNetCoreEndpointResponse(context.Response.StatusCode, context.Response.ContentType, await reader.ReadToEndAsync(cancellationToken).ConfigureAwait(false));
    }

    private static bool HasValue(JsonElement? value) => value is { ValueKind: not JsonValueKind.Null and not JsonValueKind.Undefined };

    private static void SetRequestBody(HttpContext context, JsonElement body)
    {
        var bytes = Encoding.UTF8.GetBytes(body.GetRawText());
        context.Request.ContentType = "application/json";
        context.Request.ContentLength = bytes.Length;
        context.Request.Body = new MemoryStream(bytes);
        context.Features.Set<IHttpRequestBodyDetectionFeature>(new RequestBodyDetectionFeature());
    }

    private static string ToRequestValue(JsonElement value) => value.ValueKind switch
    {
        JsonValueKind.String => value.GetString() ?? string.Empty,
        JsonValueKind.True => bool.TrueString.ToLowerInvariant(),
        JsonValueKind.False => bool.FalseString.ToLowerInvariant(),
        _ => value.GetRawText()
    };

    private static IEnumerable<string> ToRequestValues(JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.Array)
        {
            return value.EnumerateArray().Select(ToRequestValue).ToArray();
        }

        return [ToRequestValue(value)];
    }

    private sealed record InvocationInput(string Name, InvocationInputSource Source)
    {
        public static InvocationInput? Create(ApiParameterDescription parameter)
        {
            if (parameter.Source == BindingSource.Path)
            {
                return new InvocationInput(parameter.Name, InvocationInputSource.Path);
            }

            if (parameter.Source == BindingSource.Query)
            {
                return new InvocationInput(parameter.Name, InvocationInputSource.Query);
            }

            return parameter.Source == BindingSource.Body
                ? new InvocationInput("body", InvocationInputSource.Body)
                : null;
        }
    }

    private enum InvocationInputSource
    {
        Path,
        Query,
        Body
    }

    private sealed class RequestBodyDetectionFeature : IHttpRequestBodyDetectionFeature
    {
        public bool CanHaveBody => true;
    }}