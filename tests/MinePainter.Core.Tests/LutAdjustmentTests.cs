using MinePainter.Core.Adjustments;
using MinePainter.Core.Compositing;
using MinePainter.Core.Effects;
using MinePainter.Core.IO;
using MinePainter.Core.Layers;
using MinePainter.Core.Tiles;
using SkiaSharp;
using Xunit;
using static MinePainter.Core.Effects.EffectMath;

namespace MinePainter.Core.Tests;

/// <summary>LUT 調色：.cube 解析、三線性查表、逐像素路徑、存檔往返、合成器結果。</summary>
public class LutAdjustmentTests : IDisposable
{
    private readonly string _tempPath = Path.Combine(Path.GetTempPath(), $"mp-lut-{Guid.NewGuid():N}.mpp");
    public void Dispose() { if (File.Exists(_tempPath)) File.Delete(_tempPath); }

    /// <summary>2³ 的「紅藍對調」表，格點之間線性。</summary>
    private const string SwapCube = """
        # 測試用
        TITLE "紅藍對調"
        LUT_3D_SIZE 2
        DOMAIN_MIN 0 0 0
        DOMAIN_MAX 1 1 1
        0 0 0
        0 0 1
        0 1 0
        0 1 1
        1 0 0
        1 0 1
        1 1 0
        1 1 1
        """;

    [Fact]
    public void 單位表_套了等於沒套()
    {
        var lut = Lut3D.Identity(5);
        foreach (var (r, g, b) in new[] { (0, 0, 0), (255, 255, 255), (17, 200, 99), (128, 64, 250) })
        {
            lut.Lookup(r, g, b, out var nr, out var ng, out var nb);
            Assert.Equal((r, g, b), (nr, ng, nb));
        }
    }

    [Fact]
    public void 解析cube_紅藍對調_三線性內插()
    {
        var lut = Lut3D.ParseCube(SwapCube, "fallback");
        Assert.Equal("紅藍對調", lut.Name);
        Assert.Equal(2, lut.Size);
        lut.Lookup(255, 0, 0, out var r, out var g, out var b);
        Assert.Equal((0, 0, 255), (r, g, b));
        lut.Lookup(60, 100, 200, out r, out g, out b);
        Assert.Equal((200, 100, 60), (r, g, b)); // 格點之間也要線性對得上
    }

    [Fact]
    public void 解析cube_一維表展成三維()
    {
        const string oneD = "LUT_1D_SIZE 3\n0 0 0\n0.25 0.5 0.75\n1 1 1\n";
        var lut = Lut3D.ParseCube(oneD, "1d");
        lut.Lookup(128, 128, 128, out var r, out var g, out var b);
        Assert.InRange(r, 60, 68);   // 0.25 附近
        Assert.InRange(g, 124, 132); // 0.5
        Assert.InRange(b, 187, 195); // 0.75
    }

    [Fact]
    public void 解析cube_格式錯誤要丟例外()
    {
        Assert.Throws<InvalidDataException>(() => Lut3D.ParseCube("不是 cube", "x"));
        Assert.Throws<InvalidDataException>(() => Lut3D.ParseCube("LUT_3D_SIZE 2\n0 0 0\n", "x")); // 筆數不夠
    }

    [Fact]
    public void 序列化往返()
    {
        var lut = LutPresets.All[0].Lut;
        var back = Lut3D.Deserialize(lut.Serialize());
        Assert.Equal(lut.Size, back.Size);
        Assert.Equal(lut.Name, back.Name);
        for (var i = 0; i < lut.Data.Length; i += 97)
            Assert.InRange(back.Data[i], lut.Data[i] - 0.0001f, lut.Data[i] + 0.0001f);
    }

    [Fact]
    public void 逐像素_保留透明度_強度0不動()
    {
        var adj = new LutAdjustment().WithCube(SwapCube);
        var px = new[] { Premul(20, 40, 200, 255), Premul(20, 40, 200, 128), 0u };
        adj.ApplyPixels(px, px.Length);
        Unpremul(px[0], out var b, out var g, out var r, out var a);
        Assert.Equal((200, 40, 20, 255), (b, g, r, a)); // B 與 R 對調
        Unpremul(px[1], out b, out g, out r, out a);
        Assert.Equal(128, a);
        Assert.InRange(b, 197, 203);
        Assert.Equal(0u, px[2]);

        var none = adj with { Amount = 0 };
        var px2 = new[] { Premul(20, 40, 200, 255) };
        none.ApplyPixels(px2, 1);
        Assert.Equal(Premul(20, 40, 200, 255), px2[0]);

        var half = adj with { Amount = 50 };
        var px3 = new[] { Premul(0, 0, 200, 255) };
        half.ApplyPixels(px3, 1);
        Unpremul(px3[0], out b, out _, out r, out _);
        Assert.InRange(b, 95, 105);
        Assert.InRange(r, 95, 105);
    }

