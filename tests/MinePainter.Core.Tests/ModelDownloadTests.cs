using System.Security.Cryptography;
using MinePainter.Core.AI;
using Xunit;

namespace MinePainter.Core.Tests;

/// <summary>
/// App 內下載去背模型。重點是「不要在模型資料夾裡留下壞檔」：半個 .onnx 要等到推論時才會爆，
/// 那時使用者完全不知道發生什麼事。
/// </summary>
public class ModelDownloadTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "mp-dl-" + Guid.NewGuid().ToString("N"));
    private readonly Func<string, CancellationToken, Task<Stream>> _previousOpen = ModelDownloader.OpenStream;

    public ModelDownloadTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        ModelDownloader.OpenStream = _previousOpen;
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
    }

    private static ModelCatalogEntry EntryFor(byte[] content) => new(
        "test-model.onnx", "測試模型", "https://example.invalid/test-model.onnx",
        content.Length, Convert.ToHexString(MD5.HashData(content)).ToLowerInvariant(),
        Strength: "測試", Speed: "測試", Memory: "測試");

    private static void Serve(byte[] content) =>
        ModelDownloader.OpenStream = (_, _) => Task.FromResult<Stream>(new MemoryStream(content));

    [Fact]
    public async Task DownloadsAndVerifiesTheFile()
    {
        var content = new byte[64 * 1024];
        Random.Shared.NextBytes(content);
        var entry = EntryFor(content);
        Serve(content);

        var reports = new List<DownloadProgress>();
        var path = await ModelDownloader.DownloadAsync(entry, _dir, new Progress<DownloadProgress>(reports.Add));

        Assert.Equal(Path.Combine(_dir, entry.FileName), path);
        Assert.Equal(content, await File.ReadAllBytesAsync(path));
        Assert.True(ModelDownloader.IsInstalled(entry, _dir));
        // 進度會回報（Progress 是非同步派送，所以只確認有回報而不比對最後一筆）
        Assert.NotEmpty(reports);
    }

    [Fact]
    public async Task RejectsContentThatDoesNotMatchTheChecksum()
    {
        var entry = EntryFor(new byte[1024]);
        Serve(new byte[1024].Select((_, i) => (byte)i).ToArray()); // 大小對、內容不對

        await Assert.ThrowsAsync<InvalidDataException>(() => ModelDownloader.DownloadAsync(entry, _dir));

        Assert.False(File.Exists(Path.Combine(_dir, entry.FileName)));
        Assert.Empty(Directory.GetFiles(_dir)); // 連 .part 都不留
    }

    [Fact]
    public async Task RejectsATruncatedDownload()
    {
        var content = new byte[4096];
        Random.Shared.NextBytes(content);
        var entry = EntryFor(content);
        Serve(content[..2048]); // 斷線：只收到一半

        await Assert.ThrowsAsync<InvalidDataException>(() => ModelDownloader.DownloadAsync(entry, _dir));
        Assert.Empty(Directory.GetFiles(_dir));
    }

    [Fact]
    public async Task CancellingLeavesNothingBehind()
    {
        var content = new byte[1 << 20];
        var entry = EntryFor(content);
        using var cts = new CancellationTokenSource();
        ModelDownloader.OpenStream = (_, _) =>
        {
            cts.Cancel();
            return Task.FromResult<Stream>(new MemoryStream(content));
        };

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => ModelDownloader.DownloadAsync(entry, _dir, null, cts.Token));
        Assert.Empty(Directory.GetFiles(_dir));
    }

    [Fact]
    public async Task AnInstalledModelIsNotDownloadedAgain()
    {
        var content = new byte[2048];
        Random.Shared.NextBytes(content);
        var entry = EntryFor(content);
        Serve(content);
        await ModelDownloader.DownloadAsync(entry, _dir);

        var opened = 0;
        ModelDownloader.OpenStream = (_, _) => { opened++; return Task.FromResult<Stream>(new MemoryStream(content)); };
        await ModelDownloader.DownloadAsync(entry, _dir);

        Assert.Equal(0, opened);
    }

    [Fact]
    public void CatalogEntriesAreWellFormed()
    {
        Assert.NotEmpty(ModelCatalog.Entries);
        foreach (var e in ModelCatalog.Entries)
        {
            Assert.EndsWith(".onnx", e.FileName);
            Assert.StartsWith("https://", e.Url); // 模型是要載入執行的程式碼，不走明文
            Assert.True(e.SizeBytes > 0);
            Assert.Equal(32, e.Md5.Length);
            Assert.True(e.Md5.All(Uri.IsHexDigit), $"{e.FileName} 的 MD5 不是十六進位");
            Assert.False(string.IsNullOrWhiteSpace(e.Strength));
            Assert.False(string.IsNullOrWhiteSpace(e.Speed));
            Assert.False(string.IsNullOrWhiteSpace(e.Memory));
        }
        Assert.Equal(ModelCatalog.Entries.Count, ModelCatalog.Entries.Select(e => e.FileName).Distinct().Count());
        Assert.Single(ModelCatalog.Entries, e => e.Recommended); // 不熟模型的人只該看到一個「推薦」
    }

    [Fact]
    public void EveryCatalogFileNameMapsToTheRightPreprocessing()
    {
        // 前處理是靠檔名認的，所以型錄的命名不能取得讓 Preset 認錯
        foreach (var e in ModelCatalog.Entries)
        {
            var expected = e.FileName.StartsWith("u2net") || e.FileName.StartsWith("silueta") ? 320 : 1024;
            var actual = new OnnxModelInfo(Path.GetFileNameWithoutExtension(e.FileName), e.FileName).Preset.Size;
            Assert.Equal(expected, actual);
        }
    }

    [Fact]
    public void CatalogIsFoundByFileName()
    {
        Assert.Equal("ISNet 通用", ModelCatalog.Find("isnet-general-use.onnx")!.Title);
        Assert.Equal("ISNet 通用", ModelCatalog.Find("ISNET-GENERAL-USE.ONNX")!.Title);
        Assert.Null(ModelCatalog.Find("something-a-user-dropped-in.onnx"));
    }
}

/// <summary>
/// 真的連上網把型錄裡最小的模型抓下來，確認網址與 MD5 對得上。
/// 預設不跑（要網路、要 4.6 MB 流量）：設 MINEPAINTER_NET_TEST=1 才會執行。
/// </summary>
public class ModelDownloadNetworkTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "mp-dl-net-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
    }

    [Fact]
    public async Task TheSmallestCatalogEntryReallyDownloads()
    {
        if (Environment.GetEnvironmentVariable("MINEPAINTER_NET_TEST") != "1") return;

        var entry = ModelCatalog.Entries.MinBy(e => e.SizeBytes)!;
        var path = await ModelDownloader.DownloadAsync(entry, _dir);

        Assert.Equal(entry.SizeBytes, new FileInfo(path).Length);
        Assert.Equal(entry.Md5, await ModelDownloader.ComputeMd5Async(path), ignoreCase: true);
    }
}
