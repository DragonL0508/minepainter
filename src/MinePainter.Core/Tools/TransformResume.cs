using MinePainter.Core.Documents;
using MinePainter.Core.Layers;
using MinePainter.Core.Tiles;
using MinePainter.Core.Vectors;
using SkiaSharp;

namespace MinePainter.Core.Tools;

/// <summary>
/// 開變形時交給 <see cref="TransformSession.Resume"/> 的「續接資料」：各層的原始高清像素
/// ＋累積映射＋目前的框。內容由各圖層的 <see cref="LayerPixelSource"/> 組出來
/// （見 <see cref="EditorSession.BuildResumeFromLayers"/>），像素的擁有權一直在圖層那邊，
/// 本物件只是借過來用。</summary>
public sealed class TransformResume
{
    internal LayerNode Target { get; }
    internal (RasterLayer Layer, SKImage Pixels, SKRectI SrcBounds)[] Items { get; }
    internal SKMatrix PreMatrix { get; }
    internal SKRect TargetRect { get; }
    internal float RotationDeg { get; }
    internal SKSize OriginalSize { get; }

    internal TransformResume(LayerNode target,
        (RasterLayer Layer, SKImage Pixels, SKRectI SrcBounds)[] items,
        SKMatrix preMatrix, SKRect targetRect, float rotationDeg, SKSize originalSize)
    {
        Target = target;
        Items = items;
        PreMatrix = preMatrix;
        TargetRect = targetRect;
        RotationDeg = rotationDeg;
        OriginalSize = originalSize;
    }
}
