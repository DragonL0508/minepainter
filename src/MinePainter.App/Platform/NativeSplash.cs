using System.Diagnostics;
using System.Runtime.InteropServices;

namespace MinePainter.App.Platform;

/// <summary>
/// 啟動畫面（純 Win32，不依賴 Avalonia）：在 <c>Main</c> 第一行就從自己的執行緒秀出來，
/// 使用者點 exe 後一兩百毫秒內就看得到 icon；Avalonia 初始化與主視窗建構在它後面慢慢跑。
///
/// 為什麼不用 Avalonia 視窗：Avalonia 初始化本身就要 500ms 以上，等它好了再畫 splash 已經太晚。
/// 做法：WS_EX_LAYERED 視窗 + UpdateLayeredWindow 逐幀送 premultiplied BGRA，
/// icon 內嵌成 16×16 像素常數（像素圖，不需要任何圖片解碼器），光暈／陰影是解析式徑向漸層，
/// 字標用 GDI 畫一次白字黑底當 alpha 遮罩。所有動畫（進場、光暈呼吸、退場）都在這裡逐幀算。
///
/// 流程：Show() → 進場 → 呼吸等待 → 主視窗建好後 FadeOutAndWaitAsync() → 至少顯示 MinShow → 退場 →
/// 視窗銷毀 → Task 完成 → 主視窗才 Show。
/// </summary>
internal static unsafe class NativeSplash
{
    // 節奏（毫秒）：與 Styles/Animations.axaml 同一套「快但不急」的語言
    private const int MinShowMs = 1050;
    private const int IconInMs = 420;
    private const int GlowInMs = 700;
    private const int NameDelayMs = 160;
    private const int NameInMs = 460;
    private const int BreathMs = 1600;
    private const int OutMs = 260;

    // 版面（DIP，乘上 DPI 倍率後才是實際像素）
    private const int SizeDip = 300;
    private const double IconDip = 112;   // 16 格 × 7px
    private const double IconCenterYDip = 139;
    private const double GlowRadiusDip = 125;
    private const double NameCenterYDip = 213;
    private const double NameFontDip = 13;
    private const double NameSpacingDip = 2.4;

