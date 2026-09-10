using MinePainter.Core.Compositing;
using MinePainter.Core.Layers;
using MinePainter.Core.Tools;
using SkiaSharp;

namespace MinePainter.App.Rendering;

/// <summary>
/// Photoshop 語意的合成路徑：遮色片、限制通道（brst）、直通群組，以及 Skia 沒有的混合模式。
///
/// 這些都需要「畫之前的背景」——遮色片是畫前／畫後兩份之間的內插、限制通道要把某幾個通道還原、
/// 自訂混合要拿背景跟來源逐像素算。上屏的 canvas 拿不到自己的快照，所以整個可見範圍先畫進一張
/// 離屏 surface，需要背景時 Snapshot 它，最後整張貼回畫布。
///
/// 2026-09-10 之前這條路每幀都重新配置離屏 surface、把遮色片從 CPU 重新上傳（一張羽化 1000 的
/// 群組遮色片是 52 MB，每幀傳一次），一幀 40 ms；使用者開 50 層的 PSD 只剩 10～20 fps。
/// 現在：surface 池重用、遮色片貼圖按實例快取、整個可見範圍都是預設值的遮色片不做事、
/// 快照統一在幀末釋放（SkiaSharp 對沒變的 surface 會回同一個 SKImage，內層先釋放會害到外層）。
/// </summary>
public sealed unsafe partial class GpuLayerRenderer
{
    private SKRuntimeEffect? _psdCompositeEffect;
    private SKRuntimeEffect? _psdBlendEffect;

    // 兩個輸入都含背景：premul 分量直接內插，再把 Photoshop 關掉的通道還原（alpha 不動）。
    private const string PsdCompositeShader = """
        uniform shader beforeImage;
        uniform shader afterImage;
        uniform shader maskImage;
        uniform float opacity;
        uniform float3 restricted;
        half4 main(float2 p) {
            half4 before = sample(beforeImage, p);
            half4 after = sample(afterImage, p);
            half4 result = mix(before, after, sample(maskImage, p).a * opacity);
            result.rgb = mix(result.rgb, before.rgb, half3(restricted));
            return result;
        }
        """;

    // Skia 沒有的混合模式（公式與 Core.Compositing.CustomBlend 同一份：W3C 分離式混合＋src-over）。
    // mode：0 LinearBurn、1 LinearLight、2 VividLight、3 PinLight、4 HardMix、5 DarkerColor、
    // 6 LighterColor、7 Subtract、8 Divide。用 float 而不是 int：這版 SkSL 的 uniform 不吃 int。
    private const string PsdBlendShader = """
        uniform shader backdrop;
        uniform shader source;
        uniform float mode;
        uniform float opacity;
        float vivid(float b, float s) {
            if (s < 0.5) {
                float d = 2.0 * s;
                if (b >= 1.0) return 1.0;
                if (d <= 0.0) return 0.0;
                return clamp(1.0 - (1.0 - b) / d, 0.0, 1.0);
            }
            float g = 2.0 * s - 1.0;
            if (b <= 0.0) return 0.0;
            if (g >= 1.0) return 1.0;
            return clamp(b / (1.0 - g), 0.0, 1.0);
        }
        float channel(float b, float s) {
            if (mode < 0.5) return clamp(b + s - 1.0, 0.0, 1.0);
            if (mode < 1.5) return clamp(b + 2.0 * s - 1.0, 0.0, 1.0);
            if (mode < 2.5) return vivid(b, s);
            if (mode < 3.5) return s < 0.5 ? min(b, 2.0 * s) : max(b, 2.0 * s - 1.0);
            if (mode < 4.5) return vivid(b, s) < 0.5 ? 0.0 : 1.0;
            if (mode < 7.5) return clamp(b - s, 0.0, 1.0);
            return s <= 0.0 ? 1.0 : clamp(b / s, 0.0, 1.0);
        }
        half4 main(float2 p) {
            half4 d = sample(backdrop, p);
            half4 s = sample(source, p) * half(opacity);
            float sa = float(s.a);
            float da = float(d.a);
            if (sa <= 0.0) return d;
            if (da <= 0.0) return s;
            float3 cs = float3(s.rgb) / sa;
            float3 cb = float3(d.rgb) / da;
            float3 m;
            if (mode > 4.5 && mode < 6.5) {
                float ls = dot(cs, float3(0.299, 0.587, 0.114));
                float lb = dot(cb, float3(0.299, 0.587, 0.114));
                bool takeSource = mode < 5.5 ? ls < lb : ls > lb;
                m = takeSource ? cs : cb;
            } else {
                m = float3(channel(cb.r, cs.r), channel(cb.g, cs.g), channel(cb.b, cs.b));
            }
            float3 co = (1.0 - da) * cs + da * m;
            float3 rgb = co * sa + float3(d.rgb) * (1.0 - sa);
            float a = sa + da * (1.0 - sa);
            return half4(half3(rgb), half(a));
        }
        """;

