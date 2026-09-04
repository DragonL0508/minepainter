using System.Collections.Concurrent;
using MinePainter.Core.Tiles;

namespace MinePainter.Core.Compositing;

/// <summary>
/// 合成用的共用執行緒集合（GEGL 的「逐 tile 分派執行緒」在這裡的對應物）。
///
/// 為什麼不用 ThreadPool：合成是「持著文件全域鎖」在跑的，借全域池等於跟 UI、Task.Run、
/// 測試框架搶同一批執行緒；批次內只要有人卡住，卡死的是整個行程的池子。
/// 為什麼不是每個文件各開一份：一個分頁一份的話，開幾個分頁就是幾倍的執行緒，
/// 而它們大部分時間都在睡 —— 合成一次只有一個文件在跑得動（各自的鎖）。
///
/// 鐵則：交進來的工作**不准取 Document.SyncRoot**。鎖在呼叫者手上，
/// 別條執行緒取不到，那是死鎖不是等待。
/// </summary>
internal static class CompositeWorkers
{
    private sealed class Batch
    {
        public required IReadOnlyList<TileIndex> Items;
        public required Action<TileIndex> Body;
        public required CountdownEvent Done;
        public int Next;
    }

    /// <summary>執行緒數：單一效果內部本來就吃得滿核心，這裡再開太多只是互相搶。</summary>
    private static readonly int ThreadCount = Math.Clamp(Environment.ProcessorCount / 2, 1, 4);

    private static readonly ConcurrentQueue<Batch> Queue = new();
    private static readonly SemaphoreSlim Signal = new(0);
    private static Thread[]? _threads;
    private static readonly object StartGate = new();

    /// <summary>把一批格子分給共用執行緒做，呼叫者自己也一起做，全部做完才返回。</summary>
    public static void Run(IReadOnlyList<TileIndex> items, Action<TileIndex> body)
    {
        if (items.Count == 0) return;
        EnsureStarted();

        var batch = new Batch { Items = items, Body = body, Done = new CountdownEvent(items.Count) };
        Queue.Enqueue(batch);
        Signal.Release(ThreadCount);

        Drain(batch);      // 呼叫者也是一份人力
        batch.Done.Wait(); // 每一格做完會報一次數，所以這裡等的是「格子」不是「執行緒」
        // 這裡不 Dispose：最後一格的 Signal 正在別條執行緒上返回，這一刻把它處理掉是在踩人家的腳
    }

    private static void EnsureStarted()
    {
        if (Volatile.Read(ref _threads) != null) return;
        lock (StartGate)
        {
            if (_threads != null) return;
            var threads = new Thread[ThreadCount];
            for (var i = 0; i < threads.Length; i++)
            {
                threads[i] = new Thread(Loop)
                {
                    Name = $"MinePainter.Composite.{i}",
                    IsBackground = true,
                };
                threads[i].Start();
            }
            Volatile.Write(ref _threads, threads);
        }
    }

    private static void Loop()
    {
        while (true)
        {
            Signal.Wait();
            while (Queue.TryPeek(out var batch))
            {
                Drain(batch);
                // 做完了就把它移出佇列（可能已經被別人移走，那就不關我的事）
                if (Volatile.Read(ref batch.Next) >= batch.Items.Count &&
                    Queue.TryPeek(out var head) && ReferenceEquals(head, batch))
                {
                    Queue.TryDequeue(out _);
                }
                else
                {
                    break;
                }
            }
        }
    }

    private static void Drain(Batch batch)
    {
        while (true)
        {
            var i = Interlocked.Increment(ref batch.Next) - 1;
            if (i >= batch.Items.Count) return;
            try
            {
                batch.Body(batch.Items[i]);
            }
            catch (Exception)
            {
                // 一格算壞不該拖垮整批：那一格維持舊圖，之後會再被標髒
            }
            finally
            {
                batch.Done.Signal();
            }
        }
    }
}