    /// <summary>
    /// Assets/icon.png 的 16×16 像素（ARGB，逐列）。icon 換圖時用 PIL 逐格取樣重新產生：
    /// <c>Image.open('icon.png').convert('RGBA')</c>，每 64px 一格取左上角像素，格式 0xAARRGGBB。
    /// </summary>
    private static readonly uint[] IconPixels =
    {
        0x00000000, 0x00000000, 0x00000000, 0x00000000, 0x00000000, 0x00000000, 0x00000000, 0xFFC09C2D, 0xFFDFBB45, 0xFFDFBA41, 0xFFD6B341, 0xFFD0AE39, 0xFFC8A32B, 0x00000000, 0x00000000, 0x00000000,
        0x00000000, 0x00000000, 0x00000000, 0x00000000, 0x00000000, 0x00000000, 0xFFD6B34A, 0xFFFFE671, 0xFFF2D261, 0xFFF4D66C, 0xFFFEE89A, 0xFFF8DD7F, 0xFFE9C854, 0xFFCFAB32, 0xFFAD8C22, 0x00000000,
        0x00000000, 0x00000000, 0x00000000, 0x00000000, 0x00000000, 0xFFB9952B, 0xFFEDCD5F, 0xFFFFFF86, 0xFFFFFDA3, 0xFFFFFAB2, 0xFFFFF6B3, 0xFFFEE58D, 0xFFF7DC7E, 0xFFE1BD3D, 0xFFC39F1F, 0x00000000,
        0x00000000, 0x00000000, 0x00000000, 0x00000000, 0xFFBB9421, 0xFFE3C360, 0xFFF2D56D, 0xFFFFFFB0, 0xFFFFF8BF, 0xFFFFFDB8, 0xFFFFFFB8, 0xFFFEEAA2, 0xFFF9E088, 0xFFE6C85E, 0xFFD3AE2B, 0xFFB28E18,
        0x00000000, 0x00000000, 0x00000000, 0xFFB7931E, 0xFFDCB738, 0xFFEDD88B, 0xFFF8E39D, 0xFFFEEEB4, 0xFFFFF0BB, 0xFFFFFFC2, 0xFFFFF3AE, 0xFFFDEAA7, 0xFFF7DE86, 0xFFE9CF6F, 0xFFDEBB43, 0xFFB59018,
        0x00000000, 0x00000000, 0xFFCEB04A, 0xFFDAB840, 0xFFDEC46A, 0xFFF2D884, 0xFFE5CC77, 0xFFF6DE87, 0xFFFFEDAB, 0xFFFFFFC5, 0xFFFFF6BA, 0xFFFCECAD, 0xFFF9E6A3, 0xFFECCE65, 0xFFDEC055, 0xFFAC8618,
        0x00000000, 0xFFEACC61, 0xFFE6C75B, 0xFFE2C867, 0xFFD7B644, 0xFFCAAB3B, 0xFFDBBD58, 0xFFE1C15E, 0xFFE5C049, 0xFFFFE99B, 0xFFFFFFC8, 0xFFFFF3B0, 0xFFFFF7AE, 0xFFEFD270, 0xFFE1C14D, 0xFFA68218,
        0xFFBC9B2B, 0xFFFFEF8A, 0xFFF5E096, 0xFFD3B340, 0xFFE3C55B, 0xFFFFFFCF, 0xFFFFFFD2, 0xFFF9E8AB, 0xFFCDA72C, 0xFFDBB847, 0xFFFFF9C1, 0xFFFFFFCA, 0xFFFFFFC1, 0xFFFFEA89, 0xFFE6C03D, 0xFF916D10,
        0xFFC7A534, 0xFFFFF3A2, 0xFFFFF2A5, 0xFFCDAA33, 0xFFFFFBC0, 0xFFFFFFD4, 0xFFF3DF98, 0xFFD3B345, 0xFFD4AF28, 0xFFB28C1C, 0xFFFDEDB4, 0xFFFFFFC9, 0xFFFFFFC8, 0xFFFFF29E, 0xFFEAC642, 0xFF9E7912,
        0xFFD9B73F, 0xFFF9DE81, 0xFFFFF086, 0xFFC6A32B, 0xFFFFFCAB, 0xFFDDC571, 0xFFCEAA2B, 0xFFD2AD29, 0xFFC19B1B, 0xFF98730F, 0xFFF1D77B, 0xFFFFF9BD, 0xFFFFF0A4, 0xFFFFECA4, 0xFFE3BB38, 0xFF95700E,
        0xFFD8B63F, 0xFFDEC055, 0xFFFFEB88, 0xFFD0AB2F, 0xFFD9B640, 0xFFDCB52E, 0xFFE0B72B, 0xFFDAB229, 0xFFA07B0F, 0xFF9B7410, 0xFFF5D667, 0xFFEFD786, 0xFFECD88B, 0xFFFFF181, 0xFFC29926, 0x00000000,
        0xFFBB9824, 0xFFE3BF3F, 0xFFFBE390, 0xFFD6B137, 0xFFAE881A, 0xFFD3AF31, 0xFFD5AE27, 0xFFC39E18, 0xFFA77F12, 0xFFE0BA38, 0xFFF2D260, 0xFFE4CB6E, 0xFFE4C765, 0xFFF4CF4C, 0xFFA47D17, 0x00000000,
        0xFF997511, 0xFFD5AD2B, 0xFFE7CD70, 0xFFF7E29B, 0xFFBFA03B, 0xFFB4901E, 0xFFB38D1A, 0xFFC8A223, 0xFFE8C548, 0xFFF0CD55, 0xFFEAD071, 0xFFD4B856, 0xFFE7C44A, 0xFFBD9729, 0xFF946F11, 0x00000000,
        0x00000000, 0xFF9A7410, 0xFFD6B133, 0xFFE7CE73, 0xFFFBEBB0, 0xFFF8E4A0, 0xFFFDE89D, 0xFFEBCD63, 0xFFE5C963, 0xFFE3C868, 0xFFDCC369, 0xFFDDBA3F, 0xFFBB9728, 0x00000000, 0x00000000, 0x00000000,
        0x00000000, 0x00000000, 0x00000000, 0xFFB0902A, 0xFFEED57B, 0xFFF9E6A3, 0xFFFFFFBF, 0xFFFFFFAD, 0xFFFFF09E, 0xFFE5C552, 0xFFD7B12F, 0xFFA17B12, 0x00000000, 0x00000000, 0x00000000, 0x00000000,
        0x00000000, 0x00000000, 0x00000000, 0x00000000, 0xFF997514, 0xFFBE9C2F, 0xFFE8CE75, 0xFFFEDF77, 0xFFD6B23E, 0xFFAD881A, 0x15AA8624, 0x159E8624, 0x00000000, 0x00000000, 0x00000000, 0x00000000
    };

