namespace DotNetAgentSurface.Samples.TaskTracker;

/// <summary>A single tracked task, returned by the sample <see cref="TaskTrackerService"/>.</summary>
public sealed record TaskItem(int Id, string Title, string? Notes, bool IsCompleted);
