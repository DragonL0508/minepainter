using SkiaSharp;

namespace MinePainter.Core.AI;

/// <summary>
/// GrabCut（Rother et al. 2004）：使用者只給一個粗略範圍，演算法自己把範圍裡的主體和背景分開。
/// 顏色以前景／背景各一組高斯混合模型（GMM，各 5 個成分）描述，像素間以顏色差為平滑項，
/// 反覆「重新估 GMM → 圖割（min-cut）」直到收斂。這是 Photoshop／GIMP 前景選取的經典做法，
/// 不用模型、離線、無限次。
///
/// 輸入是 trimap：<see cref="Background"/>（確定背景）、<see cref="ProbableForeground"/>（使用者圈的範圍）、
/// 也可以給 <see cref="Foreground"/>（確定前景）。輸出 0／255 的二值遮罩。
/// 為了速度先把圖縮到 <see cref="MaxSide"/> 再算，呼叫端再用 <see cref="GuidedFilter"/> 以原圖精修放大。
/// </summary>
public static class GrabCut
{
    public const byte Background = 0;
    public const byte Foreground = 1;
    public const byte ProbableBackground = 2;
    public const byte ProbableForeground = 3;

    /// <summary>算的時候最長邊縮到這個尺寸（約 0.1 MP，圖割一次幾十毫秒）。</summary>
    public const int MaxSide = 320;

    private const int Components = 5;
    private const float Gamma = 50f;
    private const float Lambda = 9f * Gamma;

    /// <summary>
    /// 在來源尺寸上跑：先縮到 <see cref="MaxSide"/>、算、再把二值結果放大回來（最近鄰）。
    /// src 為 premul BGRA、trimap 與 src 同尺寸。回傳來源尺寸的 0／255 遮罩。
    /// 透明像素（alpha 0）一律當背景，不參與 GMM。
    /// </summary>
    public static byte[] Run(uint[] src, int width, int height, byte[] trimap, int iterations = 5,
        CancellationToken ct = default)
    {
        var scale = Math.Max(1f, Math.Max(width, height) / (float)MaxSide);
        var sw = Math.Max(1, (int)MathF.Round(width / scale));
        var sh = Math.Max(1, (int)MathF.Round(height / scale));

        // 縮小：顏色區塊平均、trimap 取「最保守」（區塊內有確定背景就是背景…）
        var color = new float[sw * sh * 3];
        var small = new byte[sw * sh];
        var alphaSum = new float[sw * sh];
        var cnt = new int[sw * sh];
        var hasBg = new bool[sw * sh]; var hasFg = new bool[sw * sh]; var hasPf = new bool[sw * sh];
        for (var y = 0; y < height; y++)
        {
            var sy = Math.Min(sh - 1, (int)(y / scale));
            for (var x = 0; x < width; x++)
            {
                var sx = Math.Min(sw - 1, (int)(x / scale));
                var i = sy * sw + sx;
                var p = src[y * width + x];
                var a = p >> 24;
                if (a > 0)
                {
                    var inv = 255f / a;
                    color[i * 3] += (p & 0xFF) * inv;          // B
                    color[i * 3 + 1] += ((p >> 8) & 0xFF) * inv;
                    color[i * 3 + 2] += ((p >> 16) & 0xFF) * inv;
                    alphaSum[i] += a;
                }
                cnt[i]++;
                switch (trimap[y * width + x])
                {
                    case Background: hasBg[i] = true; break;
                    case Foreground: hasFg[i] = true; break;
                    case ProbableForeground: hasPf[i] = true; break;
                }
            }
        }
        for (var i = 0; i < sw * sh; i++)
        {
            var c = Math.Max(1, cnt[i]);
            color[i * 3] /= c; color[i * 3 + 1] /= c; color[i * 3 + 2] /= c;
            var transparent = alphaSum[i] / c < 8f;
            small[i] = transparent || hasBg[i] && !hasFg[i] ? Background
                : hasFg[i] ? Foreground
                : hasPf[i] ? ProbableForeground
                : ProbableBackground;
        }

        var result = Segment(color, sw, sh, small, iterations, ct);

        if (sw == width && sh == height) return result;
        var outMask = new byte[width * height];
        for (var y = 0; y < height; y++)
        {
            var sy = Math.Min(sh - 1, (int)(y / scale));
            for (var x = 0; x < width; x++)
                outMask[y * width + x] = result[sy * sw + Math.Min(sw - 1, (int)(x / scale))];
        }
        return outMask;
    }

