using DotNetAgentSurface.Core;
using Microsoft.AspNetCore.Authorization;
using Microsoft.Extensions.DependencyInjection;

namespace DotNetAgentSurface.AspNetCore;

/// <summary>
/// Evaluates ASP.NET Core endpoint authorization metadata against the trusted invocation principal.
/// </summary>
public sealed class AspNetCoreEndpointAuthorizationPolicy : IOperationInvocationPolicy
{
    private readonly IServiceProvider _services;

    public AspNetCoreEndpointAuthorizationPolicy(IServiceProvider services)
    {
        ArgumentNullException.ThrowIfNull(services);
        _services = services;
    }

    public async ValueTask<OperationPolicyResult> EvaluateAsync(
        OperationDescriptor operation,
        IReadOnlyDictionary<string, System.Text.Json.JsonElement>? inputs,
        OperationConfirmation? confirmation = null,
        CancellationToken cancellationToken = default,
        OperationInvocationContext? invocationContext = null)
    {
        ArgumentNullException.ThrowIfNull(operation);

        var metadata = operation.PolicyMetadata;
        if (metadata.OfType<IAllowAnonymous>().Any())
        {
            return OperationPolicyResult.Allow();
        }

        var authorizeData = metadata.OfType<IAuthorizeData>().ToArray();
        if (authorizeData.Length == 0)
        {
            return OperationPolicyResult.Allow();
        }

        var principal = invocationContext?.Principal;
        if (principal?.Identity?.IsAuthenticated != true)
        {
            return OperationPolicyResult.Deny($"Operation '{operation.Name}' requires an authenticated caller.");
        }

        var authorizationService = _services.GetService<IAuthorizationService>();
        var policyProvider = _services.GetService<IAuthorizationPolicyProvider>();
        if (authorizationService is null || policyProvider is null)
        {
            return OperationPolicyResult.Deny($"Operation '{operation.Name}' cannot be authorized because ASP.NET Core authorization services are not configured.");
        }

        var policy = await AuthorizationPolicy.CombineAsync(policyProvider, authorizeData).ConfigureAwait(false);
        if (policy is null)
        {
            return OperationPolicyResult.Deny($"Operation '{operation.Name}' has an invalid authorization policy.");
        }

        var result = await authorizationService.AuthorizeAsync(principal, resource: null, policy.Requirements).ConfigureAwait(false);
        return result.Succeeded
            ? OperationPolicyResult.Allow()
            : OperationPolicyResult.Deny($"Operation '{operation.Name}' is not authorized.");
    }
}
