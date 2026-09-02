using SkiaSharp;

namespace MinePainter.Core.Vectors;

/// <summary>
/// 鋼筆路徑的錨點：位置＋進入／離開的控制把手（絕對座標）。
/// 兩個把手都等於 Point 時是「角點」（直線相接）；否則是「平滑點」（貝茲曲線）。
/// </summary>
public sealed record PenAnchor(SKPoint Point, SKPoint HandleIn, SKPoint HandleOut)
{
    public static PenAnchor Corner(SKPoint p) => new(p, p, p);

    public bool HasHandleIn => HandleIn != Point;
    public bool HasHandleOut => HandleOut != Point;
    public bool IsSmooth => HasHandleIn || HasHandleOut;

    public PenAnchor Translated(float dx, float dy) => new(
        new SKPoint(Point.X + dx, Point.Y + dy),
        new SKPoint(HandleIn.X + dx, HandleIn.Y + dy),
        new SKPoint(HandleOut.X + dx, HandleOut.Y + dy));

    /// <summary>以 Point 為軸、把手對稱：HandleOut = p、HandleIn = 鏡射（平滑點的標準建立方式）。</summary>
    public PenAnchor WithSymmetricOut(SKPoint p) => this with
    {
        HandleOut = p,
        HandleIn = new SKPoint(2 * Point.X - p.X, 2 * Point.Y - p.Y),
    };
}

/// <summary>
/// 鋼筆工具的工作路徑（Photoshop 的「工作路徑」）。immutable：每次改動回傳新實例，
/// render thread 直接讀 <see cref="Tools.EditorSession.PenPath"/> 不必鎖。
/// Active＝目前被選中／剛加入的錨點（畫把手用），-1 = 無。
/// Finished＝開放路徑已用 Enter／右鍵結束（之後點擊是開新路徑，不是接著畫）。
/// </summary>
public sealed record PenPath(PenAnchor[] Anchors, bool Closed = false, bool Finished = false, int Active = -1)
{
    public static readonly PenPath Empty = new([]);

    public int Count => Anchors.Length;
    public bool IsEmpty => Anchors.Length == 0;

    /// <summary>還能接著加錨點（開放、未結束）。</summary>
    public bool IsAppendable => !Closed && !Finished;

    public PenPath Append(PenAnchor anchor)
    {
        var next = new PenAnchor[Anchors.Length + 1];
        Anchors.CopyTo(next, 0);
        next[^1] = anchor;
        return this with { Anchors = next, Active = next.Length - 1 };
    }

    public PenPath Replace(int index, PenAnchor anchor)
    {
        if (index < 0 || index >= Anchors.Length) return this;
        var next = (PenAnchor[])Anchors.Clone();
        next[index] = anchor;
        return this with { Anchors = next };
    }

    /// <summary>移除最後一個錨點（Backspace）；封閉路徑先解封。</summary>
    public PenPath RemoveLast()
    {
        if (Anchors.Length == 0) return this;
        if (Closed) return this with { Closed = false, Finished = false, Active = Anchors.Length - 1 };
        var next = Anchors[..^1];
        return this with { Anchors = next, Finished = false, Active = next.Length - 1 };
    }

    public PenPath WithClosed() => this with { Closed = true, Finished = true, Active = 0 };

    public PenPath WithFinished() => this with { Finished = true };

    public PenPath WithActive(int index) => this with { Active = index };

    /// <summary>命中的錨點索引（容差內最近的）；-1 = 無。</summary>
    public int HitAnchor(SKPoint p, float tolerance)
    {
        var best = -1;
        var bestDist = float.MaxValue;
        for (var i = 0; i < Anchors.Length; i++)
        {
            var a = Anchors[i].Point;
            var d = Math.Max(Math.Abs(p.X - a.X), Math.Abs(p.Y - a.Y));
            if (d <= tolerance && d < bestDist)
            {
                best = i;
                bestDist = d;
            }
        }
        return best;
    }

    /// <summary>
    /// 幾何路徑。開放路徑 <paramref name="forceClose"/> 時以直線封回起點
    /// （轉選取／填滿時的語意，與 PS 相同）。
    /// </summary>
    public SKPath ToSKPath(bool forceClose = false)
    {
        var path = new SKPath();
        if (Anchors.Length == 0) return path;
        path.MoveTo(Anchors[0].Point);
        for (var i = 1; i < Anchors.Length; i++)
            AddSegment(path, Anchors[i - 1], Anchors[i]);
        if (Closed && Anchors.Length > 1)
        {
            AddSegment(path, Anchors[^1], Anchors[0]);
            path.Close();
        }
        else if (forceClose && Anchors.Length > 2)
        {
            path.Close();
        }
        return path;
    }

    private static void AddSegment(SKPath path, PenAnchor from, PenAnchor to)
    {
        if (!from.HasHandleOut && !to.HasHandleIn)
            path.LineTo(to.Point);
        else
            path.CubicTo(from.HandleOut, to.HandleIn, to.Point);
    }

    /// <summary>路徑（含把手）的外框；空路徑為 Empty。</summary>
    public SKRect Bounds
    {
        get
        {
            if (Anchors.Length == 0) return SKRect.Empty;
            float l = float.MaxValue, t = float.MaxValue, r = float.MinValue, b = float.MinValue;
            foreach (var a in Anchors)
            {
                foreach (var p in new[] { a.Point, a.HandleIn, a.HandleOut })
                {
                    l = Math.Min(l, p.X); t = Math.Min(t, p.Y);
                    r = Math.Max(r, p.X); b = Math.Max(b, p.Y);
                }
            }
            return new SKRect(l, t, r, b);
        }
    }
}
