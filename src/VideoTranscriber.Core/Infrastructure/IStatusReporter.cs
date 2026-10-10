namespace VideoTranscriber.Infrastructure;

/// <summary>
/// How the core reports what it is doing. The command-line app prints to the console; the desktop app shows the
/// same information in its window.
/// </summary>
public interface IStatusReporter
{
    void Info(string message);

    /// <summary>The start of a noticeable step, e.g. "Downloading the model".</summary>
    void Step(string message);

    void Success(string message);

    void Warning(string message);

    /// <summary>Secondary detail (shown dimmed, or only in logs).</summary>
    void Detail(string message);

    /// <summary>Starts a progress display for a long operation such as a download.</summary>
    IProgressReporter BeginProgress(string label);

    /// <summary>Asks a yes/no question. Implementations that can't ask return false without blocking.</summary>
    Task<bool> ConfirmAsync(string question, bool defaultAnswer);
}

/// <summary>Progress of one long operation. Thread-safe; may be called from any thread.</summary>
public interface IProgressReporter : IDisposable
{
    /// <param name="fraction">0..1</param>
    /// <param name="detail">Optional text such as "45 MB / 142 MB".</param>
    void Report(double fraction, string? detail = null);

    void Complete(string? detail = null);
}

/// <summary>Discards everything; useful for tests and for callers that don't need feedback.</summary>
public sealed class NullStatusReporter : IStatusReporter
{
    public static readonly NullStatusReporter Instance = new();

    public void Info(string message) { }
    public void Step(string message) { }
    public void Success(string message) { }
    public void Warning(string message) { }
    public void Detail(string message) { }
    public IProgressReporter BeginProgress(string label) => NullProgress.Instance;
    public Task<bool> ConfirmAsync(string question, bool defaultAnswer) => Task.FromResult(false);

    private sealed class NullProgress : IProgressReporter
    {
        public static readonly NullProgress Instance = new();
        public void Report(double fraction, string? detail = null) { }
        public void Complete(string? detail = null) { }
        public void Dispose() { }
    }
}
