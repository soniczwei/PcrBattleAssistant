using System.Windows.Media.Imaging;
using OpenCvSharp;

namespace PcrBattleAssistant.Models;

/// <summary>
/// 角色基础信息与多形态（3星/6星）头像模板模型
/// </summary>
public sealed class CharacterUnit : IDisposable
{
    /// <summary>
    /// 标准化后的 6 位基础 UnitID（例如 100101，3星 100131 与 6星 100161 均归一到此 ID）
    /// </summary>
    public required int UnitId { get; init; }

    /// <summary>
    /// 4 位角色短 ID（例如 1001 = UnitId / 100），用于兼容不同 URL 命名规则
    /// </summary>
    public int ShortUnitId => UnitId >= 100000 ? UnitId / 100 : UnitId;

    /// <summary>
    /// 角色名称（如“佩可莉姆”、“凯露”）
    /// </summary>
    public required string Name { get; set; }

    /// <summary>
    /// 站位分类：1=前卫, 2=中卫, 3=后卫, 0=未知
    /// </summary>
    public int PositionType { get; set; }

    /// <summary>
    /// 用于 UI 展示的默认头像
    /// </summary>
    public BitmapSource? DisplayIcon { get; set; }

    /// <summary>
    /// 同一角色下的多个头像特征模板（如 1星、3星常规、6星开花头像），统一缩放为 64x64 灰度图与彩色图
    /// </summary>
    public List<AvatarTemplate> Templates { get; } = [];

    /// <summary>
    /// 将任意形态的头像 ID（如 100131、100161、1001）标准化为 6 位基础 UnitID（100101）
    /// </summary>
    public static int NormalizeToBaseUnitId(int rawId)
    {
        if (rawId is >= 1000 and <= 9999)
        {
            return rawId * 100 + 1;
        }

        if (rawId >= 100000)
        {
            return (rawId / 100) * 100 + 1;
        }

        return rawId;
    }

    public void Dispose()
    {
        foreach (var template in Templates)
        {
            template.Dispose();
        }
        Templates.Clear();
    }
}

/// <summary>
/// 单个头像变体特征模板（例如 3星 或 6星）
/// </summary>
public sealed class AvatarTemplate : IDisposable
{
    public required int VariantId { get; init; }
    public required string SourcePath { get; init; }
    public required Mat Gray64 { get; init; }
    public required Mat Bgr64 { get; init; }
    public BitmapSource? IconBitmap { get; init; }

    public void Dispose()
    {
        Gray64.Dispose();
        Bgr64.Dispose();
    }
}
