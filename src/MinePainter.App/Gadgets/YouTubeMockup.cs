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

    /// <summary>開啟時停在哪一頁：home／search／watch。</summary>
    public string Page { get; init; } = "home";
    public bool Dark { get; init; } = true;

    /// <summary>true＝裁切填滿 16:9（cover），false＝完整顯示、留黑邊（contain）。</summary>
    public bool Cover { get; init; } = true;
    public bool AvatarFromImage { get; init; }
}

/// <summary>
/// 把目前文件的合成結果塞進一份「長得像 YouTube」的靜態網頁，丟給系統瀏覽器開。
/// 純本機檔案：不連任何網路、沒有外部資源，縮圖是內嵌的 data URI。
/// 版面是自己刻的 HTML/CSS 仿製品（YouTube 本站是動態載入 + 混淆過的樣式，抓不下來也不該抓）。
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
            .Replace("__VIEWS_RAW__", o.Views.ToString("N0", CultureInfo.InvariantCulture))
            .Replace("__UPLOADED__", Escape(o.Uploaded))
            .Replace("__DURATION__", Escape(o.Duration))
            .Replace("__PAGE__", o.Page)
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
    /// 仿 YouTube 的單頁版面：首頁網格／搜尋結果／觀看頁三種，右上角可即時切換與換深淺色。
    /// 週邊影片全是假資料（CSS 漸層縮圖），只有主打那部用使用者的圖。
    /// </summary>
    private const string Template = """
<!doctype html>
<html lang="zh-Hant" data-theme="__THEME__" data-page="__PAGE__">
<head>
<meta charset="utf-8">
<meta name="viewport" content="width=device-width, initial-scale=1">
<title>縮圖預覽（MinePainter）</title>
<style>
:root {
  --bg: #0f0f0f; --fg: #f1f1f1; --muted: #aaa; --chip: #272727; --chip-active: #f1f1f1;
  --chip-active-fg: #0f0f0f; --line: #303030; --search: #121212; --search-line: #303030;
  --hover: #272727; --btn: #272727;
}
html[data-theme="light"] {
  --bg: #fff; --fg: #0f0f0f; --muted: #606060; --chip: #f2f2f2; --chip-active: #0f0f0f;
  --chip-active-fg: #fff; --line: #e5e5e5; --search: #fff; --search-line: #ccc;
  --hover: #f2f2f2; --btn: #f2f2f2;
}
* { box-sizing: border-box; }
body {
  margin: 0; background: var(--bg); color: var(--fg);
  font: 14px/1.4 "Roboto", "Noto Sans TC", "Microsoft JhengHei", system-ui, sans-serif;
}
a { color: inherit; text-decoration: none; }

/* 頂列 */
.top {
  position: sticky; top: 0; z-index: 5; background: var(--bg);
  display: flex; align-items: center; gap: 16px; padding: 8px 16px; height: 56px;
}
.burger { width: 24px; display: grid; gap: 4px; cursor: pointer; }
.burger i { display: block; height: 2px; background: var(--fg); }
.logo { display: flex; align-items: center; gap: 5px; font-size: 20px; font-weight: 500; letter-spacing: -1px; }
.logo .play {
  width: 30px; height: 21px; border-radius: 6px; background: #f00;
  display: grid; place-items: center;
}
.logo .play::after { content: ""; border-left: 8px solid #fff; border-top: 5px solid transparent; border-bottom: 5px solid transparent; margin-left: 2px; }
.search { flex: 1; max-width: 640px; margin: 0 auto; display: flex; }
.search input {
  flex: 1; height: 38px; border: 1px solid var(--search-line); border-right: 0;
  border-radius: 19px 0 0 19px; background: var(--search); color: var(--fg);
  padding: 0 16px; font-size: 15px; outline: none;
}
.search button {
  width: 62px; height: 38px; border: 1px solid var(--search-line); border-radius: 0 19px 19px 0;
  background: var(--chip); color: var(--fg); cursor: pointer; font-size: 15px;
}
.top .avatar { margin-left: auto; }

/* 頭像 */
.avatar {
  width: 32px; height: 32px; border-radius: 50%; overflow: hidden; flex: none;
  background: #3ea6ff; color: #0f0f0f; display: grid; place-items: center;
  font-weight: 700; font-size: 15px;
}
.avatar img { width: 100%; height: 100%; object-fit: cover; display: block; }
.avatar.big { width: 40px; height: 40px; font-size: 18px; }

/* 版面 */
.shell { display: flex; }
.side { width: 210px; flex: none; padding: 10px 6px; }
.side .item { display: flex; align-items: center; gap: 22px; padding: 9px 12px; border-radius: 10px; font-size: 14px; }
.side .item.on, .side .item:hover { background: var(--hover); }
.side .dot { width: 22px; height: 22px; border-radius: 5px; background: var(--muted); opacity: .45; flex: none; }
.side hr { border: 0; border-top: 1px solid var(--line); margin: 12px 8px; }
.main { flex: 1; padding: 0 24px 60px; min-width: 0; }

/* 分類 chips */
.chips { display: flex; gap: 12px; padding: 12px 0 20px; overflow: hidden; }
.chip { background: var(--chip); border-radius: 8px; padding: 7px 12px; font-size: 13px; white-space: nowrap; }
.chip.on { background: var(--chip-active); color: var(--chip-active-fg); }

/* 縮圖 */
.thumb { position: relative; aspect-ratio: 16/9; border-radius: 12px; overflow: hidden; background: #000; }
.thumb img { width: 100%; height: 100%; object-fit: __FIT__; display: block; }
.thumb .dur {
  position: absolute; right: 8px; bottom: 8px; background: rgba(0,0,0,.8); color: #fff;
  font-size: 12px; font-weight: 500; padding: 1px 4px; border-radius: 4px;
}
.fake { background: linear-gradient(135deg, #3b4a6b, #6b3b52); }
.fake.b { background: linear-gradient(135deg, #2d5a4a, #1f3c56); }
.fake.c { background: linear-gradient(135deg, #6b5a2d, #7a3a2a); }
.fake.d { background: linear-gradient(135deg, #4a2d6b, #2a4a7a); }
.fake.e { background: linear-gradient(135deg, #2a4a3a, #5a5a2a); }

/* 首頁網格 */
.grid { display: grid; grid-template-columns: repeat(auto-fill, minmax(300px, 1fr)); gap: 40px 16px; }
.card .meta { display: flex; gap: 12px; padding-top: 12px; }
.card .title { font-size: 16px; font-weight: 500; line-height: 1.35; max-height: 2.7em; overflow: hidden; }
.card .sub { color: var(--muted); font-size: 13px; margin-top: 4px; }

/* 搜尋結果 */
.rows { display: flex; flex-direction: column; gap: 16px; padding-top: 12px; }
.row { display: flex; gap: 16px; }
.row .thumb { width: 360px; flex: none; }
.row .title { font-size: 18px; font-weight: 400; }
.row .sub { color: var(--muted); font-size: 12px; margin-top: 4px; }
.row .by { display: flex; align-items: center; gap: 8px; color: var(--muted); font-size: 12px; margin: 12px 0 6px; }
.row .by .avatar { width: 24px; height: 24px; font-size: 12px; }
.row .desc { color: var(--muted); font-size: 12px; max-height: 3em; overflow: hidden; }

/* 觀看頁 */
.watch { display: flex; gap: 24px; padding-top: 16px; }
.watch .primary { flex: 1; min-width: 0; }
.player { position: relative; aspect-ratio: 16/9; border-radius: 12px; overflow: hidden; background: #000; }
.player img { width: 100%; height: 100%; object-fit: __FIT__; display: block; }
.player .play {
  position: absolute; inset: 0; margin: auto; width: 68px; height: 48px; border-radius: 10px;
  background: rgba(0,0,0,.55); display: grid; place-items: center;
}
.player .play::after { content: ""; border-left: 20px solid #fff; border-top: 12px solid transparent; border-bottom: 12px solid transparent; margin-left: 4px; }
.player .bar { position: absolute; left: 0; right: 0; bottom: 0; height: 3px; background: rgba(255,255,255,.25); }
.player .bar i { display: block; width: 28%; height: 100%; background: #f00; }
.watch h1 { font-size: 20px; margin: 12px 0 12px; line-height: 1.35; }
.owner { display: flex; align-items: center; gap: 12px; flex-wrap: wrap; }
.owner .name { font-weight: 500; }
.owner .subs { color: var(--muted); font-size: 12px; }
.pill { background: var(--btn); border-radius: 18px; padding: 8px 16px; font-size: 14px; font-weight: 500; }
.pill.sub { background: var(--fg); color: var(--bg); }
.owner .actions { margin-left: auto; display: flex; gap: 8px; }
.desc-box { background: var(--chip); border-radius: 12px; padding: 12px; margin-top: 16px; font-size: 14px; }
.desc-box .head { font-weight: 500; margin-bottom: 6px; }
.desc-box .body { color: var(--fg); opacity: .85; white-space: pre-line; }
.rail { width: 402px; flex: none; display: flex; flex-direction: column; gap: 8px; }
.rail .r { display: flex; gap: 8px; }
.rail .thumb { width: 168px; flex: none; }
.rail .title { font-size: 14px; font-weight: 500; line-height: 1.3; max-height: 2.6em; overflow: hidden; }
.rail .sub { color: var(--muted); font-size: 12px; margin-top: 4px; }

/* 頁面切換 */
html[data-page="home"] .p-search, html[data-page="home"] .p-watch,
html[data-page="search"] .p-home, html[data-page="search"] .p-watch,
html[data-page="watch"] .p-home, html[data-page="watch"] .p-search { display: none; }

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
.mp-bar button.on { background: #d94b3a; border-color: #d94b3a; color: #fff; }
@media (max-width: 1100px) { .side { display: none; } .rail { width: 320px; } }
@media (max-width: 900px) { .watch { flex-direction: column; } .rail { width: auto; } .row .thumb { width: 240px; } }
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
    <!-- 首頁 -->
    <section class="p-home">
      <div class="chips">
        <span class="chip on">全部</span><span class="chip">遊戲</span><span class="chip">Minecraft</span>
        <span class="chip">音樂</span><span class="chip">直播</span><span class="chip">實況</span>
        <span class="chip">最新上傳</span><span class="chip">已觀看</span>
      </div>
      <div class="grid" id="homeGrid"></div>
    </section>

    <!-- 搜尋結果 -->
    <section class="p-search">
      <div class="chips">
        <span class="chip on">篩選器</span><span class="chip">影片</span><span class="chip">頻道</span>
        <span class="chip">播放清單</span><span class="chip">本週上傳</span>
      </div>
      <div class="rows" id="searchRows"></div>
    </section>

    <!-- 觀看頁 -->
    <section class="p-watch">
      <div class="watch">
        <div class="primary">
          <div class="player">
            <img src="__THUMB__" alt="">
            <span class="play"></span>
            <div class="bar"><i></i></div>
          </div>
          <h1>__TITLE__</h1>
          <div class="owner">
            <div class="avatar big" data-avatar>__AVATAR_LETTER__</div>
            <div>
              <div class="name">__CHANNEL__</div>
              <div class="subs">1.7 萬位訂閱者</div>
            </div>
            <span class="pill sub">訂閱</span>
            <div class="actions">
              <span class="pill">👍 1.2 萬　👎</span>
              <span class="pill">分享</span>
              <span class="pill">⋯</span>
            </div>
          </div>
          <div class="desc-box">
            <div class="head">__VIEWS__　__UPLOADED__</div>
            <div class="body">這是 MinePainter 的本機縮圖預覽，網頁與周邊影片都是假的，只有這張縮圖是你的作品。
用來檢查縮圖在小尺寸、深淺色背景下還讀不讀得出來。</div>
          </div>
        </div>
        <aside class="rail" id="rail"></aside>
      </div>
    </section>
  </main>
</div>

<div class="mp-bar">
  <b>MinePainter 預覽</b>
  <button data-page="home">首頁</button>
  <button data-page="search">搜尋</button>
  <button data-page="watch">觀看頁</button>
  <button id="themeBtn">深／淺</button>
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

const thumbHtml = (f) => f
  ? `<div class="thumb"><div class="fake ${f[5]}" style="width:100%;height:100%"></div><span class="dur">${f[4]}</span></div>`
  : `<div class="thumb"><img src="${MINE.thumb}" alt=""><span class="dur">${MINE.duration}</span></div>`;

const avatarHtml = (letter) => `<div class="avatar">${letter}</div>`;

// 首頁：使用者的影片排第一格，後面接假的
document.getElementById("homeGrid").innerHTML = [null, ...FAKE].map((f) => `
  <article class="card">
    ${thumbHtml(f)}
    <div class="meta">
      ${avatarHtml(f ? f[1].slice(0, 1) : MINE.letter)}
      <div>
        <div class="title">${f ? f[0] : MINE.title}</div>
        <div class="sub">${f ? f[1] : MINE.channel}</div>
        <div class="sub">${f ? `${f[2]}・${f[3]}` : `${MINE.views}・${MINE.uploaded}`}</div>
      </div>
    </div>
  </article>`).join("");

document.getElementById("searchRows").innerHTML = [null, ...FAKE.slice(0, 5)].map((f) => `
  <article class="row">
    ${thumbHtml(f)}
    <div>
      <div class="title">${f ? f[0] : MINE.title}</div>
      <div class="sub">${f ? `${f[2]}・${f[3]}` : `${MINE.views}・${MINE.uploaded}`}</div>
      <div class="by">${avatarHtml(f ? f[1].slice(0, 1) : MINE.letter)}${f ? f[1] : MINE.channel}</div>
      <div class="desc">這是假的搜尋結果內文，只是把版面撐到跟真的一樣密，好判斷縮圖在這個尺寸下還看不看得清楚。</div>
    </div>
  </article>`).join("");

document.getElementById("rail").innerHTML = FAKE.map((f) => `
  <article class="r">
    ${thumbHtml(f)}
    <div>
      <div class="title">${f[0]}</div>
      <div class="sub">${f[1]}</div>
      <div class="sub">${f[2]}・${f[3]}</div>
    </div>
  </article>`).join("");

// 頻道頭像：選了「用這張圖」就把所有頭像換成縮圖
if ("__AVATAR_MODE__" === "image") {
  document.querySelectorAll("[data-avatar]").forEach((el) => {
    el.innerHTML = `<img src="__AVATAR__" alt="">`;
  });
}

const root = document.documentElement;
const syncButtons = () => {
  document.querySelectorAll(".mp-bar button[data-page]").forEach((b) => {
    b.classList.toggle("on", b.dataset.page === root.dataset.page);
  });
};
document.querySelectorAll(".mp-bar button[data-page]").forEach((b) => {
  b.onclick = () => { root.dataset.page = b.dataset.page; syncButtons(); };
});
document.getElementById("themeBtn").onclick = () => {
  root.dataset.theme = root.dataset.theme === "dark" ? "light" : "dark";
};
syncButtons();
</script>
</body>
</html>
""";
}
