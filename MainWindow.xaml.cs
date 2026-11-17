using System.IO;
using System.Text.Json;
using System.Windows;
using Microsoft.Web.WebView2.Core;
using PcrBattleAssistant.Services;
using PcrBattleAssistant.ViewModels;

namespace PcrBattleAssistant;

public partial class MainWindow : Window
{
    private readonly AvatarRecognitionService _recognitionService;
    private readonly MainViewModel _viewModel;
    private Task _startupSyncTask = Task.CompletedTask;

    public MainWindow()
    {
        InitializeComponent();

        _recognitionService = new AvatarRecognitionService();
        _viewModel = new MainViewModel(_recognitionService);

        DataContext = _viewModel;

        Loaded += MainWindow_Loaded;
        Closed += MainWindow_Closed;
    }

    private async void MainWindow_Loaded(object sender, RoutedEventArgs e)
    {
        _startupSyncTask = _viewModel.AutoCheckAndSyncIconsOnStartupAsync();
        _ = _viewModel.AutoCheckAppUpdateOnStartupAsync();
        await InitializeWebView2Async();
    }

    private async Task InitializeWebView2Async()
    {
        try
        {
            string userDataFolder = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "PcrBattleAssistant",
                "WebView2Data");

            CoreWebView2Environment env = await CoreWebView2Environment.CreateAsync(null, userDataFolder);
            string purifyScript = WebViewBridgeService.GetDocumentCreatedPurifyScript();

            // 1. 初始化 Tab 1：作业查询 WebView2 (/battle)
            await BattleQueryWebView.EnsureCoreWebView2Async(env);
            await BattleQueryWebView.CoreWebView2.AddScriptToExecuteOnDocumentCreatedAsync(purifyScript);
            _viewModel.ExecuteQueryWebViewScriptAsync = async script =>
                BattleQueryWebView.CoreWebView2 is null
                    ? null
                    : await BattleQueryWebView.CoreWebView2.ExecuteScriptAsync(script);

            _viewModel.EvaluateQueryWebViewAsync = async script =>
            {
                if (BattleQueryWebView.CoreWebView2 is null)
                {
                    return null;
                }

                string args = JsonSerializer.Serialize(new
                {
                    expression = script,
                    awaitPromise = true,
                    returnByValue = true
                });
                string cdpResult = await BattleQueryWebView.CoreWebView2.CallDevToolsProtocolMethodAsync("Runtime.evaluate", args);
                using JsonDocument doc = JsonDocument.Parse(cdpResult);
                if (doc.RootElement.TryGetProperty("result", out JsonElement resEl) &&
                    resEl.TryGetProperty("value", out JsonElement valEl))
                {
                    return valEl.ValueKind == JsonValueKind.String ? valEl.GetString() : valEl.GetRawText();
                }

                return null;
            };

            BattleQueryWebView.CoreWebView2.NavigationCompleted += CoreWebView2_NavigationCompleted;
            BattleQueryWebView.CoreWebView2.Navigate(WebViewBridgeService.BattlePageUrl);

            // 2. 初始化 Tab 2：战绩上传 WebView2 (/battle/upload)，共用同一个 Environment 以共享登录态
            await BattleUploadWebView.EnsureCoreWebView2Async(env);
            await BattleUploadWebView.CoreWebView2.AddScriptToExecuteOnDocumentCreatedAsync(purifyScript);
            _viewModel.ExecuteUploadWebViewScriptAsync = async script =>
                BattleUploadWebView.CoreWebView2 is null
                    ? null
                    : await BattleUploadWebView.CoreWebView2.ExecuteScriptAsync(script);

            _viewModel.NavigateUploadWebViewToUploadPage = () =>
                BattleUploadWebView.CoreWebView2?.Navigate(WebViewBridgeService.UploadPageUrl);

            BattleUploadWebView.CoreWebView2.Navigate(WebViewBridgeService.UploadPageUrl);
        }
        catch (Exception ex)
        {
            _viewModel.IsInitializing = false;
            _viewModel.StatusMessage = _viewModel.Loc.Get("StatusWebViewInitFailed", ex.Message);
        }
    }

    private async void CoreWebView2_NavigationCompleted(object? sender, CoreWebView2NavigationCompletedEventArgs e)
    {
        if (!e.IsSuccess || BattleQueryWebView.CoreWebView2 is null)
        {
            _viewModel.IsInitializing = false;
            return;
        }

        string url = BattleQueryWebView.CoreWebView2.Source ?? string.Empty;
        if (!url.Contains("/battle", StringComparison.OrdinalIgnoreCase) ||
            url.Contains("/battle/upload", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        try
        {
            await _startupSyncTask;
            if (_viewModel.NeedsSpriteMapSync)
            {
                await Task.Delay(300);
                await _viewModel.TrySyncSpriteMapFromWebViewAsync();
            }
        }
        finally
        {
            _viewModel.IsInitializing = false;
        }

        await _viewModel.ApplyPrioritizeTrustedAsync(_viewModel.PrioritizeTrusted);
    }

    private void MainWindow_Closed(object? sender, EventArgs e)
    {
        _viewModel.Dispose();
    }
}
