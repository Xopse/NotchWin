using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.Collections.ObjectModel;
using System.IO;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Navigation;
using NotchWin.Services;

namespace NotchWin;

public partial class SettingsWindow : Window
{
    private readonly AppConfig _cfg;
    private readonly Action _onSaved;
    private readonly Action<string> _previewTheme;
    private string _themeId;
    private readonly ObservableCollection<ShortcutItem> _shortcuts;

    private readonly Func<Task<string>> _checkUpdates;

    public SettingsWindow(Action onSaved, Action<string> previewTheme = null, Func<Task<string>> checkUpdates = null)
    {
        // Built-in Windows 11 style dark theme (WPF on .NET 9+).
#pragma warning disable WPF0001
        ThemeMode = ThemeMode.Dark;
#pragma warning restore WPF0001

        InitializeComponent();
        _onSaved = onSaved;
        _previewTheme = previewTheme;
        _checkUpdates = checkUpdates;
        _cfg = AppConfig.Load(); // fresh copy from disk
        _themeId = Theme.Find(_cfg.Theme).Id;
        BuildThemeCards();

        foreach (var p in AiClient.Providers)
            ProviderBox.Items.Add(new ComboBoxItem { Content = p.Name, Tag = p.Id });

        _shortcuts = new ObservableCollection<ShortcutItem>(_cfg.Shortcuts.Select(s => new ShortcutItem
        {
            Name = s.Name, Icon = s.Icon, Target = s.Target, Args = s.Args,
        }));
        ShortcutsGrid.ItemsSource = _shortcuts;

        LoadValues();

        WidthSlider.ValueChanged += (s, e) => WidthLabel.Text = $"Width of the small pill: {WidthSlider.Value:0} px";
        HeightSlider.ValueChanged += (s, e) => HeightLabel.Text = $"Height of the small pill: {HeightSlider.Value:0} px";
        HoverSlider.ValueChanged += (s, e) => HoverLabel.Text = $"Hover delay before it opens: {HoverSlider.Value:0} ms";
        WidthLabel.Text = $"Width of the small pill: {WidthSlider.Value:0} px";
        HeightLabel.Text = $"Height of the small pill: {HeightSlider.Value:0} px";
        HoverLabel.Text = $"Hover delay before it opens: {HoverSlider.Value:0} ms";

        PopupSlider.ValueChanged += (s, e) => UpdatePopupLabels();
        PopupWatchSlider.ValueChanged += (s, e) => UpdatePopupLabels();
        UpdatePopupLabels();
        UpdatePositionText();
        var v = typeof(SettingsWindow).Assembly.GetName().Version;
        VersionText.Text = $"Version {v?.Major}.{v?.Minor}.{v?.Build} · a Dynamic-Island-style notch for Windows";
    }

    private bool _resetPosition;

    private void UpdatePopupLabels()
    {
        PopupLabel.Text = $"Pop-up time: {PopupSlider.Value:0.#} s";
        PopupWatchLabel.Text = $"Pop-up time while watching a video / fullscreen: {PopupWatchSlider.Value:0.#} s";
    }

    private void UpdatePositionText()
    {
        string dock = _resetPosition ? "top" : (_cfg.Dock ?? "top");
        PositionText.Text = "Currently: " + dock switch
        {
            "top" => _resetPosition || _cfg.PosX < 0 ? "docked at top centre" : "docked to the top edge",
            "bottom" => "docked to the bottom edge",
            "left" => "docked to the left edge (vertical)",
            "right" => "docked to the right edge (vertical)",
            _ => "floating",
        };
    }

    private void ResetPosition_Click(object sender, RoutedEventArgs e)
    {
        _resetPosition = true;
        UpdatePositionText();
    }

    // ------------------------------------------------------------------
    //  Load / save
    // ------------------------------------------------------------------

