using System.Runtime.InteropServices;
using SkiaSharp;

/// <summary>使用 Avalonia 隨附的 ANGLE 建立 EGL pbuffer，不建立視窗或觸碰桌面。</summary>
internal sealed class OffscreenGpu : IDisposable
{
    private const string Library = "av_libglesv2.dll";
    private IntPtr _display;
    private IntPtr _surface;
    private IntPtr _context;
    private GRGlInterface? _gl;
    private GRContext? _skia;
    private FinishDelegate? _finish;
    public GRContext Context => _skia ?? throw new ObjectDisposedException(nameof(OffscreenGpu));
    public string Renderer { get; private set; } = "";

    public OffscreenGpu()
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("ANGLE probe requires Windows");
        try
        {
            _display = eglGetDisplay(IntPtr.Zero);
            Require(_display != IntPtr.Zero && eglInitialize(_display, out _, out _) != 0, "initialize");
            Require(eglBindAPI(0x30A0) != 0, "bind GLES");
            int[] attributes = [0x3033, 1, 0x3040, 4, 0x3024, 8, 0x3023, 8, 0x3022, 8, 0x3021, 8, 0x3038];
            var configs = new IntPtr[1];
            Require(eglChooseConfig(_display, attributes, configs, 1, out var count) != 0 && count > 0, "choose config");
            _surface = eglCreatePbufferSurface(_display, configs[0], [0x3057, 1, 0x3056, 1, 0x3038]);
            _context = eglCreateContext(_display, configs[0], IntPtr.Zero, [0x3098, 2, 0x3038]);
            Require(_surface != IntPtr.Zero && _context != IntPtr.Zero, "create pbuffer/context");
            Require(eglMakeCurrent(_display, _surface, _surface, _context) != 0, "make current");
            _gl = GRGlInterface.CreateGles(eglGetProcAddress) ?? throw new InvalidOperationException("Skia GLES interface unavailable");
            _skia = GRContext.CreateGl(_gl) ?? throw new InvalidOperationException("Skia GPU context unavailable");
            _finish = Marshal.GetDelegateForFunctionPointer<FinishDelegate>(eglGetProcAddress("glFinish"));
            var getString = Marshal.GetDelegateForFunctionPointer<GetStringDelegate>(eglGetProcAddress("glGetString"));
            Renderer = Marshal.PtrToStringAnsi(getString(0x1F01)) ?? "unknown";
        }
        catch
        {
            Dispose();
            throw;
        }
    }

    public void Finish()
    {
        Context.Flush();
        _finish!();
    }

    private static void Require(bool success, string step)
    {
        if (!success) throw new InvalidOperationException($"EGL {step} failed: 0x{eglGetError():X}");
    }

    public void Dispose()
    {
        _skia?.Dispose();
        _skia = null;
        _gl?.Dispose();
        _gl = null;
        if (_display == IntPtr.Zero) return;
        eglMakeCurrent(_display, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);
        if (_context != IntPtr.Zero) eglDestroyContext(_display, _context);
        if (_surface != IntPtr.Zero) eglDestroySurface(_display, _surface);
        eglTerminate(_display);
        _context = _surface = _display = IntPtr.Zero;
    }

    [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate void FinishDelegate();
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate IntPtr GetStringDelegate(uint name);
    [DllImport(Library, EntryPoint = "EGL_GetDisplay")] private static extern IntPtr eglGetDisplay(IntPtr display);
    [DllImport(Library, EntryPoint = "EGL_Initialize")] private static extern int eglInitialize(IntPtr display, out int major, out int minor);
    [DllImport(Library, EntryPoint = "EGL_BindAPI")] private static extern int eglBindAPI(int api);
    [DllImport(Library, EntryPoint = "EGL_ChooseConfig")] private static extern int eglChooseConfig(IntPtr display, int[] attributes, [Out] IntPtr[] configs, int size, out int count);
    [DllImport(Library, EntryPoint = "EGL_CreatePbufferSurface")] private static extern IntPtr eglCreatePbufferSurface(IntPtr display, IntPtr config, int[] attributes);
    [DllImport(Library, EntryPoint = "EGL_CreateContext")] private static extern IntPtr eglCreateContext(IntPtr display, IntPtr config, IntPtr share, int[] attributes);
    [DllImport(Library, EntryPoint = "EGL_MakeCurrent")] private static extern int eglMakeCurrent(IntPtr display, IntPtr draw, IntPtr read, IntPtr context);
    [DllImport(Library, EntryPoint = "EGL_GetProcAddress", CharSet = CharSet.Ansi)] private static extern IntPtr eglGetProcAddress(string name);
    [DllImport(Library, EntryPoint = "EGL_GetError")] private static extern int eglGetError();
    [DllImport(Library, EntryPoint = "EGL_DestroyContext")] private static extern int eglDestroyContext(IntPtr display, IntPtr context);
    [DllImport(Library, EntryPoint = "EGL_DestroySurface")] private static extern int eglDestroySurface(IntPtr display, IntPtr surface);
    [DllImport(Library, EntryPoint = "EGL_Terminate")] private static extern int eglTerminate(IntPtr display);
}
