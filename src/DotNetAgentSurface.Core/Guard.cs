using System.Runtime.CompilerServices;

namespace DotNetAgentSurface.Core;

/// <summary>
/// Lightweight replacements for <see cref="ArgumentNullException.ThrowIfNull(object?, string?)"/> and
/// <see cref="ArgumentException.ThrowIfNullOrWhiteSpace(string?, string?)"/>. These BCL static helpers are not
/// available on <c>netstandard2.0</c>, so this type provides functionally equivalent behavior (including
/// parameter-name capture via <see cref="CallerArgumentExpressionAttribute"/>) uniformly across every target
/// framework this library supports.
/// </summary>
internal static class Guard
{
    public static void ThrowIfNull(
        [System.Diagnostics.CodeAnalysis.NotNull] object? argument,
        [CallerArgumentExpression(nameof(argument))] string? paramName = null)
    {
        if (argument is null)
        {
            throw new ArgumentNullException(paramName);
        }
    }

    public static void ThrowIfNullOrWhiteSpace(
        [System.Diagnostics.CodeAnalysis.NotNull] string? argument,
        [CallerArgumentExpression(nameof(argument))] string? paramName = null)
    {
        if (string.IsNullOrWhiteSpace(argument))
        {
            throw new ArgumentException("Value cannot be null or whitespace.", paramName);
        }
    }
}
