using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;

namespace GcodeRecovery.App;

/// <summary>Minimal modal yes/no dialog.</summary>
public sealed class ConfirmDialog : Window
{
    private ConfirmDialog(string title, string message, string yes, string no)
    {
        Title = title;
        Width = 560;
        SizeToContent = SizeToContent.Height;
        CanResize = false;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;

        var yesButton = new Button { Content = yes, Classes = { "accent" }, IsDefault = true };
        var noButton = new Button { Content = no, IsCancel = true };
        yesButton.Click += (_, _) => Close(true);
        noButton.Click += (_, _) => Close(false);

        Content = new StackPanel
        {
            Margin = new Thickness(20),
            Spacing = 16,
            Children =
            {
                new TextBlock { Text = message, TextWrapping = Avalonia.Media.TextWrapping.Wrap },
                new StackPanel
                {
                    Orientation = Orientation.Horizontal,
                    HorizontalAlignment = HorizontalAlignment.Right,
                    Spacing = 8,
                    Children = { noButton, yesButton },
                },
            },
        };
    }

    public static Task<bool> AskAsync(Window owner, string title, string message, string yes = "Yes", string no = "Cancel") =>
        new ConfirmDialog(title, message, yes, no).ShowDialog<bool>(owner);
}
