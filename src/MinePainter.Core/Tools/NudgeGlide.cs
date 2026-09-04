namespace MinePainter.Core.Tools;

/// <summary>
/// 方向鍵微調的節奏：按一下走一格，按住則等速滑行（由慢漸快）。
///
/// 不能直接跟著 OS 的按鍵重複走 —— 那是「延遲約 0.5 秒、之後每秒約 30 次」的離散事件，
/// 跟著它動必定一格一格跳。所以重複事件一律忽略（<see cref="Press"/> 回傳 false），
/// 位移改由畫面每一幀呼叫 <see cref="Step"/> 推進：小數累積在內部，只輸出整數像素
/// （像素內容不能次像素平移）。
///
/// 純狀態機、不碰文件，方便單元測試；UI 只負責把按鍵與幀時間餵進來。
/// </summary>
public sealed class NudgeGlide
{
    /// <summary>按住多久才開始滑行（秒）—— 之前是單次點按。</summary>
    public double HoldDelay { get; init; } = 0.16;

    /// <summary>由起步速度加速到全速要多久（秒）。</summary>
    public double RampSeconds { get; init; } = 0.7;

    /// <summary>起步速度（doc px/秒）。</summary>
    public double SlowSpeed { get; init; } = 60;

    /// <summary>全速（doc px/秒）。</summary>
    public double FastSpeed { get; init; } = 520;

    /// <summary>按住 Shift 的倍率。</summary>
    public double ShiftFactor { get; init; } = 3;

    /// <summary>Shift 現在有沒有按著（隨時可改：先按方向鍵、之後才按 Shift 也算）。</summary>
    public bool Shift { get; set; }

    /// <summary>
    /// 按著的方向 → 距離最後一次收到那個鍵的按下事件過了多久（秒）。
    /// OS 的按鍵重複會一直刷新它；超過 <see cref="LostKeyUpTimeout"/> 還沒動靜就是
    /// 那顆鍵的放開事件掉了（視窗被搶走、輸入被別的東西吃掉），當成已放開 ——
    /// 不然畫面上的東西會一直滑下去停不了。
    /// </summary>
    private readonly Dictionary<(int X, int Y), double> _held = new();

    /// <summary>多久沒有按鍵重複就視為放開（秒）。Windows 的重複延遲最長約 1 秒，留足餘裕。</summary>
    public double LostKeyUpTimeout { get; init; } = 1.5;
    private double _heldSeconds;
    private double _pendingX, _pendingY; // 單次按鍵的位移（補間送出）
    private double _glideX, _glideY;     // 滑行的小數累積

    /// <summary>沒有按著任何方向鍵，殘餘位移也送完了。</summary>
    public bool IsIdle => _held.Count == 0 && Math.Abs(_pendingX) < 1 && Math.Abs(_pendingY) < 1;

    /// <summary>有沒有按著方向鍵。</summary>
    public bool AnyHeld => _held.Count > 0;

    /// <summary>
    /// 方向鍵按下。<paramref name="step"/> 是這一下要走的格數（一般 1、Shift 10）。
    /// 回傳 false＝這是 OS 的按鍵重複（已經在滑行了），呼叫端不必再做別的事。
    /// </summary>
    public bool Press(int dirX, int dirY, int step)
    {
        if (dirX == 0 && dirY == 0) return false;
        var key = (dirX, dirY);
        if (_held.ContainsKey(key))
        {
            _held[key] = 0; // OS 的按鍵重複：只當成「還按著」的心跳
            return false;
        }
        _held[key] = 0;
        _heldSeconds = 0;
        _pendingX += dirX * step;
        _pendingY += dirY * step;
        return true;
    }

    /// <summary>方向鍵放開：不再滑行，沒送出的小數丟掉（放開就該停）。</summary>
    public void Release(int dirX, int dirY)
    {
        if (!_held.Remove((dirX, dirY))) return;
        if (_held.Count > 0) return;
        _heldSeconds = 0;
        _glideX = 0;
        _glideY = 0;
    }

    /// <summary>整組清空（失焦、換文件、目標消失）。</summary>
    public void Reset()
    {
        _held.Clear();
        _heldSeconds = 0;
        _pendingX = _pendingY = 0;
        _glideX = _glideY = 0;
    }

    /// <summary>推進一幀，回傳這一幀要走的整數像素（0 = 這幀不動）。</summary>
    public (int Dx, int Dy) Step(double dt)
    {
        DropLostKeys(dt);
        if (_held.Count > 0)
        {
            _heldSeconds += dt;
            var gliding = _heldSeconds - HoldDelay;
            if (gliding > 0)
            {
                var t = Math.Clamp(gliding / RampSeconds, 0, 1);
                var speed = (SlowSpeed + (FastSpeed - SlowSpeed) * t * t) * (Shift ? ShiftFactor : 1);
                var dirX = 0;
                var dirY = 0;
                foreach (var (x, y) in _held.Keys)
                {
                    dirX += x;
                    dirY += y;
                }
                _glideX += Math.Clamp(dirX, -1, 1) * speed * dt;
                _glideY += Math.Clamp(dirY, -1, 1) * speed * dt;
            }
        }

        return (Advance(ref _pendingX) + TakeWhole(ref _glideX),
                Advance(ref _pendingY) + TakeWhole(ref _glideY));
    }

    /// <summary>放開事件掉了的方向：當成已放開（見 <see cref="LostKeyUpTimeout"/>）。</summary>
    private void DropLostKeys(double dt)
    {
        if (_held.Count == 0) return;
        List<(int X, int Y)>? lost = null;
        foreach (var key in _held.Keys.ToList())
        {
            var since = _held[key] + dt;
            _held[key] = since;
            if (since > LostKeyUpTimeout) (lost ??= new()).Add(key);
        }
        if (lost == null) return;
        foreach (var key in lost) Release(key.X, key.Y);
    }

    /// <summary>單次按鍵的補間：每幀走剩餘的三成（至少 1px，否則永遠到不了）。</summary>
    private static int Advance(ref double remain)
    {
        if (Math.Abs(remain) < 1) return 0; // 不足一格：攢著
        var step = remain * 0.3;
        var pixels = Math.Abs(step) < 1 ? Math.Sign(remain) : (int)Math.Round(step);
        if (Math.Abs(pixels) > Math.Abs(remain)) pixels = (int)remain;
        remain -= pixels;
        return pixels;
    }

    /// <summary>滑行累積：整數部分送出去，小數留給下一幀。</summary>
    private static int TakeWhole(ref double accumulated)
    {
        var whole = (int)Math.Truncate(accumulated);
        accumulated -= whole;
        return whole;
    }
}
