using Avalonia.Threading;

namespace VideoTranscriber.Gui.Services;

/// <summary>Runs work on the UI thread. Abstracted so view models can be tested without a running app.</summary>
public interface IUiDispatcher
{
    void Post(Action action);

    Task<T> InvokeAsync<T>(Func<Task<T>> action);
}

public sealed class UiDispatcher : IUiDispatcher
{
    public void Post(Action action) => Dispatcher.UIThread.Post(action);

    public Task<T> InvokeAsync<T>(Func<Task<T>> action) => Dispatcher.UIThread.InvokeAsync(action);
}

/// <summary>Runs everything immediately on the calling thread (for tests).</summary>
public sealed class ImmediateDispatcher : IUiDispatcher
{
    public void Post(Action action) => action();

    public Task<T> InvokeAsync<T>(Func<Task<T>> action) => action();
}
