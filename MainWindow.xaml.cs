using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Windows;
using System.Windows.Navigation;
using Microsoft.Web.WebView2.Core;

namespace ShiroProViewer;

public partial class MainWindow : Window
{
    private const double GameWidth = 1275;
    private const string GamePageUrl = "https://play.games.dmm.com/game/oshirore";
    private const string IsolateGameFrameScript = """
        (() => {
            const hideScrollbars = () => {
                const style = document.createElement('style');
                style.id = 'shiropro-hide-scrollbars';
                style.textContent = `
                    * { scrollbar-width: none !important; scrollbar-gutter: auto !important; }
                    ::-webkit-scrollbar { display: none !important; width: 0 !important; height: 0 !important; }
                `;
                (document.head || document.documentElement).appendChild(style);
            };
            if (document.readyState === 'loading') {
                document.addEventListener('DOMContentLoaded', hideScrollbars, { once: true });
            } else {
                hideScrollbars();
            }

            const isTopLevel = window.top === window;
            const isolateTarget = () => {
                const target = isTopLevel
                    ? document.querySelector('iframe#game_frame')
                    : document.querySelector('canvas#canvas.emscripten');
                if (!target) return false;

                let element = target;
                while (element && element !== document.body) {
                    const parent = element.parentElement;
                    if (!parent) break;
                    for (const sibling of parent.children) {
                        if (sibling !== element) sibling.style.setProperty('display', 'none', 'important');
                    }
                    element.style.setProperty('display', 'block', 'important');
                    element = parent;
                }

                document.documentElement.style.cssText = 'margin:0!important;padding:0!important;width:100%!important;height:100%!important;overflow:hidden!important';
                document.body.style.cssText = 'margin:0!important;padding:0!important;width:100%!important;height:100%!important;overflow:hidden!important';
                target.style.setProperty('position', 'fixed', 'important');
                target.style.setProperty('top', '0', 'important');
                target.style.setProperty('left', '0', 'important');
                target.style.setProperty('width', '1275px', 'important');
                target.style.setProperty('height', isTopLevel ? '720px' : 'auto', 'important');
                target.style.setProperty('border', '0', 'important');
                if (!isTopLevel) {
                    target.style.setProperty('aspect-ratio', '16 / 9', 'important');
                }
                return true;
            };

            const observer = new MutationObserver(() => {
                if (isolateTarget()) observer.disconnect();
            });
            if (!isolateTarget()) observer.observe(document, { childList: true, subtree: true });
        })();
        """;
    private static readonly int[] ZoomPercentages = [25, 50, 66, 75, 100, 125, 150, 175, 200, 250, 300, 400];
    private readonly string _dataDirectory = Path.Combine(AppContext.BaseDirectory, "Data");
    private ViewerSettings _settings = new();
    private bool _settingsLoaded;
    private bool _settingsRecovered;
    private bool _pageLoaded;
    private bool _isCapturing;
    private ulong _navigationId;
    private double _windowChromeWidth;
    private double _windowChromeHeight;

    private System.Windows.Controls.TextBlock BrowserStatusText => (System.Windows.Controls.TextBlock)MainTabs.Template.FindName("BrowserStatusText", MainTabs);
    private string ScreenshotDirectory => string.IsNullOrWhiteSpace(_settings.ScreenshotDirectory)
        ? Path.Combine(AppContext.BaseDirectory, "Screenshots")
        : _settings.ScreenshotDirectory;

    public MainWindow()
    {
        InitializeComponent();
        var version = typeof(MainWindow).Assembly.GetName().Version
            ?? throw new InvalidOperationException("アプリのバージョン情報を取得できません。");
        AppVersionTextBlock.Text = $"ShiroProViewer : Ver {version.ToString(3)}";
        Width = GameWidth + 16;
        ZoomComboBox.ItemsSource = ZoomPercentages;
        ZoomComboBox.SelectedItem = 100;
        ScreenshotDirectoryTextBox.Text = ScreenshotDirectory;
    }

