using MinePainter.Core.Adjustments;
using MinePainter.Core.Tiles;
using SkiaSharp;

namespace MinePainter.Core.Effects;

/// <summary>
/// 圖層效果堆疊的一筆（非破壞性）：效果＋開關＋套用時的選取遮罩（null = 整層）。
/// 不可變：改參數／開關 = 換新實例（undo 只需換整份清單）。
///
/// **遮罩釘在圖層上，不是釘在畫布上。** 遮罩本身存的是套用當下的 doc 座標，
/// <see cref="MaskAnchor"/> 記下當時的圖層位移；之後圖層座標 L 對到遮罩座標 L + MaskAnchor，
/// 圖層怎麼平移遮罩都跟著內容走。以前直接拿「現在的」位移去讀，圖層一搬家遮罩就留在原地，
/// 下一次重算（例如拖出畫布再拖回來）效果就從一部分內容上消失（2026-09-17 使用者回報）。
/// </summary>
public sealed record LayerEffect(Guid Id, IEffect Effect, bool Enabled = true, MaskSurface? Mask = null)
{
    public string Name => Effect.Name;

    /// <summary>套用當時的主色（雲朵、物件外框等會用到）。</summary>
    public SKColor Color { get; init; } = SKColors.Black;

    /// <summary>
    /// 遮罩建立當時的圖層位移（<see cref="Layers.LayerNode.EffectOffset"/>）。
    /// null＝沒記（沒有遮罩、或舊資料）：照舊以目前的位移讀。
    /// </summary>
    public SKPointI? MaskAnchor { get; init; }

    public static LayerEffect Create(IEffect effect, MaskSurface? mask = null, SKColor? color = null) =>
        new(Guid.NewGuid(), effect, true, mask) { Color = color ?? SKColors.Black };

    /// <summary>
    /// 「把效果套到這個圖層、限目前的選取範圍」的唯一入口（選單、效果堆疊面板共用）。
    /// 選取蓋滿整張畫布（全選）＝整層，不帶遮罩 —— 選取永遠夾在畫布內，帶著它的話
    /// 圖層在畫布外的那部分就套不到效果，之後一拖進畫面就露出沒處理過的樣子。
    /// </summary>
    public static LayerEffect CreateFor(Layers.LayerNode layer, SKRectI canvas, IEffect effect,
        Selections.SelectionMask? selection, SKColor? color = null)
    {
        if (selection is not { IsEmpty: false } || CoversCanvas(selection, canvas))
            return Create(effect, null, color);
        return Create(effect, selection.Clone().Mask, color) with { MaskAnchor = layer.EffectOffset };
    }

    private static bool CoversCanvas(Selections.SelectionMask selection, SKRectI canvas)
    {
        if (!selection.Bounds.Contains(canvas)) return false;
        foreach (var idx in TileIndex.CoveringRect(canvas))
        {
            var tile = selection.Mask.GetForRead(idx);
            if (tile == null) return false;
            var rect = idx.ToPixelRect();
            var inter = SKRectI.Intersect(rect, canvas);
            for (var y = inter.Top; y < inter.Bottom; y++)
            {
                var row = tile.Alpha.AsSpan((y - rect.Top) * MaskTile.Size + (inter.Left - rect.Left), inter.Width);
                if (row.IndexOfAnyExcept((byte)255) >= 0) return false;
            }
        }
        return true;
    }
}

/// <summary>
/// 效果 ↔ 字串字典（.mpp 與預設集共用）。一般效果走參數描述（ParamDef）逐鍵存；
/// 調整走 IAdjustment.SaveParams（曲線的控制點才存得下）。
/// </summary>
public static class EffectSerializer
{
    public const string AdjustmentPrefix = "adjust:";

    public static string TypeIdOf(IEffect effect) => effect switch
    {
        AdjustmentEffect a => AdjustmentPrefix + a.Adjustment.TypeId,
        _ => EffectRegistry.All.FirstOrDefault(e => e.Create().GetType() == effect.GetType())?.Id
             ?? throw new NotSupportedException($"效果未登錄：{effect.GetType().Name}"),
    };

