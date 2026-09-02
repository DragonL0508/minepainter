using System.Security.Cryptography;

namespace MinePainter.Core.AI;

/// <summary>下載進度。</summary>
/// <param name="BytesRead">已下載位元組。</param>
/// <param name="TotalBytes">總位元組（取自型錄，不信任伺服器回報的長度）。</param>
public readonly record struct DownloadProgress(long BytesRead, long TotalBytes)
{
    public double Fraction => TotalBytes <= 0 ? 0 : Math.Clamp(BytesRead / (double)TotalBytes, 0, 1);
}

/// <summary>
/// 把型錄裡的模型抓進模型資料夾。
///
/// 先寫 .part 再驗 MD5 再改名，所以中途取消、斷線、當掉都不會在資料夾裡留下半個檔案
/// 被當成可用的模型（半個 .onnx 載入時才會爆，那時使用者根本不知道發生什麼事）。
/// </summary>
public static class ModelDownloader
{
    /// <summary>取得下載串流的方式（測試可替換）。</summary>
    public static Func<string, CancellationToken, Task<Stream>> OpenStream { get; set; } = HttpOpen;

    private static readonly Lazy<HttpClient> Http = new(() => new HttpClient
    {
        Timeout = TimeSpan.FromMinutes(30), // 模型有大到 1 GB 的，慢速網路要跑很久
    });

    /// <summary>模型檔已經在資料夾裡且大小正確。</summary>
    public static bool IsInstalled(ModelCatalogEntry entry, string directory)
    {
        var path = Path.Combine(directory, entry.FileName);
        try { return File.Exists(path) && new FileInfo(path).Length == entry.SizeBytes; }
        catch (IOException) { return false; }
    }

    /// <summary>
    /// 下載一個模型到 <paramref name="directory"/>。已經裝好的直接回傳路徑，不重抓。
    /// 取消會刪掉暫存檔；MD5 對不上會丟 <see cref="InvalidDataException"/>。
    /// </summary>
    public static async Task<string> DownloadAsync(ModelCatalogEntry entry, string directory,
        IProgress<DownloadProgress>? progress = null, CancellationToken ct = default)
    {
        Directory.CreateDirectory(directory);
        var target = Path.Combine(directory, entry.FileName);
        if (IsInstalled(entry, directory)) return target;

        EnsureDiskSpace(directory, entry.SizeBytes);

        var partial = target + ".part";
        try
        {
            using (var source = await OpenStream(entry.Url, ct).ConfigureAwait(false))
            using (var file = new FileStream(partial, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 20, useAsync: true))
            {
                var buffer = new byte[1 << 20];
                long read = 0;
                progress?.Report(new DownloadProgress(0, entry.SizeBytes));
                while (true)
                {
                    var n = await source.ReadAsync(buffer, ct).ConfigureAwait(false);
                    if (n == 0) break;
                    await file.WriteAsync(buffer.AsMemory(0, n), ct).ConfigureAwait(false);
                    read += n;
                    progress?.Report(new DownloadProgress(read, entry.SizeBytes));
                }
            }

            var length = new FileInfo(partial).Length;
            if (length != entry.SizeBytes)
                throw new InvalidDataException(
                    $"{entry.Title} 下載不完整（收到 {length:N0} 位元組，應該是 {entry.SizeBytes:N0}）。請重試。");

            var actual = await ComputeMd5Async(partial, ct).ConfigureAwait(false);
            if (!string.Equals(actual, entry.Md5, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException($"{entry.Title} 下載的內容校驗不符，可能被中途改動或損壞。請重試。");

            File.Move(partial, target, overwrite: true);
            return target;
        }
        catch
        {
            TryDelete(partial); // 失敗或取消都不要留半個檔
            throw;
        }
    }

    /// <summary>算檔案的 MD5（跟 rembg 公布的值比對用）。</summary>
    public static async Task<string> ComputeMd5Async(string path, CancellationToken ct = default)
    {
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 20, useAsync: true);
        var hash = await MD5.HashDataAsync(stream, ct).ConfigureAwait(false);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    /// <summary>下載前先確認硬碟塞得下（留一倍餘裕給 .part 與改名）。</summary>
    private static void EnsureDiskSpace(string directory, long needBytes)
    {
        try
        {
            var root = Path.GetPathRoot(Path.GetFullPath(directory));
            if (string.IsNullOrEmpty(root)) return;
            var free = new DriveInfo(root).AvailableFreeSpace;
            if (free < needBytes + (256L << 20))
                throw new IOException(
                    $"硬碟空間不足：需要約 {needBytes / (double)(1L << 20):0} MB，{root} 只剩 {free / (double)(1L << 20):0} MB。");
        }
        catch (ArgumentException) { /* 查不到磁碟資訊就別擋 */ }
        catch (UnauthorizedAccessException) { }
    }

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    private static async Task<Stream> HttpOpen(string url, CancellationToken ct)
    {
        var response = await Http.Value
            .GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
    }
}
