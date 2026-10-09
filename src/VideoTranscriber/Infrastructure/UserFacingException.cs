namespace VideoTranscriber.Infrastructure;

/// <summary>
/// An expected failure with a message written for the person running the tool (no stack trace is shown).
/// </summary>
public sealed class UserFacingException : Exception
{
    public UserFacingException(string message, string? hint = null, Exception? innerException = null)
        : base(message, innerException)
    {
        Hint = hint;
    }

    /// <summary>Optional advice on how to fix the problem.</summary>
    public string? Hint { get; }
}
