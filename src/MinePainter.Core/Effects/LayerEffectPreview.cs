using MinePainter.Core.History;
using MinePainter.Core.Layers;
using MinePainter.Core.Tools;
using SkiaSharp;

namespace MinePainter.Core.Effects;

/// <summary>
/// 效果對話框對「圖層效果堆疊」的即時預覽：參數一動就換掉堆疊裡的那一筆，
/// 合成器背景重算（不阻塞 UI）。確定 → 一步 history；取消 → 堆疊還原。
/// 新增時建構即先把條目放進堆疊，讓預設參數立刻看得到。
/// </summary>
public sealed class LayerEffectPreview : IEffectPreviewTarget, IDisposable
{
    private readonly EditorSession _session;
    private readonly RasterLayer _layer;
    private readonly IReadOnlyList<LayerEffect> _original;
    private readonly bool _isNew;
    private EffectSession? _source; // 直方圖／縮圖用（基底像素）
    private LayerEffect _entry;

    public LayerEffect Entry => _entry;
    public RasterLayer Layer => _layer;

    public LayerEffectPreview(EditorSession session, RasterLayer layer, LayerEffect entry, bool isNew)
    {
        _session = session;
        _layer = layer;
        _entry = entry;
        _isNew = isNew;
        lock (session.Document.SyncRoot)
        {
            _original = layer.Effects;
            if (isNew) layer.SetEffects(_original.Append(entry).ToList());
        }
    }

    public void Preview(IEffect effect, CancellationToken ct)
    {
        lock (_session.Document.SyncRoot)
        {
            var list = _layer.Effects.ToList();
            var index = list.FindIndex(e => e.Id == _entry.Id);
            if (index < 0) return;
            _entry = _entry with { Effect = effect };
            list[index] = _entry;
            _layer.SetEffects(list);
        }
    }

    public long[] Histogram() => Source.Histogram();

    public SKBitmap RenderThumbnail(int maxSize) => Source.RenderThumbnail(maxSize);

    private EffectSession Source => _source ??= new EffectSession(_session, _layer);

    /// <summary>確定：以「原清單 → 目前清單」記一步。</summary>
    public void Commit(IEffect finalEffect)
    {
        Preview(finalEffect, CancellationToken.None);
        var after = _layer.Effects;
        var label = _isNew ? $"效果：{_entry.Name}" : $"調整效果：{_entry.Name}";
        LayerEffectCommands.SetEffects(_session.Document, _session.History, _layer, _original, after, label);
    }

    public void Cancel()
    {
        lock (_session.Document.SyncRoot)
        {
            _layer.SetEffects(_original);
        }
    }

    public void Dispose() => _source?.Dispose();
}