    private static Thread? _thread;
    private static int _state; // 0 = 顯示中, 1 = 要求退場, 2 = 立刻銷毀
    private static int _holdMs = MinShowMs;
    private static readonly TaskCompletionSource _closed = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private static readonly TaskCompletionSource _fadeStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private static WndProcDelegate? _wndProc; // 防止 delegate 被 GC

    /// <summary>在 Main 第一行呼叫。非 Windows 平台什麼都不做。</summary>
    public static void Show()
    {
        if (!OperatingSystem.IsWindows())
        {
            _fadeStarted.TrySetResult();
            _closed.TrySetResult();
            return;
        }
        // 開發驗證用：MINEPAINTER_DEBUG_SPLASH_HOLD=<毫秒> 讓啟動畫面停久一點（截圖用）
        if (int.TryParse(Environment.GetEnvironmentVariable("MINEPAINTER_DEBUG_SPLASH_HOLD"), out var ms))
            _holdMs = ms;

        _thread = new Thread(Run) { IsBackground = true, Name = "Splash" };
        _thread.Start();
    }

    /// <summary>要求退場（會先等到至少顯示 MinShow 才真的開始淡出）。</summary>
    public static void RequestFadeOut() => Interlocked.CompareExchange(ref _state, 1, 0);

    /// <summary>退場動畫開始淡出的那一刻完成（主視窗在此時開始 Show，剛好在淡出結束時接上）。</summary>
    public static Task FadeOutStarted => _fadeStarted.Task;

    /// <summary>視窗真正銷毀後完成。</summary>
    public static Task Closed => _closed.Task;

    /// <summary>立刻銷毀（主視窗建構失敗時用，不播退場）。</summary>
    public static void Kill() => Interlocked.Exchange(ref _state, 2);

    // ───────────────────────── 執行緒主體 ─────────────────────────

    private static void Run()
    {
        try { RunCore(); }
        catch { /* 啟動畫面壞了不能拖累 app */ }
        finally
        {
            _fadeStarted.TrySetResult();
            _closed.TrySetResult();
        }
    }