    private void ProjectHomeHyperlink_RequestNavigate(object sender, RequestNavigateEventArgs e)
    {
        e.Handled = true;
        try
        {
            Process.Start(new ProcessStartInfo(e.Uri.AbsoluteUri) { UseShellExecute = true });
        }
        catch (Win32Exception ex)
        {
            MessageBox.Show(this, $"プロジェクトのリンクを開けませんでした。\n\n{ex.Message}",
                "リンクを開けません", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        catch (InvalidOperationException ex)
        {
            MessageBox.Show(this, $"プロジェクトのリンクを開けませんでした。\n\n{ex.Message}",
                "リンクを開けません", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private async void Window_Loaded(object sender, RoutedEventArgs e)
    {
        try
        {
            Directory.CreateDirectory(_dataDirectory);
            _settings = await LoadSettingsAsync();

            var zoomPercentage = ZoomPercentages.MinBy(value => Math.Abs(value / 100.0 - _settings.ZoomFactor));
            var zoomFactor = zoomPercentage / 100.0;
            var zoomAdjusted = _settings.ZoomFactor != zoomFactor;
            _settings.ZoomFactor = zoomFactor;
            AlwaysOnTopCheckBox.IsChecked = _settings.AlwaysOnTop;
            MutedCheckBox.IsChecked = _settings.IsMuted;
            DarkModeCheckBox.IsChecked = _settings.IsDarkMode;
            ApplyTheme();
            UpdateLayout();
            _windowChromeWidth = ActualWidth - Browser.ActualWidth;
            _windowChromeHeight = ActualHeight - Browser.ActualHeight;
            ZoomComboBox.SelectedItem = zoomPercentage;
            Topmost = _settings.AlwaysOnTop;
            ScreenshotDirectoryTextBox.Text = ScreenshotDirectory;

            var browserDataDirectory = Path.Combine(_dataDirectory, "Browser");
            Directory.CreateDirectory(browserDataDirectory);
            var environment = await CoreWebView2Environment.CreateAsync(null, browserDataDirectory);
            await Browser.EnsureCoreWebView2Async(environment);
            Browser.CoreWebView2.IsMuted = _settings.IsMuted;
            ApplyZoom();
            await Browser.CoreWebView2.AddScriptToExecuteOnDocumentCreatedAsync(IsolateGameFrameScript);
            Browser.CoreWebView2.Navigate(GamePageUrl);

            _settingsLoaded = true;
            if (zoomAdjusted || _settingsRecovered)
            {
                SaveSettings();
            }
            if (zoomAdjusted)
            {
                MessageBox.Show(this, $"保存されていた表示倍率を、最も近い選択肢の {zoomPercentage}% に変更しました。",
                    "表示倍率", MessageBoxButton.OK, MessageBoxImage.Information);
            }
        }
        catch (UnauthorizedAccessException ex)
        {
            ShowStartupError(ex);
        }
        catch (IOException ex)
        {
            ShowStartupError(ex);
        }
        catch (JsonException ex)
        {
            ShowStartupError(ex);
        }
        catch (WebView2RuntimeNotFoundException ex)
        {
            ShowStartupError(ex);
        }
        catch (COMException ex)
        {
            ShowStartupError(ex);
        }
        catch (InvalidOperationException ex)
        {
            ShowStartupError(ex);
        }
    }

    private void Browser_NavigationStarting(object? sender, CoreWebView2NavigationStartingEventArgs e)
    {
        _navigationId = e.NavigationId;
        _pageLoaded = false;
        BrowserStatusText.Text = "ページを読み込み中です。";
    }

    private void Browser_NavigationCompleted(object? sender, CoreWebView2NavigationCompletedEventArgs e)
    {
        if (e.NavigationId != _navigationId)
        {
            return;
        }

        _pageLoaded = e.IsSuccess;
        BrowserStatusText.Text = e.IsSuccess
            ? "ページの読み込みが完了しました。"
            : $"ページを読み込めませんでした: {e.WebErrorStatus}";
    }

    private async void ScreenshotButton_Click(object sender, RoutedEventArgs e)
    {
        var coreWebView = Browser.CoreWebView2;
        if (!_settingsLoaded || !_pageLoaded || coreWebView is null)
        {
            MessageBox.Show(this, "ページの読み込みが完了してから撮影してください。", "スクリーンショット",
                MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        if (_isCapturing)
        {
            MessageBox.Show(this, "撮影中です。保存が完了してから再度撮影してください。", "スクリーンショット",
                MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        _isCapturing = true;
        string? temporaryPath = null;
        try
        {
            var navigationId = _navigationId;
            var screenshotDirectory = ScreenshotDirectory;
            var filePath = Path.Combine(screenshotDirectory, $"Screenshot_{DateTime.Now:yyyyMMdd_HHmmss_fff}.png");
            using var imageStream = new MemoryStream();
            await coreWebView.CapturePreviewAsync(CoreWebView2CapturePreviewImageFormat.Png, imageStream);
            if (!_pageLoaded || navigationId != _navigationId)
            {
                throw new InvalidOperationException("撮影中にページが切り替わりました。読み込み完了後に再度撮影してください。");
            }
            if (imageStream.Length == 0)
            {
                throw new InvalidDataException("撮影した画像が空です。");
            }

            Directory.CreateDirectory(screenshotDirectory);
            temporaryPath = filePath + $".{Guid.NewGuid():N}.tmp";
            imageStream.Position = 0;
            using (var fileStream = new FileStream(temporaryPath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                imageStream.CopyTo(fileStream);
                fileStream.Flush(flushToDisk: true);
            }
            File.Move(temporaryPath, filePath);
            BrowserStatusText.Text = $"保存しました: {Path.GetFileName(filePath)}";
        }
        catch (ArgumentException ex)
        {
            ShowScreenshotError(ex);
        }
        catch (NotSupportedException ex)
        {
            ShowScreenshotError(ex);
        }
        catch (UnauthorizedAccessException ex)
        {
            ShowScreenshotError(ex);
        }
        catch (IOException ex)
        {
            ShowScreenshotError(ex);
        }
        catch (InvalidDataException ex)
        {
            ShowScreenshotError(ex);
        }
        catch (COMException ex)
        {
            ShowScreenshotError(ex);
        }
        catch (InvalidOperationException ex)
        {
            ShowScreenshotError(ex);
        }
        finally
        {
            _isCapturing = false;
            DeleteTemporaryFile(temporaryPath);
        }
    }

    private void ReloadButton_Click(object sender, RoutedEventArgs e)
    {
        if (!_settingsLoaded || Browser.CoreWebView2 is null)
        {
            MessageBox.Show(this, "ブラウザーの起動が完了してから再読み込みしてください。", "再読み込み",
                MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        try
        {
            Browser.CoreWebView2.Reload();
        }
        catch (COMException ex)
        {
            MessageBox.Show(this, $"再読み込みできませんでした。\n\n{ex.Message}", "再読み込みエラー",
                MessageBoxButton.OK, MessageBoxImage.Error);
        }
        catch (InvalidOperationException ex)
        {
            MessageBox.Show(this, $"再読み込みできませんでした。\n\n{ex.Message}", "再読み込みエラー",
                MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void SelectScreenshotDirectory_Click(object sender, RoutedEventArgs e)
    {
        if (!_settingsLoaded)
        {
            MessageBox.Show(this, "ブラウザーの起動が完了してから保存先を選択してください。", "保存先の設定",
                MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var dialog = new Microsoft.Win32.OpenFolderDialog
        {
            Title = "スクリーンショットの保存先を選択",
            InitialDirectory = Directory.Exists(ScreenshotDirectory) ? ScreenshotDirectory : AppContext.BaseDirectory,
            Multiselect = false
        };
        if (dialog.ShowDialog(this) == true)
        {
            _settings.ScreenshotDirectory = dialog.FolderName;
            ScreenshotDirectoryTextBox.Text = ScreenshotDirectory;
            SaveSettings();
        }
    }

    private void AlwaysOnTopCheckBox_Changed(object sender, RoutedEventArgs e)
    {
        Topmost = AlwaysOnTopCheckBox.IsChecked == true;
        _settings.AlwaysOnTop = Topmost;
        SaveSettings();
    }

    private void ZoomComboBox_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        if (!_settingsLoaded)
        {
            return;
        }

        if (ZoomComboBox.SelectedItem is not int zoomPercentage || !ZoomPercentages.Contains(zoomPercentage))
        {
            MessageBox.Show(this, "一覧から表示倍率を選択してください。", "表示倍率",
                MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        _settings.ZoomFactor = zoomPercentage / 100.0;
        ApplyZoom();
        SaveSettings();
    }

    private void ApplyZoom()
    {
        WindowState = WindowState.Normal;
        Browser.ZoomFactor = _settings.ZoomFactor;
        Browser.Width = GameWidth * _settings.ZoomFactor;
        Browser.Height = 720 * _settings.ZoomFactor;
        Width = Browser.Width + _windowChromeWidth;
        Height = Browser.Height + _windowChromeHeight;
    }

    private void MutedCheckBox_Changed(object sender, RoutedEventArgs e)
    {
        var isMuted = MutedCheckBox.IsChecked == true;
        _settings.IsMuted = isMuted;
        if (Browser.CoreWebView2 is not null)
        {
            Browser.CoreWebView2.IsMuted = isMuted;
        }

        SaveSettings();
    }

    private void DarkModeCheckBox_Changed(object sender, RoutedEventArgs e)
    {
        if (!_settingsLoaded)
        {
            return;
        }

        _settings.IsDarkMode = DarkModeCheckBox.IsChecked == true;
        var selectedTab = MainTabs.SelectedIndex;
        MainTabs.SelectedIndex = 0;
        ApplyTheme();
        Browser.ClearValue(WidthProperty);
        Browser.ClearValue(HeightProperty);
        UpdateLayout();
        _windowChromeWidth = ActualWidth - Browser.ActualWidth;
        _windowChromeHeight = ActualHeight - Browser.ActualHeight;
        ApplyZoom();
        MainTabs.SelectedIndex = selectedTab;
        SaveSettings();
    }

    private void ApplyTheme()
    {
#pragma warning disable WPF0001
        ThemeMode = _settings.IsDarkMode ? System.Windows.ThemeMode.Dark : System.Windows.ThemeMode.Light;
#pragma warning restore WPF0001
    }

    private async Task<ViewerSettings> LoadSettingsAsync()
    {
        var settingsPath = Path.Combine(_dataDirectory, "settings.json");
        var backupPath = settingsPath + ".bak";
        if (!File.Exists(settingsPath) && !File.Exists(backupPath))
        {
            return new ViewerSettings();
        }

        try
        {
            return await ReadSettingsAsync(settingsPath);
        }
        catch (Exception ex) when (ex is JsonException or InvalidDataException or FileNotFoundException)
        {
            _settingsRecovered = true;
            ViewerSettings recoveredSettings;
            try
            {
                recoveredSettings = await ReadSettingsAsync(backupPath);
            }
            catch (Exception backupException) when (backupException is JsonException or InvalidDataException or IOException or UnauthorizedAccessException)
            {
                MessageBox.Show(this,
                    $"設定ファイルを読み込めず、バックアップからも復旧できませんでした。初期設定で起動します。\n\n設定: {ex.Message}\nバックアップ: {backupException.Message}",
                    "設定の復旧", MessageBoxButton.OK, MessageBoxImage.Warning);
                return new ViewerSettings();
            }

            MessageBox.Show(this, $"設定ファイルを読み込めなかったため、前回保存時のバックアップから復旧しました。\n\n{ex.Message}",
                "設定の復旧", MessageBoxButton.OK, MessageBoxImage.Warning);
            return recoveredSettings;
        }
    }

    private static async Task<ViewerSettings> ReadSettingsAsync(string settingsPath)
    {
        await using var settingsStream = File.OpenRead(settingsPath);
        var settings = await JsonSerializer.DeserializeAsync<ViewerSettings>(settingsStream)
            ?? throw new InvalidDataException("設定ファイルの内容が空です。");
        if (!double.IsFinite(settings.ZoomFactor) || settings.ZoomFactor <= 0)
        {
            throw new InvalidDataException("設定ファイルの表示倍率が不正です。");
        }

        if (!string.IsNullOrWhiteSpace(settings.ScreenshotDirectory))
        {
            try
            {
                var fullPath = Path.GetFullPath(settings.ScreenshotDirectory);
                var root = Path.GetPathRoot(fullPath)
                    ?? throw new ArgumentException("保存先のルートを取得できません。");
                var components = fullPath[root.Length..].Split(
                    [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar], StringSplitOptions.None);
                if (components.Any(component => component.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0))
                {
                    throw new ArgumentException("保存先に使用できない文字が含まれています。");
                }
                settings.ScreenshotDirectory = fullPath;
            }
            catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
            {
                throw new InvalidDataException($"設定ファイルのスクリーンショット保存先が不正です。{ex.Message}", ex);
            }
        }

        return settings;
    }

    private void SaveSettings()
    {
        if (!_settingsLoaded)
        {
            return;
        }

        var settingsPath = Path.Combine(_dataDirectory, "settings.json");
        var temporaryPath = settingsPath + $".{Guid.NewGuid():N}.tmp";
        try
        {
            using (var settingsStream = new FileStream(temporaryPath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                JsonSerializer.Serialize(settingsStream, _settings, new JsonSerializerOptions { WriteIndented = true });
                settingsStream.Flush(flushToDisk: true);
            }
            if (File.Exists(settingsPath))
            {
                File.Replace(temporaryPath, settingsPath, _settingsRecovered ? null : settingsPath + ".bak");
            }
            else
            {
                File.Move(temporaryPath, settingsPath);
            }
            _settingsRecovered = false;
        }
        catch (UnauthorizedAccessException ex)
        {
            MessageBox.Show(this, $"設定を保存できませんでした。アプリの保存先に書き込み権限があるか確認してください。\n\n{ex.Message}",
                "設定の保存エラー", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        catch (IOException ex)
        {
            MessageBox.Show(this, $"設定を保存できませんでした。\n\n{ex.Message}",
                "設定の保存エラー", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            DeleteTemporaryFile(temporaryPath);
        }
    }

    private void DeleteTemporaryFile(string? temporaryPath)
    {
        if (temporaryPath is null)
        {
            return;
        }

        try
        {
            File.Delete(temporaryPath);
        }
        catch (UnauthorizedAccessException ex)
        {
            MessageBox.Show(this, $"一時ファイルを削除できませんでした。\n{temporaryPath}\n\n{ex.Message}",
                "一時ファイルの削除エラー", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        catch (IOException ex)
        {
            MessageBox.Show(this, $"一時ファイルを削除できませんでした。\n{temporaryPath}\n\n{ex.Message}",
                "一時ファイルの削除エラー", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void ShowStartupError(Exception exception)
    {
        MessageBox.Show(this,
            $"ブラウザーを起動できませんでした。アプリの保存先に書き込み権限があること、および WebView2 ランタイムがインストールされていることを確認してください。\n\n{exception.Message}",
            "起動エラー", MessageBoxButton.OK, MessageBoxImage.Error);
    }

    private void ShowScreenshotError(Exception exception)
    {
        MessageBox.Show(this, $"スクリーンショットを保存できませんでした。\n\n{exception.Message}",
            "保存エラー", MessageBoxButton.OK, MessageBoxImage.Error);
    }

    private sealed class ViewerSettings
    {
        public bool AlwaysOnTop { get; set; }
        public bool IsMuted { get; set; }
        public bool IsDarkMode { get; set; } = true;
        public double ZoomFactor { get; set; } = 1;
        public string? ScreenshotDirectory { get; set; }
    }
}