    /// <summary>這一幀的快照：統一幀末釋放（見類別註解）。</summary>
    private readonly List<SKImage> _frameSnapshots = new();

    /// <summary>可見範圍大小的離屏 surface 池（背景複本、隔離群組、遮色片覆蓋圖都是這個尺寸）。</summary>
    private readonly Stack<SKSurface> _scratchPool = new();
    private readonly List<SKSurface> _scratchInUse = new();
    private SKImageInfo _scratchInfo;
    private SKSurface? _psdSurface;

    /// <summary>一張遮色片的 GPU 貼圖（只含畫布內的那一段；畫布外永遠看不到）。</summary>
    private sealed class MaskTexture
    {
        public SKImage Image = null!;
        public SKRectI Bounds;
        public long Used;
    }

    /// <summary>key＝<see cref="LayerMask.Rendered"/> 的實例：遮色片是不可變 record，換了實例才要重建。</summary>
    private readonly Dictionary<LayerMask, MaskTexture> _maskTextures = new(ReferenceEqualityComparer.Instance);

    /// <summary>診斷／測試：上一幀上傳了幾張遮色片貼圖（熱快取應為零）。</summary>
    public int LastMaskUploads { get; private set; }

    /// <summary>診斷／測試：上一幀做了幾次背景快照（遮色片／限制通道／自訂混合各要一次）。</summary>
    public int LastBackdropSnapshots { get; private set; }

    /// <summary>這棵樹（只看看得見的節點）需不需要走這條路。</summary>
    private static bool NeedsPsdComposite(GroupLayer group)
    {
        foreach (var child in group.Children)
        {
            if (!child.IsVisible || child.Opacity <= 0) continue;
            if (child.RestrictedChannels != 0 || child.Mask != null || CustomBlend.IsCustom(child.BlendMode)) return true;
            if (child is GroupLayer { IsPassThrough: true }) return true;
            if (child is GroupLayer nested && NeedsPsdComposite(nested)) return true;
        }
        return false;
    }

    private bool TryDrawPsdComposite(SKCanvas canvas, EditorSession session, SKRectI visibleDoc)
    {
        _psdCompositeEffect ??= SKRuntimeEffect.Create(PsdCompositeShader, out _);
        _psdBlendEffect ??= SKRuntimeEffect.Create(PsdBlendShader, out _);
        if (_psdCompositeEffect == null || _psdBlendEffect == null) return false;
        LastMaskUploads = 0;
        LastBackdropSnapshots = 0;

        var matrix = canvas.TotalMatrix;
        var mapped = matrix.MapRect(new SKRect(visibleDoc.Left, visibleDoc.Top, visibleDoc.Right, visibleDoc.Bottom));
        var bounds = SKRectI.Intersect(canvas.DeviceClipBounds, new SKRectI(
            (int)Math.Floor(mapped.Left), (int)Math.Floor(mapped.Top),
            (int)Math.Ceiling(mapped.Right), (int)Math.Ceiling(mapped.Bottom)));
        if (bounds.IsEmpty) return true;
        var info = new SKImageInfo(bounds.Width, bounds.Height, SKColorType.Bgra8888, SKAlphaType.Premul);
        try
        {
            var surface = AcquirePsdSurface(info);
            if (surface == null) return false;
            var target = surface.Canvas;
            target.Translate(-bounds.Left, -bounds.Top);
            target.Concat(ref matrix);
            target.ClipRect(new SKRect(visibleDoc.Left, visibleDoc.Top, visibleDoc.Right, visibleDoc.Bottom));
            if (!DrawPsdChildren(surface, session, session.Document.Root, visibleDoc)) return false;
            var result = Snap(surface);
            canvas.Save();
            canvas.ResetMatrix();
            canvas.DrawImage(result, bounds.Left, bounds.Top);
            canvas.Restore();
            SweepMaskTextures();
            return true;
        }
        finally
        {
            EndPsdFrame();
        }
    }

