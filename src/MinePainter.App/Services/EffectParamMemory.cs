using MinePainter.Core.Effects;

namespace MinePainter.App.Services;

/// <summary>
/// 每種效果／調整記住「上次確定套用」的參數（存在 settings.json），
/// 下次從選單開同一個效果時直接沿用，不必重調。
/// </summary>
public static class EffectParamMemory
{
    /// <summary>
    /// 從選單新建效果時：有記憶就套上記住的參數（顏色也包含在內）；沒有記憶才用預設值，
    /// 並把「預設帶主色」的顏色參數（外框色之類）填成目前主色。
    /// 以前是先 Recall 再一律套主色，記住的外框顏色每次都被主色蓋掉。
    /// </summary>
    public static IEffect Recall(IEffect fresh, SkiaSharp.SKColor primary)
    {
        try
        {
            var id = EffectSerializer.TypeIdOf(fresh);
            if (AppSettings.Instance.EffectParams.TryGetValue(id, out var saved) && saved.Count > 0)
                return EffectSerializer.Load(id, saved);
        }
        catch
        {
            // 記憶壞掉就當沒有
        }
        return EffectSerializer.WithPrimaryColor(fresh, primary);
    }

    /// <summary>使用者按下確定之後：記住這組參數。</summary>
    public static void Remember(IEffect effect)
    {
        try
        {
            var id = EffectSerializer.TypeIdOf(effect);
            AppSettings.Instance.EffectParams[id] = EffectSerializer.Save(effect);
            AppSettings.Instance.Save();
        }
        catch
        {
            // 記不住就算了，不影響套用
        }
    }
}
