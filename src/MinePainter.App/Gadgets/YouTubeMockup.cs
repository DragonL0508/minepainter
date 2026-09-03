using System.Globalization;
using System.Text;
using MinePainter.Core.Compositing;
using MinePainter.Core.Documents;
using SkiaSharp;

namespace MinePainter.App.Gadgets;

/// <summary>「YouTube 縮圖預覽」小工具的參數（由對話框拍成純值後傳進來）。</summary>
public sealed record YouTubeMockupOptions
{
    public string Title { get; init; } = "";
    public string Channel { get; init; } = "";
    public long Views { get; init; }
    public string Uploaded { get; init; } = "";
    public string Duration { get; init; } = "";
    public bool Dark { get; init; } = true;

    /// <summary>true＝裁切填滿 16:9（cover），false＝完整顯示、留黑邊（contain）。</summary>
    public bool Cover { get; init; } = true;
    public bool AvatarFromImage { get; init; }
}

/// <summary>
/// 把目前文件的合成結果塞進一份「長得像 YouTube 首頁」的靜態網頁，丟給系統瀏覽器開。
/// 純本機檔案：不連任何網路、沒有外部資源，縮圖是內嵌的 data URI。
/// <para>
/// 版面是自己刻的 HTML/CSS 仿製品（YouTube 本站動態載入 + 混淆過的樣式，抓不下來也不該抓），
/// 但尺寸與色票取自 1920 寬深色實機的 getComputedStyle 量測，關鍵值都標在對應的 CSS 註解裡。
/// 只做首頁：搜尋結果頁與觀看頁還沒有可信的量測，寧可不做也不要憑印象生一個不像的版面。
/// </para>
/// </summary>
public static class YouTubeMockup
{
    /// <summary>縮圖內嵌前的長邊上限：1280 已足夠銳利，再大只是把 HTML 撐肥。</summary>
    private const int MaxThumbWidth = 1280;

    /// <summary>合成 → 縮圖 → 產生網頁 → 回傳暫存檔路徑。可在背景執行緒呼叫。</summary>
    public static string Render(Document doc, YouTubeMockupOptions options)
    {
        var png = EncodeThumb(doc);
        var html = BuildHtml(png, options);

        var dir = Path.Combine(Path.GetTempPath(), "MinePainter", "youtube-preview");
        Directory.CreateDirectory(dir);
        CleanOld(dir);
        // 檔名帶時間戳：同名檔案瀏覽器可能拿快取的舊版，看起來就像縮圖沒更新
        var path = Path.Combine(dir, $"preview-{DateTime.Now:yyyyMMdd-HHmmss-fff}.html");
        File.WriteAllText(path, html, new UTF8Encoding(false));
        return path;
    }

    private static void CleanOld(string dir)
    {
        try
        {
            var cutoff = DateTime.Now.AddDays(-1);
            foreach (var file in Directory.EnumerateFiles(dir, "preview-*.html"))
            {
                if (File.GetLastWriteTime(file) < cutoff) File.Delete(file);
            }
        }
        catch
        {
            // 清暫存失敗不值得打斷預覽
        }
    }

    private static byte[] EncodeThumb(Document doc)
    {
        using var composite = Compositor.RenderComposite(doc);
        if (composite.Width <= MaxThumbWidth)
        {
            using var direct = composite.Encode(SKEncodedImageFormat.Png, 100)
                ?? throw new InvalidOperationException("影像編碼失敗");
            return direct.ToArray();
        }

        var w = MaxThumbWidth;
        var h = Math.Max(1, (int)Math.Round(composite.Height * (double)w / composite.Width));
        var info = new SKImageInfo(w, h, SKColorType.Bgra8888, SKAlphaType.Premul);
        using var surface = SKSurface.Create(info);
        surface.Canvas.Clear(SKColors.Transparent);
        using (var paint = new SKPaint { FilterQuality = SKFilterQuality.High })
        {
            surface.Canvas.DrawImage(composite, SKRect.Create(w, h), paint);
        }
        surface.Canvas.Flush();
        using var scaled = surface.Snapshot();
        using var encoded = scaled.Encode(SKEncodedImageFormat.Png, 100)
            ?? throw new InvalidOperationException("影像編碼失敗");
        return encoded.ToArray();
    }