    // ---- surface 池與快照 ----

    private SKSurface? CreatePsdSurface(SKImageInfo info) =>
        _gpuContext != null ? SKSurface.Create(_gpuContext, true, info) : SKSurface.Create(info);

    private SKSurface? AcquirePsdSurface(SKImageInfo info)
    {
        if (_scratchInfo != info) ResetScratchPool(info);
        if (_psdSurface == null)
        {
            _psdSurface = CreatePsdSurface(info);
            if (_psdSurface == null) return null;
        }
        PrepareSurface(_psdSurface);
        return _psdSurface;
    }

    private SKSurface? AcquireScratch()
    {
        SKSurface? surface;
        if (_scratchPool.Count > 0) surface = _scratchPool.Pop();
        else
        {
            surface = CreatePsdSurface(_scratchInfo);
            if (surface == null) return null;
        }
        PrepareSurface(surface);
        _scratchInUse.Add(surface);
        return surface;
    }

    /// <summary>還回池子（這一幀之內就可以再借出去；它的快照另外活到幀末）。</summary>
    private void ReleaseScratch(SKSurface surface)
    {
        if (_scratchInUse.Remove(surface)) _scratchPool.Push(surface);
    }

    private static void PrepareSurface(SKSurface surface)
    {
        var c = surface.Canvas;
        c.RestoreToCount(1); // 上一次用它的人留下的裁切與矩陣
        c.Save();
        c.ResetMatrix();
        c.Clear(SKColors.Transparent);
    }

    private void ResetScratchPool(SKImageInfo info)
    {
        foreach (var s in _scratchPool) s.Dispose();
        _scratchPool.Clear();
        foreach (var s in _scratchInUse) s.Dispose();
        _scratchInUse.Clear();
        _psdSurface?.Dispose();
        _psdSurface = null;
        _scratchInfo = info;
    }

    private void DisposePsdResources()
    {
        ResetScratchPool(default);
        foreach (var image in _frameSnapshots) image.Dispose();
        _frameSnapshots.Clear();
        foreach (var texture in _maskTextures.Values) texture.Image.Dispose();
        _maskTextures.Clear();
    }

    private void EndPsdFrame()
    {
        foreach (var image in _frameSnapshots) image.Dispose();
        _frameSnapshots.Clear();
        foreach (var s in _scratchInUse) _scratchPool.Push(s);
        _scratchInUse.Clear();
    }

    /// <summary>快照這張 surface；影像在幀末才釋放。</summary>
    private SKImage Snap(SKSurface surface)
    {
        surface.Canvas.Flush();
        var image = surface.Snapshot();
        _frameSnapshots.Add(image);
        return image;
    }

    private SKImage SnapBackdrop(SKSurface surface)
    {
        LastBackdropSnapshots++;
        return Snap(surface);
    }

    // ---- 遮色片 ----

    private enum MaskCoverage { Full, Empty, Partial }

