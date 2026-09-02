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

第一次要先設定一次：

1. GitHub → Settings → Pages → Source 選 **Deploy from a branch**，分支 `main`、資料夾 `/docs`
2. 網址會是 `https://dragonl0508.github.io/minepainter/`

> Repository 目前是 private。GitHub Pages 與 Release 附件對外公開都需要 repo 是 public
> （private repo 的 Pages 要付費方案，附件也需要登入才能下載）。要給別人下載就得先把 repo 轉成 public。

## 結構

- `src/MinePainter.Core` — 文件、圖層、選取、工具、效果、歷史紀錄等核心邏輯（不依賴 UI）
- `src/MinePainter.App` — Avalonia 介面
- `tests/MinePainter.Core.Tests` — 核心單元測試
