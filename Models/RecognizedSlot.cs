using System.Windows.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;

namespace PcrBattleAssistant.Models;

/// <summary>
/// 顶部原生识别确认条中的单个角色槽位状态（共 5 个槽位，按 X 坐标从左到右排列）
/// </summary>
public partial class RecognizedSlot : ObservableObject
{
    /// <summary>
    /// 槽位序号（0 ~ 4）
    /// </summary>
    [ObservableProperty]
    private int _slotIndex;

    /// <summary>
    /// 识别出的角色基础 UnitID（如 100101）
    /// </summary>
    [ObservableProperty]
    private int _unitId;

    /// <summary>
    /// 识别出的角色名称
    /// </summary>
    [ObservableProperty]
    private string _unitName = "未识别";

    /// <summary>
    /// 模板匹配归一化相关系数置信度（0.0 ~ 1.0）
    /// </summary>
    [ObservableProperty]
    private double _confidence;

    /// <summary>
    /// 匹配到的本地特征库标准头像
    /// </summary>
    [ObservableProperty]
    private BitmapSource? _matchedIcon;

    /// <summary>
    /// 从原图中裁切出的 64x64 原始头像切片（便于对比确认）
    /// </summary>
    [ObservableProperty]
    private BitmapSource? _croppedSlice;

    /// <summary>
    /// 优先显示匹配到的标准头像，若无则显示原图裁切切片
    /// </summary>
    public BitmapSource? EffectiveAvatar => MatchedIcon ?? CroppedSlice;

    public string ConfidenceText => Confidence > 0 ? $"{Confidence:P0}" : "--";

    partial void OnMatchedIconChanged(BitmapSource? value) => OnPropertyChanged(nameof(EffectiveAvatar));
    partial void OnCroppedSliceChanged(BitmapSource? value) => OnPropertyChanged(nameof(EffectiveAvatar));
    partial void OnConfidenceChanged(double value) => OnPropertyChanged(nameof(ConfidenceText));
}