    /// <summary>遮色片在可見範圍上的樣子：整片預設值就不必逐像素做（絕大多數格子都是這種）。</summary>
    private static MaskCoverage ClassifyMask(LayerMask? mask, SKRectI visibleDoc)
    {
        if (mask == null) return MaskCoverage.Full;
        var rendered = mask.Rendered;
        if (rendered.Bounds.IntersectsWith(visibleDoc)) return MaskCoverage.Partial;
        return rendered.DefaultValue switch
        {
            255 => MaskCoverage.Full,
            0 => MaskCoverage.Empty,
            _ => MaskCoverage.Partial,
        };
    }

    /// <summary>遮色片的貼圖（Alpha8；只含畫布內那段）。上傳一次，之後每幀直接貼。</summary>
    private MaskTexture? MaskTextureFor(LayerMask rendered)
    {
        if (_maskTextures.TryGetValue(rendered, out var cached))
        {
            cached.Used = _frame;
            return cached;
        }
        var bounds = SKRectI.Intersect(rendered.Bounds, _docBounds);
        if (bounds.IsEmpty) return null;
        var info = new SKImageInfo(bounds.Width, bounds.Height, SKColorType.Alpha8, SKAlphaType.Premul);
        SKImage? image;
        if (bounds == rendered.Bounds)
        {
            fixed (byte* ptr = rendered.Alpha) image = SKImage.FromPixelCopy(info, (IntPtr)ptr, bounds.Width);
        }
        else
        {
            var cropped = new byte[bounds.Width * bounds.Height];
            for (var y = 0; y < bounds.Height; y++)
            {
                var srcRow = (y + bounds.Top - rendered.Bounds.Top) * rendered.Bounds.Width + (bounds.Left - rendered.Bounds.Left);
                Array.Copy(rendered.Alpha, srcRow, cropped, y * bounds.Width, bounds.Width);
            }
            fixed (byte* ptr = cropped) image = SKImage.FromPixelCopy(info, (IntPtr)ptr, bounds.Width);
        }
        if (image == null) return null;
        if (_gpuContext != null)
        {
            // 現在就送上 GPU：不然每幀第一次貼時 Skia 才上傳，遮色片一大就是每幀一次的停頓
            var texture = image.ToTextureImage(_gpuContext);
            if (texture != null)
            {
                image.Dispose();
                image = texture;
            }
        }
        LastMaskUploads++;
        var entry = new MaskTexture { Image = image, Bounds = bounds, Used = _frame };
        _maskTextures[rendered] = entry;
        return entry;
    }

    /// <summary>兩秒沒被畫到的遮色片貼圖就放掉（換了遮色片實例、圖層刪掉、切分頁）。</summary>
    private void SweepMaskTextures()
    {
        if (_maskTextures.Count == 0) return;
        List<LayerMask>? dead = null;
        foreach (var (key, texture) in _maskTextures)
        {
            if (texture.Used >= _frame - 120) continue;
            (dead ??= new()).Add(key);
        }
        if (dead == null) return;
        foreach (var key in dead)
        {
            _maskTextures[key].Image.Dispose();
            _maskTextures.Remove(key);
        }
    }

    // ---- 圖層樹 ----

