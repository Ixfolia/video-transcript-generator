using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using VideoTranscriber.Gui.Services;
using VideoTranscriber.Gui.ViewModels;
using VideoTranscriber.Gui.Views;

namespace VideoTranscriber.Gui;

public partial class App : Application
{
    internal static StartupArguments StartupArguments { get; set; } = new([], null, null);

    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            var window = new MainWindow();
            var settings = StartupArguments.IsSmokeTest ? new SettingsStore(path: null) : SettingsStore.Default;
            var viewModel = new MainWindowViewModel(new DialogService(window), settings, new UiDispatcher());
            window.DataContext = viewModel;
            desktop.MainWindow = window;
            desktop.ShutdownRequested += (_, _) => viewModel.Shutdown();

            viewModel.AddFiles(StartupArguments.Files);

            if (StartupArguments is { IsSmokeTest: true } smoke)
            {
                window.Opened += async (_, _) =>
                {
                    bool ok = await viewModel.RunSmokeTestAsync(smoke.SmokeTestVideo!, smoke.SmokeTestOutput!);
                    desktop.Shutdown(ok ? 0 : 1);
                };
            }
        }

        base.OnFrameworkInitializationCompleted();
    }
}
