using SkiaSharp;

namespace MinePainter.Core.Vectors;
public sealed record TextDeform(SKMatrix Projective, Tools.WarpMesh? Warp)
{
    public static readonly TextDeform None = new(SKMatrix.Identity, null);

    public bool IsIdentity => Warp == null && IsIdentityMatrix(Projective);

    private static bool IsIdentityMatrix(SKMatrix m) =>
        Math.Abs(m.ScaleX - 1) < 1e-5f && Math.Abs(m.ScaleY - 1) < 1e-5f &&
        Math.Abs(m.SkewX) < 1e-5f && Math.Abs(m.SkewY) < 1e-5f &&
        Math.Abs(m.TransX) < 1e-3f && Math.Abs(m.TransY) < 1e-3f &&
        Math.Abs(m.Persp0) < 1e-9f && Math.Abs(m.Persp1) < 1e-9f && Math.Abs(m.Persp2 - 1) < 1e-5f;

    public SKPoint MapPoint(SKPoint p)
    {
        var q = Projective.MapPoint(p);
        return Warp?.MapPoint(q) ?? q;
    }

    /// <summary>矩形經整套變形後的外接矩形。</summary>
    public SKRect MapBounds(SKRect r)
    {
        var q = Projective.MapRect(r);
        return Warp?.MapBounds(q) ?? q;
    }

    /// <summary>輸入端平移 d（文字搬家）：輸出也跟著平移。</summary>
    public TextDeform Translated(float dx, float dy)
    {
        var t = SKMatrix.CreateTranslation(dx, dy);
        var p = SKMatrix.Concat(t, SKMatrix.Concat(Projective, SKMatrix.CreateTranslation(-dx, -dy)));
        return new TextDeform(p, Warp?.TranslatedWithFrame(dx, dy));
    }

    /// <summary>輸出端再套一個矩陣（仿射精確；透視在有網格時是控制點近似）。</summary>
    public TextDeform Then(SKMatrix m) => Warp == null
        ? new TextDeform(SKMatrix.Concat(m, Projective), null)
        : new TextDeform(Projective, Warp.Transformed(m));

    /// <summary>輸出端再套一張網格。</summary>
    public TextDeform Then(Tools.WarpMesh mesh) => Warp == null
        ? new TextDeform(Projective, mesh)
        : new TextDeform(Projective, Warp.Then(mesh));

    public bool Equals(TextDeform? other) =>
        other != null && Projective == other.Projective &&
        (Warp == null ? other.Warp == null
            : other.Warp != null && Warp.Frame == other.Warp.Frame &&
              Tools.QuadGeometry.NearlyEqual(Warp.Points, other.Warp.Points, 0f));

    public override int GetHashCode() => Projective.GetHashCode();
}