    private bool DrawPsdChildren(SKSurface target, EditorSession session, GroupLayer group, SKRectI visibleDoc)
    {
        foreach (var child in group.Children)
        {
            if (!child.IsVisible || child.Opacity <= 0) continue;
            // 群組的遮色片包住整組的操作，在 DrawPsdGroup 裡處理
            var childMask = child is GroupLayer ? null : child.Mask;
            var coverage = ClassifyMask(childMask, visibleDoc);
            if (coverage == MaskCoverage.Empty) continue; // 可見範圍整片被遮掉：畫了等於沒畫

            var custom = child is not GroupLayer && CustomBlend.IsCustom(child.BlendMode);
            var restore = child.RestrictedChannels != 0 || coverage == MaskCoverage.Partial;
            var before = restore || custom ? SnapBackdrop(target) : null;
            switch (child)
            {
                case RasterLayer raster when custom:
                {
                    var content = AcquireScratch();
                    if (content == null) return false;
                    content.Canvas.SetMatrix(target.Canvas.TotalMatrix);
                    DrawRaster(content.Canvas, session, raster, visibleDoc, plain: true);
                    if (!BlendCustom(target, before!, Snap(content), raster.Opacity, raster.BlendMode)) return false;
                    ReleaseScratch(content);
                    break;
                }
                case RasterLayer raster:
                    DrawRaster(target.Canvas, session, raster, visibleDoc);
                    break;
                case AdjustmentLayer adjustment:
                {
                    var source = Snap(target);
                    using var paint = new SKPaint
                    {
                        ColorFilter = AdjustmentFilter(adjustment),
                        Color = SKColors.White.WithAlpha((byte)(adjustment.Opacity * 255)),
                        BlendMode = adjustment.Opacity >= 1 ? SKBlendMode.Src : SKBlendMode.SrcOver,
                    };
                    DrawPsdDeviceImage(target.Canvas, source, paint);
                    break;
                }
                case GroupLayer nested:
                    if (!DrawPsdGroup(target, session, nested, visibleDoc)) return false;
                    break;
            }
            if (restore && !ApplyPsdMask(target, before!, coverage == MaskCoverage.Partial ? childMask : null, 1, child.RestrictedChannels))
                return false;
        }
        return true;
    }

    private bool DrawPsdGroup(SKSurface target, EditorSession session, GroupLayer group, SKRectI visibleDoc)
    {
        var coverage = ClassifyMask(group.Mask, visibleDoc);
        if (coverage == MaskCoverage.Empty) return true;
        var mask = coverage == MaskCoverage.Partial ? group.Mask : null;
        var pass = group.IsPassThrough && group.BlendMode == BlendMode.Normal && !group.HasActiveEffects;
        if (pass)
        {
            // 直通：子層直接畫在背景上；遮色片與群組不透明度作用在「畫前／畫後」的差異上
            var restore = mask != null || group.Opacity < 1;
            var before = restore ? SnapBackdrop(target) : null;
            if (!DrawPsdChildren(target, session, group, visibleDoc)) return false;
            return !restore || ApplyPsdMask(target, before!, mask, group.Opacity, 0);
        }

        // 隔離：整組先畫到透明底，再以群組的不透明度／混合模式疊上
        var custom = CustomBlend.IsCustom(group.BlendMode);
        var backdrop = mask != null || custom ? SnapBackdrop(target) : null;
        var isolated = AcquireScratch();
        if (isolated == null) return false;
        isolated.Canvas.SetMatrix(target.Canvas.TotalMatrix);
        isolated.Canvas.ClipRect(new SKRect(visibleDoc.Left, visibleDoc.Top, visibleDoc.Right, visibleDoc.Bottom));
        if (group.EffectsRendered)
        {
            // 整組套過效果的那份已經算好了就直接畫它（外框／陰影包住整組，而不是每個子層各一份）
            DrawSurface(isolated.Canvas, GroupImages(group), group.FxCache.Surface, SKPointI.Empty,
                visibleDoc, 1f, BlendMode.Normal);
        }
        else if (!DrawPsdChildren(isolated, session, group, visibleDoc)) return false;

        var content = Snap(isolated);
        if (custom)
        {
            if (!BlendCustom(target, backdrop!, content, group.Opacity, group.BlendMode)) return false;
        }
        else
        {
            using var paint = LayerPaint(group, null);
            DrawPsdDeviceImage(target.Canvas, content, paint);
        }
        ReleaseScratch(isolated);
        return mask == null || ApplyPsdMask(target, backdrop!, mask, 1, 0);
    }

