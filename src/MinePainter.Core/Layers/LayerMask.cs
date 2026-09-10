using SkiaSharp;

namespace MinePainter.Core.Layers;

/// <summary>
/// Layer coverage in document coordinates. Alpha is immutable after construction:
/// replace the record and pixel array when editing coverage.
/// </summary>
public sealed record LayerMask(SKRectI Bounds, byte[] Alpha, byte DefaultValue)
{
    public float Feather { get; init; }
    public float Density { get; init; } = 1;
    public bool Enabled { get; init; } = true;
    public bool Inverted { get; init; }
    private LayerMask? _rendered;
    private (float Feather, float Density, bool Enabled, bool Inverted) _renderedSettings;

    // A with-expression may replace Bounds, Alpha or DefaultValue after this copy.
    // Derived coverage must never survive that copy, even when settings match.
    private LayerMask(LayerMask original)
    {
        Bounds = original.Bounds;
        Alpha = original.Alpha;
        DefaultValue = original.DefaultValue;
        Feather = original.Feather;
        Density = original.Density;
        Enabled = original.Enabled;
        Inverted = original.Inverted;
    }

    public LayerMask Rendered
    {
        get
        {
            if (Enabled && !Inverted && Feather <= 0 && Density >= 1) return this;
            var settings = (Feather, Density, Enabled, Inverted);
            if (_rendered != null && _renderedSettings == settings) return _rendered;
            lock (Alpha)
            {
                if (_rendered != null && _renderedSettings == settings) return _rendered;
                _rendered = BuildCoverage();
                _renderedSettings = settings;
                return _rendered;
            }
        }
    }

    public byte At(int x, int y) => Rendered.RawAt(x, y);
    private byte RawAt(int x, int y) => Bounds.Contains(x, y)
        ? Alpha[(y - Bounds.Top) * Bounds.Width + x - Bounds.Left] : DefaultValue;

    public LayerMask Translated(int dx, int dy)
    {
        if (dx == 0 && dy == 0) return this;
        var bounds = Bounds;
        bounds.Offset(dx, dy);
        return this with { Bounds = bounds };
    }

    public unsafe LayerMask Transformed(SKMatrix matrix, Tools.WarpMesh? warp = null)
    {
        if (matrix.IsIdentity && warp == null) return this;
        if (Bounds.IsEmpty) return this;
        var mapped = matrix.MapRect(Bounds);
        var target = warp?.MapBounds(mapped) ?? mapped;
        var bounds = new SKRectI((int)MathF.Floor(target.Left), (int)MathF.Floor(target.Top),
            (int)MathF.Ceiling(target.Right), (int)MathF.Ceiling(target.Bottom));
        if (bounds.IsEmpty) return this with { Bounds = SKRectI.Empty, Alpha = [] };
        // Opaque grayscale retains zero coverage under SrcOver and mesh sampling.
        using var original = new SKBitmap(new SKImageInfo(Bounds.Width, Bounds.Height,
            SKColorType.Bgra8888, SKAlphaType.Premul));
        var src = (uint*)original.GetPixels();
        for (var i = 0; i < Alpha.Length; i++) src[i] = 0xff000000u | (uint)Alpha[i] * 0x010101u;
        using var result = new SKBitmap(new SKImageInfo(bounds.Width, bounds.Height,
            SKColorType.Bgra8888, SKAlphaType.Premul));
        using (var canvas = new SKCanvas(result))
        {
            canvas.Clear(new SKColor(DefaultValue, DefaultValue, DefaultValue));
            canvas.Translate(-bounds.Left, -bounds.Top);
            if (warp != null)
            {
                using var image = SKImage.FromBitmap(original);
                warp.Draw(canvas, image, Bounds, matrix, SKFilterQuality.Medium, mapped);
            }
            else
            {
                canvas.Concat(ref matrix);
                using var paint = new SKPaint { FilterQuality = SKFilterQuality.Medium, IsAntialias = true };
                canvas.DrawBitmap(original, Bounds.Left, Bounds.Top, paint);
            }
        }
        var alpha = new byte[checked(bounds.Width * bounds.Height)];
        var dst = (uint*)result.GetPixels();
        for (var i = 0; i < alpha.Length; i++) alpha[i] = (byte)dst[i];
        return this with { Bounds = bounds, Alpha = alpha };
    }

    private unsafe LayerMask BuildCoverage()
    {
        if (!Enabled || Density <= 0) return new LayerMask(SKRectI.Empty, [], 255);
        var sigma = Math.Clamp(Feather, 0, 1000);
        var padding = (int)MathF.Ceiling(sigma * 3);
        var bounds = Bounds;
        if (!bounds.IsEmpty) bounds.Inflate(padding, padding);
        byte Adjust(byte value)
        {
            if (Inverted) value = (byte)(255 - value);
            return (byte)Math.Clamp(MathF.Round(255 - (255 - value) * Math.Clamp(Density, 0, 1)), 0, 255);
        }
        var outside = Adjust(DefaultValue);
        if (bounds.IsEmpty) return new LayerMask(bounds, [], outside);
        using var original = new SKBitmap(new SKImageInfo(bounds.Width, bounds.Height, SKColorType.Bgra8888, SKAlphaType.Premul));
        original.Erase(SKColors.White.WithAlpha(DefaultValue));
        var pixels = (uint*)original.GetPixels();
        for (var y = 0; y < Bounds.Height; y++)
        for (var x = 0; x < Bounds.Width; x++)
        {
            var a = Alpha[y * Bounds.Width + x];
            pixels[(y + padding) * original.Width + x + padding] = (uint)(a * 0x01010101u);
        }
        using var blurred = new SKBitmap(original.Info);
        using (var canvas = new SKCanvas(blurred))
        using (var filter = sigma > 0 ? SKImageFilter.CreateBlur(sigma, sigma, SKShaderTileMode.Clamp) : null)
        using (var paint = new SKPaint { ImageFilter = filter, BlendMode = SKBlendMode.Src })
            canvas.DrawBitmap(original, 0, 0, paint);
        var output = new byte[checked(bounds.Width * bounds.Height)];
        var data = (uint*)blurred.GetPixels();
        for (var i = 0; i < output.Length; i++) output[i] = Adjust((byte)(data[i] >> 24));
        return new LayerMask(bounds, output, outside);
    }

    public LayerMask Scale(float sx, float sy)
    {
        var bounds = new SKRectI((int)MathF.Floor(Bounds.Left * sx), (int)MathF.Floor(Bounds.Top * sy),
            (int)MathF.Ceiling(Bounds.Right * sx), (int)MathF.Ceiling(Bounds.Bottom * sy));
        var alpha = new byte[checked(bounds.Width * bounds.Height)];
        for (var y = bounds.Top; y < bounds.Bottom; y++)
        for (var x = bounds.Left; x < bounds.Right; x++)
            alpha[(y - bounds.Top) * bounds.Width + x - bounds.Left] = RawAt((int)MathF.Floor(x / sx), (int)MathF.Floor(y / sy));
        return new LayerMask(bounds, alpha, DefaultValue) { Feather = Feather * MathF.Sqrt(sx * sy),
            Density = Density, Enabled = Enabled, Inverted = Inverted };
    }
}
