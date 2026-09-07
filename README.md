<p align="center"><img src="docs/icon.png" width="96" alt="" /></p>
<h1 align="center">MinePainter</h1>
<p align="center">你用什麼軟體做縮圖？我：黃金荷包蛋。</p>
<p align="center"><a href="https://dragonl0508.github.io/minepainter/"><b>下載</b></a> · <a href="https://github.com/DragonL0508/minepainter/releases">所有版本</a> · <a href="LICENSE">MIT 授權</a></p>
<p align="center"><a href="https://github.com/DragonL0508/minepainter/actions/workflows/ci.yml"><img src="https://github.com/DragonL0508/minepainter/actions/workflows/ci.yml/badge.svg" alt="CI" /></a></p>

<br />

## 安裝

解壓縮，執行 `MinePainter.exe`。不用裝 .NET。

第一次執行會自己裝到 `%LocalAppData%\Programs\MinePainter`，並登記檔案關聯。
移除：設定 → 應用程式 → MinePainter → 解除安裝。

Windows 10 / 11 64 位元。

## 格式

| | |
| --- | --- |
| **開啟** | `.mpp` `.pdn` `.psd` `.png` `.jpg` `.bmp` `.gif` `.webp` |
| **儲存** | `.mpp` `.png` `.jpg` `.bmp` `.gif` `.webp` |
| **匯出** | `.psd`（圖層、群組、可編輯文字、圖層樣式、調整圖層盡量保留；對不上的效果轉成像素） `.pdn`（合併成單一圖層） |

## 開發

.NET 8 + Avalonia 11.3 + SkiaSharp 2.88。只出 Windows x64。

```
run.bat                     建置並啟動（Release）
dotnet test MinePainter.sln 全部測試（Core + App headless）
publish.bat [sc|fd] [版本]   發佈到 dist\（sc 自包含、fd 依賴框架）
release.bat 1.8.2           推標籤，GitHub Actions 跑測試、建置、出 Release
```

| | |
| --- | --- |
| `src/MinePainter.Core` | 文件模型、工具、歷史、效果、格式。**只依賴 SkiaSharp，沒有任何 UI 相依** |
| `src/MinePainter.App` | Avalonia 介面、平台整合、渲染上屏 |
| `src/MinePainter.Thumbnails` | 檔案總管縮圖處理常式（NativeAOT，publish.bat 才建） |
| `tools/ThumbPack` | 把 YouTube 預覽縮圖原檔轉成 WebP 內嵌資源 |
| `tests/MinePainter.Core.Tests` | Core 的行為測試（xUnit） |
| `tests/MinePainter.App.Tests` | 介面互動測試（Avalonia.Headless.XUnit） |

以下是這個專案的守則。**新加的程式碼一律照這裡走；改了守則就改這裡。**
每一條都是踩過雷才寫下來的，括號裡是為什麼。

### 分層

- **Core 不知道 UI 存在。** Core 只依賴 SkiaSharp；任何 Avalonia 型別、對話框、剪貼簿、檔案挑選器都在 App。Core 要通知使用者用 `EditorSession.Notify`（App 接成 toast）。
- **App 不直接改文件。** 所有改到 `Document`／圖層的操作都是 Core 的指令（`History/*Commands.cs`），App 只呼叫指令、刷新畫面。（同一個操作要能從選單、快捷鍵、測試三個入口叫到，邏輯只能在一處。）
- **圖層面板是多選的。** 拿選取一律用 `LayersPanel.SelectedNodes`，交給 `LayerCommands` 的多節點版（`GroupNodes`／`MoveNodes`／`ShiftNodes`／`RemoveNodes`，內部先 `NormalizeSelection` 去掉祖先已選的子層）並綑成一步 undo；作用中圖層永遠是選取裡的一個。守門：`MultiSelectLayerCommandTests`、`LayersPanelMultiSelectTests`。
- **Core 子目錄職責**：`Documents` 文件與縮放規則 · `Layers` 圖層樹、原始高清來源 · `Tiles` 稀疏像素表面、遮罩 · `History` 所有可 undo 的指令 · `Tools` 互動工具與 `EditorSession` · `Effects` 非破壞性效果堆疊 · `Adjustments` 色彩調整 · `Vectors` 文字／形狀物件 · `Selections` 選取與浮動內容 · `Compositing` 合成 · `IO` `.mpp`／`.pdn`／`.psd`／影像編解碼 · `AI` 去背。
- **App 子目錄職責**：`Views` 視窗與面板 · `Controls` 可重用控制項（含 `Motion`） · `Rendering` 畫布上屏與 GPU 路徑 · `Services` 設定、字型、更新、安裝 · `Platform` Win32 互通。

