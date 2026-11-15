using Microsoft.Extensions.Logging;

namespace DotNetAgentSurface.Core;

/// <summary>Who enforces <see cref="AgentSafetyLevel.Confirm"/>/<see cref="AgentSafetyLevel.Dangerous"/> gating.</summary>
public enum OperationConfirmationMode
{
    /// <summary>
    /// The invoker enforces confirmation: an operation above <see cref="AgentSafetyLevel.Safe"/> fails unless a policy
    /// implementing <see cref="IConfirmationEnforcingPolicy"/> is supplied. This is the default.
    /// </summary>
    PolicyEnforced = 0,

    /// <summary>
    /// The operations own their gates (feature flags, environment checks, ...). <see cref="OperationDescriptor.SafetyLevel"/>
    /// is advisory metadata only and no <see cref="IConfirmationEnforcingPolicy"/> is required. Policies that are
    /// supplied still run.
    /// </summary>
    OperationEnforced,
}

/// <summary>Optional pipeline settings for <see cref="OperationInvoker"/>.</summary>
public sealed class OperationInvokerOptions
{
    /// <summary>Filters wrapped around every invocation, outermost first.</summary>
    public IList<IOperationInvocationFilter> Filters { get; } = new List<IOperationInvocationFilter>();

    /// <summary>Maps exceptions to results. Defaults to <see cref="DefaultOperationExceptionMapper"/> using <see cref="Logger"/>.</summary>
    public IOperationExceptionMapper? ExceptionMapper { get; set; }

    /// <summary>Receives the invocation log scope and, with the default mapper, the unexpected-exception log entries.</summary>
    public ILogger? Logger { get; set; }

    /// <summary>Selects who enforces safety gating. See <see cref="OperationConfirmationMode"/>.</summary>
    public OperationConfirmationMode ConfirmationMode { get; set; }

    /// <summary>
    /// Creates a target for an instance operation when no service of its type is registered. Instances created this way
    /// are disposed after the invocation.
    /// </summary>
    public Func<IServiceProvider, Type, object?>? TargetFactory { get; set; }
}
