# MinePainter

<p align="center">
  <img src="docs/icon.png" width="112" alt="" />
</p>

<p align="center">
  <b>你用什麼軟體做縮圖？我：黃金荷包蛋。</b><br />
  一套 Windows 上的圖片編輯器，照著 paint.net 的手感做，補上那些「做到一半才發現要改」的地方。
</p>

<p align="center">
  <a href="https://dragonl0508.github.io/minepainter/"><b>下載</b></a> ·
  <a href="https://github.com/DragonL0508/minepainter/releases">所有版本</a>
</p>

---

## 這是什麼

MinePainter 是給「做縮圖、修個圖、拼個素材」用的圖片編輯器。介面、快捷鍵、檔案格式都學 paint.net，
所以用過 paint.net 的人不必重新學；差別在於它把破壞性的操作盡量變成可以反悔的。

- **42 種效果、8 種調整** —— 模糊、扭曲、風格化、藝術、雜訊、碎形，常用的那些一次到位
- **效果不會寫死在像素裡** —— 外框、陰影、光暈、漸層掛在圖層上，做到一半想改參數、關掉、換順序都行
- **文字之後還能改** —— 存進 `.mpp`，下次開檔照樣改字、換字型、調外框
- **AI 一鍵去背** —— 內建模型，離線就能跑，不必把圖片上傳到任何網站
- **智慧參考線** —— 拖曳時自動貼齊邊緣與中心線，方向鍵微調會由慢漸快地滑行
- **分頁與完整歷史** —— 多份專案同時開著切換，每一步都留在歷史面板裡
- **跟 Windows 長在一起** —— 按兩下圖片直接開進來、檔案總管看得到 `.mpp` 縮圖、開著的視窗會接手新檔案
- **讀得懂 paint.net 的 `.pdn`**

## 安裝

到[下載頁](https://dragonl0508.github.io/minepainter/)拿 `MinePainter-<版本>-win-x64.zip`，解壓縮後執行 `MinePainter.exe` 就能用 ——
不必安裝、不必先裝 .NET。

第一次執行時它會把自己安裝到 `%LocalAppData%\Programs\MinePainter`（不需要系統管理員），
順便建開始功能表捷徑、登記檔案關聯與 `.mpp` 縮圖。之後不管從哪裡點，跑的都是那一份。

- **要設成預設的看圖／改圖程式**：設定 → 檔案關聯 → 選格式 →「登記並前往設定」
  （Windows 不允許程式自己指定預設程式，最後一步要在系統設定裡按）
- **要移除**：設定 → 應用程式 → MinePainter → 解除安裝。關聯、捷徑、縮圖處理常式都會一起清掉
- **想純綠色使用**：解除安裝過一次之後就不會再自動安裝；或把 `%AppData%\MinePainter\settings.json`
  裡的 `AutoInstall` 設成 `false`

新版會在啟動時靜默檢查，有更新才提示，更新是原地覆蓋同一個執行檔。

系統需求：Windows 10 / 11 64 位元。

## 檔案格式

| 格式 | 開啟 | 儲存 |
| --- | :---: | :---: |
| `.mpp`（MinePainter 專案：圖層、文字、效果堆疊都留著） | ✅ | ✅ |
| `.png` `.jpg` `.bmp` `.gif` `.webp` | ✅ | ✅ |
| `.pdn`（paint.net 專案） | ✅ | — |

`.mpp` 其實是個 ZIP：裡面是 `manifest.json`、各圖層的 PNG，還有一張縮圖 —— 檔案總管的預覽圖就是讀它。

---

## 給開發者

.NET 8 + [Avalonia](https://avaloniaui.net/) + [SkiaSharp](https://github.com/mono/SkiaSharp)。

```bat
run.bat                                rem 建置 Release 並啟動（可接一個圖片或 .mpp 路徑）
dotnet test MinePainter.sln            rem 跑測試
```

### 結構

- `src/MinePainter.Core` —— 文件、圖層、選取、工具、效果、歷史紀錄等核心邏輯（不依賴 UI）
- `src/MinePainter.App` —— Avalonia 介面
- `src/MinePainter.Thumbnails` —— 檔案總管的 `.mpp` 縮圖處理常式。檔案總管會把它載進自己的 COM
  代理程序，所以它不能是那支單一檔案的 exe、也不能依賴 .NET 執行階段 —— 用 NativeAOT 編成原生 DLL，
  嵌進 exe，安裝時才寫出來註冊
- `tests/` —— 核心與介面的單元測試

### 發佈

```bat
publish.bat              rem 自包含單一 exe，對方不必安裝 .NET
publish.bat fd           rem 依賴框架的小 exe，對方需先裝 .NET 8 Desktop Runtime
publish.bat sc 1.4.2     rem 指定版本號
```

會先用 NativeAOT 編好縮圖 DLL（需要 MSVC 建置工具），再把它嵌進 exe。
輸出在 `dist\MinePainter-<版本>-win-x64\`，旁邊有同名 `.zip`。

### 上版

```bat
release.bat 1.4.2
```

確認沒有未 commit 的改動 → `git push` → 打 `v1.4.2` 標籤推上去。
之後由 GitHub Actions（`.github/workflows/release.yml`）在 Windows runner 上接手：
跑測試 → 用同一支 `publish.bat` 建置兩種 exe → 建立 Release 並附上兩個 zip。
也可以到 Actions 頁面用「發佈版本」手動觸發並填版本號。

下載頁是 `docs/index.html`（GitHub Pages），用 GitHub API 讀「最新的 Release」把下載連結、
版本、檔案大小填進去 —— 發新版不必改網頁。
