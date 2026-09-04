using MinePainter.Core.Documents;
using MinePainter.Core.IO;
using MinePainter.Core.Layers;
using MinePainter.Core.Vectors;
using SkiaSharp;
using Xunit;

namespace MinePainter.Core.Tests;

/// <summary>
/// 缺字型偵測：專案檔只記字型家族名，換一台機器沒裝那支，Skia 會安靜換一支畫出來 ——
/// 開檔時要抓得出來才提示得了。
/// </summary>
public class FontAvailabilityTests
{
    private const string Missing = "MinePainter No Such Font 12345";

    private static (Document Doc, RasterLayer Layer) NewDoc()
    {
        var doc = ImageCodec.CreateBlankDocument(256, 256, SKColors.White);
        var layer = new RasterLayer { Name = "文字" };
        lock (doc.SyncRoot) doc.Root.Add(layer);
        return (doc, layer);
    }

    private static void AddText(RasterLayer layer, string family, string text)
    {
        lock (layer.Document!.SyncRoot)
            layer.AddElement(new TextElement { Text = text, FontFamily = family, FontSize = 32 });
    }

    [Fact]
    public void IsAvailable_TrueForInstalled_FalseForUnknown()
    {
        Assert.False(FontAvailability.IsAvailable(Missing));
        Assert.True(FontAvailability.IsAvailable("")); // 空的＝用預設，不算缺
        var installed = SKFontManager.Default.FontFamilies.FirstOrDefault();
        if (installed != null) Assert.True(FontAvailability.IsAvailable(installed));
    }

    [Fact]
    public void MissingIn_ReportsFamilyCountAndSample()
    {
        var (doc, layer) = NewDoc();
        AddText(layer, Missing, "第一段文字\n第二行");
        AddText(layer, Missing, "另一段");
        var installed = SKFontManager.Default.FontFamilies.FirstOrDefault();
        if (installed != null) AddText(layer, installed, "這支有裝");

        var missing = FontAvailability.MissingIn(doc);
        var entry = Assert.Single(missing);
        Assert.Equal(Missing, entry.Family);
        Assert.Equal(2, entry.TextCount);          // 用到幾段文字
        Assert.Equal("第一段文字", entry.Sample);   // 只取第一行當線索
        doc.Dispose();
    }

    [Fact]
    public void MissingIn_EmptyWhenEverythingIsInstalled()
    {
        var (doc, layer) = NewDoc();
        var installed = SKFontManager.Default.FontFamilies.FirstOrDefault();
        if (installed != null) AddText(layer, installed, "有裝");
        Assert.Empty(FontAvailability.MissingIn(doc));
        doc.Dispose();
    }

    [Fact]
    public void ReplaceFontFamilies_SwapsEveryUse_InOneUndoStep()
    {
        var (doc, layer) = NewDoc();
        AddText(layer, Missing, "第一段");
        AddText(layer, Missing, "第二段");
        var keep = SKFontManager.Default.FontFamilies.FirstOrDefault() ?? "Arial";
        AddText(layer, keep, "不該被動到");

        using var session = new MinePainter.Core.Tools.EditorSession(doc);
        var before = session.History.UndoStack.Count;
        var replaced = MinePainter.Core.History.VectorCommands.ReplaceFontFamilies(
            doc, session.History, new Dictionary<string, string> { [Missing] = keep }, "替換缺少的字型");

        Assert.Equal(2, replaced);
        Assert.All(layer.Elements, e => Assert.Equal(keep, ((TextElement)e).FontFamily));
        Assert.Equal(before + 1, session.History.UndoStack.Count); // 一步 undo

        Assert.True(session.Undo());
        Assert.Equal(2, layer.Elements.Count(e => ((TextElement)e).FontFamily == Missing));
    }

    [Fact]
    public void ReplaceFontFamilies_NoOpWhenNothingMatches()
    {
        var (doc, layer) = NewDoc();
        AddText(layer, Missing, "文字");
        using var session = new MinePainter.Core.Tools.EditorSession(doc);
        var before = session.History.UndoStack.Count;

        var replaced = MinePainter.Core.History.VectorCommands.ReplaceFontFamilies(
            doc, session.History, new Dictionary<string, string> { ["不存在的來源"] = "Arial" }, "替換");

        Assert.Equal(0, replaced);
        Assert.Equal(before, session.History.UndoStack.Count); // 沒動就不記一步
    }

    [Fact]
    public void MissingIn_SurvivesMppRoundTrip()
    {
        var (doc, layer) = NewDoc();
        AddText(layer, Missing, "存進檔案的文字");
        var path = Path.Combine(Path.GetTempPath(), $"mp-missing-font-{Guid.NewGuid():N}.mpp");
        try
        {
            MppFormat.Save(doc, path);
            using var loaded = MppFormat.Load(path);
            var entry = Assert.Single(FontAvailability.MissingIn(loaded));
            Assert.Equal(Missing, entry.Family); // 家族名有存進檔案，開檔時才問得出來
        }
        finally
        {
            File.Delete(path);
            doc.Dispose();
        }
    }
}