    /// <summary>直接在給定尺寸上跑（color 為每像素 3 個 float，0..255）。回傳 0／255 遮罩。</summary>
    public static byte[] Segment(float[] color, int w, int h, byte[] trimap, int iterations, CancellationToken ct = default)
    {
        var n = w * h;
        var label = (byte[])trimap.Clone();
        var mask = new byte[n];

        // 沒有任何可能前景就沒得算
        var anyFg = false; var anyBg = false;
        for (var i = 0; i < n; i++)
        {
            if (label[i] is Foreground or ProbableForeground) anyFg = true;
            else anyBg = true;
        }
        if (!anyFg || !anyBg)
        {
            for (var i = 0; i < n; i++) mask[i] = label[i] is Foreground or ProbableForeground ? (byte)255 : (byte)0;
            return mask;
        }

        var beta = ComputeBeta(color, w, h);
        var (leftW, upW, upLeftW, upRightW) = ComputeNeighborWeights(color, w, h, beta);

        var bgGmm = new Gmm(); var fgGmm = new Gmm();
        var comp = new byte[n];
        InitGmms(color, label, bgGmm, fgGmm, comp);

        var graph = new MaxFlow(n, n * 4);
        for (var iter = 0; iter < iterations; iter++)
        {
            ct.ThrowIfCancellationRequested();
            AssignComponents(color, label, bgGmm, fgGmm, comp);
            LearnGmms(color, label, comp, bgGmm, fgGmm);
            graph.Reset();
            BuildGraph(graph, color, w, h, label, bgGmm, fgGmm, leftW, upW, upLeftW, upRightW);
            graph.Compute();
            var changed = 0;
            for (var i = 0; i < n; i++)
            {
                if (label[i] is Background or Foreground) continue;
                var fg = graph.IsSource(i);
                var next = fg ? ProbableForeground : ProbableBackground;
                if (next != label[i]) changed++;
                label[i] = next;
            }
            if (changed == 0 && iter > 0) break;
        }

        for (var i = 0; i < n; i++) mask[i] = label[i] is Foreground or ProbableForeground ? (byte)255 : (byte)0;
        return mask;
    }

    // ---- 平滑項 ----

    private static float ComputeBeta(float[] c, int w, int h)
    {
        double sum = 0; long count = 0;
        for (var y = 0; y < h; y++)
        for (var x = 0; x < w; x++)
        {
            var i = y * w + x;
            if (x > 0) { sum += Dist2(c, i, i - 1); count++; }
            if (y > 0) { sum += Dist2(c, i, i - w); count++; }
            if (x > 0 && y > 0) { sum += Dist2(c, i, i - w - 1); count++; }
            if (x < w - 1 && y > 0) { sum += Dist2(c, i, i - w + 1); count++; }
        }
        if (count == 0 || sum <= 1e-9) return 0f;
        return (float)(1.0 / (2.0 * sum / count));
    }

    private static float Dist2(float[] c, int a, int b)
    {
        var d0 = c[a * 3] - c[b * 3]; var d1 = c[a * 3 + 1] - c[b * 3 + 1]; var d2 = c[a * 3 + 2] - c[b * 3 + 2];
        return d0 * d0 + d1 * d1 + d2 * d2;
    }