### 文件與像素的鐵律

1. **所有像素讀寫都在 `Document.SyncRoot` 內。** 合成器在背景執行緒讀，工具在 UI 執行緒寫；連 `Snapshot()` 也要在鎖內。長時間的運算（推論、重取樣）在鎖外做，只在讀出／寫回時短暫持鎖。
2. **一個使用者動作＝一步 undo。** 每個改文件的指令都 `History.Push` 一個 `IHistoryEntry`；多個子步驟用 `CompositeHistoryEntry` 綁成一步。像素變更用 `TileDeltaEntry.Capture`（只存動到的格）；互為反操作的（翻轉、旋轉）不存像素。undo 之後畫面上「什麼都沒變」的空步驟是 bug。
3. **UI 走 `EditorSession.Undo/Redo`**，不直接碰 `HistoryManager`（它是 internal）。session 會先落地浮動內容與變形。
4. **文字圖層不變式：有物件（文字／形狀）的圖層永遠沒有像素。** 任何會往圖層寫像素的入口（筆刷、貼上、填滿、去背、合併）遇到文字圖層要拒絕或改貼到新圖層。守門：`TextLayerInvariantTests`。
5. **原始高清來源（快速模式的命脈）。** 圖層可帶 `LayerPixelSource`（原圖＋矩陣），失效判準是 `Revision` 對不上。**任何改圖層像素的操作，能保留它就要保留**：寫像素前 `ValidPixelSource` + `TakePixelSource()`，寫完掛新來源並對齊 `Revision`，undo/redo 用 `PixelSourceSwapEntry`。工具箱：`Masked`（遮罩套到原圖）、`Rebased`（仿射映射串進矩陣）、`Copy`、`OutputRender.RenderLayerAsSource`（含效果在輸出解析度算一份）。刻意作廢的只有筆刷、填色、文字平面化、向下合併。守門：`PixelSourceSurvivalTests`、`FastModeWorkflowTests`。
6. **整份文件縮放的規則只有一份：`ScaleRules`。** 調整影像大小、快速模式輸出、開檔轉模式都走它（像素從原圖重畫、效果的像素長度參數與遮罩跟著縮、文字重新排版）。兩條路結果要一樣。
7. **效果快取是圖層座標，與畫布無關。** 平移圖層不重算效果：位置變了用 `InvalidateComposite`，內容變了才 `Invalidate`。效果的輸出會延伸 `SourceMargin`，任何「重算範圍」都要含 margin。守門：`EffectCacheInvalidationTests`。
   位置相關的效果（`IsPositionIndependent = false`：暈影、聚焦、像素化…）以**畫布**為範圍、永遠整層重算 —— 圓心與半對角線看的是範圍，只算髒區或拿內容框當範圍都會讓圓跑掉（顯示切換後聚焦變深就是這樣來的）。
   **效果算爆了不能悄悄略過。** renderer 會跳過那一條讓其餘照算，但一定透過 `LayerEffectRenderer.EffectFailed` 回報（App 記 `error.log` ＋ toast），同一條只報一次、算成功後才重置。守門：`EffectFailureReportTests`。
8. **「內容範圍」不能只信 em box，要含實際著墨。** 字面超出行高的字型、重音、外框都會超出排版框（`TextElement.Bounds` = 排版框 ∪ 著墨框）。
9. **效果、調整、物件都是不可變 record。** 改參數用 `with`；參數描述在 `ParamDef`／`SliderParam`，像素長度的參數標 `Geometric = true`（縮放時才會跟著縮）。
   調整預設是 Skia 色彩濾鏡；濾鏡表達不了的（3D LUT）標 `RequiresPixelPath = true` 並實作 `ApplyPixels`，合成器與破壞性套用走像素路徑、GPU 路徑整份退回合成器（SkiaSharp 2.88 的 runtime shader 在 CPU raster 會直接崩，不能用）。參數之外的大塊資料走 `SaveData`（.mpp 的 `AdjustmentData`、效果堆疊的 `data`）。
