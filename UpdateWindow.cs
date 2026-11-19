using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using NotchWin.Services;

namespace NotchWin;

/// <summary>"NotchWin 1.8.0 is out": what's new, plus Update now / Later / Skip this version.</summary>
public class UpdateWindow : Window
{
    public UpdateWindow(Updater.Release release, Version current, Func<Task> install, Action skip)
    {
#pragma warning disable WPF0001
        ThemeMode = ThemeMode.Dark;
#pragma warning restore WPF0001
        Title = "NotchWin update";
        Width = 540;
        Height = 560;
        MinHeight = 360;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        FontFamily = new FontFamily("Segoe UI Variable Text, Segoe UI");
        try { Icon = BitmapFrame.Create(new Uri("pack://application:,,,/Assets/NotchWin.ico")); } catch { }

        var root = new Grid { Margin = new Thickness(26, 22, 26, 20) };
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        // Header: logo + "NotchWin v1.8.0 is out"
        var header = new StackPanel { Orientation = Orientation.Horizontal };
        try
        {
            header.Children.Add(new Image
            {
                Source = new BitmapImage(new Uri("pack://application:,,,/Assets/logo-128.png")),
                Width = 60, Height = 60, Margin = new Thickness(0, 0, 16, 0),
            });
        }
        catch { }
        var titles = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        titles.Children.Add(new TextBlock { Text = $"NotchWin {release.Tag} is out", FontSize = 22, FontWeight = FontWeights.SemiBold });
        titles.Children.Add(new TextBlock { Text = $"You have version {current}.", Opacity = 0.7, Margin = new Thickness(0, 2, 0, 0) });
        header.Children.Add(titles);
        root.Children.Add(header);

        // What's new
        var notesPanel = new StackPanel();
        notesPanel.Children.Add(new TextBlock { Text = "What's new", FontSize = 15, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 22, 0, 8) });
        notesPanel.Children.Add(new TextBlock { Text = Clean(release.Notes), TextWrapping = TextWrapping.Wrap, LineHeight = 21 });
        var scroll = new ScrollViewer { Content = notesPanel, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        Grid.SetRow(scroll, 1);
        root.Children.Add(scroll);

        // Buttons
        var status = new TextBlock { VerticalAlignment = VerticalAlignment.Center, Opacity = 0.75, TextWrapping = TextWrapping.Wrap, MaxWidth = 200 };
        var skipBtn = new Button { Content = "Skip this version", Margin = new Thickness(0, 0, 8, 0) };
        var laterBtn = new Button { Content = "Later", Margin = new Thickness(0, 0, 8, 0), MinWidth = 80 };
        var updateBtn = new Button { Content = "Update now", MinWidth = 110, IsDefault = true };
        try { updateBtn.Style = (Style)FindResource("AccentButtonStyle"); } catch { /* theme without accent style */ }

        skipBtn.Click += (s, e) => { skip?.Invoke(); Close(); };
        laterBtn.Click += (s, e) => Close();
        updateBtn.Click += async (s, e) =>
        {
            skipBtn.IsEnabled = laterBtn.IsEnabled = updateBtn.IsEnabled = false;
            status.Text = "Downloading... NotchWin will restart by itself.";
            try { await install(); }
            catch (Exception ex)
            {
                status.Text = "Update failed: " + ex.Message;
                skipBtn.IsEnabled = laterBtn.IsEnabled = updateBtn.IsEnabled = true;
            }
        };

        var buttons = new DockPanel { Margin = new Thickness(0, 16, 0, 0), LastChildFill = false };
        DockPanel.SetDock(status, Dock.Left);
        buttons.Children.Add(status);
        foreach (var b in new UIElement[] { updateBtn, laterBtn, skipBtn })
        {
            DockPanel.SetDock(b, Dock.Right);
            buttons.Children.Add(b);
        }
        Grid.SetRow(buttons, 2);
        root.Children.Add(buttons);

        Content = root;
    }

    /// <summary>Turns GitHub's release notes (Markdown) into plain readable text.</summary>
    private static string Clean(string md)
    {
        if (string.IsNullOrWhiteSpace(md)) return "Bug fixes and improvements.";
        string t = md.Replace("\r", "");
        t = Regex.Replace(t, @"^#{1,6}\s*", "", RegexOptions.Multiline);      // headings
        t = Regex.Replace(t, @"^\s*[-*+]\s+", "•  ", RegexOptions.Multiline);  // bullets
        t = Regex.Replace(t, @"\*\*(.+?)\*\*", "$1");                          // bold
        t = Regex.Replace(t, @"`([^`]+)`", "$1");                               // code
        t = Regex.Replace(t, @"\[([^\]]+)\]\([^)]+\)", "$1");                   // links → their text
        t = Regex.Replace(t, @"\n{3,}", "\n\n");
        return t.Trim();
    }
}
