using DotNetAgentSurface.Core;

namespace DotNetAgentSurface.Samples.LegacyDesktop;

/// <summary>
/// A tiny self-contained service (no external state, no async) exposed the same way the other
/// samples expose <c>TaskTrackerService</c>. Kept separate from
/// <c>DotNetAgentSurface.Samples.TaskTracker</c> because that project targets <c>net10.0</c> only,
/// while this sample specifically needs a service compiled for <c>net472</c>.
/// </summary>
public sealed class GreeterService
{
    [AgentOperation("greet", "Builds a greeting for the given name", Category = "greetings")]
    public string Greet(string name, string? honorific = null)
    {
        // ArgumentException.ThrowIfNullOrWhiteSpace has no netstandard2.0/net472 equivalent, so this
        // sample uses the same inline check DotNetAgentSurface.Mcp uses at its one cross-assembly call
        // site (Guard is internal to DotNetAgentSurface.Core and not exposed via InternalsVisibleTo).
        if (string.IsNullOrWhiteSpace(name))
        {
            throw new ArgumentException("Value cannot be null or whitespace.", nameof(name));
        }

        return honorific is null
            ? $"Hello, {name}!"
            : $"Hello, {honorific} {name}!";
    }

    [AgentOperation("count-letters", "Counts occurrences of a letter in a name", Category = "greetings")]
    public int CountLetters(string name, char letter)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            throw new ArgumentException("Value cannot be null or whitespace.", nameof(name));
        }

        var count = 0;
        foreach (var current in name)
        {
            if (char.ToLowerInvariant(current) == char.ToLowerInvariant(letter))
            {
                count++;
            }
        }

        return count;
    }
}