    private static (float[] Left, float[] Up, float[] UpLeft, float[] UpRight) ComputeNeighborWeights(
        float[] c, int w, int h, float beta)
    {
        var n = w * h;
        var left = new float[n]; var up = new float[n]; var upLeft = new float[n]; var upRight = new float[n];
        var diag = Gamma / MathF.Sqrt(2f);
        for (var y = 0; y < h; y++)
        for (var x = 0; x < w; x++)
        {
            var i = y * w + x;
            if (x > 0) left[i] = Gamma * MathF.Exp(-beta * Dist2(c, i, i - 1));
            if (y > 0) up[i] = Gamma * MathF.Exp(-beta * Dist2(c, i, i - w));
            if (x > 0 && y > 0) upLeft[i] = diag * MathF.Exp(-beta * Dist2(c, i, i - w - 1));
            if (x < w - 1 && y > 0) upRight[i] = diag * MathF.Exp(-beta * Dist2(c, i, i - w + 1));
        }
        return (left, up, upLeft, upRight);
    }

    // ---- GMM ----

    /// <summary>
    /// K 個 3 維全共變異數高斯，權重為樣本比例。數值全用 double、機率以 log 域算
    /// （log-sum-exp）：真實影像裡帶陰影的平面色塊，顏色排在一條線上、共變異數近乎奇異，
    /// float 直接算會溢位成無限大。共變異數對角一律加一點正則化，避免尖到誰都不像的成分。
    /// </summary>
    private sealed class Gmm
    {
        private const double Regularization = 0.5; // 灰階平方；約 0.7 個灰階的標準差

        public readonly double[] Weight = new double[Components];
        public readonly double[] Mean = new double[Components * 3];
        public readonly double[] InvCov = new double[Components * 9];
        public readonly double[] LogNorm = new double[Components]; // log(w) - 0.5·log(det)

        private readonly double[] _sum = new double[Components * 3];
        private readonly double[] _prod = new double[Components * 9];
        private readonly int[] _count = new int[Components];
        private int _total;

        public void BeginLearning()
        {
            Array.Clear(_sum); Array.Clear(_prod); Array.Clear(_count); _total = 0;
        }

        public void AddSample(int k, float[] c, int i)
        {
            double b = c[i * 3], g = c[i * 3 + 1], r = c[i * 3 + 2];
            _sum[k * 3] += b; _sum[k * 3 + 1] += g; _sum[k * 3 + 2] += r;
            _prod[k * 9] += b * b; _prod[k * 9 + 1] += b * g; _prod[k * 9 + 2] += b * r;
            _prod[k * 9 + 3] += g * b; _prod[k * 9 + 4] += g * g; _prod[k * 9 + 5] += g * r;
            _prod[k * 9 + 6] += r * b; _prod[k * 9 + 7] += r * g; _prod[k * 9 + 8] += r * r;
            _count[k]++; _total++;
        }

        public void EndLearning()
        {
            var cov = new double[9];
            for (var k = 0; k < Components; k++)
            {
                var nk = _count[k];
                if (nk == 0) { Weight[k] = 0; LogNorm[k] = double.NegativeInfinity; continue; }
                Weight[k] = (double)nk / _total;
                for (var d = 0; d < 3; d++) Mean[k * 3 + d] = _sum[k * 3 + d] / nk;
                for (var a = 0; a < 3; a++)
                for (var b = 0; b < 3; b++)
                    cov[a * 3 + b] = _prod[k * 9 + a * 3 + b] / nk - Mean[k * 3 + a] * Mean[k * 3 + b];
                cov[0] += Regularization; cov[4] += Regularization; cov[8] += Regularization;
                var det = Determinant(cov);
                if (det <= 1e-12)
                {
                    cov[0] += 1; cov[4] += 1; cov[8] += 1;
                    det = Determinant(cov);
                }
                Inverse(cov, InvCov, k * 9, det);
                LogNorm[k] = Math.Log(Weight[k]) - 0.5 * Math.Log(det);
            }
        }