10. **`.psd` 匯出以「Photoshop 裡還能改」為準。** `PsdFormat.Save` 是 `Load` 的反向：文字寫 `TySh`、效果寫 `lfx2`、調整寫參數區塊，鍵與單位照讀取端；對不上的效果整層烙成像素並回報 warnings，不准悄悄少一條效果。守門：`PsdSaveTests`（寫出再讀回）。`.pdn` 匯出只寫單一圖層（paint.net 沒有群組／文字／效果，逐層搬只會得到烙死的像素），物件圖照 paint.net 5.1 真檔逐欄位寫，改欄位前先用真檔傾印對照；守門：`PdnSaveTests`。
11. **`.mpp` 向後相容。** 加欄位要 bump `MppFormat.FormatVersion`、舊檔照讀、新檔在舊版開得起來或明確拒絕。每次改格式都要有 `MppFormatTests`。
12. **Skia 物件的生命週期要明確。** `SKImage`／`SKBitmap`／`SKPath` 誰擁有誰釋放寫在註解裡；多份物件共用同一張 `SKImage` 時（`LayerPixelSource.Rebased`），只有一個擁有者，其餘 `Detach()`。合成執行緒可能在物件釋放後才畫到它，尺寸類屬性建構時就快取。

### UI 一致性

- **對照物是 paint.net（與其開源複刻 Pinta）。** 拿不定行為時先看它們怎麼做；刻意不採納的要寫下原因。
- **動畫時長只從 `Controls/Motion.cs` 拿**：Quick 100ms（退場、按壓）、Base 160ms（進場、狀態）、Move 200ms（FLIP、指示器滑動）、Emphasis 240ms（toast）；進場 CubicEaseOut、退場 CubicEaseIn。不要寫死毫秒數。
- **滾輪往上＝數值變小**（與拉條方向一致）；**拉條雙擊＝回預設值**；**彈出層一律置頂**。
- **選單子清單點開後滑鼠移出不自動關**；按鈕下拉用 `ClickSubmenuMenuFlyout`。
- **文字精簡繁中**：選單、toast、對話框都是一句話講完，技術詞（remove.bg、API Key）保留原文。
- **使用者設定都在 `Services/AppSettings`**，改完 `Save()`；不要各自存檔。
- **回饋用 toast（`Toasts.Show`）**，不用 MessageBox；會擋住流程的才開對話框。
- **快速模式要「能做就做」**：使用者在代理畫布上的每個操作，輸出時都應該拿到原始解析度的結果（見鐵律 5）；做不到的操作要在 UI 上講清楚（`WarnIfPixelToolInFastMode`）。

### 程式碼風格

- 格式由 `.editorconfig` 定：4 空格、UTF-8、檔案範圍命名空間、私有欄位 `_camelCase`。
- `Nullable` 開、`ImplicitUsings` 開、`LangVersion latest`。型別能 `sealed` 就 `sealed`，不可變資料用 `record`。
- **建置零警告**（`TreatWarningsAsErrors` 在 `Directory.Build.props`）。過時 API 要換掉，不是壓掉。
- **註解寫繁中，講「為什麼」不講「做什麼」**：每個公開型別／方法有 `<summary>`；踩過的雷寫在出事的那一行旁邊（含日期與使用者回報的原話更好）。程式碼本身講得清楚的不重複。
- 不留 `TODO`／`FIXME`：要做就做，不做就開 issue。
- 一個檔案一個主要型別；新功能的邏輯放 Core 指令或獨立的 View／Service，不要往 `MainWindow` 裡堆。`MainWindow` 已按職責拆成 partial 檔：`MainWindow.axaml.cs` 只留欄位、建構子、工具切換與 `RefreshUiState`；選單各一檔（`FileMenu`／`EditMenu`／`ImageMenu`／`EffectsMenu`／`LayersMenu`／`ViewMenu`）、`Tabs` 文件分頁、`DragDrop` 拖放、`ToolOptions`／`TextOptions` 工具列選項、`Shortcuts`、`Settings`、`Update`、`Panels` 浮動面板、`CanvasTextEdit` 畫布內文字編輯、`Debug` 除錯種子與計時。新的選單項目或工具列群組放進對應的 partial；沒有對應的就開新 partial，單檔不要超過 700 行。
- 路徑含中文（`桌面`）：跑 `.bat` 用 PowerShell 並設 `$env:CI="true"` 跳過 `pause`；PowerShell 腳本存成含 BOM 的 UTF-8。

