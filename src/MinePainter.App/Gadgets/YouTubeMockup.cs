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

        // 一律無條件捨去（1.29 萬＝1.2 萬）；滿十只留整數（183.3 萬＝183 萬），跟實站一致
        static string Trim(double value)
        {
            if (value >= 10) return Math.Floor(value).ToString("0", CultureInfo.InvariantCulture);
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
            .Replace("__THUMBS__", BuildThumbLibrary())
            .Replace("__THEME__", o.Dark ? "dark" : "light")
            .Replace("__FIT__", o.Cover ? "cover" : "contain");
    }

    /// <summary>把內建圖庫排成頁面吃的陣列字面：t＝標題（＝檔名）、s＝WebP 的 data URI。</summary>
    private static string BuildThumbLibrary()
    {
        var items = YouTubeThumbLibrary.All.Select(t =>
            $$"""{ t: "{{Escape(t.Title)}}", s: "data:image/webp;base64,{{Convert.ToBase64String(t.Webp)}}" }""");
        return "[" + string.Join(",\n", items) + "]";
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
    /// 仿 YouTube 首頁：頂列、側欄導覽、分類 chips、影片網格。
    /// 3 欄 × 6 列共 18 部：週邊縮圖與標題取自內建圖庫（<see cref="YouTubeThumbLibrary"/>），
    /// 頻道與數字每次重新整理隨機配，使用者的圖每次落在隨機一格。
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
/* #center 實測 732 × 40。用絕對定位對齊「視窗」正中央：flex 置中會被左邊的
   選單鈕與 logo 推歪，跟實機一樣要以畫面中線為準 */
.search {
  position: absolute; left: 50%; transform: translateX(-50%);
  width: min(732px, calc(100% - 340px)); display: flex; height: 40px;
}
.search input {
  flex: 1; height: 40px; border: 1px solid var(--search-line); border-right: 0;
  border-radius: 40px 0 0 40px; background: var(--search); color: var(--fg);
  padding: 0 16px; font-size: 16px; outline: none; min-width: 0;
}
.search button {
  width: 64px; height: 40px; border: 1px solid var(--search-line); border-radius: 0 40px 40px 0;
  background: var(--chip); color: var(--fg); cursor: pointer;
  display: grid; place-items: center; padding: 0;
}
.search button svg { width: 20px; height: 20px; }

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
/* 週邊影片的縮圖：內建圖庫的圖一律裁切填滿（只有使用者自己那張跟著 --FIT 走） */
.thumb > img { width: 100%; height: 100%; object-fit: cover; display: block; }
.thumb > .own { object-fit: __FIT__; }

/* 網格：#contents 實測 padding-top 24；卡片 533×400、margin 0 8px 32px（＝欄距 16、列距 32）。
   固定 3 欄 × 6 列＝18 部，跟 1920 寬實機一樣；窄視窗才降欄避免溢出 */
.grid {
  display: grid; grid-template-columns: repeat(3, minmax(0, 1fr));
  gap: 32px 16px; padding-top: 24px;
}
.card .meta { display: flex; gap: 12px; padding-top: 12px; }
.card .meta .avatar { width: 36px; height: 36px; }
.card .title {
  font: 500 16px/22px inherit; max-height: 44px; overflow: hidden;
  display: -webkit-box; -webkit-line-clamp: 2; -webkit-box-orient: vertical;
}
.card .sub { color: var(--muted); font: 400 12px/18px inherit; }

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
@media (max-width: 1100px) { .side { display: none; } }
@media (max-width: 1000px) { .grid { grid-template-columns: repeat(2, minmax(0, 1fr)); } }
@media (max-width: 640px) { .grid { grid-template-columns: minmax(0, 1fr); } .search { display: none; } }
</style>
</head>
<body>

<div class="top">
  <div class="burger"><i></i><i></i><i></i></div>
  <div class="logo"><span class="play"></span>YouTube<sup style="font-size:10px;opacity:.7">TW</sup></div>
  <div class="search">
    <input value="__CHANNEL__" readonly>
    <button aria-label="搜尋">
      <svg viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round">
        <circle cx="10.5" cy="10.5" r="6.5" /><line x1="15.6" y1="15.6" x2="21" y2="21" />
      </svg>
    </button>
  </div>
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
    <div class="grid" id="grid"></div>
  </main>
</div>

<div class="mp-bar">
  <b>MinePainter 預覽</b>
  <button id="shuffleBtn">重新整理</button>
  <button id="themeBtn">深／淺色</button>
</div>

<script>
const MINE = {
  title: "__TITLE__", channel: "__CHANNEL__", views: "__VIEWS__",
  uploaded: "__UPLOADED__", duration: "__DURATION__", thumb: "__THUMB__",
  // 頻道名可能被轉成 &quot; 之類的實體，取首字不能自己 slice，用 C# 算好的
  letter: "__AVATAR_LETTER__",
};
// ── 週邊假影片：縮圖與標題來自內建圖庫，其餘每次重新整理隨機配 ────────────────
const rand = (n) => Math.floor(Math.random() * n);
const pick = (a) => a[rand(a.length)];
const pad2 = (n) => String(n).padStart(2, "0");

const CHANNELS = [
  "紅石小教室", "方塊建築師", "像素工坊", "礦坑筆記", "指令實驗室",
  "綠寶石頻道", "一格工程部", "夜視玩家", "苦力怕日常", "建築藍圖社",
];
const AGES = [
  "3 小時前", "7 小時前", "1 天前", "2 天前", "4 天前", "6 天前",
  "9 天前", "2 週前", "3 週前", "1 個月前", "2 個月前", "5 個月前",
];

// 觀看數：對數分佈（多數幾萬、偶爾破百萬），格式跟 C# 的 FormatViews 一致
const randomViews = () => Math.floor(Math.exp(Math.random() * Math.log(4e6 / 400)) * 400);
const formatViews = (v) => {
  const trim = (x) => {
    if (x >= 10) return String(Math.floor(x));
    const t = Math.floor(x * 10) / 10;
    return t === Math.floor(t) ? String(t) : t.toFixed(1);
  };
  if (v >= 1e8) return trim(v / 1e8) + "億次觀看";
  if (v >= 1e4) return trim(v / 1e4) + "萬次觀看";
  return v.toLocaleString("en-US") + " 次觀看";
};
const randomDuration = () => (rand(10) === 0
  ? `${1 + rand(3)}:${pad2(rand(60))}:${pad2(rand(60))}`   // 偶爾一部直播存檔
  : `${1 + rand(38)}:${pad2(rand(60))}`);

// 頻道頭像：跟 YouTube 預設頭像一樣是純色底 + 白色首字，顏色由頻道名決定（同名同色）
const hashOf = (text) => [...text].reduce((h, c) => (h * 31 + c.charCodeAt(0)) >>> 0, 7);
const avatarBg = (name) => `hsl(${hashOf(name) % 360} 55% 42%)`;

// 內建縮圖庫（C# 從 Assets/YouTubePreview/ 內嵌進來；t＝標題＝檔名、s＝WebP data URI）
const THUMBS = __THUMBS__;
// 圖庫是空的就退回純色底，其餘功能照常
const BLANKS = ["#3b4a6b", "#2d5a4a", "#6b5a2d", "#4a2d6b", "#2a4a3a"];

// 每次 render 洗一副牌、每張最多發一次：圖庫不夠 count 張時，寧可多出的格子
// 退回純色底，也不要讓同一部影片在同一頁重複出現
const dealThumbs = (count) => {
  const deck = THUMBS.slice();
  for (let i = deck.length - 1; i > 0; i--) {
    const j = rand(i + 1);
    [deck[i], deck[j]] = [deck[j], deck[i]];
  }
  const out = deck.slice(0, count);
  while (out.length < count) out.push(null);
  return out;
};

const makeFake = (thumb) => ({
  title: thumb ? thumb.t : "（沒有更多不重複的縮圖了）",
  channel: pick(CHANNELS),
  views: formatViews(randomViews()),
  age: pick(AGES),
  duration: randomDuration(),
  thumb: thumb
    ? `<img src="${thumb.s}" alt="">`
    : `<div style="width:100%;height:100%;background:${pick(BLANKS)}"></div>`,
});

const thumbHtml = (f) => f
  ? `<div class="thumb">${f.thumb}<span class="dur">${f.duration}</span></div>`
  : `<div class="thumb"><img class="own" src="${MINE.thumb}" alt=""><span class="dur">${MINE.duration}</span></div>`;

const cardHtml = (f) => `
  <article class="card">
    ${thumbHtml(f)}
    <div class="meta">
      <div class="avatar" data-own-avatar="${f ? 0 : 1}"
           style="background:${f ? avatarBg(f.channel) : "#3ea6ff"};color:${f ? "#fff" : "#0f0f0f"}">${
             f ? f.channel.slice(0, 1) : MINE.letter}</div>
      <div>
        <div class="title">${f ? f.title : MINE.title}</div>
        <div class="sub">${f ? f.channel : MINE.channel}</div>
        <div class="sub">${f ? `${f.views}・${f.age}` : `${MINE.views}・${MINE.uploaded}`}</div>
      </div>
    </div>
  </article>`;

// 使用者那部每次落在隨機的一格：縮圖在角落、在中間、被別人夾住看起來差很多
const grid = document.getElementById("grid");
const render = () => {
  const cards = dealThumbs(17).map(makeFake);
  cards.splice(rand(cards.length + 1), 0, null);
  grid.innerHTML = cards.map(cardHtml).join("");
  // 頻道頭像：選了「用這張圖」只換自己那格，假影片維持首字
  if ("__AVATAR_MODE__" === "image") {
    const own = grid.querySelector("[data-own-avatar='1']");
    if (own) own.innerHTML = `<img src="__AVATAR__" alt="">`;
  }
};
render();

document.getElementById("shuffleBtn").onclick = render;
document.getElementById("themeBtn").onclick = () => {
  const root = document.documentElement;
  root.dataset.theme = root.dataset.theme === "dark" ? "light" : "dark";
};
</script>
</body>
</html>
""";
}