        /// <summary>-log p(color)（各成分加權和，log-sum-exp）。</summary>
        public double NegLog(float[] c, int i)
        {
            Span<double> lp = stackalloc double[Components];
            var max = double.NegativeInfinity;
            for (var k = 0; k < Components; k++)
            {
                lp[k] = Weight[k] > 0 ? LogComponent(k, c, i) : double.NegativeInfinity;
                if (lp[k] > max) max = lp[k];
            }
            if (double.IsNegativeInfinity(max)) return 1e4;
            var sum = 0.0;
            for (var k = 0; k < Components; k++) if (!double.IsNegativeInfinity(lp[k])) sum += Math.Exp(lp[k] - max);
            return -(max + Math.Log(sum));
        }

        /// <summary>log(w_k · N_k(color))（沒有常數項）。</summary>
        private double LogComponent(int k, float[] c, int i)
        {
            var d0 = c[i * 3] - Mean[k * 3]; var d1 = c[i * 3 + 1] - Mean[k * 3 + 1]; var d2 = c[i * 3 + 2] - Mean[k * 3 + 2];
            var o = k * 9;
            var m = d0 * (d0 * InvCov[o] + d1 * InvCov[o + 3] + d2 * InvCov[o + 6])
                  + d1 * (d0 * InvCov[o + 1] + d1 * InvCov[o + 4] + d2 * InvCov[o + 7])
                  + d2 * (d0 * InvCov[o + 2] + d1 * InvCov[o + 5] + d2 * InvCov[o + 8]);
            return LogNorm[k] - 0.5 * m;
        }

        public int BestComponent(float[] c, int i)
        {
            var best = 0; var bestP = double.NegativeInfinity;
            for (var k = 0; k < Components; k++)
            {
                if (Weight[k] <= 0) continue;
                var p = LogComponent(k, c, i);
                if (p > bestP) { bestP = p; best = k; }
            }
            return best;
        }

        private static double Determinant(double[] m) =>
            m[0] * (m[4] * m[8] - m[5] * m[7])
            - m[1] * (m[3] * m[8] - m[5] * m[6])
            + m[2] * (m[3] * m[7] - m[4] * m[6]);

        private static void Inverse(double[] m, double[] inv, int io, double det)
        {
            var d = 1.0 / det;
            inv[io] = (m[4] * m[8] - m[5] * m[7]) * d;
            inv[io + 1] = -(m[1] * m[8] - m[2] * m[7]) * d;
            inv[io + 2] = (m[1] * m[5] - m[2] * m[4]) * d;
            inv[io + 3] = -(m[3] * m[8] - m[5] * m[6]) * d;
            inv[io + 4] = (m[0] * m[8] - m[2] * m[6]) * d;
            inv[io + 5] = -(m[0] * m[5] - m[2] * m[3]) * d;
            inv[io + 6] = (m[3] * m[7] - m[4] * m[6]) * d;
            inv[io + 7] = -(m[0] * m[7] - m[1] * m[6]) * d;
            inv[io + 8] = (m[0] * m[4] - m[1] * m[3]) * d;
        }
    }

    /// <summary>初始化：前景／背景樣本各做 k-means 分成 K 群，當成 GMM 的初始成分。</summary>
    private static void InitGmms(float[] c, byte[] label, Gmm bg, Gmm fg, byte[] comp)
    {
        var bgIdx = new List<int>(); var fgIdx = new List<int>();
        for (var i = 0; i < label.Length; i++)
            (label[i] is Background or ProbableBackground ? bgIdx : fgIdx).Add(i);
        KMeans(c, bgIdx, comp);
        KMeans(c, fgIdx, comp);
        LearnGmms(c, label, comp, bg, fg);
    }

