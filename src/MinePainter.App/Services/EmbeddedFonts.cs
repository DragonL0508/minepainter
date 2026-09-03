using Avalonia.Platform;
using MinePainter.Core.Vectors;

namespace MinePainter.App.Services;

/// <summary>
/// 內嵌字型（Noto Sans TC，OFL）。英文版 Windows 的中日韓字型屬 Features on Demand，
/// 沒裝中文語言支援的機器上系統一支 CJK 字型都沒有，UI 與文字工具會整片豆腐框；
/// 帶一支進來當最後一關的後備，跟系統語系無關。
///
/// 兩條路都要接：Avalonia 的 UI 文字走 <see cref="Avalonia.Media.Fonts.FontManagerOptions"/>
/// 的 avares 位址（Program.cs），Core 的畫布排版走 <see cref="BundledFont"/>（Skia 那邊看不到
/// avares，得另外把位元組餵進去）。
/// </summary>
public static class EmbeddedFonts
{
    /// <summary>字型檔內的家族名（字型下拉也以這個名字顯示）。</summary>
    public const string FamilyName = "Noto Sans TC";

    /// <summary>
    /// Avalonia 用的家族位址；系統沒安裝這支，只能靠這個位址取到。
    /// fonts: scheme = 走 <see cref="Register"/> 掛進去的記憶體字型集合（見 <see cref="MemoryFontCollection"/>），
    /// 不是 Avalonia 自己用串流建的那份。
    /// </summary>
    public const string FamilyUri = "fonts:MinePainter#Noto Sans TC";

    private const string AssetUri = "avares://MinePainter.App/Assets/Fonts/NotoSansTC-Regular.otf";

    /// <summary>字型集合的 key（FontManager 規定 fonts: scheme）與字型檔所在資料夾。</summary>
    private static readonly Uri CollectionKey = new("fonts:MinePainter");
    private static readonly Uri FolderUri = new("avares://MinePainter.App/Assets/Fonts");

    /// <summary>掛進 FontManager 的記憶體字型集合（診斷用；掛失敗為 null）。</summary>
    public static MemoryFontCollection? Collection { get; private set; }

    /// <summary>診斷：掛字型集合時的例外（沒有就 null）。</summary>
    public static string? RegisterError { get; private set; }

    /// <summary>把內嵌字型交給 Core（Skia 排版用）並以記憶體字型集合掛進 Avalonia。開視窗前呼叫一次。</summary>
    public static void Register()
    {
        try
        {
            using var stream = AssetLoader.Open(new Uri(AssetUri));
            BundledFont.Register(stream);
        }
        catch
        {
            // 資源不見了也不該擋開機：有系統中文字型的機器照樣正常
        }

        if (Environment.GetEnvironmentVariable("MINEPAINTER_DEBUG_STREAMFONT") == "1") return; // 效能對照：用 Avalonia 內建的 stream 版
        try
        {
            Collection = new MemoryFontCollection(CollectionKey, FolderUri);
            Avalonia.Media.FontManager.Current.AddFontCollection(Collection);
        }
        catch (Exception ex)
        {
            // 掛不上就退回 Avalonia 自己的 EmbeddedFontCollection（功能一樣，只是慢）
            RegisterError = ex.ToString();
        }
    }
}

/// <summary>
/// 內嵌字型的字型集合，功能同 Avalonia 內建的 EmbeddedFontCollection，差別只在把字型檔以
/// 「不可 seek 的串流」交給 SkiaSharp：可 seek 的 managed Stream 會被包成 SKManagedStream，
/// DirectWrite 之後每讀一段字形資料都要回呼 managed 端，每個新字形光柵化 0.2–0.8ms、量字寬也慢，
/// UI 一有中文就整幀 15ms 起跳；不可 seek 的串流 SkiaSharp 會整份複製進原生記憶體，之後全是原生存取。
/// 啟動時以 <see cref="Register"/> 掛進 FontManager，FontFallbacks 指向同一個 key 就會用到它。
/// </summary>
public sealed class MemoryFontCollection : Avalonia.Media.Fonts.EmbeddedFontCollection
{
    private readonly Uri _key;
    private readonly Uri _source;

    public MemoryFontCollection(Uri key, Uri source) : base(key, source)
    {
        _key = key;
        _source = source;
    }

    /// <summary>診斷：這份字面是不是本集合建的。</summary>
    public bool Owns(Avalonia.Media.IGlyphTypeface glyphTypeface) =>
        _glyphTypefaceCache.Values.Any(d => d.Values.Any(g => ReferenceEquals(g, glyphTypeface)));

    /// <summary>診斷：Initialize 走了哪條路。</summary>
    public string Diagnostics { get; private set; } = "not initialized";

