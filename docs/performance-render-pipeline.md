# 渲染與運算效能重構 — 2026-09-08

分支：`perf/render-pipeline`。這是已落地的第一批核心改造，不代表已完成全 GPU 影像引擎，也沒有與其他製圖軟體做產品級排名比較。

## 已完成

- **調整圖層線性管線**：可見裝置像素大小的離屏表面依序套用調整，每層來源只繪製一次。原來 n 個半透明調整會遞迴重繪下方 2^n 次。保留 Src／SrcOver 與群組不透明度語意，配置失敗仍有舊路徑。
- **LOD 共用來源與快取預算**：LOD 建立重用版本化的原始 tile 影像。單格修改不再複製同區塊的全部 64 格；不同倍率的快取以 LRU 預算管理，不再三幀後淘汰。預設每 viewport 沿用合成器的可用記憶體 1/8、限制在 64–512 MiB，避免固定 128 MiB 容不下多個 4K 圖層而每幀重傳。幀末收斂至預算；不含 Skia 自身的貼圖／mipmap，也不限制繪製中的瞬間工作集。刪除圖層及更換 GPU context 時清理資源。
- **線性時間遮罩形態學**：單調佇列取代逐半徑掃描，收縮／擴張由 O(Nr) 改為 O(N)。維持方框、clamp 邊界與 8-bit 軟遮罩語意。
- **效果來源 COW 快照**：點陣圖層只在文件鎖內取得 tile、可見物件及位移快照；大陣列複製與文字／向量點陣化移到鎖外。讀取完成即釋放 tile 引用，取消也會釋放。群組來源合成、遮罩複製及效果結果寫回仍持有文件鎖。
- **預覽來源去重**：以實際取樣範圍保留最新來源，避免調整半徑時保留許多相同的全圖陣列。每個執行中的 context 仍持有自己的輸出。
- **可重跑量測**：`tools/PerfBench` 包含原版遮罩演算法對照、CPU raster 結構計數及真正的離屏 ANGLE GPU 驗證。不建立視窗、不注入桌面輸入。

## 測量方式與結果

環境：Windows 10.0.26200、.NET 8.0.21、16 個邏輯處理器。GPU 為 NVIDIA GeForce RTX 4060 Laptop GPU，ANGLE Direct3D11，驅動字串 D3D11-32.0.15.9174。

CPU 基準暖機兩次後取五次中位數。來源是固定種子的隨機 1024×1024 軟遮罩；新舊均有多核心平行化，計時包含配置，並先驗證輸出相同。

| 遮罩操作 | 原版 | 新版 | 此次倍率 |
|---|---:|---:|---:|
| 收縮半徑 1 | 6.57 ms | 5.55 ms | 1.18× |
| 擴張半徑 1 | 6.97 ms | 4.55 ms | 1.53× |
| 收縮半徑 16 | 15.53 ms | 5.16 ms | 3.01× |
| 擴張半徑 16 | 20.95 ms | 6.15 ms | 3.40× |
| 收縮半徑 64 | 50.74 ms | 4.38 ms | 11.60× |
| 擴張半徑 64 | 65.72 ms | 3.19 ms | 20.62× |

GPU 新舊各暖機兩次、五次中位數。每幀以 glFinish 等待完成，數值包含 CPU 提交、配置及 GPU 完成等待，**不是 GPU timestamp，也不是整個 App 的輸入到顯示延遲**。原版直接呼叫保留的遞迴 fallback；只有根群組場景比較原版，避免巢狀群組轉入新管線污染比較。

以下是最後一輪 513×385 半透明來源、每個調整強度 50% 的結果：

| 調整層數 | 原版 | 新版 | 此次倍率 |
|---|---:|---:|---:|
| 1 | 0.618 ms | 0.679 ms | 0.91× |
| 5 | 1.692 ms | 0.425 ms | 3.98× |
| 10 | 48.931 ms | 0.467 ms | 104.80× |

單一調整場景未加速，離屏管線有額外成本。筆電 GPU 時脈、排程及暖機會影響數值：前一輪十層結果為 44.092 → 0.785 ms（56.13×）。不能把特定病態堆疊的倍率解讀為整個軟體的加速倍率。

另一場景為真正 **3840×2160 視口、十層調整**：新版中位數 **15.355 ms**，每幀繪製來源 135 格。此值不含 Avalonia UI 與視窗呈現的所有成本，不能直接保證 60 FPS；此大場景沒有量測舊版。

### 畫面與資源驗證

- 新舊 GPU 根群組場景逐位元組相同。
- 與 CPU 匯出比較，十層場景 GPU 最多差 3/255；原版也有完全相同的差異。量測工具同時守住新舊相等與 CPU 參考最大誤差界線。
- 巢狀群組與透明來源額外與 CPU 參考比對。
- 真 GPU LOD 通過熱快取、單格修改像素、GPU → raster → GPU context 切換驗證。
- 2048² LOD 冷建需複製 64 格，熱快取 0 格；修改一格時只複製 1 格、重建 1 張 LOD。
- 測試刻意阻塞來源物件的 Render，確認另一執行緒仍能取得 Document.SyncRoot；修改來源像素、刪除物件、改位移不影響已捕捉的快照。

## 重跑

```powershell
dotnet test MinePainter.sln -c Release
dotnet run --project tools/PerfBench -c Release
dotnet run --project tools/PerfBench -c Release -- --gpu
```

GPU 工具使用專案鎖定的 Avalonia ANGLE 原生庫；需要 Windows 與可用的 EGL/GLES 裝置，失敗會回傳非零狀態，不會假裝量到了硬體 GPU。輸出的 Renderer 字串用於辨識實際裝置或軟體後端。

## 本機測試版

輸出：`dist/MinePainter-perf-render-pipeline/MinePainter.App.exe`，自包含單檔、ReadyToRun，產品版本 `1.8.11-perf.1`。測試包的 AssemblyVersion 刻意保留 `1.0.0.0`，沿用既有開發版規則停用自動安裝、交棒到安裝版與自動更新；FileVersion 為 `1.8.11.0`。先關閉正在執行的 MinePainter 再開測試版，避免單一實例機制將啟動交給舊程式。仍共用使用者設定。

打包指令：

```powershell
dotnet publish src/MinePainter.App -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:EnableCompressionInSingleFile=false -p:PublishReadyToRun=true -p:DebugType=none -p:Version=1.8.11-perf.1 -p:AssemblyVersion=1.0.0.0 -p:FileVersion=1.8.11.0 -p:InformationalVersion=1.8.11-perf.1 -o dist/MinePainter-perf-render-pipeline
```

## 接續工程的邊界

CPU 合成批次與主要 GPU 圖層繪製仍共用文件鎖；本批只移出效果來源的重操作。完整 RenderSnapshot 尚未建立。

一般圖層效果仍以 CPU 為準，自訂混合與 LUT 仍可能使文件退回 CPU。尚未實作 GPU LUT／距離場、效果節點中間結果快取、按需 CPU 合成與區塊式檔案格式。後續應以 4K／8K 真實專案、長筆劃、圖層拖曳、效果滑桿的 p95／p99 延遲與記憶體峰值，決定下一批工作優先序。