    /// <summary>
    /// 把「畫之前」與「畫之後」依遮色片覆蓋值內插回 target，並還原限制的通道。
    /// <paramref name="sourceMask"/> 為 null＝整片 255（只還原通道）。
    /// </summary>
    private bool ApplyPsdMask(SKSurface target, SKImage before, LayerMask? sourceMask, float opacity, int channels)
    {
        SKImage? coverage = null;
        if (sourceMask != null)
        {
            var rendered = sourceMask.Rendered;
            var maskSurface = AcquireScratch();
            if (maskSurface == null) return false;
            var c = maskSurface.Canvas;
            c.Clear(SKColors.White.WithAlpha(rendered.DefaultValue));
            if (MaskTextureFor(rendered) is { } texture)
            {
                c.SetMatrix(target.Canvas.TotalMatrix);
                // 貼圖範圍內的值整個取代預設值（有洞的地方也要換掉外圍的預設）
                using var clear = new SKPaint { BlendMode = SKBlendMode.Clear };
                c.DrawRect(new SKRect(texture.Bounds.Left, texture.Bounds.Top, texture.Bounds.Right, texture.Bounds.Bottom), clear);
                var matrix = target.Canvas.TotalMatrix;
                var minifying = Math.Abs(matrix.ScaleX * matrix.ScaleY - matrix.SkewX * matrix.SkewY) < 1;
                using var paint = new SKPaint
                {
                    Color = SKColors.White,
                    // 縮小檢視時與 tile 的 LOD 一樣先濾波；完成的覆蓋圖之後是 1:1 取樣
                    FilterQuality = minifying ? SKFilterQuality.Medium : SKFilterQuality.None,
                };
                c.DrawImage(texture.Image, texture.Bounds.Left, texture.Bounds.Top, paint);
            }
            coverage = Snap(maskSurface);
            ReleaseScratch(maskSurface);
        }
        return MergePsdResult(target, before, coverage, opacity, channels);
    }

    private bool MergePsdResult(SKSurface target, SKImage before, SKImage? mask, float opacity, int channels)
    {
        // 內建的 Skia runtime shader 在 raster surface 上會直接中止（連常數色都會），
        // 硬體 surface 走 shader、軟體渲染用同一套 premul 算術逐像素算。
        if (_gpuContext == null) return MergePsdSoftware(target, before, mask, opacity, channels);
        var after = Snap(target);
        using var beforeShader = before.ToShader();
        using var afterShader = after.ToShader();
        using var maskShader = mask?.ToShader() ?? SKShader.CreateColor(SKColors.White);
        var children = new SKRuntimeEffectChildren(_psdCompositeEffect!)
        {
            ["beforeImage"] = beforeShader, ["afterImage"] = afterShader, ["maskImage"] = maskShader,
        };
        var uniforms = new SKRuntimeEffectUniforms(_psdCompositeEffect!)
        {
            ["opacity"] = opacity,
            ["restricted"] = new float[] { (channels & 1) != 0 ? 1 : 0, (channels & 2) != 0 ? 1 : 0, (channels & 4) != 0 ? 1 : 0 },
        };
        using var shader = _psdCompositeEffect!.ToShader(false, uniforms, children);
        if (shader == null) return false;
        using var paint = new SKPaint { Shader = shader, BlendMode = SKBlendMode.Src };
        FillDevice(target, after.Width, after.Height, paint);
        return true;
    }

    /// <summary>自訂混合模式：來源（已隔離、不透明度 1）以 <paramref name="mode"/> 疊到背景上，整張寫回 target。</summary>
    private bool BlendCustom(SKSurface target, SKImage backdrop, SKImage source, float opacity, BlendMode mode)
    {
        if (_gpuContext == null) return BlendCustomSoftware(target, backdrop, source, opacity, mode);
        using var backdropShader = backdrop.ToShader();
        using var sourceShader = source.ToShader();
        var children = new SKRuntimeEffectChildren(_psdBlendEffect!)
        {
            ["backdrop"] = backdropShader, ["source"] = sourceShader,
        };
        var uniforms = new SKRuntimeEffectUniforms(_psdBlendEffect!)
        {
            ["mode"] = (float)CustomBlendIndex(mode),
            ["opacity"] = Math.Clamp(opacity, 0f, 1f),
        };
        using var shader = _psdBlendEffect!.ToShader(false, uniforms, children);
        if (shader == null) return false;
        using var paint = new SKPaint { Shader = shader, BlendMode = SKBlendMode.Src };
        FillDevice(target, backdrop.Width, backdrop.Height, paint);
        return true;
    }

