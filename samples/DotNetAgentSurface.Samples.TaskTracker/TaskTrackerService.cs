using DotNetAgentSurface.Core;

namespace DotNetAgentSurface.Samples.TaskTracker;

/// <summary>
/// A minimal in-memory task tracker shared by the CLI and MCP sample hosts. Every public
/// operation is annotated with <see cref="AgentOperationAttribute"/> so it is discoverable
/// through <see cref="OperationCatalog.Discover(Type[])"/> and exposed identically on both surfaces.
/// </summary>
public sealed class TaskTrackerService
{
    private readonly Dictionary<int, TaskItem> _tasks = [];
    private int _nextId = 1;

    /// <summary>
    /// Lists all tracked tasks in ascending identifier order.
    /// </summary>
    [AgentOperation(
        "list-tasks",
        Category = "tasks",
        IsIdempotent = true,
        Examples = [""])]
    public IReadOnlyList<TaskItem> ListTasks() => [.. _tasks.Values.OrderBy(task => task.Id)];

    /// <summary>
    /// Creates an incomplete task with a required title and optional notes.
    /// </summary>
    /// <param name="title">The task title.</param>
    /// <param name="notes">Optional notes about the task.</param>
    [AgentOperation(
        "add-task",
        Category = "tasks",
        Examples = ["--title \"Write docs\" --notes \"Include CLI skill generation\""])]
    public TaskItem AddTask(string title, string? notes = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(title);

        var task = new TaskItem(_nextId++, title, notes, IsCompleted: false);
        _tasks[task.Id] = task;
        return task;
    }

    /// <summary>
    /// Marks the task identified by id as completed.
    /// </summary>
    /// <param name="id">The identifier of the task to complete.</param>
    [AgentOperation(
        "complete-task",
        Category = "tasks",
        Examples = ["--id 1"])]
    public TaskItem CompleteTask(int id)
    {
        if (!_tasks.TryGetValue(id, out var task))
        {
            throw new InvalidOperationException($"No task with id {id} exists.");
        }

        var completed = task with { IsCompleted = true };
        _tasks[id] = completed;
        return completed;
    }

    /// <summary>
    /// Permanently deletes the task identified by id; invoke only after explicit user confirmation.
    /// </summary>
    /// <param name="id">The identifier of the task to remove.</param>
    [AgentOperation(
        "remove-task",
        Category = "tasks",
        SafetyLevel = AgentSafetyLevel.Dangerous,
        Examples = ["--id 1"])]
    public void RemoveTask(int id)
    {
        if (!_tasks.Remove(id))
        {
            throw new InvalidOperationException($"No task with id {id} exists.");
        }
    }
}