    public static Dictionary<string, string> Save(IEffect effect)
    {
        var dict = new Dictionary<string, string>();
        if (effect is AdjustmentEffect adj)
        {
            foreach (var (k, v) in adj.Adjustment.SaveParams())
                dict[k] = v.ToString(System.Globalization.CultureInfo.InvariantCulture);
            if (adj.Adjustment.SaveData() is { } data) dict["data"] = data;
            return dict;
        }

        var inv = System.Globalization.CultureInfo.InvariantCulture;
        foreach (var def in effect.Parameters)
        {
            switch (def)
            {
                case SliderParam s: dict[s.Key] = s.Get(effect).ToString(inv); break;
                case AngleParam a: dict[a.Key] = a.Get(effect).ToString(inv); break;
                case BoolParam b: dict[b.Key] = b.Get(effect) ? "1" : "0"; break;
                case ChoiceParam c: dict[c.Key] = c.Get(effect).ToString(inv); break;
                case ColorParam col: dict[col.Key] = ((uint)col.Get(effect)).ToString("X8"); break;
                case GradientParam g: dict[g.Key] = g.Get(effect).Serialize(); break;
                case PointParam p:
                    var v = p.Get(effect);
                    dict[p.Key] = $"{v.X.ToString(inv)},{v.Y.ToString(inv)}";
                    break;
            }
        }
        return dict;
    }

    /// <summary>從選單新增時：有「預設帶主色」的顏色參數先填上目前主色。</summary>
    public static IEffect WithPrimaryColor(IEffect effect, SKColor primary)
    {
        object current = effect;
        foreach (var def in effect.Parameters)
        {
            if (def is ColorParam { UsePrimaryByDefault: true } c) current = c.With(current, primary);
        }
        return (IEffect)current;
    }

    public static IEffect Load(string typeId, IReadOnlyDictionary<string, string>? parameters)
    {
        parameters ??= new Dictionary<string, string>();
        var inv = System.Globalization.CultureInfo.InvariantCulture;

        if (typeId.StartsWith(AdjustmentPrefix, StringComparison.Ordinal))
        {
            var floats = new Dictionary<string, float>();
            foreach (var (k, v) in parameters)
                if (k != "data" && float.TryParse(v, System.Globalization.NumberStyles.Float, inv, out var f)) floats[k] = f;
            return new AdjustmentEffect(AdjustmentRegistry.Load(typeId[AdjustmentPrefix.Length..], floats,
                parameters.GetValueOrDefault("data")));
        }

        var entry = EffectRegistry.All.FirstOrDefault(e => e.Id == typeId)
            ?? throw new InvalidDataException($"未知效果：{typeId}");
        object effect = entry.Create();
        // 跑兩遍：有些參數要等模式切過去才會出現在 Parameters 裡（例如內光暈的光源角度）
        for (var pass = 0; pass < 2; pass++)
        foreach (var def in ((IEffect)effect).Parameters)
        {
            if (!parameters.TryGetValue(def.Key, out var raw))
            {
                // 舊檔的兩色漸層（起始色／結束色兩個鍵）→ 兩節點
                if (def is GradientParam { LegacyStartKey: { } sk, LegacyEndKey: { } ek } lg &&
                    parameters.TryGetValue(sk, out var sRaw) && parameters.TryGetValue(ek, out var eRaw) &&
                    uint.TryParse(sRaw, System.Globalization.NumberStyles.HexNumber, inv, out var sArgb) &&
                    uint.TryParse(eRaw, System.Globalization.NumberStyles.HexNumber, inv, out var eArgb))
                {
                    effect = lg.With(effect, GradientStops.Two(new SkiaSharp.SKColor(sArgb), new SkiaSharp.SKColor(eArgb)));
                }
                continue;
            }
            try
            {
                effect = def switch
                {
                    SliderParam s => s.With(effect, double.Parse(raw, inv)),
                    AngleParam a => a.With(effect, double.Parse(raw, inv)),
                    BoolParam b => b.With(effect, raw == "1" || raw.Equals("true", StringComparison.OrdinalIgnoreCase)),
                    ChoiceParam c => c.With(effect, int.Parse(raw, inv)),
                    ColorParam col => col.With(effect, new SkiaSharp.SKColor(uint.Parse(raw, System.Globalization.NumberStyles.HexNumber, inv))),
                    GradientParam g when GradientStops.TryParse(raw, out var stops) => g.With(effect, stops),
                    PointParam p when raw.Split(',') is [var xs, var ys] =>
                        p.With(effect, (float.Parse(xs, inv), float.Parse(ys, inv))),
                    _ => effect,
                };
            }
            catch (FormatException)
            {
                // 壞掉的值略過，保留預設
            }
        }
        return (IEffect)effect;
    }
}
