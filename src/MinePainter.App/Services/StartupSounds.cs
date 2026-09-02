using System.Reflection;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;

namespace MinePainter.App.Services;

/// <summary>
/// 啟動音效：三段短音分別在「啟動畫面出現」「載入完成、開始退場」「主視窗現身」時播。
/// 三段相隔不到一秒、每段約兩秒，所以走 NAudio 的混音器讓它們疊著播，而不是 PlaySound 那種後者打斷前者。
/// 音效以內嵌資源（WAV）附在 exe 裡，啟動畫面出現時 Avalonia 還沒初始化，不能走 avares。
/// 任何失敗（沒有音訊裝置、遠端桌面…）都靜默略過，音效不該擋住啟動。
/// </summary>
internal static class StartupSounds
{
    private static readonly object Gate = new();
    private static WaveOutEvent? _output;
    private static MixingSampleProvider? _mixer;
    private static bool _disabled;

    public static bool Enabled => AppSettings.Instance.StartupSounds;

    /// <summary>啟動畫面出現。</summary>
    public static void SplashShown() => Play("Sound_1.wav");

    /// <summary>載入完成，啟動畫面開始退場。</summary>
    public static void LoadingFinished() => Play("Sound_2.wav");

    /// <summary>主視窗現身。</summary>
    public static void MainWindowShown() => Play("Sound_3.wav");

    private static void Play(string name)
    {
        if (_disabled) return;
        try
        {
            if (!Enabled) return;
        }
        catch
        {
            return; // 設定檔壞掉也不該炸在音效上
        }

        ThreadPool.QueueUserWorkItem(_ =>
        {
            try
            {
                lock (Gate)
                {
                    if (_disabled) return;
                    if (_output == null)
                    {
                        _mixer = new MixingSampleProvider(WaveFormat.CreateIeeeFloatWaveFormat(44100, 2)) { ReadFully = true };
                        _output = new WaveOutEvent { DesiredLatency = 80 };
                        _output.Init(_mixer);
                        _output.Play();
                    }
                    var stream = Assembly.GetExecutingAssembly()
                        .GetManifestResourceStream("MinePainter.App.Assets.Sounds." + name);
                    if (stream == null) return;
                    var reader = new WaveFileReader(stream);
                    ISampleProvider sample = reader.ToSampleProvider();
                    if (sample.WaveFormat.Channels == 1) sample = new MonoToStereoSampleProvider(sample);
                    if (sample.WaveFormat.SampleRate != 44100) sample = new WdlResamplingSampleProvider(sample, 44100);
                    _mixer!.AddMixerInput(sample);
                }
            }
            catch
            {
                _disabled = true; // 沒有音訊裝置之類：之後全部略過
            }
        });
    }
}
