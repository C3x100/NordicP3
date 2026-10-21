using System.Windows;
using System.Windows.Controls;

namespace NordicP3;

/// <summary>Small modal that requires typing a word to confirm a destructive action.</summary>
public static class ConfirmDialog
{
    public static bool Ask(Window owner, string message, string word)
    {
        var box = new TextBox { Margin = new Thickness(0, 10, 0, 10), FontSize = 14 };
        var ok = new Button { Content = "Continue", IsEnabled = false, IsDefault = true, Margin = new Thickness(0, 0, 8, 0) };
        var cancel = new Button { Content = "Cancel", IsCancel = true };
        box.TextChanged += (_, _) => ok.IsEnabled = box.Text.Trim() == word;

        var win = new Window
        {
            Title = "Confirm",
            Owner = owner,
            Width = 460,
            SizeToContent = SizeToContent.Height,
            ResizeMode = ResizeMode.NoResize,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Content = new StackPanel
            {
                Margin = new Thickness(16),
                Children =
                {
                    new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap },
                    box,
                    new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Children = { ok, cancel } },
                },
            },
        };
        ok.Click += (_, _) => win.DialogResult = true;
        win.Loaded += (_, _) => box.Focus();
        return win.ShowDialog() == true;
    }
}