    private void LoadValues()
    {
        // Notch
        SelectByTag(HideModeBox, _cfg.HideMode, "Games");
        WidthSlider.Value = _cfg.CompactWidth;
        HeightSlider.Value = _cfg.CompactHeight;
        HoverSlider.Value = _cfg.HoverDelayMs;
        ShowSongBox.IsChecked = _cfg.ShowSongInPill;
        ShowEqBox.IsChecked = _cfg.ShowEqualizer;
        ShowWeatherBox.IsChecked = _cfg.ShowWeatherInPill;
        SongPopupBox.IsChecked = _cfg.ShowSongPopups;
        StartupBox.IsChecked = _cfg.RunOnStartup;
        AutoUpdateBox.IsChecked = _cfg.AutoUpdate;

        // Voice & AI
        SelectByTag(MusicBox, (_cfg.MusicService ?? "").Contains("spotify") ? "spotify" : "youtube music", "youtube music");
        SelectByTag(VoiceModeBox, _cfg.VoiceMode, "WakeWord");
        WakeWordsBox.Text = string.Join(", ", _cfg.WakeWords);
        SelectByTag(WakeSensBox, _cfg.WakeSensitivity, "medium");
        SelectByTag(VoskBox, _cfg.VoskModelUrl == AppConfig.VoskUs ? "us" : "in", "in");
        SpeakBox.IsChecked = _cfg.SpeakReplies;
        SelectByTag(SpeechEngineBox, _cfg.SpeechEngine, "auto");
        SpeechKeyBox.Password = _cfg.GroqSpeechKey ?? "";
        SelectByTag(ProviderBox, _cfg.AiProvider, "groq");
        KeyBox.Password = _cfg.AiApiKey ?? "";
        BaseUrlBox.Text = _cfg.AiBaseUrl ?? "";
        ModelBox.Text = _cfg.AiModel ?? "";
        UpdateProviderUi();

        // Weather & streaming
        CityBox.Text = _cfg.City ?? "";
        FahrenheitBox.IsChecked = _cfg.UseFahrenheit;
        ObsPortBox.Text = _cfg.ObsPort.ToString();
        ObsPasswordBox.Password = _cfg.ObsPassword ?? "";

        // Notifications
        AllNotifBox.IsChecked = _cfg.ShowAllNotifications;
        PopupSlider.Value = _cfg.NotificationPopupSeconds;
        PopupWatchSlider.Value = _cfg.NotificationPopupSecondsWhileWatching;
        AppsBox.Text = string.Join(", ", _cfg.ImportantApps);
        KeywordsBox.Text = string.Join(", ", _cfg.ImportantKeywords);

        // Games
        ExtraGamesBox.Text = string.Join(Environment.NewLine, _cfg.ExtraGameExes);
        IgnoreExesBox.Text = string.Join(Environment.NewLine, _cfg.IgnoreExes);
        FoldersBox.Text = string.Join(Environment.NewLine, _cfg.GameFolders);
    }