    private static void KMeans(float[] c, List<int> idx, byte[] comp)
    {
        if (idx.Count == 0) return;
        var centers = new float[Components * 3];
        // 初始中心：等距抽樣（樣本順序是掃描順序，等距取到的顏色分佈夠廣）
        for (var k = 0; k < Components; k++)
        {
            var i = idx[(int)((long)idx.Count * k / Components)];
            centers[k * 3] = c[i * 3]; centers[k * 3 + 1] = c[i * 3 + 1]; centers[k * 3 + 2] = c[i * 3 + 2];
        }
        var sum = new double[Components * 3]; var cnt = new int[Components];
        for (var it = 0; it < 10; it++)
        {
            Array.Clear(sum); Array.Clear(cnt);
            foreach (var i in idx)
            {
                var best = 0; var bestD = float.MaxValue;
                for (var k = 0; k < Components; k++)
                {
                    var d0 = c[i * 3] - centers[k * 3]; var d1 = c[i * 3 + 1] - centers[k * 3 + 1]; var d2 = c[i * 3 + 2] - centers[k * 3 + 2];
                    var d = d0 * d0 + d1 * d1 + d2 * d2;
                    if (d < bestD) { bestD = d; best = k; }
                }
                comp[i] = (byte)best;
                sum[best * 3] += c[i * 3]; sum[best * 3 + 1] += c[i * 3 + 1]; sum[best * 3 + 2] += c[i * 3 + 2];
                cnt[best]++;
            }
            for (var k = 0; k < Components; k++)
            {
                if (cnt[k] == 0) continue;
                centers[k * 3] = (float)(sum[k * 3] / cnt[k]);
                centers[k * 3 + 1] = (float)(sum[k * 3 + 1] / cnt[k]);
                centers[k * 3 + 2] = (float)(sum[k * 3 + 2] / cnt[k]);
            }
        }
    }

    private static void AssignComponents(float[] c, byte[] label, Gmm bg, Gmm fg, byte[] comp)
    {
        for (var i = 0; i < label.Length; i++)
            comp[i] = (byte)(label[i] is Background or ProbableBackground ? bg.BestComponent(c, i) : fg.BestComponent(c, i));
    }

    private static void LearnGmms(float[] c, byte[] label, byte[] comp, Gmm bg, Gmm fg)
    {
        bg.BeginLearning(); fg.BeginLearning();
        for (var i = 0; i < label.Length; i++)
            (label[i] is Background or ProbableBackground ? bg : fg).AddSample(comp[i], c, i);
        bg.EndLearning(); fg.EndLearning();
    }

    // ---- 圖 ----

    private static void BuildGraph(MaxFlow g, float[] c, int w, int h, byte[] label, Gmm bg, Gmm fg,
        float[] left, float[] up, float[] upLeft, float[] upRight)
    {
        var n = w * h;
        for (var i = 0; i < n; i++)
        {
            float toSource, toSink;
            switch (label[i])
            {
                case Background: toSource = 0; toSink = Lambda; break;
                case Foreground: toSource = Lambda; toSink = 0; break;
                default:
                    // 資料項：-log p；像背景 → 剪掉與 source 的邊便宜
                    toSource = (float)Math.Min(bg.NegLog(c, i), 1e4);
                    toSink = (float)Math.Min(fg.NegLog(c, i), 1e4);
                    break;
            }
            g.AddTerminal(i, toSource, toSink);
        }
        for (var y = 0; y < h; y++)
        for (var x = 0; x < w; x++)
        {
            var i = y * w + x;
            if (x > 0) g.AddEdge(i, i - 1, left[i]);
            if (y > 0) g.AddEdge(i, i - w, up[i]);
            if (x > 0 && y > 0) g.AddEdge(i, i - w - 1, upLeft[i]);
            if (x < w - 1 && y > 0) g.AddEdge(i, i - w + 1, upRight[i]);
        }
    }

    /// <summary>
    /// Boykov–Kolmogorov 最大流／最小割（兩棵搜尋樹、路徑增廣、孤兒收養），
    /// 格狀圖上比 push-relabel 快得多。所有邊都是無向（正反容量相同）。
    /// </summary>
    internal sealed class MaxFlow
    {
        private const int None = -1;
        private const int Terminal = -2;
        private const int Orphan = -3;
        private const byte Free = 0, SourceTree = 1, SinkTree = 2;

