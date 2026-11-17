using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;

namespace PcrBattleAssistant.Models;

/// <summary>
/// 识别场景类型
/// </summary>
public enum BattleSceneType
{
    /// <summary>
    /// 图1（扫码）：普通竞技场选人列表（右侧同时识别 3 个对手阵容，用户可点击选择要查哪一个阵容）
    /// </summary>
    ArenaThreeOpponents = 1,

    /// <summary>
    /// 图2（扫码/上传）：公主竞技场 3v3 对战履历详情（含左侧进攻方 3 队与右侧防守方 3 队）
    /// </summary>
    PrincessArenaThreeTeams = 2,

    /// <summary>
    /// 图1（战绩上传）：普通竞技场 1v1 对战履历详情弹窗（左侧进攻方 1 队，右侧防守方 1 队）
    /// </summary>
    ArenaSingleHistoryDetail = 3
}

/// <summary>
/// 单个识别出的 5 人阵容组（共 3 组：对应图1的 3 个对手阵容，或图2的 1队/2队/3队）
/// </summary>
public partial class RecognizedTeamGroup : ObservableObject
{
    /// <summary>
    /// 队伍序号（1, 2, 3）
    /// </summary>
    [ObservableProperty]
    private int _teamNumber;

    /// <summary>
    /// 显示标题（如“对手阵容 1” 或 “防守 1队”）
    /// </summary>
    [ObservableProperty]
    private string _title = string.Empty;

    /// <summary>
    /// 当前是否被选中为活动查询阵容
    /// </summary>
    [ObservableProperty]
    private bool _isSelected;

    /// <summary>
    /// 该阵容内的 5 个角色槽位（从左到右）
    /// </summary>
    public ObservableCollection<RecognizedSlot> Slots { get; } = [];

    public RecognizedTeamGroup(int teamNumber, string title)
    {
        _teamNumber = teamNumber;
        _title = title;
        for (int i = 0; i < 5; i++)
        {
            Slots.Add(new RecognizedSlot
            {
                SlotIndex = i,
                UnitId = 0,
                UnitName = $"槽位 {i + 1}"
            });
        }
    }

    public IReadOnlyList<int> GetValidUnitIds() =>
        Slots.Select(s => s.UnitId).Where(id => id > 0).ToList();
}
