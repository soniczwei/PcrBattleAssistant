using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Windows.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PcrBattleAssistant.Models;
using PcrBattleAssistant.Services;

namespace PcrBattleAssistant.ViewModels;

/// <summary>
/// 主界面 ViewModel：
/// 支持「扫码」内存截取 PrincessConnectReDive.exe 窗口、调试模式详情与区间可视化、
/// バトルアリーナ模式（图1）与 プリンセスアリーナ模式（图2）自动识图录入
/// </summary>
public partial class MainViewModel : ObservableObject, IDisposable
{
    private readonly AvatarRecognitionService _recognitionService;

    /// <summary>
    /// Tab 1（作业查询 /battle）WebView2 脚本执行委托
    /// </summary>
    public Func<string, Task<string?>>? ExecuteQueryWebViewScriptAsync { get; set; }

    /// <summary>
    /// Tab 1（作业查询 /battle）支持 await Promise 的异步脚本求值委托
    /// </summary>
    public Func<string, Task<string?>>? EvaluateQueryWebViewAsync { get; set; }

    /// <summary>
    /// Tab 2（战绩上传 /battle/upload）WebView2 脚本执行委托
    /// </summary>
    public Func<string, Task<string?>>? ExecuteUploadWebViewScriptAsync { get; set; }

    /// <summary>
    /// 导航/重置 Tab 2 到战绩上传页面
    /// </summary>
    public Action? NavigateUploadWebViewToUploadPage { get; set; }

    /// <summary>
    /// 当前激活的主工作区 Tab（0 = 作业查询, 1 = 战绩上传）
    /// </summary>
    [ObservableProperty]
    private int _selectedWebTabIndex;

    /// <summary>
    /// 多语言本地化服务
    /// </summary>
    public LocalizationService Loc { get; } = new();

    /// <summary>
    /// 窗口标题：PcrBattleAssistant + 版本号
    /// </summary>
    public string WindowTitle { get; }

    /// <summary>
    /// 左侧：进攻方识别出的队伍（队伍1 / 队伍2 / 队伍3，战绩上传使用）
    /// </summary>
    public ObservableCollection<RecognizedTeamGroup> AttackTeamGroups { get; } = [];

    /// <summary>
    /// 右侧：防守方识别出的队伍（队伍1 / 队伍2 / 队伍3，扫码查作业使用）
    /// </summary>
    public ObservableCollection<RecognizedTeamGroup> DefenseTeamGroups { get; } = [];

