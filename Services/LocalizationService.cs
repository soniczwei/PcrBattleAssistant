using System.Globalization;
using System.IO;
using System.Text.Json;
using CommunityToolkit.Mvvm.ComponentModel;

namespace PcrBattleAssistant.Services;

public enum AppLanguage
{
    ZhCn = 0,
    JaJp = 1,
    EnUs = 2
}

public sealed record LanguageOption(AppLanguage Language, string DisplayName);

/// <summary>
/// 多语言本地化服务：支持 简体中文 / 日本語 / English 实时切换，自动持久化用户选择并支持首次启动按系统语言匹配
/// </summary>
public partial class LocalizationService : ObservableObject
{
    private static readonly string SettingsFilePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "PcrBattleAssistant",
        "settings.json");

    [ObservableProperty]
    private AppLanguage _currentLanguage;

    public LocalizationService()
    {
        _currentLanguage = LoadSavedLanguageOrDetectSystem();
    }

    public IReadOnlyList<LanguageOption> AvailableLanguages { get; } =
    [
        new(AppLanguage.ZhCn, "简体中文"),
        new(AppLanguage.JaJp, "日本語"),
        new(AppLanguage.EnUs, "English")
    ];

    public string ScanButtonText => Get("ScanButtonText");
    public string UploadButtonText => Get("UploadButtonText");
    public string DebugModeText => Get("DebugModeText");
    public string UpdateCharsText => Get("UpdateCharsText");
    public string AttackLabel => Get("AttackLabel");
    public string DefenseLabel => Get("DefenseLabel");
    public string AnalyzeZoneTitle => Get("AnalyzeZoneTitle");
    public string WaitingScreenshotText => Get("WaitingScreenshotText");
    public string AttackSliceTitle => Get("AttackSliceTitle");
    public string DefenseSliceTitle => Get("DefenseSliceTitle");
    public string CropSliceTooltip => Get("CropSliceTooltip");
    public string MatchedTemplateTooltip => Get("MatchedTemplateTooltip");
    public string QueryTabTitle => Get("QueryTabTitle");
    public string UploadTabTitle => Get("UploadTabTitle");
    public string RowUploadBtnText => Get("RowUploadBtnText");
    public string SwapSidesTooltip => Get("SwapSidesTooltip");
    public string PrioritizeTrustedText => Get("PrioritizeTrustedText");
    public string InitOverlayTitle => Get("InitOverlayTitle");

    public string GetTeamTitle(int teamNumber) => CurrentLanguage switch
    {
        AppLanguage.JaJp => $"パーティ{teamNumber}",
        AppLanguage.EnUs => $"Team {teamNumber}",
        _ => $"队伍{teamNumber}"
    };

    public string Get(string key, params object[] args)
    {
        string template = CurrentLanguage switch
        {
            AppLanguage.JaJp => JaStrings.GetValueOrDefault(key, key),
            AppLanguage.EnUs => EnStrings.GetValueOrDefault(key, key),
            _ => ZhStrings.GetValueOrDefault(key, key)
        };

        return args.Length > 0 ? string.Format(template, args) : template;
    }

    partial void OnCurrentLanguageChanged(AppLanguage value)
    {
        SaveLanguagePreference(value);
        // 通知所有本地化属性即时刷新
        OnPropertyChanged(string.Empty);
    }

    private static AppLanguage LoadSavedLanguageOrDetectSystem()
    {
        try
        {
            if (File.Exists(SettingsFilePath))
            {
                string json = File.ReadAllText(SettingsFilePath);
                using JsonDocument doc = JsonDocument.Parse(json);
                if (doc.RootElement.TryGetProperty("language", out JsonElement langEl) &&
                    Enum.TryParse(langEl.GetString(), ignoreCase: true, out AppLanguage saved))
                {
                    return saved;
                }
            }
        }
        catch
        {
        }

        // 首次启动时根据系统 UI 语言自动匹配
        return CultureInfo.CurrentUICulture.TwoLetterISOLanguageName.ToLowerInvariant() switch
        {
            "ja" => AppLanguage.JaJp,
            "zh" => AppLanguage.ZhCn,
            _ => AppLanguage.EnUs
        };
    }

    public static bool LoadPrioritizeTrusted()
    {
        try
        {
            if (File.Exists(SettingsFilePath))
            {
                using JsonDocument doc = JsonDocument.Parse(File.ReadAllText(SettingsFilePath));
                if (doc.RootElement.TryGetProperty("prioritizeTrusted", out JsonElement el) &&
                    el.ValueKind == JsonValueKind.True)
                {
                    return true;
                }
            }
        }
        catch
        {
        }
        return false;
    }

    public static void SavePrioritizeTrusted(bool enabled)
    {
        UpdateSettingsFile(prioritizeTrusted: enabled);
    }

    private static void SaveLanguagePreference(AppLanguage language)
    {
        UpdateSettingsFile(language: language);
    }

    private static void UpdateSettingsFile(AppLanguage? language = null, bool? prioritizeTrusted = null)
    {
        try
        {
            string? dir = Path.GetDirectoryName(SettingsFilePath);
            if (!string.IsNullOrEmpty(dir))
            {
                Directory.CreateDirectory(dir);
            }

            string existingLang = LoadSavedLanguageOrDetectSystem().ToString();
            bool existingPrioritize = LoadPrioritizeTrusted();

            var payload = new
            {
                language = language?.ToString() ?? existingLang,
                prioritizeTrusted = prioritizeTrusted ?? existingPrioritize
            };
            File.WriteAllText(SettingsFilePath, JsonSerializer.Serialize(payload));
        }
        catch
        {
        }
    }

    private static readonly Dictionary<string, string> ZhStrings = new()
    {
        ["ScanButtonText"] = "扫描",
        ["UploadButtonText"] = "战绩上传",
        ["DebugModeText"] = "调试模式",
        ["UpdateCharsText"] = "更新角色",
        ["AttackLabel"] = "进攻",
        ["DefenseLabel"] = "防守",
        ["AnalyzeZoneTitle"] = "正在分析的区间",
        ["WaitingScreenshotText"] = "等待扫描截图...",
        ["AttackSliceTitle"] = "进攻方切片与匹配度",
        ["DefenseSliceTitle"] = "防守方切片与匹配度",
        ["CropSliceTooltip"] = "原图切片",
        ["MatchedTemplateTooltip"] = "匹配模板",
        ["StatusReady"] = "就绪",
        ["StatusScanning"] = "正在扫描...",
        ["StatusScanningUpload"] = "正在扫描上传...",
        ["StatusWebViewNotReady"] = "WebView 尚未就绪",
        ["StatusNeedUploadPage"] = "请先在网页点击「上传」",
        ["StatusCheckingUpdate"] = "正在检查角色更新...",
        ["StatusUpdatedCount"] = "已更新 {0} 张角色",
        ["StatusAutoUpdatedCount"] = "已自动更新 {0} 张角色",
        ["StatusCheckFailed"] = "检查失败，请检查网络",
        ["StatusAlreadyLatest"] = "角色库已是最新",
        ["StatusUpdateError"] = "更新异常：{0}",
        ["StatusRecognizeFailed"] = "识别失败：{0}",
        ["StatusWebViewInitFailed"] = "WebView2 初始化失败（请确认已安装 WebView2 Runtime）：{0}",
        ["ModeArena"] = "バトルアリーナ模式",
        ["ModePrincess"] = "プリンセスアリーナ模式",
        ["SyncDownloading3x"] = "正在下载 3★ 角色雪碧图...",
        ["SyncDownloading6x"] = "正在下载 6★ 角色雪碧图...",
        ["SyncSlicing"] = "正在本地切分角色并重载特征库...",
        ["ErrGameNotFound"] = "未找到正在运行的 {0}.exe，请先启动游戏",
        ["ErrWindowHandleNotFound"] = "已检测到 {0}.exe，但尚未获取到主窗口句柄",
        ["ErrClientRectFailed"] = "无法获取游戏窗口客户区尺寸",
        ["ErrClientSizeInvalid"] = "游戏窗口客户区尺寸异常 ({0}x{1})",
        ["ErrCaptureFailed"] = "捕获游戏窗口画面失败",
        ["QueryTabTitle"] = "作业查询",
        ["UploadTabTitle"] = "战绩上传",
        ["RowUploadBtnText"] = "上传",
        ["StatusUploadTabNotReady"] = "上传页面尚未就绪",
        ["StatusFilledUploadTeam"] = "已填入第 {0} 队攻防阵容",
        ["SwapSidesTooltip"] = "互换进攻与防守",
        ["StatusSwappedSides"] = "已互换进攻与防守阵容",
        ["PrioritizeTrustedText"] = "信任置顶",
        ["InitOverlayTitle"] = "正在初始化",
        ["StatusInitializingIcons"] = "正在初始化角色头像库，请稍候...",
        ["StatusExtractingSpriteMap"] = "正在从页面提取角色坐标并切分头像..."
    };

    private static readonly Dictionary<string, string> JaStrings = new()
    {
        ["ScanButtonText"] = "スキャン",
        ["UploadButtonText"] = "戦績登録",
        ["DebugModeText"] = "デバッグ",
        ["UpdateCharsText"] = "キャラ更新",
        ["AttackLabel"] = "攻撃",
        ["DefenseLabel"] = "防衛",
        ["AnalyzeZoneTitle"] = "解析エリア",
        ["WaitingScreenshotText"] = "スキャン待機中...",
        ["AttackSliceTitle"] = "攻撃側スライス・一致率",
        ["DefenseSliceTitle"] = "防衛側スライス・一致率",
        ["CropSliceTooltip"] = "元画像",
        ["MatchedTemplateTooltip"] = "一致テンプレート",
        ["StatusReady"] = "待機中",
        ["StatusScanning"] = "スキャン中...",
        ["StatusScanningUpload"] = "登録用スキャン中...",
        ["StatusWebViewNotReady"] = "WebView 準備中",
        ["StatusNeedUploadPage"] = "先にWeb側で「上传」を開いてください",
        ["StatusCheckingUpdate"] = "キャラ更新を確認中...",
        ["StatusUpdatedCount"] = "{0} 枚のキャラを更新しました",
        ["StatusAutoUpdatedCount"] = "{0} 枚のキャラを自動更新しました",
        ["StatusCheckFailed"] = "確認失敗：ネットワークを確認してください",
        ["StatusAlreadyLatest"] = "キャラデータは最新です",
        ["StatusUpdateError"] = "更新エラー：{0}",
        ["StatusRecognizeFailed"] = "認識失敗：{0}",
        ["StatusWebViewInitFailed"] = "WebView2 初期化失敗（WebView2 Runtime を確認してください）：{0}",
        ["ModeArena"] = "バトルアリーナモード",
        ["ModePrincess"] = "プリンセスアリーナモード",
        ["SyncDownloading3x"] = "3★ スプライト画像をダウンロード中...",
        ["SyncDownloading6x"] = "6★ スプライト画像をダウンロード中...",
        ["SyncSlicing"] = "画像をローカル分割・再読み込み中...",
        ["ErrGameNotFound"] = "{0}.exe が見つかりません。先にゲームを起動してください",
        ["ErrWindowHandleNotFound"] = "{0}.exe を検出しましたが、ウィンドウを取得できませんでした",
        ["ErrClientRectFailed"] = "ゲームウィンドウのサイズを取得できませんでした",
        ["ErrClientSizeInvalid"] = "ゲームウィンドウのサイズが異常です ({0}x{1})",
        ["ErrCaptureFailed"] = "ゲーム画面のキャプチャに失敗しました",
        ["QueryTabTitle"] = "編成検索",
        ["UploadTabTitle"] = "戦績登録",
        ["RowUploadBtnText"] = "登録",
        ["StatusUploadTabNotReady"] = "登録ページ準備中",
        ["StatusFilledUploadTeam"] = "パーティ{0}の攻防を入力しました",
        ["SwapSidesTooltip"] = "攻撃と防衛を入れ替え",
        ["StatusSwappedSides"] = "攻撃と防衛を入れ替えました",
        ["PrioritizeTrustedText"] = "信頼優先",
        ["InitOverlayTitle"] = "初期化中",
        ["StatusInitializingIcons"] = "キャラアイコンを初期化しています。しばらくお待ちください...",
        ["StatusExtractingSpriteMap"] = "ページから座標を抽出して画像を分割中..."
    };

    private static readonly Dictionary<string, string> EnStrings = new()
    {
        ["ScanButtonText"] = "Scan",
        ["UploadButtonText"] = "Upload",
        ["DebugModeText"] = "Debug",
        ["UpdateCharsText"] = "Update Icons",
        ["AttackLabel"] = "Attack",
        ["DefenseLabel"] = "Defense",
        ["AnalyzeZoneTitle"] = "Analyze Zone",
        ["WaitingScreenshotText"] = "Waiting for scan...",
        ["AttackSliceTitle"] = "Attack Slices & Confidence",
        ["DefenseSliceTitle"] = "Defense Slices & Confidence",
        ["CropSliceTooltip"] = "Cropped",
        ["MatchedTemplateTooltip"] = "Matched",
        ["StatusReady"] = "Ready",
        ["StatusScanning"] = "Scanning...",
        ["StatusScanningUpload"] = "Scanning for upload...",
        ["StatusWebViewNotReady"] = "WebView is not ready",
        ["StatusNeedUploadPage"] = "Open the Upload page in WebView first",
        ["StatusCheckingUpdate"] = "Checking icon updates...",
        ["StatusUpdatedCount"] = "Updated {0} icons",
        ["StatusAutoUpdatedCount"] = "Auto-updated {0} icons",
        ["StatusCheckFailed"] = "Update check failed, check network",
        ["StatusAlreadyLatest"] = "Icons are up to date",
        ["StatusUpdateError"] = "Update error: {0}",
        ["StatusRecognizeFailed"] = "Recognition failed: {0}",
        ["StatusWebViewInitFailed"] = "WebView2 init failed (check WebView2 Runtime): {0}",
        ["ModeArena"] = "Battle Arena Mode",
        ["ModePrincess"] = "Princess Arena Mode",
        ["SyncDownloading3x"] = "Downloading 3★ sprite sheet...",
        ["SyncDownloading6x"] = "Downloading 6★ sprite sheet...",
        ["SyncSlicing"] = "Slicing icons & reloading templates...",
        ["ErrGameNotFound"] = "{0}.exe not found. Please launch the game first",
        ["ErrWindowHandleNotFound"] = "Detected {0}.exe, but main window handle is unavailable",
        ["ErrClientRectFailed"] = "Failed to get game window client rect",
        ["ErrClientSizeInvalid"] = "Invalid game window size ({0}x{1})",
        ["ErrCaptureFailed"] = "Failed to capture game window",
        ["QueryTabTitle"] = "Battle Query",
        ["UploadTabTitle"] = "Upload Result",
        ["RowUploadBtnText"] = "Upload",
        ["StatusUploadTabNotReady"] = "Upload page is not ready",
        ["StatusFilledUploadTeam"] = "Filled Team {0} Attack & Defense",
        ["SwapSidesTooltip"] = "Swap Attack & Defense",
        ["StatusSwappedSides"] = "Swapped Attack & Defense",
        ["PrioritizeTrustedText"] = "Trusted First",
        ["InitOverlayTitle"] = "Initializing",
        ["StatusInitializingIcons"] = "Initializing character icons, please wait...",
        ["StatusExtractingSpriteMap"] = "Extracting sprite coordinates & slicing icons..."
    };
}
