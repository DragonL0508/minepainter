# MinePainter

<p align="center"><img src="docs/icon.png" width="96" alt="" /></p>

Windows 圖片編輯器。操作跟 paint.net 一樣，多了這些：

- 效果掛在圖層上，隨時能改
- 文字存檔後還能改
- 內建 AI 去背，離線
- 開得了 `.pdn`

**[下載](https://dragonl0508.github.io/minepainter/)** · [所有版本](https://github.com/DragonL0508/minepainter/releases)

## 安裝

解壓縮，執行 `MinePainter.exe`。不用裝 .NET。

第一次執行會自己裝到 `%LocalAppData%\Programs\MinePainter`，並登記檔案關聯。
移除：設定 → 應用程式 → MinePainter → 解除安裝。

Windows 10 / 11 64 位元。

## 格式

| | |
| --- | --- |
| 開啟 | `.mpp` `.pdn` `.png` `.jpg` `.bmp` `.gif` `.webp` |
| 儲存 | `.mpp` `.png` `.jpg` `.bmp` `.gif` `.webp` |

## 開發

.NET 8 + Avalonia + SkiaSharp

```bat
run.bat                  建置並啟動
dotnet test              測試
publish.bat [sc|fd] [版本]  發佈到 dist\
release.bat 1.4.2        推標籤，GitHub Actions 出 Release
```

- `src/MinePainter.Core` 核心邏輯
- `src/MinePainter.App` 介面
- `src/MinePainter.Thumbnails` 檔案總管縮圖（NativeAOT）
- `tests/` 測試
