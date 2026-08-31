using System.Text.Json.Nodes;

namespace DotNetAgentSurface.CommandLine;

/// <summary>
/// Renders an already-normalized, JSON-compatible operation result into CLI stdout text for a specific output
/// format (TOON, JSON, ...).
/// </summary>
/// <remarks>
/// <para>
/// Renderers are purely a formatting boundary: projection (<c>--fields</c> / default field selection),
/// truncation (<c>--full</c> and the truncation limit), and list count/empty-state wrapping all happen once,
/// upstream, in <see cref="AgentOutputProjector"/>, so every renderer produces consistent content and only the
/// surface syntax differs between formats. Renderers must not re-apply or bypass that normalization.
/// </para>
/// <para>
/// This interface only covers rendering a <em>successful</em> invocation's value. Error text continues to flow
/// through <see cref="CommandLineExecutionResult.Failure(string, int)"/> as a plain, already-clean message (see
/// <see cref="OperationCommandLineAdapter"/>), since operation/usage errors are simple strings rather than
/// structured, potentially large or deeply-nested payloads that benefit from projection/truncation.
/// </para>
/// </remarks>
public interface IAgentOutputRenderer
{
    /// <summary>
    /// Renders <paramref name="normalizedValue"/> (the output of <see cref="AgentOutputProjector.Project"/>) as
    /// CLI stdout text.
    /// </summary>
    string Render(JsonNode? normalizedValue);
}
