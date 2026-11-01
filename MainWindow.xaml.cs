using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Windows;
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
    private readonly string _dataDirectory = Path.Combine(AppContext.BaseDirectory, "Data");
    private ViewerSettings _settings = new();
    private bool _settingsLoaded;

    public MainWindow()
    {
        InitializeComponent();
        Width = GameWidth + 16;
        ZoomValueText.Text = $"{ZoomSlider.Value:P0}";
    }

    private async void Window_Loaded(object sender, RoutedEventArgs e)
    {
        try
        {
            Directory.CreateDirectory(_dataDirectory);
            var settingsPath = Path.Combine(_dataDirectory, "settings.json");
            if (File.Exists(settingsPath))
            {
                await using var settingsStream = File.OpenRead(settingsPath);
                _settings = await JsonSerializer.DeserializeAsync<ViewerSettings>(settingsStream)
                    ?? throw new InvalidDataException("設定ファイルの内容が空です。");
            }

            AlwaysOnTopCheckBox.IsChecked = _settings.AlwaysOnTop;
            MutedCheckBox.IsChecked = _settings.IsMuted;
            ZoomSlider.Value = _settings.ZoomFactor;
            Topmost = _settings.AlwaysOnTop;

            var browserDataDirectory = Path.Combine(_dataDirectory, "Browser");
            Directory.CreateDirectory(browserDataDirectory);
            var environment = await CoreWebView2Environment.CreateAsync(null, browserDataDirectory);
            await Browser.EnsureCoreWebView2Async(environment);
            Browser.CoreWebView2.IsMuted = _settings.IsMuted;
            Browser.ZoomFactor = _settings.ZoomFactor;
            await Browser.CoreWebView2.AddScriptToExecuteOnDocumentCreatedAsync(IsolateGameFrameScript);
            Browser.CoreWebView2.Navigate(GamePageUrl);

            _settingsLoaded = true;
            BrowserStatusText.Text = "御城プロジェクトRE を読み込みました。";
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

    private async void ScreenshotButton_Click(object sender, RoutedEventArgs e)
    {
        if (Browser.CoreWebView2 is null)
        {
            MessageBox.Show(this, "ゲーム画面の読み込みが完了してから撮影してください。", "スクリーンショット",
                MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        try
        {
            var screenshotDirectory = Path.Combine(AppContext.BaseDirectory, "Screenshots");
            Directory.CreateDirectory(screenshotDirectory);
            var filePath = Path.Combine(screenshotDirectory, $"Screenshot_{DateTime.Now:yyyyMMdd_HHmmss_fff}.png");
            await using var imageStream = File.Create(filePath);
            await Browser.CoreWebView2.CapturePreviewAsync(CoreWebView2CapturePreviewImageFormat.Png, imageStream);
            BrowserStatusText.Text = $"保存しました: {Path.GetFileName(filePath)}";
        }
        catch (UnauthorizedAccessException ex)
        {
            ShowScreenshotError(ex);
        }
        catch (IOException ex)
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
    }

    private void AlwaysOnTopCheckBox_Changed(object sender, RoutedEventArgs e)
    {
        Topmost = AlwaysOnTopCheckBox.IsChecked == true;
        _settings.AlwaysOnTop = Topmost;
        SaveSettings();
    }

    private void ZoomSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (ZoomValueText is null || ZoomSlider is null)
        {
            return;
        }

        ZoomValueText.Text = $"{ZoomSlider.Value:P0}";
        if (!_settingsLoaded)
        {
            return;
        }

        _settings.ZoomFactor = ZoomSlider.Value;
        Browser.ZoomFactor = ZoomSlider.Value;
        SaveSettings();
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

    private void SaveSettings()
    {
        if (!_settingsLoaded)
        {
            return;
        }

        try
        {
            var settingsPath = Path.Combine(_dataDirectory, "settings.json");
            var json = JsonSerializer.Serialize(_settings, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(settingsPath, json);
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
        public double ZoomFactor { get; set; } = 1;
    }
}
