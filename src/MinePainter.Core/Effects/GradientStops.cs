using SkiaSharp;

namespace MinePainter.Core.Effects;

/// <summary>漸層的一個節點：位置 0..1 與顏色（未預乘）。</summary>
public readonly record struct GradientStop(float Position, SKColor Color);

/// <summary>
/// 多節點漸層（不可變）：節點依位置排序，至少一個。兩節點之間線性內插 RGBA，
/// 首節點之前／末節點之後取端點色。存檔格式："pos:AARRGGBB;pos:AARRGGBB;…"。
/// </summary>
public sealed class GradientStops : IEquatable<GradientStops>
{
    private readonly GradientStop[] _stops;

    public IReadOnlyList<GradientStop> Stops => _stops;
    public int Count => _stops.Length;
    public GradientStop this[int index] => _stops[index];

    public GradientStops(IEnumerable<GradientStop> stops)
    {
        _stops = stops
            .Select(s => s with { Position = Math.Clamp(s.Position, 0f, 1f) })
            .OrderBy(s => s.Position)
            .ToArray();
        if (_stops.Length == 0) _stops = [new GradientStop(0f, SKColors.Black)];
    }

    public static GradientStops Two(SKColor start, SKColor end) =>
        new([new GradientStop(0f, start), new GradientStop(1f, end)]);

    public SKColor First => _stops[0].Color;
    public SKColor Last => _stops[^1].Color;

    /// <summary>t 處的顏色（未預乘；alpha 也內插）。</summary>
    public SKColor ColorAt(float t)
    {
        if (_stops.Length == 1 || t <= _stops[0].Position) return _stops[0].Color;
        if (t >= _stops[^1].Position) return _stops[^1].Color;
        var i = 1;
        while (i < _stops.Length - 1 && _stops[i].Position < t) i++;
        var a = _stops[i - 1];
        var b = _stops[i];
        var span = b.Position - a.Position;
        var f = span <= 1e-6f ? 1f : (t - a.Position) / span;
        return Lerp(a.Color, b.Color, f);
    }

    private static SKColor Lerp(SKColor a, SKColor b, float f) => new(
        (byte)Math.Round(a.Red + (b.Red - a.Red) * f),
        (byte)Math.Round(a.Green + (b.Green - a.Green) * f),
        (byte)Math.Round(a.Blue + (b.Blue - a.Blue) * f),
        (byte)Math.Round(a.Alpha + (b.Alpha - a.Alpha) * f));

    /// <summary>查表：size 格（含兩端）。</summary>
    public SKColor[] BuildLut(int size = 257)
    {
        var lut = new SKColor[size];
        for (var i = 0; i < size; i++) lut[i] = ColorAt(i / (float)(size - 1));
        return lut;
    }

    // ---- 編輯（回傳新實例）----

    public GradientStops WithStop(int index, GradientStop stop)
    {
        var copy = (GradientStop[])_stops.Clone();
        copy[index] = stop;
        return new GradientStops(copy);
    }

    public GradientStops WithColor(int index, SKColor color) => WithStop(index, _stops[index] with { Color = color });
    public GradientStops WithPosition(int index, float position) => WithStop(index, _stops[index] with { Position = position });

    public GradientStops Add(GradientStop stop) => new(_stops.Append(stop));

    /// <summary>在 t 處插入一個節點，顏色取該處的漸層色（視覺上不變）。</summary>
    public GradientStops Insert(float t) => Add(new GradientStop(t, ColorAt(t)));

    public GradientStops RemoveAt(int index)
    {
        if (_stops.Length <= 2) return this; // 至少留兩個節點才叫漸層
        return new GradientStops(_stops.Where((_, i) => i != index));
    }

    public GradientStops Reversed() => new(_stops.Select(s => s with { Position = 1f - s.Position }));

    /// <summary>首節點換色（相容舊的「起始色」欄位）。</summary>
    public GradientStops WithStart(SKColor color) => WithColor(0, color);

    /// <summary>末節點換色（相容舊的「結束色」欄位）。</summary>
    public GradientStops WithEnd(SKColor color) => WithColor(_stops.Length - 1, color);

    /// <summary>排序後的節點索引（給 UI：位置改變後同一節點可能換索引）。</summary>
    public int IndexOf(GradientStop stop) => Array.IndexOf(_stops, stop);

    // ---- 序列化 ----

    public string Serialize()
    {
        var inv = System.Globalization.CultureInfo.InvariantCulture;
        return string.Join(";", _stops.Select(s => $"{s.Position.ToString("0.####", inv)}:{(uint)s.Color:X8}"));
    }

    public static bool TryParse(string? text, out GradientStops stops)
    {
        stops = null!;
        if (string.IsNullOrWhiteSpace(text)) return false;
        var inv = System.Globalization.CultureInfo.InvariantCulture;
        var list = new List<GradientStop>();
        foreach (var part in text.Split(';', StringSplitOptions.RemoveEmptyEntries))
        {
            var pieces = part.Split(':');
            if (pieces.Length != 2) return false;
            if (!float.TryParse(pieces[0], System.Globalization.NumberStyles.Float, inv, out var pos)) return false;
            if (!uint.TryParse(pieces[1], System.Globalization.NumberStyles.HexNumber, inv, out var argb)) return false;
            list.Add(new GradientStop(pos, new SKColor(argb)));
        }
        if (list.Count == 0) return false;
        stops = new GradientStops(list);
        return true;
    }

    public bool Equals(GradientStops? other) =>
        other != null && _stops.AsSpan().SequenceEqual(other._stops);

    public override bool Equals(object? obj) => Equals(obj as GradientStops);

    public override int GetHashCode()
    {
        var h = new HashCode();
        foreach (var s in _stops) h.Add(s);
        return h.ToHashCode();
    }
}
