using System.Windows;
using System.Windows.Controls;

namespace ZPlus.Admin.Views;

/// <summary>Minimal modal prompt for a single text value (e.g. a new password).</summary>
public static class InputDialog
{
    public static string? Show(Window owner, string prompt)
    {
        var input = new TextBox { Margin = new Thickness(0, 8, 0, 12), MinWidth = 280 };
        var ok = new Button { Content = "OK", IsDefault = true, Padding = new Thickness(16, 6, 16, 6) };
        var cancel = new Button
        {
            Content = "Cancel", IsCancel = true, Margin = new Thickness(8, 0, 0, 0),
            Padding = new Thickness(16, 6, 16, 6),
        };

        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        buttons.Children.Add(ok);
        buttons.Children.Add(cancel);

        var panel = new StackPanel { Margin = new Thickness(16) };
        panel.Children.Add(new TextBlock { Text = prompt });
        panel.Children.Add(input);
        panel.Children.Add(buttons);

        var dialog = new Window
        {
            Title = "Z+ Admin",
            Owner = owner,
            Content = panel,
            SizeToContent = SizeToContent.WidthAndHeight,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            ResizeMode = ResizeMode.NoResize,
            Background = (System.Windows.Media.Brush)Application.Current.Resources["WindowBg"],
        };
        ok.Click += (_, _) => dialog.DialogResult = true;
        input.Focus();

        return dialog.ShowDialog() == true ? input.Text : null;
    }
}
