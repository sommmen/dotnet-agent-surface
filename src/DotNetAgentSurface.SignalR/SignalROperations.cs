using DotNetAgentSurface.Core;

namespace DotNetAgentSurface.SignalR;

/// <summary>Describes a server-to-client message accepted by SignalR.</summary>
public sealed record SignalRSendResult(
    string Target,
    string MethodName,
    int ArgumentCount,
    DateTimeOffset AcceptedAt);

/// <summary>Configures the stable SignalR server-to-client send operations.</summary>
public sealed class SignalROperationsOptions
{
    /// <summary>Gets or sets the category assigned to every send operation.</summary>
    public string? Category { get; set; } = "SignalR";

    /// <summary>Gets or sets the safety level assigned to every send operation.</summary>
    public AgentSafetyLevel SendSafetyLevel { get; set; } = AgentSafetyLevel.Confirm;
}
