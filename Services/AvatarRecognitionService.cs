using System.IO;
using System.Net.Http;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Windows.Media.Imaging;
using OpenCvSharp;
using OpenCvSharp.WpfExtensions;
using PcrBattleAssistant.Models;

namespace PcrBattleAssistant.Services;

/// <summary>
/// 单个槽位的图像识别结果
/// </summary>
public sealed record RecognizedSlotResult(
    int SlotIndex,
    int UnitId,
    string UnitName,
    double Confidence,
    BitmapSource CroppedSlice,
    BitmapSource? MatchedIcon);

/// <summary>
/// 单个 5 人队伍的识别结果
/// </summary>
public sealed record RecognizedTeamResult(
    int TeamNumber,
    string Title,
    IReadOnlyList<RecognizedSlotResult> Slots);

/// <summary>
/// 多阵容（3个阵容/3个队伍）完整图像识别结果（含调试模式标注图）
/// </summary>
public sealed record MultiTeamRecognitionResult(
    BattleSceneType SceneType,
    string SceneDescription,
    IReadOnlyList<RecognizedTeamResult> Teams,
    BitmapSource DebugAnnotatedImage);

/// <summary>
/// 战绩上传模式识别结果：
/// 包含左侧进攻方队伍（1队或3队）、右侧防守方队伍（1队或3队）、匹配到的当前上传队伍序号以及调试标注图
/// </summary>
public sealed record UploadRecognitionResult(
    BattleSceneType SceneType,
    string SceneDescription,
    IReadOnlyList<RecognizedTeamResult> AttackTeams,
    IReadOnlyList<RecognizedTeamResult> DefenseTeams,
    int MatchedTeamIndex,
    BitmapSource DebugAnnotatedImage);

/// <summary>
/// 模块 B：基于 OpenCvSharp4 的本地角色头像特征库管理、多阵容裁切、调试区间绘制与模板匹配引擎
/// </summary>
public sealed partial class AvatarRecognitionService : IDisposable
{
    public const int TemplateSize = 64;
    private const int SpriteTileSize = 60;

    // 64x64 头像切片中避开顶部 "Lv405" 文字与底部星级/属性图标的纯面部特征区域 (44x30)
    private static readonly Rect QueryFaceRect = new(10, 16, 44, 30);

    // 在 64x64 标准模板上供 44x30 面部特征块滑动搜索的窗口 (允许 ±8px 平移容差)
    private static readonly Rect TemplateSearchWindow = new(2, 8, 60, 46);

    private readonly Dictionary<int, CharacterUnit> _unitCatalog = [];
    private readonly object _syncRoot = new();
    private static readonly HttpClient SharedHttpClient = new()
    {
        Timeout = TimeSpan.FromSeconds(20)
    };

    public string AssetsDirectory { get; }
    public string IconDirectory { get; }
    public string MappingFilePath { get; }
    public string SpritesMapFilePath { get; }
    public string UnitsZhFilePath { get; }

    public AvatarRecognitionService(string? iconDirectory = null)
    {
        string baseDir = AppDomain.CurrentDomain.BaseDirectory;
        AssetsDirectory = Path.Combine(baseDir, "Assets");
        IconDirectory = iconDirectory ?? Path.Combine(AssetsDirectory, "Icons");
        MappingFilePath = Path.Combine(AssetsDirectory, "unit_mapping.json");
        SpritesMapFilePath = Path.Combine(AssetsDirectory, "sprites_map.json");
        UnitsZhFilePath = Path.Combine(AssetsDirectory, "units_zh.json");

        Directory.CreateDirectory(IconDirectory);
        EnsureDefaultMappingFile();
    }

    public IReadOnlyList<CharacterUnit> GetAllUnits()
    {
        lock (_syncRoot)
        {
            return _unitCatalog.Values.OrderBy(u => u.UnitId).ToList();
        }
    }

    public CharacterUnit? GetUnitById(int unitId)
    {
        int normalized = CharacterUnit.NormalizeToBaseUnitId(unitId);
        lock (_syncRoot)
        {
            return _unitCatalog.GetValueOrDefault(normalized);
        }
    }

    /// <summary>
    /// 加载本地头像库与 IconFile -> UnitID 映射表。
    /// 若本地 Icons 文件夹尚未切分，自动从内置的 pcrdfans_statics 雪碧图 (charas.png / charas6x.png) 切分全部 3★ 与 6★ 头像。
    /// </summary>
    public int LoadLocalLibrary(string? customDirectory = null)
    {
        string targetDir = customDirectory ?? IconDirectory;
        if (!Directory.Exists(targetDir))
        {
            Directory.CreateDirectory(targetDir);
        }

        int existingPngCount = Directory.EnumerateFiles(targetDir, "*.png", SearchOption.TopDirectoryOnly).Count();
        if (existingPngCount < 50)
        {
            TrySliceBundledSpriteSheetsToIcons(targetDir);
        }

        var fileToUnitMap = LoadCustomMappingDictionary();
        var unitMetaDict = LoadAllUnitMetadata();

        var supportedExts = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            ".png", ".webp", ".jpg", ".jpeg", ".bmp"
        };

        var files = Directory.EnumerateFiles(targetDir, "*.*", SearchOption.AllDirectories)
            .Where(f => supportedExts.Contains(Path.GetExtension(f)))
            .ToList();

