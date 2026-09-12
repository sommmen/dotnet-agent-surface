namespace DotNetAgentSurface.Hangfire;

/// <summary>
/// Marks a class-based Hangfire job as discoverable by <c>RegisterAttributeJobs(...)</c> (enqueue-oriented) and
/// <c>RegisterAttributeWorkflowTests(...)</c> (direct in-process execution) without requiring the type to
/// implement <see cref="IHangfireJob"/>/<see cref="IHangfireJob{TOptions}"/> at all. Apply this attribute directly
/// to a pre-existing job base class — including one that already implements its own, unrelated marker interface —
/// or to an individual concrete job type.
/// <para>
/// Discovery for an attribute-marked type is purely structural (duck-typed): it looks for a public instance
/// <c>Execute</c>/<c>ExecuteAsync</c> method returning <see cref="Task"/> or <see cref="ValueTask"/> with either a
/// single <see cref="CancellationToken"/> parameter (a parameterless job) or a <c>(TOptions, CancellationToken)</c>
/// parameter pair (an options-based job), inferring <c>TOptions</c> from the method's first parameter instead of
/// from a closed generic interface argument. Discovery never constructs a job instance itself — it only inspects
/// types via reflection and hands Hangfire (or, for workflow tests, the local invocation pipeline) the job
/// type/method pair.
/// </para>
/// <para>
/// This attribute is <see cref="AttributeUsageAttribute.Inherited"/>, so annotating a shared base class is enough
/// to make every concrete subclass discoverable without repeating the attribute on each one.
/// </para>
/// </summary>
[AttributeUsage(AttributeTargets.Class, Inherited = true, AllowMultiple = false)]
public sealed class HangfireJobAttribute : Attribute
{
    /// <summary>Marks a type for structural discovery without constraining its inferred options type.</summary>
    public HangfireJobAttribute()
    {
    }

    /// <summary>
    /// Marks a type for structural discovery, constraining it to the execution method whose first parameter is
    /// exactly <paramref name="optionsType"/>. Use this constructor when a type exposes more than one
    /// differently-shaped <c>Execute</c>/<c>ExecuteAsync</c> method and discovery would otherwise be ambiguous.
    /// </summary>
    public HangfireJobAttribute(Type optionsType)
    {
        OptionsType = optionsType ?? throw new ArgumentNullException(nameof(optionsType));
    }

    /// <summary>
    /// Gets the explicit options type supplied to the constructor, or <see langword="null"/> when the options
    /// type (if any) should be inferred from whichever single execution-method shape is found.
    /// </summary>
    public Type? OptionsType { get; }
}
