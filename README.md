# MinePainter

以 Avalonia + SkiaSharp 打造的 paint.net 風格繪圖程式（.NET 8，Windows）。

## 開發

```bat
run.bat            rem 建置 Release 並啟動（可接一個圖片或 .mpp 檔路徑）
dotnet test tests\MinePainter.Core.Tests
```

## 發佈給其他人

```bat
publish.bat              rem 自包含單一 exe，對方不必安裝 .NET
publish.bat fd           rem 依賴框架的小 exe，對方需先裝 .NET 8 Desktop Runtime
publish.bat sc 1.2.0     rem 指定版本號
```

輸出在 `dist\MinePainter-<版本>-win-x64\MinePainter.exe`，旁邊會有同名 `.zip` 可直接傳送。

## 上版（GitHub Release + 下載頁）

```bat
release.bat 1.2.0
```

做的事：確認沒有沒 commit 的改動 → `git push` → 打 `v1.2.0` 標籤推上去。
之後由 GitHub Actions（`.github/workflows/release.yml`）在 Windows runner 上接手：
跑測試 → 用同一支 `publish.bat` 建置兩種 exe → 建立 Release 並附上兩個 zip。
也可以到 Actions 頁面用「發佈版本」手動觸發並填版本號。

下載頁是 `docs/index.html`（GitHub Pages），用 GitHub API 讀「最新的 Release」把下載連結、
版本、檔案大小填進去 —— 發新版不必改網頁。

第一次要先把 Pages 打開（只做一次）：

1. 直接開 <https://github.com/DragonL0508/minepainter/settings/pages>
   （或 repo 上方 **Settings** 分頁 → 左側 **Code and automation** → **Pages**）
2. **Build and deployment** 的 **Source** 選 `Deploy from a branch`
3. **Branch** 選 `main`、資料夾選 `/docs` → **Save**
4. 一兩分鐘後網址是 <https://dragonl0508.github.io/minepainter/>

看不到 Settings 分頁＝目前登入的帳號沒有這個 repo 的管理權限（要用 owner 帳號）。

## 結構

- `src/MinePainter.Core` — 文件、圖層、選取、工具、效果、歷史紀錄等核心邏輯（不依賴 UI）
- `src/MinePainter.App` — Avalonia 介面
- `tests/MinePainter.Core.Tests` — 核心單元測試
