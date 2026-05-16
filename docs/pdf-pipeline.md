# PDF パイプライン技術リファレンス (v2.x)

> 現行 Virtual Print Demo は **Microsoft Print To PDF** + **PDFium (PDFtoImage NuGet)** によるカラー PNG 出力パイプラインを採用しています。
> 旧 XPS Class Driver ベースの実装については [xps-internals.md](xps-internals.md) (歴史的アーカイブ) を参照してください。
> 移行の経緯と代替案の評価は [design-history.md](design-history.md) の「§7 カラー対応のための PDF 移行」を参照してください。

## 1. パイプライン全体像

```
 ┌─────────────────────┐               ┌────────────────────────────┐
 │ Windows アプリ      │ ──印刷──▶ │ Microsoft Print To PDF      │
 │ (Edge / Word ...)   │              │   PrintCapabilities:        │
 └─────────────────────┘              │     PageOutputColor = Color │
                                       └──────────────┬──────────────┘
                                                      │ PDF バイトストリーム
                                                      ▼
                              ┌─────────────────────────────────────┐
                              │ Local Port Monitor                  │
                              │ C:\VirtualPrintDemo\.spool\spool.pdf │
                              └──────────────┬──────────────────────┘
                                             │ FileSystemWatcher
                                             ▼
                  ┌──────────────────────────────────────────────────┐
                  │ VirtualPrinter.App.exe --watch (常駐, HKLM\Run)  │
                  │   1. 完了検知 ("%PDF-" 先頭 + "%%EOF" 末尾)      │
                  │   2. 1500 ms 静粛 + 排他オープン                 │
                  │   3. <job>\print.pdf へ移動                      │
                  │   4. PdfToPngRenderer で 300 DPI カラー PNG 化   │
                  └──────────────┬───────────────────────────────────┘
                                 │
                                 ▼
        C:\VirtualPrintDemo\<日時>_PrintJob\
          ├─ print.pdf
          └─ page_NNN.png   (1 ページ 1 ファイル, カラー)
```

## 2. なぜ Print To PDF なのか

調査の結果、Microsoft XPS Class Driver は PrintCapabilities に **`PageOutputColor` フィーチャを宣言していない**ことが判明しました。これがあると Edge / Chrome / Word などのアプリが `psk:Color` を選んだうえでカラーのコンテンツストリームを送信しますが、宣言が無い場合**アプリ側で送信前にグレースケールへ前段ラスタライズ**するため、後段で何をしてもカラーは復元できません。

| ドライバ | PageOutputColor 宣言 | アプリの挙動 | スプール到達時 |
|---|---|---|---|
| Microsoft XPS Class Driver | ❌ なし | グレースケールでラスタライズ | グレースケール XPS |
| Microsoft Print To PDF | ✅ あり (Color / Monochrome) | カラーで送信 | カラー PDF |

Install スクリプトはこの宣言を **明示的に確認** します:

```powershell
$caps = $printer.GetPrintCapabilitiesAsXml()
[xml]$capsXml = $caps
$colorFeature = $capsXml.SelectNodes(
    "//*[local-name()='Feature' and contains(@name,'PageOutputColor')]")
if ($colorFeature.Count -eq 0) {
    Write-Warning "PageOutputColor feature not advertised — output may be grayscale."
}
```

確認結果は `C:\VirtualPrintDemo\printer-capabilities.xml` に保存されるので、トラブルシュート時に参照できます。

加えて、デフォルト DEVMODE の Color フラグも `Set-PrintConfiguration -Color $true` で明示的に True にしています。これは「ドライバはカラー対応だがプリンタキューの既定がモノクロ」というケースを潰すためです。

## 3. 完了検知ロジック

PDF は ZIP と違って明確なヘッダ・トレーラ署名を持つため、完了検知は素直に書けます。

`Workflow/SpoolWatcher.cs` の `WaitForStableAsync`:

1. **ヘッダ確認**: 先頭 5 バイトが `"%PDF-"` (0x25 0x50 0x44 0x46 0x2D) であること
2. **トレーラ確認**: 末尾 2 KiB のどこかに `"%%EOF"` バイト列が含まれること
3. **静粛期間**: 1500 ms ファイルサイズが変化しないこと
4. **排他オープン**: `FileShare.None` で `FileStream` を開ければ書込みハンドルが解放されている

最大 30 秒待機し、いずれかの条件を満たさなかった場合は対象ファイルを `.failed/` に退避して次のジョブに備えます。

`%%EOF` を末尾 2 KiB に限定しているのは、PDF が増分更新 (incremental update) で複数の `%%EOF` を持つことがあるためです。最後の `%%EOF` がファイル末尾近傍に存在することを以て「現在書込み中ではない」と判定しています。

## 4. レンダリング (`PdfToPngRenderer`)

`PDFtoImage` NuGet (PDFium ラッパ) を使用。

