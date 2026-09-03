using MinePainter.App.Gadgets;
using MinePainter.Core.Documents;
using MinePainter.Core.Layers;
using SkiaSharp;
using Xunit;

namespace MinePainter.App.Tests;

public class YouTubeMockupTests
{
    private static Document MakeDoc(int w = 1920, int h = 1080)
    {
        var doc = new Document(w, h);
        var layer = new RasterLayer { Name = "圖" };
        layer.Surface.Fill(new SKRectI(0, 0, w, h), new SKColor(200, 60, 40));
        lock (doc.SyncRoot)
        {
            doc.Root.Add(layer);
            doc.ActiveLayer = layer;
        }
        return doc;
    }

    [Fact]
    public void 產生的網頁內嵌縮圖且不留佔位符()
    {
        using var doc = MakeDoc();
        var path = YouTubeMockup.Render(doc, new YouTubeMockupOptions
        {
            Title = "測試標題",
            Channel = "測試頻道",
            Views = 12345,
            Uploaded = "3 小時前",
            Duration = "10:32",
            Dark = false,
        });

        var html = File.ReadAllText(path);
        Assert.Contains("data:image/png;base64,", html);
        Assert.Contains("測試標題", html);
        Assert.Contains("測試頻道", html);
        Assert.Contains("data-theme=\"light\"", html);
        Assert.DoesNotContain("__", html); // 佔位符全部被換掉了
        File.Delete(path);
    }

    [Fact]
    public void 標題裡的引號與反斜線不會破壞腳本()
    {
        using var doc = MakeDoc(64, 64);
        var path = YouTubeMockup.Render(doc, new YouTubeMockupOptions
        {
            Title = "他說 \"C:\\Users\" <b>粗體</b> & 符號",
            Channel = "頻道",
            Duration = "0:30",
        });

        var html = File.ReadAllText(path);
        // JS 字面裡不能出現生的 " 或 \，否則整頁腳本壞掉、版面空白
        var script = html[html.IndexOf("const MINE = {", StringComparison.Ordinal)..];
        var mineBlock = script[..script.IndexOf("};", StringComparison.Ordinal)];
        Assert.DoesNotContain("\\", mineBlock);
        Assert.Contains("&quot;", mineBlock);
        Assert.Contains("&#92;", mineBlock);
        Assert.Contains("&lt;b&gt;", mineBlock);  // 標籤被轉義，不會真的插進版面
        Assert.DoesNotContain("<b>", mineBlock);
        File.Delete(path);
    }

    [Theory]
    [InlineData(0, "0 次觀看")]
    [InlineData(9999, "9,999 次觀看")]
    [InlineData(12345, "1.2萬次觀看")]
    [InlineData(1_200_000, "120萬次觀看")]
    [InlineData(345_000_000, "3.4億次觀看")] // 無條件捨去，跟 YouTube 一樣
    public void 觀看數照繁中習慣格式化(long views, string expected)
    {
        Assert.Equal(expected, YouTubeMockup.FormatViews(views));
    }

    [Fact]
    public void 超大文件縮到長邊上限以內()
    {
        using var doc = MakeDoc(3000, 1688);
        var path = YouTubeMockup.Render(doc, new YouTubeMockupOptions { Title = "大圖", Channel = "頻道" });

        var html = File.ReadAllText(path);
        var start = html.IndexOf("data:image/png;base64,", StringComparison.Ordinal)
                    + "data:image/png;base64,".Length;
        var end = html.IndexOf('"', start);
        using var image = SKBitmap.Decode(Convert.FromBase64String(html[start..end]));
        Assert.Equal(1280, image.Width);
        File.Delete(path);
    }
}