    [Fact]
    public void 每個預設集都算得出來且不是單位表()
    {
        foreach (var preset in LutPresets.All)
        {
            var lut = preset.Lut;
            var changed = 0;
            for (var v = 0; v < 256; v += 15)
            {
                lut.Lookup(v, 255 - v, (v * 3) & 0xFF, out var r, out var g, out var b);
                if (r != v || g != 255 - v || b != ((v * 3) & 0xFF)) changed++;
            }
            Assert.True(changed > 5, $"預設集「{preset.Name}」幾乎沒改到顏色");
        }
    }

    [Fact]
    public void 破壞性套用_走像素路徑_與逐像素結果一致()
    {
        var adj = new LutAdjustment { Preset = 0, Lut = LutPresets.All[0].Lut };
        var src = new uint[16 * 16];
        for (var i = 0; i < src.Length; i++) src[i] = Premul(i * 7 & 0xFF, i * 3 & 0xFF, i * 11 & 0xFF, 255);
        var ctx = EffectContext.FromPixels(src, 16, 16);
        new AdjustmentEffect(adj).Render(ctx);

        var expected = (uint[])src.Clone();
        adj.ApplyPixels(expected, expected.Length);
        Assert.Equal(expected, ctx.Dst);
    }

    [Fact]
    public void 效果堆疊序列化_自訂表帶資料_預設集不帶()
    {
        var custom = new AdjustmentEffect(new LutAdjustment().WithCube(SwapCube) with { Amount = 70 });
        var saved = EffectSerializer.Save(custom);
        Assert.True(saved.ContainsKey("data"));
        var loaded = Assert.IsType<AdjustmentEffect>(EffectSerializer.Load(EffectSerializer.TypeIdOf(custom), saved));
        var lut = Assert.IsType<LutAdjustment>(loaded.Adjustment);
        Assert.Equal(LutAdjustment.CustomPreset, lut.Preset);
        Assert.Equal(70, lut.Amount);
        Assert.Equal("紅藍對調", lut.LutName);
        lut.Lut.Lookup(255, 0, 0, out var r, out _, out var b);
        Assert.Equal((0, 255), (r, b));

        var preset = new AdjustmentEffect(new LutAdjustment { Preset = 3, Lut = LutPresets.All[3].Lut });
        var savedPreset = EffectSerializer.Save(preset);
        Assert.False(savedPreset.ContainsKey("data"));
        var loadedPreset = (LutAdjustment)((AdjustmentEffect)EffectSerializer.Load("adjust:lut", savedPreset)).Adjustment;
        Assert.Equal(3, loadedPreset.Preset);
        Assert.Equal(LutPresets.All[3].Name, loadedPreset.LutName);
    }

    [Fact]
    public void mpp往返_自訂LUT調整圖層()
    {
        using var doc = ImageCodec.CreateBlankDocument(64, 64, new SKColor(255, 0, 0));
        var adj = new AdjustmentLayer(new LutAdjustment().WithCube(SwapCube) with { Amount = 80 }) { Opacity = 0.9f };
        var presetAdj = new AdjustmentLayer(new LutAdjustment { Preset = 1, Lut = LutPresets.All[1].Lut });
        lock (doc.SyncRoot)
        {
            doc.Root.Add(adj);
            doc.Root.Add(presetAdj);
        }
        MppFormat.Save(doc, _tempPath);
        using var loaded = MppFormat.Load(_tempPath);

        var a = Assert.IsType<LutAdjustment>(Assert.IsType<AdjustmentLayer>(loaded.Root.Children[1]).Adjustment);
        Assert.Equal(LutAdjustment.CustomPreset, a.Preset);
        Assert.Equal(80, a.Amount);
        Assert.Equal("紅藍對調", a.LutName);
        var p = Assert.IsType<LutAdjustment>(Assert.IsType<AdjustmentLayer>(loaded.Root.Children[2]).Adjustment);
        Assert.Equal(1, p.Preset);
    }

    [Fact]
    public void 合成器_LUT調整圖層_走像素路徑()
    {
        using var doc = ImageCodec.CreateBlankDocument(256, 256, new SKColor(200, 50, 50));
        var adj = new AdjustmentLayer(new LutAdjustment().WithCube(SwapCube));
        lock (doc.SyncRoot) doc.Root.Add(adj);

        using var compositor = new Compositor(doc);
        var deadline = Environment.TickCount64 + 3000;
        SKColor last = default;
        while (Environment.TickCount64 < deadline)
        {
            compositor.TryGetTile(TileIndex.FromPixel(128, 128), out _);
            last = compositor.SamplePixel(128, 128);
            if (last.Alpha == 255 && last.Blue > 190) break;
            Thread.Sleep(15);
        }
        Assert.InRange(last.Blue, 195, 205); // 紅藍對調：R=200 跑到 B
        Assert.InRange(last.Red, 45, 55);
        Assert.InRange(last.Green, 45, 55);
    }
}

file static class LutTestExtensions
{
    public static LutAdjustment WithCube(this LutAdjustment adj, string cubeText) =>
        adj with { Preset = LutAdjustment.CustomPreset, Lut = Lut3D.ParseCube(cubeText, "test") };
}