    private static void RunCore()
    {
        var hInstance = GetModuleHandleW(null);
        _wndProc = WndProc;
        var className = "MinePainterSplash";
        fixed (char* pClass = className)
        {
            var wc = new WNDCLASSEXW
            {
                cbSize = (uint)sizeof(WNDCLASSEXW),
                lpfnWndProc = Marshal.GetFunctionPointerForDelegate(_wndProc),
                hInstance = hInstance,
                lpszClassName = pClass,
            };
            if (RegisterClassExW(&wc) == 0) return;
        }

        // 主螢幕與 DPI（manifest 是 PerMonitorV2，拿到的都是實際像素）
        var monitor = MonitorFromPoint(new POINT { x = 0, y = 0 }, MONITOR_DEFAULTTOPRIMARY);
        var mi = new MONITORINFO { cbSize = (uint)sizeof(MONITORINFO) };
        GetMonitorInfoW(monitor, &mi);
        uint dpiX = 96, dpiY = 96;
        try { GetDpiForMonitor(monitor, 0, &dpiX, &dpiY); } catch { }
        var s = dpiX / 96.0;
        var size = (int)Math.Round(SizeDip * s);

        var x = mi.rcMonitor.left + (mi.rcMonitor.right - mi.rcMonitor.left - size) / 2;
        var y = mi.rcMonitor.top + (mi.rcMonitor.bottom - mi.rcMonitor.top - size) / 2;
        // 開發驗證用：MINEPAINTER_DEBUG_OFFSCREEN=1 時啟動畫面也擺到主螢幕右側之外
        if (Environment.GetEnvironmentVariable("MINEPAINTER_DEBUG_OFFSCREEN") == "1")
        {
            x = mi.rcMonitor.right + 40;
            y = mi.rcMonitor.top + 40;
        }

        var hwnd = CreateWindowExW(
            WS_EX_LAYERED | WS_EX_TOPMOST | WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE | WS_EX_TRANSPARENT,
            className, "", WS_POPUP, x, y, size, size, 0, 0, hInstance, null);
        if (hwnd == 0) return;

        var screenDc = GetDC(0);
        var memDc = CreateCompatibleDC(screenDc);
        uint* bits;
        var bmi = new BITMAPINFOHEADER
        {
            biSize = (uint)sizeof(BITMAPINFOHEADER),
            biWidth = size,
            biHeight = -size, // top-down
            biPlanes = 1,
            biBitCount = 32,
        };
        var dib = CreateDIBSection(screenDc, &bmi, 0, (void**)&bits, 0, 0);
        var oldBmp = SelectObject(memDc, dib);

        var text = RenderTextMask(screenDc, s);
        var frame = new uint[size * size];
        var clock = Stopwatch.StartNew();
        long outStart = -1;
        var shown = false;

        try
        {
            while (true)
            {
                MSG msg;
                while (PeekMessageW(&msg, 0, 0, 0, PM_REMOVE))
                {
                    if (msg.message == WM_QUIT) return;
                    TranslateMessage(&msg);
                    DispatchMessageW(&msg);
                }

                var state = Volatile.Read(ref _state);
                if (state == 2) return;
                var t = clock.ElapsedMilliseconds;
                if (state == 1 && outStart < 0 && t >= _holdMs)
                {
                    outStart = t;
                    _fadeStarted.TrySetResult();
                }
                if (outStart >= 0 && t - outStart >= OutMs) return;

                RenderFrame(frame, size, s, t, outStart, text);
                fixed (uint* src = frame)
                    Buffer.MemoryCopy(src, bits, (long)size * size * 4, (long)size * size * 4);

                var ptDst = new POINT { x = x, y = y };
                var sz = new SIZE { cx = size, cy = size };
                var ptSrc = new POINT();
                var blend = new BLENDFUNCTION { BlendOp = AC_SRC_OVER, SourceConstantAlpha = 255, AlphaFormat = AC_SRC_ALPHA };
                UpdateLayeredWindow(hwnd, 0, &ptDst, &sz, memDc, &ptSrc, 0, &blend, ULW_ALPHA);

                if (!shown)
                {
                    ShowWindow(hwnd, SW_SHOWNOACTIVATE);
                    shown = true;
                    clock.Restart(); // 進場動畫從真正看得到的那一刻起算
                }

                Thread.Sleep(8);
            }
        }
        finally
        {
            DestroyWindow(hwnd);
            SelectObject(memDc, oldBmp);
            DeleteObject(dib);
            DeleteDC(memDc);
            ReleaseDC(0, screenDc);
        }
    }

    private static nint WndProc(nint hwnd, uint msg, nuint wParam, nint lParam) =>
        DefWindowProcW(hwnd, msg, wParam, lParam);

    // ───────────────────────── 每幀繪製 ─────────────────────────

    private sealed class TextMask
    {
        public int Width, Height;
        public byte[] Alpha = [];   // 字本體
        public byte[] Shadow = [];  // 模糊過的字（陰影用）
    }