    /// <summary>
    /// 同 EmbeddedFontCollection.Initialize，只差串流包成不可 seek。
    /// （基底的家族清單是 private，這裡填不進去；FontManager 找字面只看 _glyphTypefaceCache，夠用。）
    /// </summary>
    public override void Initialize(Avalonia.Platform.IFontManagerImpl fontManager)
    {
        // 最快的一條：直接拿 Core 已經從 SKData（原生記憶體）建好的 SKTypeface，反射建 Avalonia.Skia 的
        // GlyphTypefaceImpl(SKTypeface, FontSimulations)。SkiaSharp 從任何 managed Stream 建的字面都會走
        // DirectWrite → managed 回呼，讀字形輪廓（算 bounds）一個字 1.5ms；記憶體版 16µs。
        if (BundledFont.Typeface is { } memoryTypeface && TryWrapSkTypeface(fontManager, memoryTypeface, Avalonia.Media.FontSimulations.None) is { } wrapped)
        {
            Diagnostics = "reflected GlyphTypefaceImpl over BundledFont.Typeface";
            Add(wrapped);
            // 粗體也先建好（假粗體）：不然 Avalonia 要粗體時會自己合成一份，走的又是慢路
            if (TryWrapSkTypeface(fontManager, memoryTypeface, Avalonia.Media.FontSimulations.Bold) is { } bold) Add(bold);
            return;
        }

        // IFontManagerImpl.TryCreateGlyphTypeface(Stream, FontSimulations, out IGlyphTypeface) 是 internal，只能反射叫
        var create = fontManager.GetType().GetMethod("TryCreateGlyphTypeface",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic,
            null, [typeof(Stream), typeof(Avalonia.Media.FontSimulations), typeof(Avalonia.Media.IGlyphTypeface).MakeByRefType()], null)
            ?? fontManager.GetType().GetInterfaces()
                .Select(i => i.GetMethod("TryCreateGlyphTypeface", [typeof(Stream), typeof(Avalonia.Media.FontSimulations), typeof(Avalonia.Media.IGlyphTypeface).MakeByRefType()]))
                .FirstOrDefault(m => m != null);
        if (create == null)
        {
            Diagnostics = "reflection failed, base.Initialize";
            base.Initialize(fontManager); // 版本不合就退回內建（慢但正確）
            return;
        }
        Diagnostics = "memory path";

        foreach (var asset in Avalonia.Media.Fonts.FontFamilyLoader.LoadFontAssets(_source))
        {
            using var raw = AssetLoader.Open(asset);
            using var stream = new ForwardOnlyStream(raw);
            var args = new object?[] { stream, Avalonia.Media.FontSimulations.None, null };
            if (create.Invoke(fontManager, args) is not true || args[2] is not Avalonia.Media.IGlyphTypeface glyphTypeface) continue;
            Add(glyphTypeface);
        }
    }

    private void Add(Avalonia.Media.IGlyphTypeface glyphTypeface)
    {
        var typefaces = _glyphTypefaceCache.GetOrAdd(glyphTypeface.FamilyName,
            _ => new System.Collections.Concurrent.ConcurrentDictionary<Avalonia.Media.Fonts.FontCollectionKey, Avalonia.Media.IGlyphTypeface?>());
        typefaces.TryAdd(new Avalonia.Media.Fonts.FontCollectionKey
        {
            Style = glyphTypeface.Style,
            Weight = glyphTypeface.Weight,
            Stretch = glyphTypeface.Stretch,
        }, glyphTypeface);
        Diagnostics += $" +{glyphTypeface.FamilyName}/{glyphTypeface.Weight}";
    }

    /// <summary>反射 new Avalonia.Skia.GlyphTypefaceImpl(SKTypeface, FontSimulations)；版本不合回 null。</summary>
    private static Avalonia.Media.IGlyphTypeface? TryWrapSkTypeface(Avalonia.Platform.IFontManagerImpl fontManager, SkiaSharp.SKTypeface typeface,
        Avalonia.Media.FontSimulations simulations)
    {
        try
        {
            var implType = fontManager.GetType().Assembly.GetType("Avalonia.Skia.GlyphTypefaceImpl");
            if (implType == null) return null;
            var ctor = implType.GetConstructors(System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic)
                .FirstOrDefault(c =>
                {
                    var ps = c.GetParameters();
                    return ps.Length == 2 && ps[0].ParameterType == typeof(SkiaSharp.SKTypeface) && ps[1].ParameterType == typeof(Avalonia.Media.FontSimulations);
                });
            return ctor?.Invoke([typeface, simulations]) as Avalonia.Media.IGlyphTypeface;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>CanSeek=false 的唯讀包裝：讓 SkiaSharp 走「整份讀進原生記憶體」那條路。</summary>
    private sealed class ForwardOnlyStream : Stream
    {
        private readonly Stream _inner;
        public ForwardOnlyStream(Stream inner) => _inner = inner;
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) => _inner.Read(buffer, offset, count);
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