        lock (_syncRoot)
        {
            foreach (var old in _unitCatalog.Values)
            {
                old.Dispose();
            }
            _unitCatalog.Clear();

            foreach (var (baseId, meta) in unitMetaDict)
            {
                _unitCatalog[baseId] = new CharacterUnit
                {
                    UnitId = baseId,
                    Name = meta.Name,
                    PositionType = meta.Position
                };
            }

            int loadedTemplates = 0;

            foreach (string filePath in files)
            {
                string fileName = Path.GetFileName(filePath);
                if (!TryResolveIconUnitId(fileName, fileToUnitMap, out int variantId, out int baseUnitId))
                {
                    continue;
                }

                using Mat raw = Cv2.ImRead(filePath, ImreadModes.Color);
                if (raw.Empty())
                {
                    continue;
                }

                // 缩放到 64x64 全图并截取 TemplateSearchWindow 供滑动匹配
                using Mat full64 = new();
                Cv2.Resize(raw, full64, new Size(TemplateSize, TemplateSize), 0, 0, InterpolationFlags.Area);

                Mat bgrWindow = new(full64, TemplateSearchWindow);
                Mat grayWindow = new();
                Cv2.CvtColor(bgrWindow, grayWindow, ColorConversionCodes.BGR2GRAY);

                BitmapSource iconBmp = full64.ToBitmapSource();
                iconBmp.Freeze();

                if (!_unitCatalog.TryGetValue(baseUnitId, out var unit))
                {
                    string unitName = unitMetaDict.TryGetValue(baseUnitId, out var meta)
                        ? meta.Name
                        : $"角色 {baseUnitId / 100}";
                    int pos = unitMetaDict.TryGetValue(baseUnitId, out var m) ? m.Position : 0;

                    unit = new CharacterUnit
                    {
                        UnitId = baseUnitId,
                        Name = unitName,
                        PositionType = pos,
                        DisplayIcon = iconBmp
                    };
                    _unitCatalog[baseUnitId] = unit;
                }
                else if (unit.DisplayIcon is null || variantId % 100 == 31)
                {
                    unit.DisplayIcon = iconBmp;
                }

                unit.Templates.Add(new AvatarTemplate
                {
                    VariantId = variantId,
                    SourcePath = filePath,
                    Bgr64 = bgrWindow,
                    Gray64 = grayWindow,
                    IconBitmap = iconBmp
                });

                loadedTemplates++;
            }

            return loadedTemplates;
        }
    }

    /// <summary>
    /// 检查并从 peterli110/pcrdfans_statics 拉取最新的 3★/6★ 雪碧图，切分并重载本地特征库
    /// </summary>
    public async Task<(bool Updated, int SlicedCount, string Version)> CheckAndSyncOnlineAsync(
        bool forceUpdate = false,
        IProgress<string>? progress = null,
        CancellationToken cancellationToken = default)
    {
        Directory.CreateDirectory(AssetsDirectory);
        Directory.CreateDirectory(IconDirectory);

        string syncVersionPath = Path.Combine(AssetsDirectory, "sync_version.txt");
        string? remoteVersion = await FetchLatestStaticsAndUnitsAsync(cancellationToken);

        string? remoteSourceKey = string.IsNullOrEmpty(remoteVersion)
            ? null
            : $"charas@{remoteVersion}.png|charas6x@{remoteVersion}.png";

        if (!forceUpdate)
        {
            if (string.IsNullOrEmpty(remoteSourceKey))
            {
                return (false, 0, string.Empty);
            }

            string? localSourceKey = File.Exists(syncVersionPath)
                ? (await File.ReadAllTextAsync(syncVersionPath, cancellationToken)).Trim()
                : null;

            int existingPngCount = Directory.EnumerateFiles(IconDirectory, "*.png", SearchOption.TopDirectoryOnly).Count();
            if (string.Equals(localSourceKey, remoteSourceKey, StringComparison.Ordinal) && existingPngCount >= 50)
            {
                return (false, 0, remoteVersion!);
            }
        }

        string verTag = string.IsNullOrEmpty(remoteVersion) ? string.Empty : $"@{remoteVersion}";
        string charas3xPath = Path.Combine(AssetsDirectory, "charas.png");
        string charas6xPath = Path.Combine(AssetsDirectory, "charas6x.png");

        string[] charas3xUrls =
        [
            $"https://cdn.jsdelivr.net/gh/peterli110/pcrdfans_statics{verTag}/static/sprites/charas.png",
            $"https://fastly.jsdelivr.net/gh/peterli110/pcrdfans_statics{verTag}/static/sprites/charas.png",
            "https://raw.githubusercontent.com/peterli110/pcrdfans_statics/master/static/sprites/charas.png"
        ];

        string[] charas6xUrls =
        [
            $"https://cdn.jsdelivr.net/gh/peterli110/pcrdfans_statics{verTag}/static/sprites/charas6x.png",
            $"https://fastly.jsdelivr.net/gh/peterli110/pcrdfans_statics{verTag}/static/sprites/charas6x.png",
            "https://raw.githubusercontent.com/peterli110/pcrdfans_statics/master/static/sprites/charas6x.png"
        ];

        progress?.Report("SyncDownloading3x");
        bool ok3x = await TryDownloadFileFromMirrorsAsync(charas3xUrls, charas3xPath, cancellationToken);

        progress?.Report("SyncDownloading6x");
        bool ok6x = await TryDownloadFileFromMirrorsAsync(charas6xUrls, charas6xPath, cancellationToken);

        if (!ok3x && !ok6x)
        {
            return (false, 0, remoteVersion ?? string.Empty);
        }

        progress?.Report("SyncSlicing");
        int slicedCount = TrySliceBundledSpriteSheetsToIcons(IconDirectory, overwriteExisting: true);

        if (slicedCount > 0 && !string.IsNullOrEmpty(remoteSourceKey))
        {
            await File.WriteAllTextAsync(syncVersionPath, remoteSourceKey, cancellationToken);
        }

        LoadLocalLibrary();
        return (true, slicedCount, remoteVersion ?? "latest");
    }

    /// <summary>
    /// 从 WebView2 实时渲染的选人面板合并最新雪碧图坐标到 sprites_map.json，若有新增角色则自动补切并重载特征库
    /// </summary>
    public int MergeSpriteMapFromWebView(string? extractedJson)
    {
        if (string.IsNullOrWhiteSpace(extractedJson))
        {
            return 0;
        }

        try
        {
            string json = extractedJson;
            if (json.StartsWith('"') && json.EndsWith('"'))
            {
                json = JsonSerializer.Deserialize<string>(json) ?? string.Empty;
            }
            if (string.IsNullOrWhiteSpace(json))
            {
                return 0;
            }

            using JsonDocument incomingDoc = JsonDocument.Parse(json);
            bool hasStar3 = incomingDoc.RootElement.TryGetProperty("star3", out JsonElement incomingStar3);
            bool hasStar6 = incomingDoc.RootElement.TryGetProperty("star6", out JsonElement incomingStar6);
            if (!hasStar3 && !hasStar6)
            {
                return 0;
            }

            var (star3, star6) = ReadSpritesMapFile();

            int addedOrChanged = 0;
            if (hasStar3)
            {
                addedOrChanged += MergeSpriteEntries(incomingStar3, star3);
            }
            if (hasStar6)
            {
                addedOrChanged += MergeSpriteEntries(incomingStar6, star6);
            }

            if (addedOrChanged == 0)
            {
                return 0;
            }

            WriteSpritesMapFile(star3, star6);

            int sliced = TrySliceBundledSpriteSheetsToIcons(IconDirectory, overwriteExisting: true);
            if (sliced > 0)
            {
                LoadLocalLibrary();
            }
            return addedOrChanged;
        }
        catch
        {
            return 0;
        }
    }

    public bool HasStar3SpriteMap()
    {
        try
        {
            var (star3, _) = ReadSpritesMapFile();
            return star3.Count >= 50;
        }
        catch
        {
            return false;
        }
    }

    private (SortedDictionary<string, (int X, int Y)> Star3, SortedDictionary<string, (int X, int Y)> Star6) ReadSpritesMapFile()
    {
        var star3 = new SortedDictionary<string, (int X, int Y)>(StringComparer.Ordinal);
        var star6 = new SortedDictionary<string, (int X, int Y)>(StringComparer.Ordinal);

        if (!File.Exists(SpritesMapFilePath))
        {
            return (star3, star6);
        }

        using JsonDocument existingDoc = JsonDocument.Parse(File.ReadAllText(SpritesMapFilePath));
        if (existingDoc.RootElement.TryGetProperty("star3", out JsonElement ex3))
        {
            foreach (JsonProperty p in ex3.EnumerateObject())
            {
                star3[p.Name] = (p.Value.GetProperty("x").GetInt32(), p.Value.GetProperty("y").GetInt32());
            }
        }
        if (existingDoc.RootElement.TryGetProperty("star6", out JsonElement ex6))
        {
            foreach (JsonProperty p in ex6.EnumerateObject())
            {
                star6[p.Name] = (p.Value.GetProperty("x").GetInt32(), p.Value.GetProperty("y").GetInt32());
            }
        }

        return (star3, star6);
    }

    private void WriteSpritesMapFile(
        SortedDictionary<string, (int X, int Y)> star3,
        SortedDictionary<string, (int X, int Y)> star6)
    {
        Directory.CreateDirectory(AssetsDirectory);
        var payload = new
        {
            star3 = star3.ToDictionary(kv => kv.Key, kv => new { x = kv.Value.X, y = kv.Value.Y }),
            star6 = star6.ToDictionary(kv => kv.Key, kv => new { x = kv.Value.X, y = kv.Value.Y })
        };
        File.WriteAllText(SpritesMapFilePath, JsonSerializer.Serialize(payload));
    }

    private static int MergeSpriteEntries(JsonElement incoming, SortedDictionary<string, (int X, int Y)> target)
    {
        int changed = 0;
        foreach (JsonProperty p in incoming.EnumerateObject())
        {
            int x = p.Value.TryGetProperty("x", out var xp) ? xp.GetInt32() : -1;
            int y = p.Value.TryGetProperty("y", out var yp) ? yp.GetInt32() : -1;
            if (x < 0 || y < 0) continue;

            if (!target.TryGetValue(p.Name, out var oldCoord) || oldCoord.X != x || oldCoord.Y != y)
            {
                target[p.Name] = (x, y);
                changed++;
            }
        }
        return changed;
    }

    /// <summary>
    /// 从 peterli110/pcrdfans_statics 拉取最新的 3★ 雪碧图与 6★ 雪碧图并切分为单角色头像文件
    /// </summary>
    public async Task<int> SyncIconsFromPcrdfansStaticsAsync(
        IProgress<string>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var (_, slicedCount, _) = await CheckAndSyncOnlineAsync(forceUpdate: true, progress: progress, cancellationToken: cancellationToken);
        return slicedCount;
    }

    private async Task<string?> FetchLatestStaticsAndUnitsAsync(CancellationToken cancellationToken)
    {
        try
        {
            using var response = await SharedHttpClient.GetAsync("https://www.pcrdfans.com/battle", cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                return null;
            }

            string html = await response.Content.ReadAsStringAsync(cancellationToken);
            TryMergeUnitsFromBattleHtml(html);
            Match m = StaticsVersionRegex().Match(html);
            return m.Success ? m.Groups[1].Value : null;
        }
        catch
        {
            return null;
        }
    }

    private void TryMergeUnitsFromBattleHtml(string html)
    {
        try
        {
            Match m = NextDataRegex().Match(html);
            if (!m.Success) return;

            using JsonDocument nextDoc = JsonDocument.Parse(m.Groups[1].Value);
            if (!nextDoc.RootElement.TryGetProperty("props", out var propsEl) ||
                !propsEl.TryGetProperty("initialState", out var stateEl) ||
                !stateEl.TryGetProperty("server", out var serverEl) ||
                !serverEl.TryGetProperty("unitObject", out var unitObjEl))
            {
                return;
            }

            var existingUnits = new Dictionary<int, (string Name, int Place, int MaxRarity)>();
            if (File.Exists(UnitsZhFilePath))
            {
                using JsonDocument oldDoc = JsonDocument.Parse(File.ReadAllText(UnitsZhFilePath));
                if (oldDoc.RootElement.ValueKind == JsonValueKind.Array)
                {
                    foreach (JsonElement item in oldDoc.RootElement.EnumerateArray())
                    {
                        int id = item.TryGetProperty("id", out var idEl) ? idEl.GetInt32() : 0;
                        string name = item.TryGetProperty("name", out var nameEl) ? nameEl.GetString() ?? "" : "";
                        int place = item.TryGetProperty("place", out var placeEl) ? placeEl.GetInt32() : 0;
                        int maxRarity = item.TryGetProperty("maxrarity", out var mrEl) ? mrEl.GetInt32() : 5;
                        if (id > 0 && !string.IsNullOrWhiteSpace(name))
                        {
                            existingUnits[id] = (name, place, maxRarity);
                        }
                    }
                }
            }

            bool changed = false;
            foreach (JsonProperty p in unitObjEl.EnumerateObject())
            {
                if (!int.TryParse(p.Name, out int id) || id <= 0) continue;
                int place = p.Value.TryGetProperty("place", out var placeEl) ? placeEl.GetInt32() : 0;
                int maxRarity = p.Value.TryGetProperty("maxrarity", out var mrEl) ? mrEl.GetInt32() : 5;
                string name = p.Value.TryGetProperty("name", out var nameEl) ? nameEl.GetString() ?? $"Unit {id}" : $"Unit {id}";
                if (!existingUnits.TryGetValue(id, out var old) ||
                    old.MaxRarity != maxRarity ||
                    old.Place != place ||
                    old.Name != name)
                {
                    existingUnits[id] = (name, place, maxRarity);
                    changed = true;
                }
            }

            if (changed || !File.Exists(UnitsZhFilePath))
            {
                var mergedList = existingUnits
                    .OrderBy(kv => kv.Key)
                    .Select(kv => new { id = kv.Key, name = kv.Value.Name, place = kv.Value.Place, maxrarity = kv.Value.MaxRarity })
                    .ToList();
                File.WriteAllText(UnitsZhFilePath, JsonSerializer.Serialize(mergedList));
            }

            // 同步根据 maxrarity == 6 的角色列表重建 star6 雪碧图坐标，确保新开 6★ 角色免维护自动切图
            var star6Ids = existingUnits
                .Where(kv => kv.Value.MaxRarity == 6)
                .Select(kv => kv.Key)
                .OrderBy(id => id)
                .ToList();

            if (star6Ids.Count > 0)
            {
                var (star3, star6) = ReadSpritesMapFile();
                bool star6Changed = star6.Count != star6Ids.Count;
                var nextStar6 = new SortedDictionary<string, (int X, int Y)>(StringComparer.Ordinal);
                for (int idx = 0; idx < star6Ids.Count; idx++)
                {
                    int layer = (int)Math.Floor(Math.Sqrt(idx));
                    int rem = idx - layer * layer;
                    int col = rem < layer ? layer : (rem - layer);
                    int row = rem < layer ? rem : layer;
                    var coord = (col * 62, row * 62);
                    string key = star6Ids[idx].ToString();
                    nextStar6[key] = coord;
                    if (!star6.TryGetValue(key, out var oldCoord) || oldCoord != coord)
                    {
                        star6Changed = true;
                    }
                }

                if (star6Changed)
                {
                    WriteSpritesMapFile(star3, nextStar6);
                }
            }
        }
        catch
        {
        }
    }

    [GeneratedRegex(@"pcrdfans_statics@(\d+)", RegexOptions.Compiled)]
    private static partial Regex StaticsVersionRegex();

    [GeneratedRegex(@"<script id=""__NEXT_DATA__"" type=""application/json"">(.*?)</script>", RegexOptions.Compiled | RegexOptions.Singleline)]
    private static partial Regex NextDataRegex();

    private static async Task<bool> TryDownloadFileFromMirrorsAsync(
        string[] urls,
        string destinationPath,
        CancellationToken cancellationToken)
    {
        foreach (string url in urls)
        {
            try
            {
                using var response = await SharedHttpClient.GetAsync(url, cancellationToken);
                if (response.IsSuccessStatusCode)
                {
                    byte[] bytes = await response.Content.ReadAsByteArrayAsync(cancellationToken);
                    if (bytes.Length > 4096)
                    {
                        await File.WriteAllBytesAsync(destinationPath, bytes, cancellationToken);
                        return true;
                    }
                }
            }
            catch
            {
            }
        }

        return File.Exists(destinationPath);
    }

    public int TrySliceBundledSpriteSheetsToIcons(string outputIconDir, bool overwriteExisting = false)
    {
        if (!File.Exists(SpritesMapFilePath))
        {
            return 0;
        }

        try
        {
            Directory.CreateDirectory(outputIconDir);
            string json = File.ReadAllText(SpritesMapFilePath);
            using JsonDocument doc = JsonDocument.Parse(json);

            int totalSliced = 0;

            string charas3xPath = Path.Combine(AssetsDirectory, "charas.png");
            if (File.Exists(charas3xPath) && doc.RootElement.TryGetProperty("star3", out JsonElement star3Map))
            {
                using Mat sheet3x = Cv2.ImRead(charas3xPath, ImreadModes.Color);
                if (!sheet3x.Empty())
                {
                    totalSliced += SliceSingleSpriteSheet(sheet3x, star3Map, suffix: 31, outputIconDir, overwriteExisting);
                }
            }

            string charas6xPath = Path.Combine(AssetsDirectory, "charas6x.png");
            if (File.Exists(charas6xPath) && doc.RootElement.TryGetProperty("star6", out JsonElement star6Map))
            {
                using Mat sheet6x = Cv2.ImRead(charas6xPath, ImreadModes.Color);
                if (!sheet6x.Empty())
                {
                    totalSliced += SliceSingleSpriteSheet(sheet6x, star6Map, suffix: 61, outputIconDir, overwriteExisting);
                }
            }

            return totalSliced;
        }
        catch
        {
            return 0;
        }
    }

    private static int SliceSingleSpriteSheet(
        Mat sheet,
        JsonElement coordMap,
        int suffix,
        string outputIconDir,
        bool overwriteExisting)
    {
        int count = 0;
        foreach (JsonProperty prop in coordMap.EnumerateObject())
        {
            if (!int.TryParse(prop.Name, out int baseUnitId))
            {
                continue;
            }

            int x = prop.Value.TryGetProperty("x", out var xp) ? xp.GetInt32() : -1;
            int y = prop.Value.TryGetProperty("y", out var yp) ? yp.GetInt32() : -1;
            if (x < 0 || y < 0 || x + SpriteTileSize > sheet.Width || y + SpriteTileSize > sheet.Height)
            {
                continue;
            }

            int shortId = baseUnitId >= 100000 ? baseUnitId / 100 : baseUnitId;
            int variantId = shortId * 100 + suffix;
            string outFile = Path.Combine(outputIconDir, $"icon_unit_{variantId}.png");

            if (!overwriteExisting && File.Exists(outFile))
            {
                count++;
                continue;
            }

            using Mat tile = new(sheet, new Rect(x, y, SpriteTileSize, SpriteTileSize));
            Cv2.ImWrite(outFile, tile);
            count++;
        }

        return count;
    }

    /// <summary>
    /// 核心多阵容识别入口：
    /// 自动判断截图属于【图1：竞技场3对手选择列表】还是【图2：3v3三队对战详情】，
    /// 提取右侧防守方的 3 组 × 5 人 = 15 名角色头像，并生成带有分析区间框线的调试标注图。
    /// </summary>
    public MultiTeamRecognitionResult RecognizeRightSideThreeTeams(BitmapSource screenshot)
    {
        using Mat srcMat = screenshot.ToMat();
        using Mat bgrMat = new();
        if (srcMat.Channels() == 4)
        {
            Cv2.CvtColor(srcMat, bgrMat, ColorConversionCodes.BGRA2BGR);
        }
        else if (srcMat.Channels() == 1)
        {
            Cv2.CvtColor(srcMat, bgrMat, ColorConversionCodes.GRAY2BGR);
        }
        else
        {
            srcMat.CopyTo(bgrMat);
        }

        int imgW = bgrMat.Width;
        int imgH = bgrMat.Height;

        // 1. 自动识别场景类型（图1：竞技场3对手列表 / 1v1履历详情 vs 图2：3v3防守方3队）
        BattleSceneType sceneType = DetectBattleSceneType(bgrMat);

        // 2. 计算右侧防守方总分析区间与头像框坐标
        var (defenseZone, rowBoxes, avatarGrid) = BuildSceneRegions(sceneType, imgW, imgH, isLeftAttackSide: false);

        // 3. 创建调试模式标注图（将非分析区适度压暗，突出显示右侧正在分析的防守方区间、行区间与面部特征框）
        using Mat debugMat = new();
        using (Mat darkened = new())
        {
            bgrMat.ConvertTo(darkened, -1, 0.45, 0);
            darkened.CopyTo(debugMat);
            Rect safeZone = ClampRect(defenseZone, imgW, imgH);
            using Mat activeRoiSrc = new(bgrMat, safeZone);
            using Mat activeRoiDst = new(debugMat, safeZone);
            activeRoiSrc.CopyTo(activeRoiDst);
        }

        // 绘制右侧防守方总分析区间外框 (青色)
        Cv2.Rectangle(debugMat, ClampRect(defenseZone, imgW, imgH), new Scalar(255, 255, 0), 2);
        Cv2.PutText(
            debugMat,
            sceneType == BattleSceneType.PrincessArenaThreeTeams
                ? " Analyze Zone: 3v3 Defense 3 Teams (Right)"
                : " Analyze Zone: Arena Defense (Right)",
            new Point(Math.Max(8, defenseZone.X), Math.Max(22, defenseZone.Y - 8)),
            HersheyFonts.HersheySimplex,
            0.52,
            new Scalar(0, 255, 255),
            2);

        int rowCount = rowBoxes.Count;
        var teams = new List<RecognizedTeamResult>(rowCount);

        // 图2中 3 队共计 15 名角色不重复；图1中 3 个不同对手各自的 5 名角色内不重复
        var globalUsedIn3v3 = new HashSet<int>();

        for (int row = 0; row < rowCount; row++)
        {
            Rect rowRect = ClampRect(rowBoxes[row], imgW, imgH);
            Cv2.Rectangle(debugMat, rowRect, new Scalar(0, 165, 255), 1);

            var rowUsedIds = sceneType == BattleSceneType.PrincessArenaThreeTeams
                ? globalUsedIn3v3
                : new HashSet<int>();

            var slotResults = RecognizeFiveSlotsInRow(bgrMat, debugMat, avatarGrid[row], rowUsedIds, imgW, imgH);
            if (slotResults.All(s => s.UnitId == 0))
            {
                continue;
            }

            string teamTitle = $"队伍{row + 1}";

            teams.Add(new RecognizedTeamResult(
                TeamNumber: row + 1,
                Title: teamTitle,
                Slots: slotResults));
        }

        BitmapSource debugBmp = debugMat.ToBitmapSource();
        debugBmp.Freeze();

        string sceneDesc = sceneType == BattleSceneType.PrincessArenaThreeTeams
            ? "プリンセスアリーナ模式"
            : "バトルアリーナ模式";

        return new MultiTeamRecognitionResult(
            SceneType: sceneType,
            SceneDescription: sceneDesc,
            Teams: teams,
            DebugAnnotatedImage: debugBmp);
    }

    /// <summary>
    /// 统一智能识图入口：
    /// - 若为【バトルアリーナ对手列表】：识别右侧 3 个对手防守阵容（AttackTeams 为空）
    /// - 若为【1v1 / 3v3 対戦履歴詳細】：同时识别左侧进攻方与右侧防守方阵容
    /// </summary>
    public UploadRecognitionResult RecognizeUnifiedScreen(
        BitmapSource screenshot,
        IReadOnlyCollection<int>? webDefUnitIds = null)
    {
        using Mat srcMat = screenshot.ToMat();
        using Mat bgrMat = new();
        if (srcMat.Channels() == 4)
        {
            Cv2.CvtColor(srcMat, bgrMat, ColorConversionCodes.BGRA2BGR);
        }
        else if (srcMat.Channels() == 1)
        {
            Cv2.CvtColor(srcMat, bgrMat, ColorConversionCodes.GRAY2BGR);
        }
        else
        {
            srcMat.CopyTo(bgrMat);
        }

        BattleSceneType sceneType = DetectBattleSceneType(bgrMat);
        if (sceneType == BattleSceneType.ArenaThreeOpponents)
        {
            MultiTeamRecognitionResult arenaRes = RecognizeRightSideThreeTeams(screenshot);
            return new UploadRecognitionResult(
                SceneType: arenaRes.SceneType,
                SceneDescription: arenaRes.SceneDescription,
                AttackTeams: [],
                DefenseTeams: arenaRes.Teams,
                MatchedTeamIndex: 0,
                DebugAnnotatedImage: arenaRes.DebugAnnotatedImage);
        }

        return RecognizeUploadTeams(screenshot, webDefUnitIds);
    }

    /// <summary>
    /// 战绩上传识别入口：
    /// - 若为图片1（バトルアリーナ 1v1 対戦履歴詳細）：识别左侧进攻方 1 队（及右侧防守方 1 队），直接选用左侧队伍。
    /// - 若为图片2（プリンセスアリーナ 3v3 対戦履歴詳細）：同时识别左侧进攻方 3 队与右侧防守方 3 队，
    ///   并与 WebView 右侧当前正在上传的防守阵容（webDefenseUnitIds）比对，自动定位对应的进攻方队伍（パーティ1/2/3）。
    /// </summary>
    public UploadRecognitionResult RecognizeUploadTeams(
        BitmapSource screenshot,
        IReadOnlyCollection<int>? webDefenseUnitIds = null)
    {
        using Mat srcMat = screenshot.ToMat();
        using Mat bgrMat = new();
        if (srcMat.Channels() == 4)
        {
            Cv2.CvtColor(srcMat, bgrMat, ColorConversionCodes.BGRA2BGR);
        }
        else if (srcMat.Channels() == 1)
        {
            Cv2.CvtColor(srcMat, bgrMat, ColorConversionCodes.GRAY2BGR);
        }
        else
        {
            srcMat.CopyTo(bgrMat);
        }

        int imgW = bgrMat.Width;
        int imgH = bgrMat.Height;

        BattleSceneType detectedScene = DetectBattleSceneType(bgrMat);
        // 在战绩上传模式下，如果不是 3v3 履历详情，则按 1v1 履历详情处理
        BattleSceneType uploadScene = detectedScene == BattleSceneType.PrincessArenaThreeTeams
            ? BattleSceneType.PrincessArenaThreeTeams
            : BattleSceneType.ArenaSingleHistoryDetail;

        var (attackZone, attackRowBoxes, attackGrid) = BuildSceneRegions(uploadScene, imgW, imgH, isLeftAttackSide: true);
        var (defenseZone, defenseRowBoxes, defenseGrid) = BuildSceneRegions(uploadScene, imgW, imgH, isLeftAttackSide: false);

        using Mat debugMat = new();
        using (Mat darkened = new())
        {
            bgrMat.ConvertTo(darkened, -1, 0.45, 0);
            darkened.CopyTo(debugMat);

            Rect safeAtkZone = ClampRect(attackZone, imgW, imgH);
            using (Mat atkSrc = new(bgrMat, safeAtkZone))
            using (Mat atkDst = new(debugMat, safeAtkZone))
            {
                atkSrc.CopyTo(atkDst);
            }

            if (uploadScene == BattleSceneType.PrincessArenaThreeTeams)
            {
                Rect safeDefZone = ClampRect(defenseZone, imgW, imgH);
                using Mat defSrc = new(bgrMat, safeDefZone);
                using Mat defDst = new(debugMat, safeDefZone);
                defSrc.CopyTo(defDst);
            }
        }

        // 标注左侧进攻方分析区间 (青色)
        Cv2.Rectangle(debugMat, ClampRect(attackZone, imgW, imgH), new Scalar(255, 255, 0), 2);
        Cv2.PutText(
            debugMat,
            uploadScene == BattleSceneType.PrincessArenaThreeTeams
                ? " Attack Zone: 3v3 Left 3 Teams"
                : " Attack Zone: 1v1 Left Team",
            new Point(Math.Max(8, attackZone.X), Math.Max(22, attackZone.Y - 8)),
            HersheyFonts.HersheySimplex,
            0.50,
            new Scalar(0, 255, 255),
            2);

        if (uploadScene == BattleSceneType.PrincessArenaThreeTeams)
        {
            Cv2.Rectangle(debugMat, ClampRect(defenseZone, imgW, imgH), new Scalar(255, 180, 0), 2);
            Cv2.PutText(
                debugMat,
                " Defense Match Zone (Right)",
                new Point(Math.Max(8, defenseZone.X), Math.Max(22, defenseZone.Y - 8)),
                HersheyFonts.HersheySimplex,
                0.50,
                new Scalar(255, 200, 0),
                2);
        }

        int rowCount = attackRowBoxes.Count;
        var attackTeams = new List<RecognizedTeamResult>(rowCount);
        var defenseTeams = new List<RecognizedTeamResult>(rowCount);

        var globalUsedAttack = new HashSet<int>();
        var globalUsedDefense = new HashSet<int>();

        for (int row = 0; row < rowCount; row++)
        {
            Rect atkRowRect = ClampRect(attackRowBoxes[row], imgW, imgH);
            Cv2.Rectangle(debugMat, atkRowRect, new Scalar(0, 165, 255), 1);

            var atkUsed = uploadScene == BattleSceneType.PrincessArenaThreeTeams
                ? globalUsedAttack
                : new HashSet<int>();
            var atkSlots = RecognizeFiveSlotsInRow(bgrMat, debugMat, attackGrid[row], atkUsed, imgW, imgH);

            var defUsed = uploadScene == BattleSceneType.PrincessArenaThreeTeams
                ? globalUsedDefense
                : new HashSet<int>();
            var defSlots = RecognizeFiveSlotsInRow(
                bgrMat,
                uploadScene == BattleSceneType.PrincessArenaThreeTeams ? debugMat : null,
                defenseGrid[row],
                defUsed,
                imgW,
                imgH);

            if (atkSlots.All(s => s.UnitId == 0) && defSlots.All(s => s.UnitId == 0))
            {
                continue;
            }

            attackTeams.Add(new RecognizedTeamResult(
                TeamNumber: row + 1,
                Title: $"队伍{row + 1}",
                Slots: atkSlots));

            defenseTeams.Add(new RecognizedTeamResult(
                TeamNumber: row + 1,
                Title: $"队伍{row + 1}",
                Slots: defSlots));
        }

        // 判断当前 WebView 上传的是哪一个队伍（0, 1, 2）
        int matchedIndex = 0;
        if (uploadScene == BattleSceneType.PrincessArenaThreeTeams &&
            webDefenseUnitIds is not null &&
            webDefenseUnitIds.Count > 0)
        {
            var normalizedWebDef = webDefenseUnitIds
                .Where(id => id > 0)
                .Select(CharacterUnit.NormalizeToBaseUnitId)
                .ToHashSet();

            int bestOverlap = -1;
            for (int r = 0; r < defenseTeams.Count; r++)
            {
                int overlap = defenseTeams[r].Slots
                    .Select(s => CharacterUnit.NormalizeToBaseUnitId(s.UnitId))
                    .Count(id => id > 0 && normalizedWebDef.Contains(id));

                if (overlap > bestOverlap)
                {
                    bestOverlap = overlap;
                    matchedIndex = r;
                }
            }
        }

        // 在调试标注图上用亮绿色粗框高亮最终选中的进攻方队伍行（以及匹配到的防守方队伍行）
        if (matchedIndex >= 0 && matchedIndex < attackRowBoxes.Count)
        {
            Rect selectedAtkRow = ClampRect(attackRowBoxes[matchedIndex], imgW, imgH);
            Cv2.Rectangle(debugMat, selectedAtkRow, new Scalar(0, 255, 0), 3);

            if (uploadScene == BattleSceneType.PrincessArenaThreeTeams && matchedIndex < defenseRowBoxes.Count)
            {
                Rect selectedDefRow = ClampRect(defenseRowBoxes[matchedIndex], imgW, imgH);
                Cv2.Rectangle(debugMat, selectedDefRow, new Scalar(0, 255, 0), 3);
            }
        }

        BitmapSource debugBmp = debugMat.ToBitmapSource();
        debugBmp.Freeze();

        string sceneDesc = uploadScene == BattleSceneType.PrincessArenaThreeTeams
            ? "プリンセスアリーナ模式"
            : "バトルアリーナ模式";

        return new UploadRecognitionResult(
            SceneType: uploadScene,
            SceneDescription: sceneDesc,
            AttackTeams: attackTeams,
            DefenseTeams: defenseTeams,
            MatchedTeamIndex: matchedIndex,
            DebugAnnotatedImage: debugBmp);
    }

    private List<RecognizedSlotResult> RecognizeFiveSlotsInRow(
        Mat bgrMat,
        Mat? debugMat,
        IReadOnlyList<Rect> rowAvatarRects,
        HashSet<int> usedIds,
        int imgW,
        int imgH)
    {
        var slotResults = new List<RecognizedSlotResult>(5);

        for (int col = 0; col < 5; col++)
        {
            Rect avatarBox = ClampRect(rowAvatarRects[col], imgW, imgH);
            using Mat rawSlice = new(bgrMat, avatarBox);

            using Mat slice64 = new();
            Cv2.Resize(rawSlice, slice64, new Size(TemplateSize, TemplateSize), 0, 0, InterpolationFlags.Area);
            BitmapSource previewBmp = slice64.ToBitmapSource();
            previewBmp.Freeze();

            using Mat faceBgr = new(slice64, QueryFaceRect);

            // 若槽位区域颜色极其平坦（例如 3v3 2:0 提前结束时第 3 队为空白底色），直接视为空槽位
            Cv2.MeanStdDev(faceBgr, out _, out Scalar stddev);
            double avgStdDev = (stddev.Val0 + stddev.Val1 + stddev.Val2) / 3.0;
            if (avgStdDev < 12.0)
            {
                slotResults.Add(new RecognizedSlotResult(
                    SlotIndex: col,
                    UnitId: 0,
                    UnitName: string.Empty,
                    Confidence: 0,
                    CroppedSlice: previewBmp,
                    MatchedIcon: null));
                continue;
            }

            using Mat faceGray = new();
            Cv2.CvtColor(faceBgr, faceGray, ColorConversionCodes.BGR2GRAY);

            var (bestUnit, bestIcon, bestScore) = MatchSingleFacePatch(faceGray, faceBgr, usedIds);

            if (bestUnit is not null)
            {
                usedIds.Add(bestUnit.UnitId);
                slotResults.Add(new RecognizedSlotResult(
                    SlotIndex: col,
                    UnitId: bestUnit.UnitId,
                    UnitName: bestUnit.Name,
                    Confidence: bestScore,
                    CroppedSlice: previewBmp,
                    MatchedIcon: bestIcon ?? bestUnit.DisplayIcon));
            }
            else
            {
                slotResults.Add(new RecognizedSlotResult(
                    SlotIndex: col,
                    UnitId: 0,
                    UnitName: "未识别",
                    Confidence: 0,
                    CroppedSlice: previewBmp,
                    MatchedIcon: null));
            }

            if (debugMat is not null)
            {
                Cv2.Rectangle(debugMat, avatarBox, new Scalar(0, 0, 255), 2);
                Rect debugFaceRect = new(
                    avatarBox.X + (int)Math.Round(avatarBox.Width * (QueryFaceRect.X / (double)TemplateSize)),
                    avatarBox.Y + (int)Math.Round(avatarBox.Height * (QueryFaceRect.Y / (double)TemplateSize)),
                    (int)Math.Round(avatarBox.Width * (QueryFaceRect.Width / (double)TemplateSize)),
                    (int)Math.Round(avatarBox.Height * (QueryFaceRect.Height / (double)TemplateSize)));
                Cv2.Rectangle(debugMat, ClampRect(debugFaceRect, imgW, imgH), new Scalar(0, 255, 0), 1);

                string label = bestUnit is not null ? $"{bestUnit.ShortUnitId}" : "?";
                Cv2.PutText(
                    debugMat,
                    label,
                    new Point(avatarBox.X + 2, avatarBox.Y + avatarBox.Height - 3),
                    HersheyFonts.HersheySimplex,
                    0.35,
                    new Scalar(50, 255, 50),
                    1);
            }
        }

        return slotResults;
    }

    /// <summary>
    /// 自动判断截图属于：
    /// 1. ArenaThreeOpponents（バトルアリーナ 3对手列表）
    /// 2. ArenaSingleHistoryDetail（バトルアリーナ 1v1 対戦履歴詳細弹窗）
    /// 3. PrincessArenaThreeTeams（プリンセスアリーナ 3v3 対戦履歴詳細弹窗）
    /// </summary>
    public static BattleSceneType DetectBattleSceneType(Mat bgrMat)
    {
        int w = bgrMat.Width;
        int h = bgrMat.Height;

        // 特征 1：图1左上角 (X: 7%..24%, Y: 3.5%..8.5%) 是纯白底色的“バトルアリーナ”标题牌 (R>215, G>215, B>215)
        Rect topLeftTitleRoi = ClampRect(
            new Rect((int)(w * 0.07), (int)(h * 0.035), (int)(w * 0.17), (int)(h * 0.050)),
            w,
            h);
        using Mat topLeftPatch = new(bgrMat, topLeftTitleRoi);
        Scalar meanTopLeft = Cv2.Mean(topLeftPatch);
        if (meanTopLeft.Val0 > 215 && meanTopLeft.Val1 > 215 && meanTopLeft.Val2 > 215)
        {
            return BattleSceneType.ArenaThreeOpponents;
        }

        // 特征 2：バトルアリーナ 1v1 対戦履歴詳細弹窗在 Y: 25.0%..29.5% 处有亮皇家蓝标题栏 ("対戦履歴詳細")
        Rect midHeaderRoi = ClampRect(
            new Rect((int)(w * 0.30), (int)(h * 0.250), (int)(w * 0.40), (int)(h * 0.045)),
            w,
            h);
        using Mat midHeaderPatch = new(bgrMat, midHeaderRoi);
        Scalar meanMid = Cv2.Mean(midHeaderPatch);
        if (meanMid.Val0 > 135 && meanMid.Val2 < 115 && (meanMid.Val0 - meanMid.Val2) > 65 && (meanMid.Val0 - meanMid.Val1) > 45)
        {
            return BattleSceneType.ArenaSingleHistoryDetail;
        }

        // 特征 3：プリンセスアリーナ 3v3 対戦履歴詳細弹窗顶部 (X: 30%..70%, Y: 5.0%..9.8%) 是亮皇家蓝标题栏 ("対戦履歴詳細")
        Rect topHeaderRoi = ClampRect(
            new Rect((int)(w * 0.30), (int)(h * 0.050), (int)(w * 0.40), (int)(h * 0.048)),
            w,
            h);
        using Mat topHeaderPatch = new(bgrMat, topHeaderRoi);
        Scalar meanTop = Cv2.Mean(topHeaderPatch);
        if (meanTop.Val0 > 135 && meanTop.Val2 < 115 && (meanTop.Val0 - meanTop.Val2) > 65 && (meanTop.Val0 - meanTop.Val1) > 45)
        {
            return BattleSceneType.PrincessArenaThreeTeams;
        }

        return BattleSceneType.ArenaThreeOpponents;
    }

    /// <summary>
    /// 根据场景类型及左/右侧（进攻方/防守方）构建分析区间及角色头像精确坐标
    /// </summary>
    private static (Rect ZoneRect, IReadOnlyList<Rect> RowBoxes, IReadOnlyList<IReadOnlyList<Rect>> AvatarGrid)
        BuildSceneRegions(BattleSceneType sceneType, int imgW, int imgH, bool isLeftAttackSide)
    {
        var rowBoxes = new List<Rect>();
        var avatarGrid = new List<IReadOnlyList<Rect>>();

        if (sceneType == BattleSceneType.ArenaThreeOpponents && !isLeftAttackSide)
        {
            // 图1（扫码）：竞技场选人列表（右侧 3 个对手卡片内的 5 人阵容）
            // 基准标定 (870x489): X0=603, stepX=44.5, Y=[149, 253, 357], size=41x41
            double[] normYs = [149.0 / 489.0, 253.0 / 489.0, 357.0 / 489.0];
            double normX0 = 603.0 / 870.0;
            double normStepX = 44.5 / 870.0;
            int boxW = Math.Max(16, (int)Math.Round(imgW * (41.0 / 870.0)));
            int boxH = Math.Max(16, (int)Math.Round(imgH * (41.0 / 489.0)));

            for (int r = 0; r < 3; r++)
            {
                int y = (int)Math.Round(imgH * normYs[r]);
                var rowAvatars = new List<Rect>(5);
                for (int c = 0; c < 5; c++)
                {
                    int x = (int)Math.Round(imgW * (normX0 + c * normStepX));
                    rowAvatars.Add(new Rect(x, y, boxW, boxH));
                }

                avatarGrid.Add(rowAvatars);
                int rowX = rowAvatars[0].X - 4;
                int rowRight = rowAvatars[^1].X + rowAvatars[^1].Width + 4;
                rowBoxes.Add(new Rect(rowX, y - 4, rowRight - rowX, boxH + 8));
            }

            Rect zone = new(
                rowBoxes[0].X - 8,
                rowBoxes[0].Y - 10,
                rowBoxes[0].Width + 16,
                (rowBoxes[^1].Y + rowBoxes[^1].Height) - rowBoxes[0].Y + 20);

            return (zone, rowBoxes, avatarGrid);
        }

        if (sceneType == BattleSceneType.ArenaSingleHistoryDetail)
        {
            // 图1（战绩上传）：バトルアリーナ 1v1 对战履历详情弹窗（单行 5 人）
            // 基准标定 (987x555):
            // 左侧进攻方: X0=61/987, stepX=68.5/987, Y=272/555, size=63x63
            // 右侧防守方: X0=476/987, stepX=68.5/987, Y=272/555, size=63x63
            double normY = 272.0 / 555.0;
            double normX0 = isLeftAttackSide ? (61.0 / 987.0) : (476.0 / 987.0);
            double normStepX = 68.5 / 987.0;
            int boxW = Math.Max(20, (int)Math.Round(imgW * (63.0 / 987.0)));
            int boxH = Math.Max(20, (int)Math.Round(imgH * (63.0 / 555.0)));

            int y = (int)Math.Round(imgH * normY);
            var rowAvatars = new List<Rect>(5);
            for (int c = 0; c < 5; c++)
            {
                int x = (int)Math.Round(imgW * (normX0 + c * normStepX));
                rowAvatars.Add(new Rect(x, y, boxW, boxH));
            }

            avatarGrid.Add(rowAvatars);
            int rowX = rowAvatars[0].X - 4;
            int rowRight = rowAvatars[^1].X + rowAvatars[^1].Width + 4;
            rowBoxes.Add(new Rect(rowX, y - 4, rowRight - rowX, boxH + 8));

            Rect zone = new(
                rowBoxes[0].X - 8,
                rowBoxes[0].Y - 10,
                rowBoxes[0].Width + 16,
                rowBoxes[0].Height + 20);

            return (zone, rowBoxes, avatarGrid);
        }

        // 图2（扫码/战绩上传）：プリンセスアリーナ 3v3 对战履历详情（3行 × 5人）
        // 基准标定 (987x555 / 870x489):
        // 左侧进攻方: X0=61/987 (0.0618), stepX=68.5/987 (0.0694), Y=[156/555, 270/555, 383/555], size=63x63
        // 右侧防守方: X0=476/987 (0.4820), stepX=68.5/987 (0.0694), Y=[156/555, 270/555, 383/555], size=63x63
        {
            double[] normYs = [156.0 / 555.0, 270.0 / 555.0, 383.0 / 555.0];
            double normX0 = isLeftAttackSide ? (61.0 / 987.0) : (476.0 / 987.0);
            double normStepX = 68.5 / 987.0;
            int boxW = Math.Max(20, (int)Math.Round(imgW * (63.0 / 987.0)));
            int boxH = Math.Max(20, (int)Math.Round(imgH * (63.0 / 555.0)));

            for (int r = 0; r < 3; r++)
            {
                int y = (int)Math.Round(imgH * normYs[r]);
                var rowAvatars = new List<Rect>(5);
                for (int c = 0; c < 5; c++)
                {
                    int x = (int)Math.Round(imgW * (normX0 + c * normStepX));
                    rowAvatars.Add(new Rect(x, y, boxW, boxH));
                }

                avatarGrid.Add(rowAvatars);
                int rowX = rowAvatars[0].X - 4;
                int rowRight = rowAvatars[^1].X + rowAvatars[^1].Width + 4;
                rowBoxes.Add(new Rect(rowX, y - 4, rowRight - rowX, boxH + 8));
            }

            Rect zone = new(
                rowBoxes[0].X - 8,
                rowBoxes[0].Y - 10,
                rowBoxes[0].Width + 16,
                (rowBoxes[2].Y + rowBoxes[2].Height) - rowBoxes[0].Y + 20);

            return (zone, rowBoxes, avatarGrid);
        }
    }

    /// <summary>
    /// 在 58x42 的模板窗口内滑动匹配 48x32 的纯面部特征块（自动容忍 ±5px 位置偏移，且完全不受 Lv 文字与星级遮挡影响）
    /// </summary>
    private (CharacterUnit? Unit, BitmapSource? MatchedVariantIcon, double Score) MatchSingleFacePatch(
        Mat queryFaceGray,
        Mat queryFaceBgr,
        HashSet<int> excludeUnitIds)
    {
        CharacterUnit? bestUnit = null;
        BitmapSource? bestIcon = null;
        double bestScore = -1.0;

        lock (_syncRoot)
        {
            foreach (CharacterUnit unit in _unitCatalog.Values)
            {
                if (unit.Templates.Count == 0 || excludeUnitIds.Contains(unit.UnitId))
                {
                    continue;
                }

                foreach (AvatarTemplate tpl in unit.Templates)
                {
                    // 1. BGR 彩色三通道滑动相关系数匹配（区分发色、瞳色与肤色）
                    using Mat bgrResult = new();
                    Cv2.MatchTemplate(tpl.Bgr64, queryFaceBgr, bgrResult, TemplateMatchModes.CCoeffNormed);
                    Cv2.MinMaxLoc(bgrResult, out _, out double bgrScore);

                    // 2. 灰度图滑动相关系数匹配（区分五官边缘结构）
                    using Mat grayResult = new();
                    Cv2.MatchTemplate(tpl.Gray64, queryFaceGray, grayResult, TemplateMatchModes.CCoeffNormed);
                    Cv2.MinMaxLoc(grayResult, out _, out double grayScore);

                    double combinedScore = bgrScore * 0.65 + grayScore * 0.35;
                    if (combinedScore > bestScore)
                    {
                        bestScore = combinedScore;
                        bestUnit = unit;
                        bestIcon = tpl.IconBitmap ?? unit.DisplayIcon;
                    }
                }
            }
        }

        return (bestUnit, bestIcon, Math.Clamp(bestScore, 0.0, 1.0));
    }

    private static Rect ClampRect(Rect rect, int maxW, int maxH)
    {
        int x = Math.Clamp(rect.X, 0, Math.Max(0, maxW - 1));
        int y = Math.Clamp(rect.Y, 0, Math.Max(0, maxH - 1));
        int w = Math.Clamp(rect.Width, 1, maxW - x);
        int h = Math.Clamp(rect.Height, 1, maxH - y);
        return new Rect(x, y, w, h);
    }

    public static bool TryResolveIconUnitId(
        string fileName,
        IReadOnlyDictionary<string, int> customMap,
        out int variantId,
        out int baseUnitId)
    {
        if (customMap.TryGetValue(fileName, out int mappedId))
        {
            variantId = mappedId;
            baseUnitId = CharacterUnit.NormalizeToBaseUnitId(mappedId);
            return true;
        }

        Match match = SixOrFourDigitIdRegex().Match(fileName);
        if (match.Success && int.TryParse(match.Groups[1].Value, out int parsed))
        {
            variantId = parsed;
            baseUnitId = CharacterUnit.NormalizeToBaseUnitId(parsed);
            return true;
        }

        variantId = 0;
        baseUnitId = 0;
        return false;
    }

    [GeneratedRegex(@"(\d{6}|\d{4})", RegexOptions.Compiled)]
    private static partial Regex SixOrFourDigitIdRegex();

    private Dictionary<string, int> LoadCustomMappingDictionary()
    {
        try
        {
            if (File.Exists(MappingFilePath))
            {
                string json = File.ReadAllText(MappingFilePath);
                var dict = JsonSerializer.Deserialize<Dictionary<string, int>>(json);
                if (dict is not null)
                {
                    return new Dictionary<string, int>(dict, StringComparer.OrdinalIgnoreCase);
                }
            }
        }
        catch
        {
        }

        return new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
    }

    private void EnsureDefaultMappingFile()
    {
        if (File.Exists(MappingFilePath))
        {
            return;
        }

        string? dir = Path.GetDirectoryName(MappingFilePath);
        if (!string.IsNullOrEmpty(dir))
        {
            Directory.CreateDirectory(dir);
        }

        var sampleMapping = new Dictionary<string, int>
        {
            ["icon_unit_100111.png"] = 100101,
            ["icon_unit_100131.png"] = 100101,
            ["icon_unit_100161.png"] = 100101,
            ["icon_unit_100211.png"] = 100201,
            ["icon_unit_100231.png"] = 100201,
            ["icon_unit_100261.png"] = 100201
        };

        string json = JsonSerializer.Serialize(sampleMapping, new JsonSerializerOptions { WriteIndented = true });
        File.WriteAllText(MappingFilePath, json);
    }

    private Dictionary<int, (string Name, int Position)> LoadAllUnitMetadata()
    {
        var result = new Dictionary<int, (string Name, int Position)>();
        var nicknames = GetBuiltInUnitNicknames();

        if (File.Exists(UnitsZhFilePath))
        {
            try
            {
                string json = File.ReadAllText(UnitsZhFilePath);
                using JsonDocument doc = JsonDocument.Parse(json);
                if (doc.RootElement.ValueKind == JsonValueKind.Array)
                {
                    foreach (JsonElement item in doc.RootElement.EnumerateArray())
                    {
                        int id = item.TryGetProperty("id", out var idEl) ? idEl.GetInt32() : 0;
                        string name = item.TryGetProperty("name", out var nameEl) ? nameEl.GetString() ?? "" : "";
                        int place = item.TryGetProperty("place", out var placeEl) ? placeEl.GetInt32() : 0;

                        if (id > 0 && !string.IsNullOrWhiteSpace(name))
                        {
                            if (nicknames.TryGetValue(id, out string? alias) && !name.Contains(alias))
                            {
                                name = $"{name}({alias})";
                            }
                            result[id] = (name, place);
                        }
                    }
                }
            }
            catch
            {
            }
        }

        foreach (var (id, alias) in nicknames)
        {
            if (!result.ContainsKey(id))
            {
                result[id] = (alias, 0);
            }
        }

        return result;
    }

    private static IReadOnlyDictionary<int, string> GetBuiltInUnitNicknames() =>
        new Dictionary<int, string>
        {
            [100101] = "猫拳",
            [100201] = "对不起",
            [100301] = "剑圣",
            [100401] = "炸弹人",
            [100501] = "跳跳虎",
            [100601] = "妹法",
            [100701] = "布丁",
            [100801] = "镜子",
            [100901] = "中二",
            [101001] = "狐狸",
            [101101] = "妹弓",
            [101201] = "星法",
            [101301] = "77k",
            [101401] = "驴",
            [101501] = "圣母",
            [101601] = "暴击弓",
            [101701] = "狗拳",
            [101801] = "魅魔",
            [102001] = "兔子",
            [102201] = "姐法",
            [102601] = "松鼠",
            [102701] = "病娇",
            [102801] = "充电宝",
            [102901] = "偶像",
            [103001] = "扇子",
            [103201] = "哈哈剑",
            [103301] = "奶牛",
            [103401] = "黄骑",
            [103601] = "xcw/小仓唯",
            [103801] = "TP弓",
            [104301] = "狼",
            [104401] = "yly/吸血鬼",
            [104501] = "抖M",
            [104601] = "猫剑",
            [104701] = "黑骑",
            [104901] = "姐姐",
            [105001] = "大眼",
            [105101] = "眼罩",
            [105201] = "羊驼",
            [105301] = "毛二力",
            [105401] = "裁缝",
            [105501] = "路人妹",
            [105601] = "流夏",
            [105701] = "团长",
            [105801] = "吃货",
            [105901] = "妈",
            [106001] = "黑猫",
            [106101] = "511/矛依未",
            [106301] = "亚里莎",
            [106801] = "晶/女王",
            [107001] = "448/似似花",
            [107101] = "克总",
            [107501] = "水吃",
            [107601] = "水妈",
            [107801] = "水黑",
            [108701] = "春猫",
            [108801] = "春田",
            [108901] = "春剑",
            [110301] = "水电",
            [110401] = "水狼",
            [110601] = "水狐",
            [111001] = "优妮",
            [111401] = "露娜",
            [111801] = "春吃",
            [111901] = "春妈",
            [112001] = "春黑",
            [180101] = "公主猫拳",
            [180201] = "公主优衣",
            [180301] = "公主剑圣",
            [180401] = "公主吃货",
            [180501] = "蝶妈",
            [180601] = "公主黑猫",
            [138001] = "薇奥莱特(伊邪那美)",
            [138101] = "莱拉艾尔(完美)",
            [138201] = "涅妃=涅拉(鬼面佛心)",
            [138301] = "佩可莉姆(阿斯特赖亚)",
            [138701] = "若菜(冬日)",
            [138801] = "栞(冬日)",
            [138901] = "格蕾丝(兔女郎)",
            [139001] = "镜华(哥特)",
            [139101] = "凯露(霸瞳天星)",
            [139201] = "美穗(少战)",
            [139301] = "真穗(少战)",
            [139401] = "逸见艾丽卡(少战)",
            [139501] = "莉莉(女武神)",
            [139601] = "克莉娅(女武神)",
            [139701] = "普蕾西亚(女武神)",
            [139801] = "雪菲(冰龙超克)",
            [139901] = "路易斯玛丽(夏日)",
            [140001] = "克蕾琪塔(夏日)",
            [140101] = "吹雪(夏日)",
            [140201] = "斑比(夏日)",
            [140301] = "缇娅",
            [140401] = "莉莎",
            [140501] = "乌尔姆(炎龙)",
            [140601] = "克罗茜(潜艇)",
            [140701] = "咲恋(枪手)",
            [140801] = "爱梅斯(枪手)",
            [140901] = "阿格莱亚",
            [141001] = "黄金船(赛马娘)",
            [141101] = "杏目(赛马娘)",
            [141201] = "幸运丁香(赛马娘)"
        };

    public void Dispose()
    {
        lock (_syncRoot)
        {
            foreach (var unit in _unitCatalog.Values)
            {
                unit.Dispose();
            }
            _unitCatalog.Clear();
        }
    }
}