```csharp
using var pdfStream = File.OpenRead(pdfPath);
var options = new RenderOptions(Dpi: 300);
var bitmaps = Conversion.ToImages(
    pdfStream, leaveOpen: false, password: null, options: options);

int pageIndex = 1;
foreach (SKBitmap bmp in bitmaps)
{
    var outPath = Path.Combine(outDir, $"page_{pageIndex:D3}.png");
    using var fs = File.Create(outPath);
    using var skStream = new SKManagedWStream(fs);
    bmp.Encode(skStream, SKEncodedImageFormat.Png, quality: 100);
    pageIndex++;
}
```

| パラメータ | 値 | 備考 |
|---|---|---|
| DPI | 300.0 | `RenderOptions(Dpi: 300)` |
| カラーモード | カラー (SKBitmap 既定) | Print To PDF 経由のため入力 PDF が既にカラー |
| 出力フォーマット | PNG | `SKEncodedImageFormat.Png` |
| 品質 | 100 | PNG はロスレスのため実質意味なし。互換性指定 |
| ファイル名 | `page_NNN.png` (3 桁ゼロ詰め) | 1 始まり |

### 4.1 ネイティブ依存

`PDFtoImage` は次の 2 つのネイティブ DLL を引き連れてきます:

| DLL | 提供元 | RID |
|---|---|---|
| `pdfium.dll` | bblanchon/pdfium-binaries | win-x64 / win-arm64 |
| `libSkiaSharp.dll` | SkiaSharp 公式 | win-x64 / win-arm64 |

`dotnet publish -r win-x64 --no-self-contained` の `runtimes/win-x64/native/` 配下に配置されます。Install スクリプトはこれらを `C:\Program Files\VirtualPrintDemo\` へまるごとコピーします。

## 5. ジョブフォルダのレイアウト

```
C:\VirtualPrintDemo\<yyyyMMdd_HHmmss>_PrintJob\
  ├─ print.pdf          ← 元の PDF を保全 (デバッグ・再レンダ用)
  ├─ page_001.png       ← 1 ページ目
  ├─ page_002.png
  └─ page_NNN.png
```

`print.pdf` を残しているのは、ユーザーが PDF をそのまま使いたいケース (アーカイブ・別アプリでの参照) に備えるためと、`--render` モードでの再現性を確保するためです。

## 6. `--render` モード (CI / バッチ用)

```powershell
& 'C:\Program Files\VirtualPrintDemo\VirtualPrinter.App.exe' `
    --render 'C:\path\to\some.pdf'
```

任意の PDF を 1 回だけレンダリングして終了します。出力フォルダは `C:\VirtualPrintDemo\<日時>_PrintJob\` (常駐モードと同じ命名規則)。CI でゴールデンファイル比較するときや、`.failed/` に退避された PDF を手動で再試行するときに便利です。

## 7. インストール時のドライバ・ポート設定

`scripts\Install-VirtualPrinter.ps1`:

```powershell
$portPath = 'C:\VirtualPrintDemo\.spool\spool.pdf'
Add-PrinterPort -Name $portPath
Add-Printer -Name 'Virtual Print Demo' `
            -DriverName 'Microsoft Print To PDF' `
            -PortName $portPath

# カラーをデフォルトに固定
Set-PrintConfiguration -PrinterName 'Virtual Print Demo' -Color $true

# Capabilities を検証してログに残す
$printer = Get-CimInstance Win32_Printer -Filter "Name='Virtual Print Demo'"
$capsXml = $printer.GetPrintCapabilitiesAsXml()
$capsXml | Out-File 'C:\VirtualPrintDemo\printer-capabilities.xml' -Encoding UTF8
```

Local Port Monitor を「ファイルパスをポート名に渡す」用法で使う点は旧実装 (XPS) と同じです。違いはドライバとファイル拡張子だけ。

## 8. 既知の制約

- ARM64 ホストでの動作は publish 構成上は対応しているが、本リポジトリでは現状 x64 環境で end-to-end の自動検証のみ実施済
- Print To PDF はジョブ名をポートに伝えないため、出力フォルダ名はタイムスタンプベース (旧 XPS 実装と同じ制約)
- 同時印刷ジョブは依然として後勝ち (ローカルファイルポートが単一パスへ書き込むため)
- 暗号化 PDF が来た場合は `Conversion.ToImages` がパスワードを要求して失敗する。現実的にはアプリが暗号化 PDF を Print To PDF 経由で生成することはないが、`--render` で外部 PDF を渡す場合に注意

## 9. 関連ドキュメント

- [README.md](../README.md) / [README-en.md](../README-en.md) — 利用者向け概要・インストール手順
- [architecture.md](architecture.md) — コンポーネント構成
- [troubleshooting.md](troubleshooting.md) — 症状別の診断手順
- [design-history.md](design-history.md) — 設計判断の歴史 (§7 が PDF 移行)
- [xps-internals.md](xps-internals.md) — 旧 XPS パイプラインの歴史的リファレンス