    /// <summary>YouTube 繁中的觀看數寫法：一萬以下逐位、以上用萬／億並保留一位小數。</summary>
    public static string FormatViews(long views)
    {
        if (views >= 100_000_000) return Trim(views / 100_000_000.0) + "億次觀看";
        if (views >= 10_000) return Trim(views / 10_000.0) + "萬次觀看";
        return views.ToString("N0", CultureInfo.InvariantCulture) + " 次觀看";

        // YouTube 是無條件捨去（1.29 萬顯示 1.2 萬），不是四捨五入
        static string Trim(double value)
        {
            var truncated = Math.Floor(value * 10) / 10;
            return truncated.ToString(truncated == Math.Floor(truncated) ? "0" : "0.0",
                CultureInfo.InvariantCulture);
        }
    }

    private static string BuildHtml(byte[] png, YouTubeMockupOptions o)
    {
        var thumb = "data:image/png;base64," + Convert.ToBase64String(png);
        return Template
            .Replace("__THUMB__", thumb)
            .Replace("__AVATAR__", o.AvatarFromImage ? thumb : "")
            .Replace("__AVATAR_MODE__", o.AvatarFromImage ? "image" : "letter")
            .Replace("__AVATAR_LETTER__", Escape(FirstGlyph(o.Channel)))
            .Replace("__TITLE__", Escape(o.Title))
            .Replace("__CHANNEL__", Escape(o.Channel))
            .Replace("__VIEWS__", Escape(FormatViews(o.Views)))
            .Replace("__UPLOADED__", Escape(o.Uploaded))
            .Replace("__DURATION__", Escape(o.Duration))
            .Replace("__THEME__", o.Dark ? "dark" : "light")
            .Replace("__FIT__", o.Cover ? "cover" : "contain");
    }

    private static string FirstGlyph(string text)
    {
        var trimmed = text.Trim();
        if (trimmed.Length == 0) return "M";
        // 代理對（emoji 等）要整對拿，否則會切出半個字元
        return char.IsHighSurrogate(trimmed[0]) && trimmed.Length > 1
            ? trimmed[..2]
            : trimmed[..1];
    }

    /// <summary>
    /// 這些值會同時出現在 HTML 與 JS 字串字面（腳本再用 innerHTML 寫回去，實體會被解回原字元），
    /// 所以連反斜線也要變成實體，否則使用者標題裡的 \ 會讓 JS 字面爆掉整頁空白。
    /// </summary>
    private static string Escape(string text) => text
        .Replace("&", "&amp;")
        .Replace("\\", "&#92;")
        .Replace("<", "&lt;")
        .Replace(">", "&gt;")
        .Replace("\"", "&quot;");

