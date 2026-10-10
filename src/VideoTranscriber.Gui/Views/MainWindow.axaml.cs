using System.Collections.Specialized;
using System.ComponentModel;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using VideoTranscriber.Gui.ViewModels;

namespace VideoTranscriber.Gui.Views;

public partial class MainWindow : Window
{
    private MainWindowViewModel? viewModel;

    public MainWindow()
    {
        InitializeComponent();

        // Videos can be dropped anywhere on the window.
        AddHandler(DragDrop.DragEnterEvent, OnDragOver);
        AddHandler(DragDrop.DragOverEvent, OnDragOver);
        AddHandler(DragDrop.DragLeaveEvent, (_, _) => SetDragOver(false));
        AddHandler(DragDrop.DropEvent, OnDrop);
    }

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        if (viewModel is not null)
        {
            viewModel.PropertyChanged -= OnViewModelChanged;
            viewModel.Log.CollectionChanged -= OnLogChanged;
        }

        viewModel = DataContext as MainWindowViewModel;
        if (viewModel is not null)
        {
            viewModel.PropertyChanged += OnViewModelChanged;
            viewModel.Log.CollectionChanged += OnLogChanged;
        }
    }

    private void OnDragOver(object? sender, DragEventArgs e)
    {
        bool hasFiles = e.DataTransfer.Contains(DataFormat.File);
        e.DragEffects = hasFiles ? DragDropEffects.Copy : DragDropEffects.None;
        SetDragOver(hasFiles);
    }

    private void OnDrop(object? sender, DragEventArgs e)
    {
        SetDragOver(false);
        if (viewModel is null)
        {
            return;
        }

        // Files added during a run wait in the list until Start is pressed again.

        IEnumerable<IStorageItem> items = e.DataTransfer.TryGetFiles() ?? [];
        viewModel.AddFiles(items.Select(i => i.TryGetLocalPath()).OfType<string>());
    }

    private void SetDragOver(bool value)
    {
        if (viewModel is not null)
        {
            viewModel.IsDragOver = value;
        }
    }

    // Keep the newest recognized text and log lines in view.
    private void OnViewModelChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(MainWindowViewModel.SelectedFile) && viewModel?.SelectedFile is { } item)
        {
            item.PropertyChanged -= OnSelectedFileChanged;
            item.PropertyChanged += OnSelectedFileChanged;
        }
    }

    private void OnSelectedFileChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(FileItemViewModel.TranscriptText) && ReferenceEquals(sender, viewModel?.SelectedFile))
        {
            Dispatcher.UIThread.Post(() => TranscriptScroller.ScrollToEnd(), DispatcherPriority.Background);
        }
    }

    private void OnLogChanged(object? sender, NotifyCollectionChangedEventArgs e) =>
        Dispatcher.UIThread.Post(() => LogScroller.ScrollToEnd(), DispatcherPriority.Background);
}