    private static void RenderFrame(uint[] frame, int size, double s, long t, long outStart, TextMask text)
    {
        // 進場
        var iconP = EaseOutCubic(Clamp01(t / (double)IconInMs));
        var glowP = EaseOutCubic(Clamp01(t / (double)GlowInMs));
        var nameP = EaseOutCubic(Clamp01((t - NameDelayMs) / (double)NameInMs));

        var iconOpacity = iconP;
        var iconScale = 0.86 + 0.14 * iconP;
        var glowOpacity = glowP;
        var glowScale = 0.7 + 0.3 * glowP;
        var nameOpacity = nameP;
        var nameOffsetY = 8 * (1 - nameP);

        // 光暈呼吸：進場完後 1.6 秒一趟來回（sine in-out）
        if (t > GlowInMs)
        {
            var phase = ((t - GlowInMs) % (2 * BreathMs)) / (double)BreathMs; // 0..2
            var k = phase <= 1 ? phase : 2 - phase;
            k = 0.5 - 0.5 * Math.Cos(Math.PI * k);
            glowOpacity *= 1 - 0.2 * k;
            glowScale *= 1 + 0.04 * k;
        }

        // 退場：淡出 + 微放大（像被主視窗接走）
        if (outStart >= 0)
        {
            var e = EaseInCubic(Clamp01((t - outStart) / (double)OutMs));
            iconOpacity *= 1 - e;
            glowOpacity *= 1 - e;
            nameOpacity *= 1 - e;
            iconScale += (1.08 - iconScale) * e;
            glowScale += (1.15 - glowScale) * e;
        }

        Array.Clear(frame);
        var cx = size / 2.0;
        var cy = size / 2.0;

        // 1. 光暈（刻意很淡，只是讓 icon 不像貼在桌面上）：#1EFFD24A @0 → #0CFFC61A @0.45 → 透明 @1
        var glowR = GlowRadiusDip * s * glowScale;
        if (glowOpacity > 0.002)
        {
            var r0 = (int)Math.Max(0, cy - glowR - 1);
            var r1 = (int)Math.Min(size - 1, cy + glowR + 1);
            for (var py = r0; py <= r1; py++)
            {
                var dy = py + 0.5 - cy;
                for (var px = 0; px < size; px++)
                {
                    var dx = px + 0.5 - cx;
                    var d = Math.Sqrt(dx * dx + dy * dy) / glowR;
                    if (d >= 1) continue;
                    double a, gg, bb;
                    if (d < 0.45)
                    {
                        var k = d / 0.45;
                        a = Lerp(0x1E, 0x0C, k); gg = Lerp(0xD2, 0xC6, k); bb = Lerp(0x4A, 0x1A, k);
                    }
                    else
                    {
                        var k = (d - 0.45) / 0.55;
                        a = Lerp(0x0C, 0, k); gg = 0xC6; bb = 0x1A;
                    }
                    a *= glowOpacity / 255.0;
                    frame[py * size + px] = Premul(0xFF, gg, bb, a);
                }
            }
        }

        // 2. icon 陰影：偏下 8dip、模糊 26dip、#66 黑
        var iconHalf = IconDip * s * iconScale / 2;
        var iconCy = IconCenterYDip * s;
        if (iconOpacity > 0.002)
        {
            var shR = iconHalf * 0.94;
            var blur = 26 * s / 2;
            var scy = iconCy + 8 * s;
            var r0 = (int)Math.Max(0, scy - shR - blur);
            var r1 = (int)Math.Min(size - 1, scy + shR + blur);
            var c0 = (int)Math.Max(0, cx - shR - blur);
            var c1 = (int)Math.Min(size - 1, cx + shR + blur);
            for (var py = r0; py <= r1; py++)
            {
                var dy = py + 0.5 - scy;
                for (var px = c0; px <= c1; px++)
                {
                    var dx = px + 0.5 - cx;
                    var d = Math.Sqrt(dx * dx + dy * dy);
                    var k = SmoothStep((shR + blur - d) / (2 * blur));
                    if (k <= 0) continue;
                    var a = 0x66 / 255.0 * k * iconOpacity;
                    Over(ref frame[py * size + px], Premul(0, 0, 0, a));
                }
            }
        }

        // 3. icon：16×16 最近鄰放大
        if (iconOpacity > 0.002)
        {
            var left = cx - iconHalf;
            var top = iconCy - iconHalf;
            var cell = iconHalf * 2 / 16;
            var r0 = (int)Math.Max(0, Math.Floor(top));
            var r1 = (int)Math.Min(size - 1, Math.Ceiling(top + iconHalf * 2));
            var c0 = (int)Math.Max(0, Math.Floor(left));
            var c1 = (int)Math.Min(size - 1, Math.Ceiling(left + iconHalf * 2));
            for (var py = r0; py <= r1; py++)
            {
                var gy = (int)Math.Floor((py + 0.5 - top) / cell);
                if (gy < 0 || gy > 15) continue;
                for (var px = c0; px <= c1; px++)
                {
                    var gx = (int)Math.Floor((px + 0.5 - left) / cell);
                    if (gx < 0 || gx > 15) continue;
                    var p = IconPixels[gy * 16 + gx];
                    var a = (p >> 24) / 255.0 * iconOpacity;
                    if (a <= 0) continue;
                    Over(ref frame[py * size + px], Premul((p >> 16) & 0xFF, (p >> 8) & 0xFF, p & 0xFF, a));
                }
            }
        }

        // 4. 字標：先陰影（#B0 黑，往下 1dip）再白字（#F2）
        if (nameOpacity > 0.002 && text.Width > 0)
        {
            var tx = (int)Math.Round(cx - text.Width / 2.0 + NameSpacingDip * s / 2); // 補回字距造成的右側空白
            var ty = (int)Math.Round(NameCenterYDip * s + nameOffsetY * s - text.Height / 2.0);
            BlitMask(frame, size, text.Shadow, text.Width, text.Height, tx, ty + (int)Math.Round(1 * s), 0, 0, 0, 0xB0 / 255.0 * nameOpacity);
            BlitMask(frame, size, text.Alpha, text.Width, text.Height, tx, ty, 255, 255, 255, 0xF2 / 255.0 * nameOpacity);
        }
    }