    /// <summary>shader 裡的模式編號（順序寫在 <see cref="PsdBlendShader"/> 的註解）。</summary>
    internal static int CustomBlendIndex(BlendMode mode) => mode switch
    {
        BlendMode.LinearBurn => 0,
        BlendMode.LinearLight => 1,
        BlendMode.VividLight => 2,
        BlendMode.PinLight => 3,
        BlendMode.HardMix => 4,
        BlendMode.DarkerColor => 5,
        BlendMode.LighterColor => 6,
        BlendMode.Subtract => 7,
        BlendMode.Divide => 8,
        _ => throw new ArgumentOutOfRangeException(nameof(mode), mode, "不是自訂混合模式"),
    };

    private static void FillDevice(SKSurface target, int width, int height, SKPaint paint)
    {
        target.Canvas.Save();
        target.Canvas.ResetMatrix();
        target.Canvas.DrawRect(0, 0, width, height, paint);
        target.Canvas.Restore();
    }

    private static bool MergePsdSoftware(SKSurface target, SKImage before, SKImage? mask, float opacity, int channels)
    {
        using var original = SKBitmap.FromImage(before);
        using var after = new SKBitmap(original.Info);
        if (!target.ReadPixels(after.Info, after.GetPixels(), after.RowBytes, 0, 0)) return false;
        using var coverage = mask != null ? SKBitmap.FromImage(mask) : null;
        var a = (byte*)original.GetPixels();
        var b = (byte*)after.GetPixels();
        var m = coverage == null ? null : (byte*)coverage.GetPixels();
        for (var y = 0; y < after.Height; y++)
        for (var x = 0; x < after.Width; x++)
        {
            var amount = (int)MathF.Round((m == null ? 255 : m[y * coverage!.RowBytes + x * 4 + 3]) * opacity);
            for (var c = 0; c < 4; c++)
            {
                var i = y * after.RowBytes + x * 4 + c;
                b[i] = c < 3 && (channels & (1 << (2 - c))) != 0 ? a[i]
                    : (byte)((a[i] * (255 - amount) + b[i] * amount + 127) / 255);
            }
        }
        using var paint = new SKPaint { BlendMode = SKBlendMode.Src };
        target.Canvas.Save();
        target.Canvas.ResetMatrix();
        target.Canvas.DrawBitmap(after, 0, 0, paint);
        target.Canvas.Restore();
        return true;
    }

    private static bool BlendCustomSoftware(SKSurface target, SKImage backdrop, SKImage source, float opacity, BlendMode mode)
    {
        using var dst = SKBitmap.FromImage(backdrop);
        using var src = SKBitmap.FromImage(source);
        if (dst.Width != src.Width || dst.Height != src.Height) return false;
        var d = (uint*)dst.GetPixels();
        var s = (uint*)src.GetPixels();
        var alphaScale = (byte)MathF.Round(Math.Clamp(opacity, 0f, 1f) * 255);
        var count = dst.Width * dst.Height;
        for (var i = 0; i < count; i++)
        {
            var pixel = s[i];
            if (pixel == 0) continue;
            if (alphaScale < 255) pixel = LayerPixelSource.ScalePremul(pixel, alphaScale);
            d[i] = CustomBlend.Blend(pixel, d[i], mode);
        }
        using var paint = new SKPaint { BlendMode = SKBlendMode.Src };
        target.Canvas.Save();
        target.Canvas.ResetMatrix();
        target.Canvas.DrawBitmap(dst, 0, 0, paint);
        target.Canvas.Restore();
        return true;
    }

    private static void DrawPsdDeviceImage(SKCanvas canvas, SKImage image, SKPaint paint)
    {
        canvas.Save();
        canvas.ResetMatrix();
        canvas.DrawImage(image, 0, 0, paint);
        canvas.Restore();
    }
}
