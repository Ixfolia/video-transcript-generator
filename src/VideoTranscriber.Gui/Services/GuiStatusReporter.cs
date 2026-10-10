using VideoTranscriber.Gui.ViewModels;
using VideoTranscriber.Infrastructure;

namespace VideoTranscriber.Gui.Services;

/// <summary>Shows core status (FFmpeg and model downloads, warnings) in the window's status bar and log.</summary>
public sealed class GuiStatusReporter : IStatusReporter
{
    private readonly MainWindowViewModel viewModel;
    private readonly IUiDispatcher dispatcher;
    private readonly IDialogService dialogs;

    public GuiStatusReporter(MainWindowViewModel viewModel, IUiDispatcher dispatcher, IDialogService dialogs)
    {
        this.viewModel = viewModel;
        this.dispatcher = dispatcher;
        this.dialogs = dialogs;
    }

    public void Info(string message) => Post(() => viewModel.AddLog(message));

    public void Step(string message) => Post(() =>
    {
        viewModel.SetStatus(message);
        viewModel.AddLog(message);
    });

    public void Success(string message) => Step(message);

    public void Warning(string message) => Post(() =>
    {
        viewModel.SetStatus(message, isError: true);
        viewModel.AddLog("Warning: " + message);
    });

    public void Detail(string message) => Post(() => viewModel.AddLog(message));

    public IProgressReporter BeginProgress(string label) => new ActivityProgress(viewModel, dispatcher, label);

    public Task<bool> ConfirmAsync(string question, bool defaultAnswer) =>
        dispatcher.InvokeAsync(() => dialogs.ConfirmAsync("VideoTranscriber", question, "Download", "Cancel"));

    private void Post(Action action) => dispatcher.Post(action);

    /// <summary>Drives the progress bar in the status area (used for downloads).</summary>
    private sealed class ActivityProgress : IProgressReporter
    {
        private readonly MainWindowViewModel viewModel;
        private readonly IUiDispatcher dispatcher;
        private int lastPermille = -1;

        public ActivityProgress(MainWindowViewModel viewModel, IUiDispatcher dispatcher, string label)
        {
            this.viewModel = viewModel;
            this.dispatcher = dispatcher;
            dispatcher.Post(() =>
            {
                viewModel.ActivityLabel = label;
                viewModel.ActivityProgress = 0;
                viewModel.ActivityDetail = null;
            });
        }

        public void Report(double fraction, string? detail = null)
        {
            int permille = (int)(Math.Clamp(fraction, 0, 1) * 1000);
            if (Interlocked.Exchange(ref lastPermille, permille) == permille)
            {
                return;
            }

            dispatcher.Post(() =>
            {
                viewModel.ActivityProgress = permille / 10.0;
                viewModel.ActivityDetail = detail;
            });
        }

        public void Complete(string? detail = null) => dispatcher.Post(() =>
        {
            viewModel.ActivityProgress = 100;
            viewModel.ActivityDetail = detail;
        });

        public void Dispose() => dispatcher.Post(() => viewModel.ActivityLabel = null);
    }
}