    private static void BlitMask(uint[] frame, int size, byte[] mask, int w, int h, int x0, int y0,
        double r, double g, double b, double opacity)
    {
        for (var my = 0; my < h; my++)
        {
            var py = y0 + my;
            if (py < 0 || py >= size) continue;
            for (var mx = 0; mx < w; mx++)
            {
                var px = x0 + mx;
                if (px < 0 || px >= size) continue;
                var m = mask[my * w + mx];
                if (m == 0) continue;
                Over(ref frame[py * size + px], Premul(r, g, b, m / 255.0 * opacity));
            }
        }
    }

    /// <summary>用 GDI 把字標畫成白字黑底，取亮度當 alpha；另做一份 box blur 當陰影。</summary>
    private static TextMask RenderTextMask(nint screenDc, double s)
    {
        var result = new TextMask();
        var pad = (int)Math.Round(10 * s);
        var dc = CreateCompatibleDC(screenDc);
        var font = CreateFontW(-(int)Math.Round(NameFontDip * s), 0, 0, 0, 600, 0, 0, 0, 1 /*DEFAULT_CHARSET*/,
            0, 0, 4 /*ANTIALIASED_QUALITY*/, 0, "Segoe UI");
        var oldFont = SelectObject(dc, font);
        SetTextCharacterExtra(dc, (int)Math.Round(NameSpacingDip * s));
        SetBkColor(dc, 0x000000);
        SetTextColor(dc, 0xFFFFFF);
        SetBkMode(dc, 1 /*TRANSPARENT*/);

        const string label = "MinePainter";
        SIZE ext;
        GetTextExtentPoint32W(dc, label, label.Length, &ext);
        var w = ext.cx + pad * 2;
        var h = ext.cy + pad * 2;

        uint* bits;
        var bmi = new BITMAPINFOHEADER
        {
            biSize = (uint)sizeof(BITMAPINFOHEADER), biWidth = w, biHeight = -h, biPlanes = 1, biBitCount = 32,
        };
        var dib = CreateDIBSection(dc, &bmi, 0, (void**)&bits, 0, 0);
        var oldBmp = SelectObject(dc, dib);
        new Span<uint>(bits, w * h).Clear();
        TextOutW(dc, pad, pad, label, label.Length);
        GdiFlush();

        var alpha = new byte[w * h];
        for (var i = 0; i < w * h; i++) alpha[i] = (byte)(bits[i] & 0xFF); // 白字黑底：任一通道即亮度
        result.Width = w;
        result.Height = h;
        result.Alpha = alpha;
        result.Shadow = BoxBlur(alpha, w, h, Math.Max(1, (int)Math.Round(3 * s)), passes: 3);

        SelectObject(dc, oldBmp);
        SelectObject(dc, oldFont);
        DeleteObject(dib);
        DeleteObject(font);
        DeleteDC(dc);
        return result;
    }

