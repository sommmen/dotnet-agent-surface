using System.Runtime.CompilerServices;

namespace DotNetAgentSurface.Core;

/// <summary>
/// Lightweight replacements for <c>ArgumentNullException.ThrowIfNull(object?, string?)</c> and
/// <c>ArgumentException.ThrowIfNullOrWhiteSpace(string?, string?)</c>. These BCL static helpers are not
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
        // Checked separately (rather than via string.IsNullOrWhiteSpace) because the netstandard2.0
        // reference surface for that method lacks a [NotNullWhen(false)] annotation, which would
        // otherwise leave the compiler unable to prove `argument` is non-null below on that TFM.
        if (argument is null)
        {
            throw new ArgumentNullException(paramName);
        }

        if (argument.Trim().Length == 0)
        {
            throw new ArgumentException("Value cannot be null or whitespace.", paramName);
        }
    }
}