### 測試規範

- **每個功能、每個修掉的 bug 都要有守門測試**，放在最貼近行為的測試檔；名稱直接寫行為（中文或 `Subject_Behavior` 都可，但要讓人一眼看懂失敗的是什麼），Assert 的訊息寫「這代表什麼壞了」。
- **Core 測試不碰 GUI**：用 `EditorSession`＋指令重現使用者流程，驗輸出像素（`OutputRender.Render`、`ImageCommands.ReadRegion`）。像素比對用容許值與統計（銳利像素比例、半透明像素數），不要逐像素相等。
- **App 測試走 Avalonia.Headless**（`[AvaloniaFact]`，`TestApp` 只載 FluentTheme），合成的滑鼠鍵盤只進 headless 視窗。**絕對不對使用者的桌面注入輸入、不搶焦點**；要看真的畫面只用被動截圖。
- 需要外部資源的測試（模型、API Key）用環境變數開關（`MINEPAINTER_TEST_MODELS`），沒設就跳過或走本機替代，CI 上一定要能跑。
- 偶發失敗不是「再跑一次」：效果快取、合成器都是多執行緒，偶發＝競態，找到根因（`RenderLayerNow` 等 worker 的那類修法）。
- **commit 前 `dotnet test MinePainter.sln` 全綠、建置零警告。** CI 跑同一組：每次 push 到 `main` 與每個 PR 都跑（`.github/workflows/ci.yml`），發版前 `release.yml` 再跑一次。

### 除錯鉤子

環境變數 `MINEPAINTER_DEBUG_*` 只在開發用，程式碼裡要註明用途：
`PERF`／`PERF_CYCLE`／`PERF_BENCH`（效能記錄）、`OFFSCREEN`（離螢幕啟動供截圖）、`EFFECT`（直接開某個效果／模式）、`OVERLAY`、`HIDECANVAS`、`MENU_CYCLE`、`TEXTFX`、`TEXTBENCH`、`FONTCACHE`、`STREAMFONT`、`NOFALLBACK`、`NOTOUI`、`NOANIM`、`SPLASH_HOLD`、`PRESETS`／`PRESETS_DIR`／`PRESETS_DROP`／`PRESETS_EDIT`。
離螢幕驗證程序的單一實例名字含 `|debug`，不會接走使用者正在用的實例。

### 流程

- **每個功能做完就 commit + push**，不累積。commit message 繁中、Conventional Commits 前綴（`feat` `fix` `refactor` `perf` `chore`），第一行講使用者看得到的結果，內文講為什麼與怎麼做。
- 發版：`release.bat x.y.z`（會擋未 commit 的變更）→ Actions 跑測試、`publish.bat sc`／`fd`、建 Release。Release 說明先「### 這一版」列變更（使用者角度），最後保留「### 下載哪一個？」兩行；沒有 `gh` 的機器用 REST API PATCH `body`。
- 使用者平常跑的是 `dist\` 的發佈版：改完 Core／App 要重新 `publish.bat` 或發版才看得到。
- `dist/`、`bin/`、`obj/`、`.claude/`、AOT 產出的原生 DLL 不進版控（見 `.gitignore`）。

## 授權

[MIT](LICENSE)。內嵌的 Noto Sans TC 字型是 SIL OFL 1.1；YouTube 縮圖預覽的週邊縮圖屬各頻道所有，只作版面佔位，不在 MIT 範圍內。
