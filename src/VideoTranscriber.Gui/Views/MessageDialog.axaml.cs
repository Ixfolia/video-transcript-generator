using Avalonia.Controls;
using Avalonia.Interactivity;

namespace VideoTranscriber.Gui.Views;

/// <summary>A simple message box with one or two buttons.</summary>
public partial class MessageDialog : Window
{
    public MessageDialog()
    {
        InitializeComponent();
        YesButton.Click += (_, _) => Close(true);
        NoButton.Click += (_, _) => Close(false);
    }

    public static async Task<bool> ShowAsync(Window owner, string title, string message, string yesText, string? noText, bool isError)
    {
        var dialog = new MessageDialog { Title = isError ? "Something went wrong" : title };
        dialog.TitleText.Text = title;
        dialog.MessageText.Text = message;
        dialog.YesButton.Content = yesText;
        dialog.NoButton.Content = noText;
        dialog.NoButton.IsVisible = noText is not null;

        if (!owner.IsVisible)
        {
            // Nothing to attach to (e.g. during shutdown); treat as dismissed.
            return false;
        }

        return await dialog.ShowDialog<bool>(owner);
    }

    private void OnClose(object? sender, RoutedEventArgs e) => Close(false);
}