    private static byte[] BoxBlur(byte[] src, int w, int h, int r, int passes)
    {
        var a = (byte[])src.Clone();
        var b = new byte[w * h];
        var n = 2 * r + 1;
        for (var p = 0; p < passes; p++)
        {
            for (var y = 0; y < h; y++)
                for (var x = 0; x < w; x++)
                {
                    var sum = 0;
                    for (var k = -r; k <= r; k++) sum += a[y * w + Math.Clamp(x + k, 0, w - 1)];
                    b[y * w + x] = (byte)(sum / n);
                }
            for (var y = 0; y < h; y++)
                for (var x = 0; x < w; x++)
                {
                    var sum = 0;
                    for (var k = -r; k <= r; k++) sum += b[Math.Clamp(y + k, 0, h - 1) * w + x];
                    a[y * w + x] = (byte)(sum / n);
                }
        }
        return a;
    }

    // ───────────────────────── 小工具 ─────────────────────────

    private static double Clamp01(double v) => v < 0 ? 0 : v > 1 ? 1 : v;
    private static double Lerp(double a, double b, double k) => a + (b - a) * k;
    private static double EaseOutCubic(double t) => 1 - Math.Pow(1 - t, 3);
    private static double EaseInCubic(double t) => t * t * t;
    private static double SmoothStep(double t)
    {
        t = Clamp01(t);
        return t * t * (3 - 2 * t);
    }

    /// <summary>0–255 的 RGB 與 0–1 的 alpha → premultiplied BGRA。</summary>
    private static uint Premul(double r, double g, double b, double a)
    {
        var A = (uint)Math.Round(a * 255);
        var R = (uint)Math.Round(r * a);
        var G = (uint)Math.Round(g * a);
        var B = (uint)Math.Round(b * a);
        return (A << 24) | (R << 16) | (G << 8) | B;
    }

    /// <summary>premultiplied over：dst = src + dst × (1 − srcA)。</summary>
    private static void Over(ref uint dst, uint src)
    {
        var sa = src >> 24;
        if (sa == 0) return;
        if (sa == 255) { dst = src; return; }
        var inv = 255 - sa;
        var da = (dst >> 24) * inv / 255 + sa;
        var dr = ((dst >> 16) & 0xFF) * inv / 255 + ((src >> 16) & 0xFF);
        var dg = ((dst >> 8) & 0xFF) * inv / 255 + ((src >> 8) & 0xFF);
        var db = (dst & 0xFF) * inv / 255 + (src & 0xFF);
        dst = (Math.Min(da, 255u) << 24) | (Math.Min(dr, 255u) << 16) | (Math.Min(dg, 255u) << 8) | Math.Min(db, 255u);
    }

    // ───────────────────────── Win32 ─────────────────────────

    private const uint WS_POPUP = 0x80000000;
    private const uint WS_EX_TOPMOST = 0x00000008;
    private const uint WS_EX_TRANSPARENT = 0x00000020;
    private const uint WS_EX_TOOLWINDOW = 0x00000080;
    private const uint WS_EX_LAYERED = 0x00080000;
    private const uint WS_EX_NOACTIVATE = 0x08000000;
    private const int SW_SHOWNOACTIVATE = 4;
    private const uint PM_REMOVE = 1;
    private const uint WM_QUIT = 0x0012;
    private const uint ULW_ALPHA = 2;
    private const byte AC_SRC_OVER = 0;
    private const byte AC_SRC_ALPHA = 1;
    private const uint MONITOR_DEFAULTTOPRIMARY = 1;

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    private delegate nint WndProcDelegate(nint hwnd, uint msg, nuint wParam, nint lParam);

    [StructLayout(LayoutKind.Sequential)]
    private struct WNDCLASSEXW
    {
        public uint cbSize, style;
        public nint lpfnWndProc;
        public int cbClsExtra, cbWndExtra;
        public nint hInstance, hIcon, hCursor, hbrBackground;
        public char* lpszMenuName, lpszClassName;
        public nint hIconSm;
    }

    [StructLayout(LayoutKind.Sequential)] private struct POINT { public int x, y; }
    [StructLayout(LayoutKind.Sequential)] private struct SIZE { public int cx, cy; }
    [StructLayout(LayoutKind.Sequential)] private struct RECT { public int left, top, right, bottom; }

    [StructLayout(LayoutKind.Sequential)]
    private struct MONITORINFO { public uint cbSize; public RECT rcMonitor, rcWork; public uint dwFlags; }