        private readonly int _nodes;
        private readonly float[] _tcap;      // >0：source→node 餘量；<0：node→sink 餘量
        private readonly int[] _first;       // 節點的第一條弧
        private readonly int[] _next;        // 弧鏈
        private readonly int[] _head;        // 弧的另一端
        private readonly float[] _cap;       // 弧餘量（反向弧 = a ^ 1）
        private int _arcs;
        private readonly int[] _parent;      // 父弧（Terminal/None/Orphan）
        private readonly byte[] _tree;
        private readonly int[] _dist;
        private readonly int[] _time;
        private readonly bool[] _active;
        private readonly Queue<int> _activeQueue = new();
        private readonly Queue<int> _orphans = new();
        private int _now;

        /// <summary>最近一次 <see cref="Compute"/> 的最大流量。</summary>
        public double Flow { get; private set; }

        public MaxFlow(int nodes, int maxEdges)
        {
            _nodes = nodes;
            _tcap = new float[nodes];
            _first = new int[nodes];
            _parent = new int[nodes];
            _tree = new byte[nodes];
            _dist = new int[nodes];
            _time = new int[nodes];
            _active = new bool[nodes];
            _next = new int[maxEdges * 2];
            _head = new int[maxEdges * 2];
            _cap = new float[maxEdges * 2];
            Reset();
        }

        public void Reset()
        {
            Array.Fill(_first, None);
            Array.Clear(_tcap);
            _arcs = 0;
        }

        public void AddTerminal(int node, float toSource, float toSink) => _tcap[node] = toSource - toSink;

        public void AddEdge(int a, int b, float cap)
        {
            if (cap <= 0) return;
            var ab = _arcs++; var ba = _arcs++;
            _head[ab] = b; _cap[ab] = cap; _next[ab] = _first[a]; _first[a] = ab;
            _head[ba] = a; _cap[ba] = cap; _next[ba] = _first[b]; _first[b] = ba;
        }

        public bool IsSource(int node) => _tree[node] == SourceTree || _tree[node] == Free && _tcap[node] > 0;

        public void Compute()
        {
            Array.Clear(_tree); Array.Clear(_active); Array.Clear(_time);
            _activeQueue.Clear(); _orphans.Clear();
            _now = 0;
            Flow = 0;
            for (var i = 0; i < _nodes; i++)
            {
                if (_tcap[i] > 0) { _tree[i] = SourceTree; _parent[i] = Terminal; _dist[i] = 1; Activate(i); }
                else if (_tcap[i] < 0) { _tree[i] = SinkTree; _parent[i] = Terminal; _dist[i] = 1; Activate(i); }
                else _parent[i] = None;
            }

            while (true)
            {
                var meet = Grow(out var meetNode);
                if (meet == None) break;
                _now++;
                Augment(meet, meetNode);
                AdoptOrphans();
            }
        }

        private void Activate(int i)
        {
            if (_active[i]) return;
            _active[i] = true;
            _activeQueue.Enqueue(i);
        }

        /// <summary>兩棵樹擴張到相遇；回傳連接弧（從 source 樹節點指向 sink 樹節點）。</summary>
        private int Grow(out int fromNode)
        {
            fromNode = None;
            while (_activeQueue.Count > 0)
            {
                var i = _activeQueue.Peek();
                if (_tree[i] == Free) { _activeQueue.Dequeue(); _active[i] = false; continue; }
                for (var a = _first[i]; a != None; a = _next[a])
                {
                    var j = _head[a];
                    var res = _tree[i] == SourceTree ? _cap[a] : _cap[a ^ 1];
                    if (res <= 0) continue;
                    if (_tree[j] == Free)
                    {
                        _tree[j] = _tree[i];
                        _parent[j] = a ^ 1; // j 的父弧指回 i
                        _dist[j] = _dist[i] + 1;
                        _time[j] = _time[i];
                        Activate(j);
                    }
                    else if (_tree[j] != _tree[i])
                    {
                        // 相遇：回傳 source 側 → sink 側的弧
                        if (_tree[i] == SourceTree) { fromNode = i; return a; }
                        fromNode = j; return a ^ 1;
                    }
                    else if (_time[j] <= _time[i] && _dist[j] > _dist[i] + 1)
                    {
                        _parent[j] = a ^ 1;
                        _time[j] = _time[i];
                        _dist[j] = _dist[i] + 1;
                    }
                }
                _activeQueue.Dequeue();
                _active[i] = false;
            }
            return None;
        }

