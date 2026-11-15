namespace DotNetAgentSurface.Core;

/// <summary>
/// An operation failure whose message is written for the caller. Adapters surface <see cref="Exception.Message"/>
/// verbatim, including newlines, instead of redacting or normalizing it. Throw it only with text that is safe to
/// show to an agent or user; any other exception type may be redacted by an adapter.
/// </summary>
public class OperationException : Exception
{
    /// <summary>Initializes a new instance with a caller-safe <paramref name="message"/>.</summary>
    public OperationException(string message)
        : base(message)
    {
    }

    /// <summary>Initializes a new instance with a caller-safe <paramref name="message"/> and an inner exception that is never shown to callers.</summary>
    public OperationException(string message, Exception? innerException)
        : base(message, innerException)
    {
    }
}