    [StructLayout(LayoutKind.Sequential)]
    private struct MSG { public nint hwnd; public uint message; public nuint wParam; public nint lParam; public uint time; public POINT pt; }

    [StructLayout(LayoutKind.Sequential)]
    private struct BLENDFUNCTION { public byte BlendOp, BlendFlags, SourceConstantAlpha, AlphaFormat; }

    [StructLayout(LayoutKind.Sequential)]
    private struct BITMAPINFOHEADER
    {
        public uint biSize; public int biWidth, biHeight; public ushort biPlanes, biBitCount;
        public uint biCompression, biSizeImage; public int biXPelsPerMeter, biYPelsPerMeter; public uint biClrUsed, biClrImportant;
    }

    [DllImport("kernel32", CharSet = CharSet.Unicode)] private static extern nint GetModuleHandleW(string? name);
    [DllImport("user32")] private static extern ushort RegisterClassExW(WNDCLASSEXW* wc);
    [DllImport("user32", CharSet = CharSet.Unicode)]
    private static extern nint CreateWindowExW(uint exStyle, string className, string title, uint style,
        int x, int y, int w, int h, nint parent, nint menu, nint instance, void* param);
    [DllImport("user32")] private static extern nint DefWindowProcW(nint hwnd, uint msg, nuint wParam, nint lParam);
    [DllImport("user32")] private static extern bool DestroyWindow(nint hwnd);
    [DllImport("user32")] private static extern bool ShowWindow(nint hwnd, int cmd);
    [DllImport("user32")] private static extern bool PeekMessageW(MSG* msg, nint hwnd, uint min, uint max, uint remove);
    [DllImport("user32")] private static extern bool TranslateMessage(MSG* msg);
    [DllImport("user32")] private static extern nint DispatchMessageW(MSG* msg);
    [DllImport("user32")] private static extern nint GetDC(nint hwnd);
    [DllImport("user32")] private static extern int ReleaseDC(nint hwnd, nint dc);
    [DllImport("user32")]
    private static extern bool UpdateLayeredWindow(nint hwnd, nint dcDst, POINT* ptDst, SIZE* size, nint dcSrc,
        POINT* ptSrc, uint key, BLENDFUNCTION* blend, uint flags);
    [DllImport("user32")] private static extern nint MonitorFromPoint(POINT pt, uint flags);
    [DllImport("user32")] private static extern bool GetMonitorInfoW(nint monitor, MONITORINFO* info);
    [DllImport("shcore")] private static extern int GetDpiForMonitor(nint monitor, int type, uint* dpiX, uint* dpiY);
    [DllImport("gdi32")] private static extern nint CreateCompatibleDC(nint dc);
    [DllImport("gdi32")] private static extern bool DeleteDC(nint dc);
    [DllImport("gdi32")] private static extern nint CreateDIBSection(nint dc, BITMAPINFOHEADER* bmi, uint usage, void** bits, nint section, uint offset);
    [DllImport("gdi32")] private static extern nint SelectObject(nint dc, nint obj);
    [DllImport("gdi32")] private static extern bool DeleteObject(nint obj);
    [DllImport("gdi32")] private static extern bool GdiFlush();
    [DllImport("gdi32", CharSet = CharSet.Unicode)]
    private static extern nint CreateFontW(int height, int width, int escapement, int orientation, int weight,
        uint italic, uint underline, uint strikeOut, uint charSet, uint outPrecision, uint clipPrecision,
        uint quality, uint pitchAndFamily, string face);
    [DllImport("gdi32")] private static extern int SetTextCharacterExtra(nint dc, int extra);
    [DllImport("gdi32")] private static extern uint SetBkColor(nint dc, uint color);
    [DllImport("gdi32")] private static extern uint SetTextColor(nint dc, uint color);
    [DllImport("gdi32")] private static extern int SetBkMode(nint dc, int mode);
    [DllImport("gdi32", CharSet = CharSet.Unicode)] private static extern bool GetTextExtentPoint32W(nint dc, string text, int len, SIZE* size);
    [DllImport("gdi32", CharSet = CharSet.Unicode)] private static extern bool TextOutW(nint dc, int x, int y, string text, int len);
}
