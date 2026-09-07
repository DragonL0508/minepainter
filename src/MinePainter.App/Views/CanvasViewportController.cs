using Avalonia;
using MinePainter.App.Rendering;

namespace MinePainter.App.Views;

/// <summary>擁有目前與目標視口，統一處理初始置中及動畫內插。</summary>
internal sealed class CanvasViewportController
{
    internal ViewportTransform Current = ViewportTransform.Identity;
    internal ViewportTransform Target = ViewportTransform.Identity;
    internal bool Initialized;
    internal ViewportTransform? AutoFit;
    internal Size AutoFitSize;
    private double _lastAnimSeconds;

    internal bool Step(double now)
    {
        var dt = Math.Clamp(now - _lastAnimSeconds, 0, 0.1);
        _lastAnimSeconds = now;

        var dScale = Target.Scale - Current.Scale;
        var dx = Target.OffsetX - Current.OffsetX;
        var dy = Target.OffsetY - Current.OffsetY;

        // 已經夠接近就直接吸附，避免無止盡的微小更新
        if (Math.Abs(dScale) < Current.Scale * 0.0005 && Math.Abs(dx) < 0.05 && Math.Abs(dy) < 0.05)
        {
            if (dScale != 0 || dx != 0 || dy != 0)
            {
                Current = Target;
                return true;
            }
            return false;
        }

        var t = 1 - Math.Exp(-dt / 0.07);
        Current = new ViewportTransform(
            Current.Scale + dScale * t,
            Current.OffsetX + dx * t,
            Current.OffsetY + dy * t);
        return true;
    }

    internal bool EnsureFit(int documentWidth, int documentHeight, Size size)
    {
        if (!Initialized && size.Width > 0 && size.Height > 0)
        {
            var fit = ViewportTransform.Fit(documentWidth, documentHeight, size.Width, size.Height);
            Current = fit;
            Target = fit;
            Initialized = true;
            AutoFit = fit;
            AutoFitSize = size;
            return true;
        }
        else if (AutoFit is { } autoFit && Current == autoFit && Target == autoFit &&
                 size != AutoFitSize && size.Width > 0 && size.Height > 0)
        {
            // 控制項大小變了（視窗最大化／拉大），視口還停在舊的 fit 上：重新置中，不走動畫
            var fit = ViewportTransform.Fit(documentWidth, documentHeight, size.Width, size.Height);
            Current = fit;
            Target = fit;
            AutoFit = fit;
            AutoFitSize = size;
            return true;
        }
        return false;
    }
}