    /// <summary>
    /// 仿 YouTube 首頁：頂列、側欄導覽、分類 chips、影片網格（中間夾一排 Shorts 架）。
    /// 週邊影片全是假資料（CSS 漸層縮圖），只有第一格用使用者的圖。
    /// 標「實測」的數字來自 1920 寬深色實機量測，要改先確認有新的量測資料。
    /// </summary>
    private const string Template = """
<!doctype html>
<html lang="zh-Hant" data-theme="__THEME__">
<head>
<meta charset="utf-8">
<meta name="viewport" content="width=device-width, initial-scale=1">
<title>縮圖預覽（MinePainter）</title>
<style>
:root {
  /* 深色色票：實測 */
  --bg: #0f0f0f; --fg: #f1f1f1; --muted: #aaa;
  --chip: rgba(255,255,255,.1); --chip-active: #f1f1f1; --chip-active-fg: #0f0f0f;
  --line: #303030; --search: #121212; --search-line: #303030; --hover: rgba(255,255,255,.1);
  --guide-w: 240px;   /* #guide-content 實測 240 */
  --grid-min: 480px;  /* 內容寬 1648 要排成 3 欄（卡片實測 533） */
}
html[data-theme="light"] {
  --bg: #fff; --fg: #0f0f0f; --muted: #606060;
  --chip: rgba(0,0,0,.05); --chip-active: #0f0f0f; --chip-active-fg: #fff;
  --line: #e5e5e5; --search: #fff; --search-line: #ccc; --hover: rgba(0,0,0,.05);
}
* { box-sizing: border-box; }
body {
  margin: 0; background: var(--bg); color: var(--fg);
  font: 400 14px/20px "Roboto", "Noto Sans TC", "Microsoft JhengHei", system-ui, sans-serif;
}

/* 頂列：ytd-masthead 實測高 56 */
.top {
  position: sticky; top: 0; z-index: 5; background: var(--bg);
  display: flex; align-items: center; gap: 16px; padding: 0 16px; height: 56px;
}
.burger { width: 24px; display: grid; gap: 4px; cursor: pointer; }
.burger i { display: block; height: 2px; background: var(--fg); }
/* ytd-topbar-logo-renderer 實測 129 寬 */
.logo { display: flex; align-items: center; gap: 5px; width: 129px; font-size: 20px; font-weight: 500; letter-spacing: -1px; }
.logo .play { width: 30px; height: 21px; border-radius: 6px; background: #f00; display: grid; place-items: center; }
.logo .play::after { content: ""; border-left: 8px solid #fff; border-top: 5px solid transparent; border-bottom: 5px solid transparent; margin-left: 2px; }
/* #center 實測 732 × 40 */
.search { flex: 1; max-width: 732px; margin: 0 auto; display: flex; height: 40px; }
.search input {
  flex: 1; height: 40px; border: 1px solid var(--search-line); border-right: 0;
  border-radius: 40px 0 0 40px; background: var(--search); color: var(--fg);
  padding: 0 16px; font-size: 16px; outline: none;
}
.search button {
  width: 64px; height: 40px; border: 1px solid var(--search-line); border-radius: 0 40px 40px 0;
  background: var(--chip); color: var(--fg); cursor: pointer; font-size: 15px;
}
.top .avatar { margin-left: auto; }

.avatar {
  width: 32px; height: 32px; border-radius: 50%; overflow: hidden; flex: none;
  background: #3ea6ff; color: #0f0f0f; display: grid; place-items: center;
  font-weight: 700; font-size: 15px;
}
.avatar img { width: 100%; height: 100%; object-fit: cover; display: block; }

/* 側欄：#guide-content 實測 240 寬、項目高 40 */
.shell { display: flex; }
.side { width: var(--guide-w); flex: none; padding: 12px 12px 0; }
.side .item { display: flex; align-items: center; gap: 24px; height: 40px; padding: 0 12px; border-radius: 10px; }
.side .item.on, .side .item:hover { background: var(--hover); }
.side .dot { width: 24px; height: 24px; border-radius: 5px; background: var(--muted); opacity: .45; flex: none; }
.side hr { border: 0; border-top: 1px solid var(--line); margin: 12px 8px; }
/* 內容區左右邊距：#contents 實測 margin 0 16px */
.main { flex: 1; padding: 0 16px 60px; min-width: 0; }

/* chips：實測高 32、圓角 8、14px/20 weight 500、padding 0 12 */
.chips { display: flex; gap: 12px; padding: 12px 0; overflow: hidden; }
.chip {
  background: var(--chip); border-radius: 8px; padding: 0 12px; height: 32px;
  display: flex; align-items: center; font: 500 14px/20px inherit; white-space: nowrap;
}
.chip.on { background: var(--chip-active); color: var(--chip-active-fg); }

.thumb { position: relative; aspect-ratio: 16/9; border-radius: 12px; overflow: hidden; background: #000; }
.thumb img { width: 100%; height: 100%; object-fit: __FIT__; display: block; }
.thumb .dur {
  position: absolute; right: 8px; bottom: 8px; background: rgba(0,0,0,.8); color: #fff;
  font: 500 12px/18px inherit; padding: 0 4px; border-radius: 4px;
}
.fake { width: 100%; height: 100%; background: linear-gradient(135deg, #3b4a6b, #6b3b52); }
.fake.b { background: linear-gradient(135deg, #2d5a4a, #1f3c56); }
.fake.c { background: linear-gradient(135deg, #6b5a2d, #7a3a2a); }
.fake.d { background: linear-gradient(135deg, #4a2d6b, #2a4a7a); }
.fake.e { background: linear-gradient(135deg, #2a4a3a, #5a5a2a); }

/* 網格：#contents 實測 padding-top 24；卡片 533×400、margin 0 8px 32px（＝欄距 16、列距 32） */
.grid {
  display: grid; grid-template-columns: repeat(auto-fill, minmax(var(--grid-min), 1fr));
  gap: 32px 16px; padding-top: 24px;
}
.card .meta { display: flex; gap: 12px; padding-top: 12px; }
.card .meta .avatar { width: 36px; height: 36px; }
.card .title {
  font: 500 16px/22px inherit; max-height: 44px; overflow: hidden;
  display: -webkit-box; -webkit-line-clamp: 2; -webkit-box-orient: vertical;
}
.card .sub { color: var(--muted); font: 400 12px/18px inherit; }

/* Shorts 架：實測卡片 314×549、margin 0 8px、架身 padding 12px 0 */
.shelf { padding: 12px 0 32px; }
.shelf .head { display: flex; align-items: center; gap: 8px; font: 500 16px/22px inherit; padding-bottom: 12px; }
.shelf .head .mark { color: #f00; font-size: 18px; }
.shelf .row { display: flex; gap: 16px; overflow: hidden; }
.shelf .s { width: 314px; flex: none; }
.shelf .s .cover { aspect-ratio: 9/16; max-height: 480px; border-radius: 12px; overflow: hidden; }
.shelf .s .title {
  font: 500 14px/20px inherit; padding-top: 8px; max-height: 40px; overflow: hidden;
  display: -webkit-box; -webkit-line-clamp: 2; -webkit-box-orient: vertical;
}
.shelf .s .sub { color: var(--muted); font: 400 12px/18px inherit; }

/* MinePainter 自己的控制列（不是 YouTube 的一部分） */
.mp-bar {
  position: fixed; right: 16px; bottom: 16px; z-index: 20; display: flex; align-items: center; gap: 6px;
  background: #1b1b1b; color: #eee; border: 1px solid #3a3a3a; border-radius: 10px;
  padding: 8px 10px; font-size: 12px; box-shadow: 0 6px 24px rgba(0,0,0,.45);
}
.mp-bar b { font-weight: 600; opacity: .7; margin-right: 4px; }
.mp-bar button {
  background: #2c2c2c; color: #eee; border: 1px solid #444; border-radius: 6px;
  padding: 5px 9px; font-size: 12px; cursor: pointer; font-family: inherit;
}
@media (max-width: 1300px) { :root { --grid-min: 320px; } }
@media (max-width: 1100px) { .side { display: none; } }
</style>
</head>
<body>

<div class="top">
  <div class="burger"><i></i><i></i><i></i></div>
  <div class="logo"><span class="play"></span>YouTube<sup style="font-size:10px;opacity:.7">TW</sup></div>
  <div class="search">
    <input value="__CHANNEL__" readonly>
    <button>🔍</button>
  </div>
  <div class="avatar" data-avatar>__AVATAR_LETTER__</div>
</div>

<div class="shell">
  <nav class="side">
    <div class="item on"><span class="dot"></span>首頁</div>
    <div class="item"><span class="dot"></span>Shorts</div>
    <div class="item"><span class="dot"></span>訂閱內容</div>
    <hr>
    <div class="item"><span class="dot"></span>你的頻道</div>
    <div class="item"><span class="dot"></span>觀看紀錄</div>
    <div class="item"><span class="dot"></span>播放清單</div>
    <div class="item"><span class="dot"></span>稍後觀看</div>
    <div class="item"><span class="dot"></span>喜歡的影片</div>
  </nav>

  <main class="main">
    <div class="chips">
      <span class="chip on">全部</span><span class="chip">遊戲</span><span class="chip">Minecraft</span>
      <span class="chip">音樂</span><span class="chip">直播</span><span class="chip">實況</span>
      <span class="chip">最新上傳</span><span class="chip">已觀看</span>
    </div>
    <div class="grid" id="gridTop"></div>
    <section class="shelf">
      <div class="head"><span class="mark">▶</span>Shorts</div>
      <div class="row" id="shortsRow"></div>
    </section>
    <div class="grid" id="gridRest"></div>
  </main>
</div>

<div class="mp-bar">
  <b>MinePainter 預覽</b>
  <button id="themeBtn">深／淺色</button>
</div>

<script>
const MINE = {
  title: "__TITLE__", channel: "__CHANNEL__", views: "__VIEWS__",
  uploaded: "__UPLOADED__", duration: "__DURATION__", thumb: "__THUMB__",
  // 頻道名可能被轉成 &quot; 之類的實體，取首字不能自己 slice，用 C# 算好的
  letter: "__AVATAR_LETTER__",
};
// 週邊影片全是假的，用來把版面撐出真實密度
const FAKE = [
  ["殭屍陷阱這樣蓋，一晚清空整片刷怪塔", "紅石小教室", "8.3萬次觀看", "2 天前", "12:47", "b"],
  ["我用 30 天蓋了一座會呼吸的城市", "方塊建築師", "142萬次觀看", "3 週前", "24:05", "c"],
  ["【實況】從零開始的空島生存 EP.1", "像素工坊", "5,204 次觀看", "5 小時前", "1:58:31", "d"],
  ["新手最常搞錯的 10 個附魔順序", "礦坑筆記", "27萬次觀看", "1 個月前", "9:12", "e"],
  ["把村民交易做到破產只需要這一招", "紅石小教室", "63萬次觀看", "6 天前", "15:30", ""],
  ["這個地形產生器讓我不想再手動蓋山", "方塊建築師", "11萬次觀看", "4 天前", "18:22", "b"],
  ["用命令方塊做出會追人的雕像", "指令實驗室", "3.9萬次觀看", "9 天前", "7:44", "c"],
  ["整理了一份 1.21 全自動農場清單", "礦坑筆記", "89萬次觀看", "2 個月前", "31:16", "d"],
];
const SHORTS = [
  ["一格紅石省下半座機器", "12 萬次觀看", "b"],
  ["這樣挖礦快三倍", "48 萬次觀看", "c"],
  ["最短的自動門", "9.7 萬次觀看", "d"],
  ["村民抓不到的原因", "23 萬次觀看", "e"],
  ["三秒判斷礦脈方向", "6.1 萬次觀看", ""],
];

const thumbHtml = (f) => f
  ? `<div class="thumb"><div class="fake ${f[5]}"></div><span class="dur">${f[4]}</span></div>`
  : `<div class="thumb"><img src="${MINE.thumb}" alt=""><span class="dur">${MINE.duration}</span></div>`;

const cardHtml = (f) => `
  <article class="card">
    ${thumbHtml(f)}
    <div class="meta">
      <div class="avatar" data-own-avatar="${f ? 0 : 1}">${f ? f[1].slice(0, 1) : MINE.letter}</div>
      <div>
        <div class="title">${f ? f[0] : MINE.title}</div>
        <div class="sub">${f ? f[1] : MINE.channel}</div>
        <div class="sub">${f ? `${f[2]}・${f[3]}` : `${MINE.views}・${MINE.uploaded}`}</div>
      </div>
    </div>
  </article>`;

// 使用者的影片排第一格，Shorts 架前後各放幾部假的（跟真的首頁一樣）
document.getElementById("gridTop").innerHTML = [null, ...FAKE.slice(0, 5)].map(cardHtml).join("");
document.getElementById("gridRest").innerHTML = FAKE.slice(5).map(cardHtml).join("");
document.getElementById("shortsRow").innerHTML = SHORTS.map((s) => `
  <article class="s">
    <div class="cover"><div class="fake ${s[2]}"></div></div>
    <div class="title">${s[0]}</div>
    <div class="sub">${s[1]}</div>
  </article>`).join("");

// 頻道頭像：選了「用這張圖」只換自己的（頂列與第一格），假影片維持字母
if ("__AVATAR_MODE__" === "image") {
  document.querySelectorAll("[data-avatar], [data-own-avatar='1']").forEach((el) => {
    el.innerHTML = `<img src="__AVATAR__" alt="">`;
  });
}

document.getElementById("themeBtn").onclick = () => {
  const root = document.documentElement;
  root.dataset.theme = root.dataset.theme === "dark" ? "light" : "dark";
};
</script>
</body>
</html>
""";
}