    [ObservableProperty]
    private string _statusMessage = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsNotBusy))]
    private bool _isBusy;

    public bool IsNotBusy => !IsBusy;

    [ObservableProperty]
    private bool _isInitializing;

    [ObservableProperty]
    private string _initializationMessage = string.Empty;

    public bool NeedsSpriteMapSync => !_recognitionService.HasStar3SpriteMap();

    /// <summary>
    /// 调试模式开关
    /// </summary>
    [ObservableProperty]
    private bool _isDebugMode;

    /// <summary>
    /// 信任作业置顶开关（点赞过/自己上传的作业排在当前页最前）
    /// </summary>
    [ObservableProperty]
    private bool _prioritizeTrusted;

    partial void OnPrioritizeTrustedChanged(bool value)
    {
        LocalizationService.SavePrioritizeTrusted(value);
        _ = ApplyPrioritizeTrustedAsync(value);
    }

    public async Task ApplyPrioritizeTrustedAsync(bool enabled)
    {
        if (ExecuteQueryWebViewScriptAsync is null)
        {
            return;
        }

        try
        {
            await ExecuteQueryWebViewScriptAsync(WebViewBridgeService.BuildSetPrioritizeTrustedScript(enabled));
        }
        catch
        {
        }
    }

    /// <summary>
    /// 调试模式标注后的完整截图（保存在内存中）
    /// </summary>
    [ObservableProperty]
    private BitmapSource? _debugAnnotatedImage;

    /// <summary>
    /// 当前识别到的场景类型
    /// </summary>
    [ObservableProperty]
    private BattleSceneType _currentSceneType = BattleSceneType.ArenaThreeOpponents;

    [ObservableProperty]
    private string _currentSceneBadge = string.Empty;

    [ObservableProperty]
    private int _loadedTemplateCount;

    [ObservableProperty]
    private bool _hasAppUpdate;

    [ObservableProperty]
    private string _newVersionButtonText = "NEW";

    private string _latestReleaseUrl = AppUpdateService.ReleasesPageUrl;
    private readonly Version _currentAppVersion;

    public MainViewModel(AvatarRecognitionService recognitionService)
    {
        _recognitionService = recognitionService;

        Version? ver = Assembly.GetExecutingAssembly().GetName().Version;
        _currentAppVersion = ver ?? new Version(1, 0, 0);
        string verText = $"{_currentAppVersion.Major}.{_currentAppVersion.Minor}.{_currentAppVersion.Build}";
        WindowTitle = $"PcrBattleAssistant v{verText}";
        _statusMessage = Loc.Get("StatusReady");
        _prioritizeTrusted = LocalizationService.LoadPrioritizeTrusted();

        Loc.PropertyChanged += (_, e) =>
        {
            if (string.IsNullOrEmpty(e.PropertyName))
            {
                RefreshTeamTitlesForLanguage();
                if (!IsBusy && string.IsNullOrEmpty(CurrentSceneBadge))
                {
                    StatusMessage = Loc.Get("StatusReady");
                }
            }
        };

        for (int i = 1; i <= 3; i++)
        {
            AttackTeamGroups.Add(new RecognizedTeamGroup(i, Loc.GetTeamTitle(i))
            {
                IsSelected = i == 1
            });
            DefenseTeamGroups.Add(new RecognizedTeamGroup(i, Loc.GetTeamTitle(i))
            {
                IsSelected = i == 1
            });
        }

        ReloadLocalLibrary();
        if (LoadedTemplateCount == 0 || NeedsSpriteMapSync)
        {
            IsInitializing = true;
            InitializationMessage = Loc.Get("StatusInitializingIcons");
        }
    }

    private void RefreshTeamTitlesForLanguage()
    {
        for (int i = 0; i < AttackTeamGroups.Count; i++)
        {
            AttackTeamGroups[i].Title = Loc.GetTeamTitle(i + 1);
        }
        for (int i = 0; i < DefenseTeamGroups.Count; i++)
        {
            DefenseTeamGroups[i].Title = Loc.GetTeamTitle(i + 1);
        }
    }

    /// <summary>
    /// 统一扫描入口：捕获 PrincessConnectReDive.exe 窗口客户区到内存并自动识别场景
    /// </summary>
    [RelayCommand]
    public async Task CaptureGameWindowAsync()
    {
        if (IsBusy)
        {
            return;
        }

        StatusMessage = Loc.Get("StatusScanning");
        var (capturedBitmap, errorKey, errorArgs) = await Task.Run(GameWindowCaptureService.CaptureGameWindowToMemory);

        if (capturedBitmap is null)
        {
            StatusMessage = Loc.Get(errorKey, errorArgs);
            return;
        }

        await ProcessUnifiedScanAsync(capturedBitmap);
    }

    /// <summary>
    /// 重新加载本地头像特征库
    /// </summary>
    [RelayCommand]
    public void ReloadLocalLibrary()
    {
        LoadedTemplateCount = _recognitionService.LoadLocalLibrary();
    }

    /// <summary>
    /// 启动时后台检查 GitHub Releases 是否有新版本，有则显示 NEW 按钮供跳转下载
    /// </summary>
    public async Task AutoCheckAppUpdateOnStartupAsync()
    {
        AppUpdateCheckResult result = await AppUpdateService.CheckForUpdateAsync(_currentAppVersion);
        if (result.HasUpdate)
        {
            _latestReleaseUrl = result.ReleaseUrl;
            NewVersionButtonText = $"NEW {result.LatestTag}";
            HasAppUpdate = true;
        }
    }

    /// <summary>
    /// 跳转到 GitHub Releases 最新发布页面
    /// </summary>
    [RelayCommand]
    public void OpenLatestRelease()
    {
        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = _latestReleaseUrl,
                UseShellExecute = true
            });
        }
        catch
        {
        }
    }

    /// <summary>
    /// 启动时后台静默检查远程头像资源版本；若有更新则自动下载切分并热重载特征库
    /// </summary>
    public async Task AutoCheckAndSyncIconsOnStartupAsync()
    {
        try
        {
            var progress = new Progress<string>(key =>
            {
                string msg = Loc.Get(key);
                if (IsInitializing)
                {
                    InitializationMessage = msg;
                }
            });

            var (updated, count, _) = await Task.Run(() =>
                _recognitionService.CheckAndSyncOnlineAsync(forceUpdate: false, progress: progress));

            if (updated || NeedsSpriteMapSync)
            {
                await TrySyncSpriteMapFromWebViewAsync();
                LoadedTemplateCount = _recognitionService.GetAllUnits().Sum(u => u.Templates.Count);
                if (LoadedTemplateCount > 0)
                {
                    StatusMessage = Loc.Get("StatusAutoUpdatedCount", LoadedTemplateCount);
                }
            }
        }
        catch
        {
            // 启动静默检查失败时不打扰用户
        }
    }

    /// <summary>
    /// 若 WebView2 已加载 /battle 页面，提取实时雪碧图坐标以补充新角色并重切特征库
    /// </summary>
    public async Task TrySyncSpriteMapFromWebViewAsync()
    {
        var evaluator = EvaluateQueryWebViewAsync ?? ExecuteQueryWebViewScriptAsync;
        if (evaluator is null)
        {
            return;
        }

        try
        {
            if (IsInitializing)
            {
                InitializationMessage = Loc.Get("StatusExtractingSpriteMap");
            }

            string? extractedJson = await evaluator(WebViewBridgeService.GetExtractSpriteMapScript());
            int added = await Task.Run(() => _recognitionService.MergeSpriteMapFromWebView(extractedJson));
            if (added > 0)
            {
                LoadedTemplateCount = _recognitionService.GetAllUnits().Sum(u => u.Templates.Count);
                if (IsInitializing && LoadedTemplateCount > 0)
                {
                    StatusMessage = Loc.Get("StatusAutoUpdatedCount", LoadedTemplateCount);
                }
            }
        }
        catch
        {
        }
    }

    /// <summary>
    /// 从 peterli110/pcrdfans_statics 在线拉取 3星/6星 角色头像到本地特征库
    /// </summary>
    [RelayCommand]
    public async Task SyncOnlineIconsAsync()
    {
        if (IsBusy)
        {
            return;
        }

        IsBusy = true;
        try
        {
            var progress = new Progress<string>(key => StatusMessage = Loc.Get(key));
            StatusMessage = Loc.Get("StatusCheckingUpdate");
            var (updated, count, version) = await Task.Run(() =>
                _recognitionService.CheckAndSyncOnlineAsync(forceUpdate: false, progress: progress));
            if (updated)
            {
                await TrySyncSpriteMapFromWebViewAsync();
            }
            LoadedTemplateCount = _recognitionService.GetAllUnits().Sum(u => u.Templates.Count);
            if (updated)
            {
                StatusMessage = Loc.Get("StatusUpdatedCount", LoadedTemplateCount > 0 ? LoadedTemplateCount : count);
            }
            else
            {
                StatusMessage = string.IsNullOrEmpty(version)
                    ? Loc.Get("StatusCheckFailed")
                    : Loc.Get("StatusAlreadyLatest");
            }
        }
        catch (Exception ex)
        {
            StatusMessage = Loc.Get("StatusUpdateError", ex.Message);
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>
    /// 统一智能处理内存截图：
    /// - 对手列表：更新右侧防守方并在「作业查询」Tab 搜索
    /// - 对战履历详情（1v1/3v3）：同时识别左侧进攻方与右侧防守方并更新左右对照栏；
    ///   若当前在「战绩上传」Tab 则填入攻防阵容，若在「作业查询」Tab 则查询右侧防守方
    /// </summary>
    public async Task ProcessUnifiedScanAsync(BitmapSource screenshot)
    {
        if (IsBusy)
        {
            return;
        }

        IsBusy = true;

        try
        {
            UploadRecognitionResult result = await Task.Run(() =>
                _recognitionService.RecognizeUnifiedScreen(screenshot));

            CurrentSceneType = result.SceneType;
            string modeLabel = result.SceneType == BattleSceneType.PrincessArenaThreeTeams
                ? Loc.Get("ModePrincess")
                : Loc.Get("ModeArena");

            CurrentSceneBadge = modeLabel;
            StatusMessage = modeLabel;
            DebugAnnotatedImage = result.DebugAnnotatedImage;

            if (result.SceneType == BattleSceneType.ArenaThreeOpponents)
            {
                ApplyRecognizedTeamsToCollection(AttackTeamGroups, [], _ => false);
                ApplyRecognizedTeamsToCollection(DefenseTeamGroups, result.DefenseTeams, _ => true);
                SelectedWebTabIndex = 0;
                await PopulateAllThreeTeamsInWebViewAsync();
            }
            else
            {
                int matchedIdx = Math.Clamp(result.MatchedTeamIndex, 0, Math.Max(0, result.AttackTeams.Count - 1));
                ApplyRecognizedTeamsToCollection(
                    AttackTeamGroups,
                    result.AttackTeams,
                    t => SelectedWebTabIndex == 1 ? t == matchedIdx : (result.SceneType == BattleSceneType.PrincessArenaThreeTeams || t == 0));

                ApplyRecognizedTeamsToCollection(
                    DefenseTeamGroups,
                    result.DefenseTeams,
                    t => SelectedWebTabIndex == 1 ? t == matchedIdx : (result.SceneType == BattleSceneType.PrincessArenaThreeTeams || t == 0));

                if (SelectedWebTabIndex == 1)
                {
                    await FillUploadTabTeamsAsync(matchedIdx);
                }
                else if (result.SceneType == BattleSceneType.PrincessArenaThreeTeams)
                {
                    await PopulateAllThreeTeamsInWebViewAsync();
                }
                else
                {
                    await QuerySingleTeamGroupInWebViewAsync(DefenseTeamGroups[0], targetWebTabNumber: 1);
                }
            }
        }
        catch (Exception ex)
        {
            StatusMessage = Loc.Get("StatusRecognizeFailed", ex.Message);
        }
        finally
        {
            IsBusy = false;
        }
    }

    private void ApplyRecognizedTeamsToCollection(
        ObservableCollection<RecognizedTeamGroup> targetCollection,
        IReadOnlyList<RecognizedTeamResult> sourceTeams,
        Func<int, bool> isSelectedPredicate)
    {
        for (int t = 0; t < 3; t++)
        {
            RecognizedTeamGroup group = targetCollection[t];
            group.Title = Loc.GetTeamTitle(t + 1);

            if (t < sourceTeams.Count)
            {
                RecognizedTeamResult teamRes = sourceTeams[t];
                group.IsSelected = isSelectedPredicate(t);

                for (int s = 0; s < Math.Min(5, teamRes.Slots.Count); s++)
                {
                    RecognizedSlotResult slotRes = teamRes.Slots[s];
                    RecognizedSlot slot = group.Slots[s];
                    slot.UnitId = slotRes.UnitId;
                    slot.UnitName = slotRes.UnitName;
                    slot.Confidence = slotRes.Confidence;
                    slot.CroppedSlice = slotRes.CroppedSlice;
                    slot.MatchedIcon = slotRes.MatchedIcon;
                }
            }
            else
            {
                group.IsSelected = false;
                foreach (RecognizedSlot slot in group.Slots)
                {
                    slot.UnitId = 0;
                    slot.UnitName = string.Empty;
                    slot.Confidence = 0;
                    slot.CroppedSlice = null;
                    slot.MatchedIcon = null;
                }
            }
        }
    }

    /// <summary>
    /// 点击右侧「防守方」队伍：同步高亮整行（进攻+防守）并切换网页 1队/2队/3队 标签页（不重新查询）
    /// </summary>
    [RelayCommand]
    public Task SelectAndQueryDefenseTeamAsync(RecognizedTeamGroup? group) =>
        SelectTeamRowAsync(group);

    /// <summary>
    /// 点击左侧「进攻方」队伍：同步高亮整行（进攻+防守）并切换网页 1队/2队/3队 标签页（不重新查询）
    /// </summary>
    [RelayCommand]
    public Task SelectAndUploadAttackTeamAsync(RecognizedTeamGroup? group) =>
        SelectTeamRowAsync(group);

    private async Task SelectTeamRowAsync(RecognizedTeamGroup? group)
    {
        if (group is null || IsBusy)
        {
            return;
        }

        int idx = Math.Clamp(group.TeamNumber - 1, 0, 2);
        for (int i = 0; i < 3; i++)
        {
            AttackTeamGroups[i].IsSelected = i == idx;
            DefenseTeamGroups[i].IsSelected = i == idx;
        }

        SelectedWebTabIndex = 0;

        if (ExecuteQueryWebViewScriptAsync is null)
        {
            return;
        }

        int webTab = CurrentSceneType == BattleSceneType.ArenaSingleHistoryDetail
            ? 1
            : group.TeamNumber;

        try
        {
            await ExecuteQueryWebViewScriptAsync(WebViewBridgeService.BuildSwitchTeamTabOnlyScript(webTab));
        }
        catch
        {
        }
    }

    /// <summary>
    /// 点击每行队伍后面的「上传」按钮：切换到战绩上传 Tab，并同时选上该行的进攻与防守阵容
    /// </summary>
    [RelayCommand]
    public async Task UploadRowTeamAsync(string? teamNumberText)
    {
        if (IsBusy || !int.TryParse(teamNumberText, out int teamNumber) || teamNumber < 1 || teamNumber > 3)
        {
            return;
        }

        int idx = teamNumber - 1;
        for (int i = 0; i < 3; i++)
        {
            AttackTeamGroups[i].IsSelected = i == idx;
            DefenseTeamGroups[i].IsSelected = i == idx;
        }

        SelectedWebTabIndex = 1;
        try
        {
            await FillUploadTabTeamsAsync(idx);
        }
        catch (Exception ex)
        {
            StatusMessage = Loc.Get("StatusRecognizeFailed", ex.Message);
        }
    }

    /// <summary>
    /// 左右互换进攻与防守阵容（便于将被攻略的防守记录反转为攻防并上传）
    /// </summary>
    [RelayCommand]
    public async Task SwapAttackAndDefenseAsync()
    {
        if (IsBusy)
        {
            return;
        }

        int selectedIdx = 0;
        for (int t = 0; t < 3; t++)
        {
            RecognizedTeamGroup atk = AttackTeamGroups[t];
            RecognizedTeamGroup def = DefenseTeamGroups[t];

            if (atk.IsSelected || def.IsSelected)
            {
                selectedIdx = t;
            }

            (atk.IsSelected, def.IsSelected) = (def.IsSelected, atk.IsSelected);

            for (int s = 0; s < 5; s++)
            {
                RecognizedSlot aSlot = atk.Slots[s];
                RecognizedSlot dSlot = def.Slots[s];

                (aSlot.UnitId, dSlot.UnitId) = (dSlot.UnitId, aSlot.UnitId);
                (aSlot.UnitName, dSlot.UnitName) = (dSlot.UnitName, aSlot.UnitName);
                (aSlot.Confidence, dSlot.Confidence) = (dSlot.Confidence, aSlot.Confidence);
                (aSlot.CroppedSlice, dSlot.CroppedSlice) = (dSlot.CroppedSlice, aSlot.CroppedSlice);
                (aSlot.MatchedIcon, dSlot.MatchedIcon) = (dSlot.MatchedIcon, aSlot.MatchedIcon);
            }
        }

        StatusMessage = Loc.Get("StatusSwappedSides");

        if (SelectedWebTabIndex == 1)
        {
            try
            {
                await FillUploadTabTeamsAsync(selectedIdx);
            }
            catch
            {
            }
        }
    }

    private async Task FillUploadTabTeamsAsync(int teamIndex)
    {
        if (ExecuteUploadWebViewScriptAsync is null)
        {
            StatusMessage = Loc.Get("StatusUploadTabNotReady");
            return;
        }

        int idx = Math.Clamp(teamIndex, 0, 2);
        var atkUnitIds = AttackTeamGroups[idx].GetValidUnitIds();
        var defUnitIds = DefenseTeamGroups[idx].GetValidUnitIds();
        if (atkUnitIds.Count == 0 && defUnitIds.Count == 0)
        {
            return;
        }

        // 若用户刚在其他 Tab 登录但上传页停留在旧路径，先确保在 /battle/upload 并轮询等待页面就绪
        string? checkJson = await ExecuteUploadWebViewScriptAsync(WebViewBridgeService.GetCheckUploadPageStateScript());
        if (!WebViewBridgeService.TryParseUploadPageState(checkJson, out bool isUploadPage, out _) || !isUploadPage)
        {
            NavigateUploadWebViewToUploadPage?.Invoke();
            for (int attempt = 0; attempt < 20; attempt++)
            {
                await Task.Delay(250);
                checkJson = await ExecuteUploadWebViewScriptAsync(WebViewBridgeService.GetCheckUploadPageStateScript());
                if (WebViewBridgeService.TryParseUploadPageState(checkJson, out isUploadPage, out _) && isUploadPage)
                {
                    break;
                }
            }

            if (!isUploadPage)
            {
                StatusMessage = Loc.Get("StatusUploadTabNotReady");
                return;
            }
        }

        string script = WebViewBridgeService.BuildSelectUploadAttackTeamScript(
            atkUnitIds,
            defUnitIds,
            overwriteDefense: true);
        await ExecuteUploadWebViewScriptAsync(script);
        StatusMessage = Loc.Get("StatusFilledUploadTeam", idx + 1);
    }

    private async Task QuerySingleTeamGroupInWebViewAsync(
        RecognizedTeamGroup group,
        int targetWebTabNumber)
    {
        var unitIds = group.GetValidUnitIds();
        if (unitIds.Count == 0 || ExecuteQueryWebViewScriptAsync is null)
        {
            return;
        }

        string script = WebViewBridgeService.BuildAutoSelectAndQueryScript(unitIds, targetWebTabNumber, triggerSearch: true);
        await ExecuteQueryWebViewScriptAsync(script);
    }

    private async Task PopulateAllThreeTeamsInWebViewAsync()
    {
        if (ExecuteQueryWebViewScriptAsync is null)
        {
            return;
        }

        var threeTeamsIds = DefenseTeamGroups.Select(g => g.GetValidUnitIds()).ToList();
        if (threeTeamsIds.All(t => t.Count == 0))
        {
            return;
        }

        string script = WebViewBridgeService.BuildSelectAllThreeTeamsScript(threeTeamsIds);
        await ExecuteQueryWebViewScriptAsync(script);
    }

    public void Dispose()
    {
        _recognitionService.Dispose();
    }
}
