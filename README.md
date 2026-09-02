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

## 結構

- `src/MinePainter.Core` — 文件、圖層、選取、工具、效果、歷史紀錄等核心邏輯（不依賴 UI）
- `src/MinePainter.App` — Avalonia 介面
- `tests/MinePainter.Core.Tests` — 核心單元測試