        private void Augment(int arc, int i)
        {
            var j = _head[arc];
            // 瓶頸
            var bottleneck = _cap[arc];
            for (var x = i; ; )
            {
                var p = _parent[x];
                if (p == Terminal) { bottleneck = Math.Min(bottleneck, _tcap[x]); break; }
                bottleneck = Math.Min(bottleneck, _cap[p ^ 1]);
                x = _head[p];
            }
            for (var x = j; ; )
            {
                var p = _parent[x];
                if (p == Terminal) { bottleneck = Math.Min(bottleneck, -_tcap[x]); break; }
                bottleneck = Math.Min(bottleneck, _cap[p]);
                x = _head[p];
            }
            // 推流
            Flow += bottleneck;
            _cap[arc] -= bottleneck; _cap[arc ^ 1] += bottleneck;
            for (var x = i; ; )
            {
                var p = _parent[x];
                if (p == Terminal)
                {
                    _tcap[x] -= bottleneck;
                    if (_tcap[x] <= 0) MakeOrphan(x);
                    break;
                }
                _cap[p] += bottleneck; _cap[p ^ 1] -= bottleneck;
                if (_cap[p ^ 1] <= 0) MakeOrphan(x);
                x = _head[p];
            }
            for (var x = j; ; )
            {
                var p = _parent[x];
                if (p == Terminal)
                {
                    _tcap[x] += bottleneck;
                    if (_tcap[x] >= 0) MakeOrphan(x);
                    break;
                }
                _cap[p ^ 1] += bottleneck; _cap[p] -= bottleneck;
                if (_cap[p] <= 0) MakeOrphan(x);
                x = _head[p];
            }
        }

        private void MakeOrphan(int x)
        {
            _parent[x] = Orphan;
            _orphans.Enqueue(x);
        }

        private void AdoptOrphans()
        {
            while (_orphans.Count > 0)
            {
                var i = _orphans.Dequeue();
                if (_parent[i] != Orphan) continue;
                var tree = _tree[i];
                var bestArc = None; var bestDist = int.MaxValue;
                for (var a = _first[i]; a != None; a = _next[a])
                {
                    var j = _head[a];
                    if (_tree[j] != tree) continue;
                    var res = tree == SourceTree ? _cap[a ^ 1] : _cap[a];
                    if (res <= 0) continue;
                    // j 必須有通到終端的路（不經孤兒）
                    var d = 0; var ok = false;
                    for (var x = j; ; )
                    {
                        if (_time[x] == _now) { d += _dist[x]; ok = true; break; }
                        var p = _parent[x];
                        if (p == Terminal) { _time[x] = _now; _dist[x] = 1; d += 1; ok = true; break; }
                        if (p == Orphan || p == None) break;
                        d++;
                        x = _head[p];
                    }
                    if (!ok) continue;
                    // 沿途標記距離
                    for (var x = j; _time[x] != _now; )
                    {
                        _time[x] = _now; _dist[x] = d--;
                        var p = _parent[x];
                        if (p == Terminal) break;
                        x = _head[p];
                    }
                    d = _dist[j] + 1;
                    if (d < bestDist) { bestDist = d; bestArc = a; }
                }
                if (bestArc != None)
                {
                    _parent[i] = bestArc;
                    _time[i] = _now;
                    _dist[i] = bestDist;
                    continue;
                }
                // 收養失敗：變 free，鄰居裡指向它的變孤兒，有餘量的鄰居變 active
                _tree[i] = Free;
                _active[i] = false;
                for (var a = _first[i]; a != None; a = _next[a])
                {
                    var j = _head[a];
                    if (_tree[j] != tree) continue;
                    var res = tree == SourceTree ? _cap[a ^ 1] : _cap[a];
                    if (res > 0) Activate(j);
                    var pj = _parent[j];
                    if (pj >= 0 && _head[pj] == i) MakeOrphan(j);
                }
            }
        }
    }
}