    /// <summary>Copies everything from the window into the config object.</summary>
    private void ReadValues(AppConfig c)
    {
        c.Theme = _themeId;
        c.HideMode = TagOf(HideModeBox, "Games");
        c.CompactWidth = Math.Round(WidthSlider.Value);
        c.CompactHeight = Math.Round(HeightSlider.Value);
        c.HoverDelayMs = (int)HoverSlider.Value;
        c.ShowSongInPill = ShowSongBox.IsChecked == true;
        c.ShowEqualizer = ShowEqBox.IsChecked == true;
        c.ShowWeatherInPill = ShowWeatherBox.IsChecked == true;
        c.ShowSongPopups = SongPopupBox.IsChecked == true;
        c.RunOnStartup = StartupBox.IsChecked == true;
        c.AutoUpdate = AutoUpdateBox.IsChecked == true;

        c.MusicService = TagOf(MusicBox, "youtube music");
        c.VoiceMode = TagOf(VoiceModeBox, "WakeWord");
        c.WakeWords = SplitList(WakeWordsBox.Text, ',').Select(w => w.ToLowerInvariant()).ToList();
        if (c.WakeWords.Count == 0) c.WakeWords.Add("computer");
        c.WakeSensitivity = TagOf(WakeSensBox, "medium");
        c.VoskModelUrl = TagOf(VoskBox, "in") == "us" ? AppConfig.VoskUs : AppConfig.VoskIndian;
        c.SpeakReplies = SpeakBox.IsChecked == true;
        c.SpeechEngine = TagOf(SpeechEngineBox, "auto");
        c.GroqSpeechKey = SpeechKeyBox.Password.Trim();
        c.AiProvider = TagOf(ProviderBox, "groq");
        c.AiApiKey = KeyBox.Password.Trim();
        c.AiBaseUrl = BaseUrlBox.Text.Trim();
        c.AiModel = ModelBox.Text.Trim();

        c.City = CityBox.Text.Trim();
        c.UseFahrenheit = FahrenheitBox.IsChecked == true;
        if (int.TryParse(ObsPortBox.Text.Trim(), out int port) && port > 0 && port < 65536) c.ObsPort = port;
        c.ObsPassword = ObsPasswordBox.Password;

        c.ShowAllNotifications = AllNotifBox.IsChecked == true;
        c.NotificationPopupSeconds = PopupSlider.Value;
        c.NotificationPopupSecondsWhileWatching = PopupWatchSlider.Value;
        if (_resetPosition) { c.PosX = -1; c.PosY = 0; c.Dock = "top"; }
        else
        {
            // You may have dragged the notch while this window was open: keep where it is now.
            var current = AppConfig.Load();
            c.PosX = current.PosX;
            c.PosY = current.PosY;
            c.Dock = current.Dock;
        }
        c.ImportantApps = SplitList(AppsBox.Text, ',');
        c.ImportantKeywords = SplitList(KeywordsBox.Text, ',');

        c.ExtraGameExes = SplitList(ExtraGamesBox.Text, '\n');
        c.IgnoreExes = SplitList(IgnoreExesBox.Text, '\n');
        c.GameFolders = SplitList(FoldersBox.Text, '\n');

        ShortcutsGrid.CommitEdit(DataGridEditingUnit.Row, true);
        c.Shortcuts = _shortcuts.Where(s => !string.IsNullOrWhiteSpace(s.Target)).ToList();
    }

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        ReadValues(_cfg);
        _cfg.Save();
        Close();
        _onSaved?.Invoke();
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) => Close();

    // ------------------------------------------------------------------
    //  Themes tab
    // ------------------------------------------------------------------

    private readonly Dictionary<string, Border> _themeCards = new();

    private void BuildThemeCards()
    {
        ThemeCards.Children.Clear();
        _themeCards.Clear();
        foreach (var t in Theme.All)
        {
            var theme = t;
            var fg = new SolidColorBrush(theme.Fg);

            // A tiny notch drawn in the theme's colours.
            var dots = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Center };
            foreach (var a in new[] { theme.Accent1, theme.Accent2, theme.Accent3 })
                dots.Children.Add(new System.Windows.Shapes.Ellipse { Width = 7, Height = 7, Margin = new Thickness(3, 0, 0, 0), Fill = new SolidColorBrush(a) });
            var miniRow = new Grid();
            miniRow.Children.Add(new TextBlock { Text = "9:41 PM", Foreground = fg, FontSize = 11, FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center });
            miniRow.Children.Add(dots);
            var mini = new Border
            {
                Background = theme.MakeBackground(),
                BorderBrush = theme.MakeBrushes()["NotchBorder"],
                BorderThickness = new Thickness(theme.BorderThickness),
                CornerRadius = new CornerRadius(0, 0, 14, 14),
                Padding = new Thickness(12, 0, 12, 0),
                Height = 28,
                Width = 132,
                HorizontalAlignment = HorizontalAlignment.Center,
                Child = miniRow,
                Effect = new System.Windows.Media.Effects.DropShadowEffect
                {
                    Color = theme.Glow, BlurRadius = 14, ShadowDepth = 0, Opacity = theme.Shadow ? theme.GlowOpacity : 0,
                },
            };

            // Card: preview on a "desktop", then name + description.
            var desk = new Border
            {
                Height = 70,
                CornerRadius = new CornerRadius(10, 10, 0, 0),
                Background = new LinearGradientBrush(Color.FromRgb(0x3A, 0x4A, 0x6A), Color.FromRgb(0x1C, 0x22, 0x33), 45),
                Child = new Grid { Children = { mini }, VerticalAlignment = VerticalAlignment.Top },
            };
            var text = new StackPanel { Margin = new Thickness(12, 8, 12, 10) };
            text.Children.Add(new TextBlock { Text = theme.Name, FontWeight = FontWeights.SemiBold, FontSize = 13.5 });
            text.Children.Add(new TextBlock { Text = theme.Description, FontSize = 11.5, Opacity = 0.7, TextWrapping = TextWrapping.Wrap });

            var stack = new StackPanel();
            stack.Children.Add(desk);
            stack.Children.Add(text);

            var card = new Border
            {
                Width = 190,
                Margin = new Thickness(0, 0, 12, 12),
                CornerRadius = new CornerRadius(12),
                BorderThickness = new Thickness(2),
                Background = new SolidColorBrush(Color.FromArgb(0x18, 0xFF, 0xFF, 0xFF)),
                Child = stack,
                Cursor = System.Windows.Input.Cursors.Hand,
            };
            card.MouseLeftButtonUp += (s, e) => SelectTheme(theme.Id);
            _themeCards[theme.Id] = card;
            ThemeCards.Children.Add(card);
        }
        HighlightThemeCard();
    }

    private void SelectTheme(string id)
    {
        _themeId = id;
        HighlightThemeCard();
        _previewTheme?.Invoke(id); // try it on the real notch
    }

    private void HighlightThemeCard()
    {
        foreach (var kv in _themeCards)
            kv.Value.BorderBrush = kv.Key == _themeId
                ? new SolidColorBrush(Color.FromRgb(0x60, 0xA5, 0xFA))
                : Brushes.Transparent;
    }

    private async void CheckUpdates_Click(object sender, RoutedEventArgs e)
    {
        if (_checkUpdates == null) return;
        UpdateStatus.Text = "Checking GitHub...";
        try { UpdateStatus.Text = await _checkUpdates(); }
        catch (Exception ex) { UpdateStatus.Text = "Couldn't check: " + ex.Message; }
    }

    private async void CheckNotifs_Click(object sender, RoutedEventArgs e)
    {
        NotifReport.Visibility = Visibility.Visible;
        NotifReport.Text = "Reading...";
        var cfg = new AppConfig();
        ReadValues(cfg); // use what's on screen right now, even before saving
        var lines = await Task.Run(() => Services.NotificationService.Diagnose(cfg));
        NotifReport.Text = string.Join(Environment.NewLine, lines);
    }

    private void Quit_Click(object sender, RoutedEventArgs e)
    {
        Close();
        Application.Current.MainWindow?.Close();
    }

    // ------------------------------------------------------------------
    //  AI section
    // ------------------------------------------------------------------

    private void ProviderBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!IsLoaded) return;
        // Different provider = different model names; start from that provider's default.
        ModelBox.Items.Clear();
        ModelBox.Text = "";
        UpdateProviderUi();
    }

    private void UpdateProviderUi()
    {
        var p = AiClient.Find(TagOf(ProviderBox, "groq"));
        ProviderNote.Text = p.Note + (string.IsNullOrEmpty(p.DefaultModel) ? "" : $" Default model: {p.DefaultModel}.");
        BaseUrlPanel.Visibility = p.Id == "custom" ? Visibility.Visible : Visibility.Collapsed;
        if (string.IsNullOrEmpty(p.KeyUrl))
        {
            KeyLink.NavigateUri = null;
            KeyLinkText.Text = "";
        }
        else
        {
            KeyLink.NavigateUri = new Uri(p.KeyUrl);
            KeyLinkText.Text = "Get an API key → " + p.KeyUrl;
        }
    }

    private AppConfig AiConfigFromForm()
    {
        var c = new AppConfig
        {
            AiProvider = TagOf(ProviderBox, "groq"),
            AiApiKey = KeyBox.Password.Trim(),
            AiBaseUrl = BaseUrlBox.Text.Trim(),
            AiModel = ModelBox.Text.Trim(),
        };
        return c;
    }

    private async void LoadModels_Click(object sender, RoutedEventArgs e)
    {
        AiStatus.Text = "Loading models...";
        try
        {
            var models = await AiClient.ListModelsAsync(AiConfigFromForm());
            string current = ModelBox.Text;
            ModelBox.Items.Clear();
            foreach (var m in models) ModelBox.Items.Add(m);
            ModelBox.Text = current;
            AiStatus.Text = $"{models.Count} models found. Pick one from the list.";
            ModelBox.IsDropDownOpen = models.Count > 0;
        }
        catch (Exception ex) { AiStatus.Text = "Couldn't load models: " + ex.Message; }
    }

    private async void TestAi_Click(object sender, RoutedEventArgs e)
    {
        AiStatus.Text = "Asking...";
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            string reply = await AiClient.ChatAsync(AiConfigFromForm(),
                "You are a friendly assistant. Reply in one short sentence.",
                new List<(string, string)> { ("user", "Say hi and tell me which model you are.") }, cts.Token);
            AiStatus.Text = "✔ " + reply;
        }
        catch (Exception ex) { AiStatus.Text = "✘ " + ex.Message; }
    }

    private void Link_RequestNavigate(object sender, RequestNavigateEventArgs e)
    {
        if (e.Uri != null) CommandRouter.Open(e.Uri.AbsoluteUri);
        e.Handled = true;
    }

    // ------------------------------------------------------------------
    //  Shortcuts tab
    // ------------------------------------------------------------------

    private void AddShortcut_Click(object sender, RoutedEventArgs e)
    {
        var item = new ShortcutItem { Name = "New", Icon = "E8A7", Target = "" };
        _shortcuts.Add(item);
        ShortcutsGrid.SelectedItem = item;
        ShortcutsGrid.ScrollIntoView(item);
    }

    private void RemoveShortcut_Click(object sender, RoutedEventArgs e)
    {
        if (ShortcutsGrid.SelectedItem is ShortcutItem s) _shortcuts.Remove(s);
    }

    private void MoveUp_Click(object sender, RoutedEventArgs e)
    {
        if (ShortcutsGrid.SelectedItem is not ShortcutItem s) return;
        int i = _shortcuts.IndexOf(s);
        if (i > 0) _shortcuts.Move(i, i - 1);
    }

    // ------------------------------------------------------------------
    //  About tab
    // ------------------------------------------------------------------

    private void OpenFolder_Click(object sender, RoutedEventArgs e) => CommandRouter.Open(AppConfig.Folder);
    private void OpenLog_Click(object sender, RoutedEventArgs e) =>
        CommandRouter.Open("notepad.exe", $"\"{Path.Combine(AppConfig.Folder, "log.txt")}\"");

    // ------------------------------------------------------------------
    //  Helpers
    // ------------------------------------------------------------------

    private static void SelectByTag(ComboBox box, string tag, string fallback)
    {
        foreach (ComboBoxItem item in box.Items)
        {
            if (string.Equals(item.Tag as string, tag, StringComparison.OrdinalIgnoreCase))
            {
                box.SelectedItem = item;
                return;
            }
        }
        foreach (ComboBoxItem item in box.Items)
        {
            if (string.Equals(item.Tag as string, fallback, StringComparison.OrdinalIgnoreCase))
            {
                box.SelectedItem = item;
                return;
            }
        }
    }

    private static string TagOf(ComboBox box, string fallback) =>
        (box.SelectedItem as ComboBoxItem)?.Tag as string ?? fallback;

    private static List<string> SplitList(string text, char sep) =>
        (text ?? "").Replace("\r", "").Split(sep)
            .Select(x => x.Trim())
            .Where(x => x.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
}
