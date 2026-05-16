# アーキテクチャ詳細

> このドキュメントは Virtual Print Demo (v2.x, PDF パイプライン) の内部構造を深掘りするためのリファレンスです。
> ユーザー向けインストール手順は [README.md](../README.md) を参照してください。
> PDF パイプラインの技術的詳細 (PDFium / PDFtoImage / 完了検知) は [pdf-pipeline.md](pdf-pipeline.md) を参照してください。
> 旧 XPS Class Driver パイプライン (v1.x 以前) のリファレンスは [xps-internals.md](xps-internals.md) (歴史的アーカイブ) を、移行経緯は [design-history.md](design-history.md) を参照してください。

## 1. 全体構成

```
┌──────────────────┐      印刷       ┌────────────────────────────┐
│ ユーザーアプリ    │ ─────────────▶ │ Windows 印刷スプーラ         │
└──────────────────┘                 │  (spoolsv.exe)               │
                                      └────────────┬─────────────────┘
                                                   │ PDF バイトストリーム
                                                   ▼
                                      ┌────────────────────────────┐
                                      │ Microsoft Print To PDF      │
                                      │   PageOutputColor = Color   │
                                      └────────────┬─────────────────┘
                                                   │
                                                   ▼
                                      ┌────────────────────────────┐
                                      │ ローカルファイルポート       │
                                      │ C:\VirtualPrintDemo\         │
                                      │   .spool\spool.pdf           │
                                      └────────────┬─────────────────┘
                                                   │
                            FileSystemWatcher イベント
                                                   │
                                                   ▼
                                      ┌────────────────────────────┐
                                      │ VirtualPrinter.App           │
                                      │   --watch (常駐)             │
                                      │                              │
                                      │  ┌──────────────────┐        │
                                      │  │ SpoolWatcher     │ ① 完了検知 + 直列化
                                      │  ├──────────────────┤        │
                                      │  │ PdfToPngRenderer │ ② PDFium で PNG 化
                                      │  └──────────────────┘        │
                                      └────────────┬─────────────────┘
                                                   │
                                                   ▼
                        C:\VirtualPrintDemo\<日時>_PrintJob\
                          ├─ print.pdf
                          └─ page_NNN.png   (カラー, 300 DPI)
```

## 2. プロセスと生存サイクル

| プロセス | アーティファクト | 起動契機 | 停止契機 |
|---|---|---|---|
| 印刷ジョブ送出元 | アプリ依存 | ユーザーの「印刷」操作 | — |
| `spoolsv.exe` | Windows 標準 | OS 起動時 | OS 停止時 |
| `VirtualPrinter.App.exe --watch` | `C:\Program Files\VirtualPrintDemo\` | ユーザーログオン (`HKLM\…\Run`) ＋ Install スクリプト末尾の即時起動 | `Uninstall-VirtualPrinter.ps1` または手動 |

`VirtualPrinter.App.exe --watch` は **Windows サービスではなく**、`HKLM\Software\Microsoft\Windows\CurrentVersion\Run\VirtualPrintDemoWatcher` 経由で**ユーザーログオン時に対話的セッションで起動するウィンドウ無しプロセス**です。WPF GUI モードや BitmapFrame ベースの画像読込ヘルパーは対話的セッションでないと安定動作しないため、サービス化していません (PDFium 自体はヘッドレスで動作しますが、互換性のために現方式を維持)。

シングルインスタンス制御は `Mutex("Global\\VirtualPrintDemo.Watcher")` で行います。複数ユーザーが同時ログオンしている場合でも常駐は 1 つだけです。

## 3. 起動モード

`VirtualPrinter.App.exe` は `App.xaml.cs` で引数を見て 3 モードに分岐します。

| 引数 | モード | 概要 |
|---|---|---|
| (なし) | GUI | `MainWindow` を表示。ログのスニペットと出力フォルダを開くボタン |
| `--watch` | Watcher | ウィンドウを表示せずに `SpoolWatcher` を起動して `.spool` を監視 |
| `--render <path>` | One-shot | 指定された **PDF** を 1 回だけレンダリングして終了 (CI / バッチ用途) |

`ShutdownMode` も切り替えています:
- GUI: `OnMainWindowClose`
- Watcher: `OnExplicitShutdown` (UI スレッドの dispatcher を維持しつつ常駐)
- One-shot: ジョブ完了後 `Shutdown(0)`

## 4. コンポーネント詳細

### 4.1 `SpoolWatcher` (Workflow/SpoolWatcher.cs)

- `FileSystemWatcher` で `C:\VirtualPrintDemo\.spool\*.pdf` を監視 (`Created` / `Changed` / `Renamed`)
- 起動時に既存ファイル (再起動時の取りこぼし) もスキャン
- `SemaphoreSlim _gate` で **HandleAsync を直列化**: 連続イベントの取り合いを避け、同一スプールに対する競合を防ぐ
- 完了検知は `WaitForStableAsync`:
  - 先頭 5 バイトが `"%PDF-"` であること
  - 末尾 2 KiB のどこかに `"%%EOF"` が含まれること
  - 1500 ms の静粛期間
  - 排他オープン (`FileShare.None`) で書込みハンドル解放を確認
  - 最大 30 秒待機。失敗時は対象ファイルを `.failed/` に退避して次のジョブに備える
- 完了したスプールは固有名 (`job_YYYYMMDD_HHMMSSfff_<GUID>.pdf.tmp`) に `File.Move` してから処理開始。これにより同名ファイルへの次ジョブ書込みを即座にブロックしない
- 失敗時は `.failed/failed_YYYYMMDD_HHMMSSfff.pdf` に元のスプールを保全 (デバッグ用途)

### 4.2 `PdfToPngRenderer` (Rendering/PdfToPngRenderer.cs)

PDFium (`PDFtoImage` 5.x NuGet) を使用してページごとに PNG を書き出します。流れ:

1. 入力 `pdfPath` を `<jobDir>\print.pdf` にコピー (保全)
2. `PDFtoImage.Conversion.ToImages(stream, leaveOpen: false, password: null, options: new RenderOptions(Dpi: 300))` を呼ぶ
3. 返ってきた `IEnumerable<SKBitmap>` を 1 ページずつ列挙
4. `SKManagedWStream` + `SKEncodedImageFormat.Png` で `page_NNN.png` (3 桁ゼロ詰め) に書き出し
5. `SKBitmap` を `Dispose`

| パラメータ | 既定値 | 場所 |
|---|---|---|
| DPI | 300.0 | `new RenderOptions(Dpi: 300)` |
| カラーモード | カラー | 入力 PDF が既にカラー (Print To PDF が `psk:Color` で送る) |
| 出力フォーマット | PNG | `SKEncodedImageFormat.Png` |
| 出力ファイル名 | `page_NNN.png` (3 桁ゼロ詰め) | `RenderCore` |
| 保全 PDF | `print.pdf` | ジョブフォルダ直下 |

ネイティブ依存:

- `pdfium.dll` (PDFtoImage が同梱, win-x64 / win-arm64)
- `libSkiaSharp.dll` (SkiaSharp が同梱, win-x64 / win-arm64)

詳細は [pdf-pipeline.md](pdf-pipeline.md) を参照。

### 4.3 `Logger` (Logger.cs)

- `C:\VirtualPrintDemo\virtual-printer.log` に追記
- `Info` / `Error(message, ex)` の 2 種類
- 例外スタックトレースも `ToString()` でそのまま記録 (Failed file 解析時に役立つ)

### 4.4 `MainWindow`

GUI モード時のみ表示される最小限の管理画面。ログを末尾から表示し、出力フォルダをエクスプローラで開くボタンがあります。常駐運用時には**表示されません**。

## 5. インストーラ (`scripts\Install-VirtualPrinter.ps1`)

おおまかな処理順:

1. 管理者権限チェック (`#Requires -RunAsAdministrator`)
2. 旧バージョンの清掃 (プリンタ・ポート・常駐プロセス)。**新ポート `spool.pdf` と旧ポート `spool.xps` の両方**を対象にする (v1.x からのアップグレード対応)
3. ホスト CPU 検出 → `PROCESSOR_ARCHITECTURE` から `win-x64` / `win-arm64` を決定
4. publish フォルダがなければ `dotnet publish` を実行 (RID は上で決めた値)
5. `C:\Program Files\VirtualPrintDemo\` をクリアして publish 結果をコピー (`pdfium.dll` / `libSkiaSharp.dll` を含む)
6. `HKLM\Software\Microsoft\Windows\CurrentVersion\Run\VirtualPrintDemoWatcher` を登録 (= 全ユーザーログオン時自動起動)
7. ウォッチャを即時起動
8. `Add-PrinterPort -Name "C:\VirtualPrintDemo\.spool\spool.pdf"` で**ローカルファイルポート**を作成
9. `Add-Printer -Name "Virtual Print Demo" -DriverName "Microsoft Print To PDF" -PortName ...`
10. `Set-PrintConfiguration -Color $true` で**カラーをデフォルト**に固定
11. `Win32_Printer.GetPrintCapabilitiesAsXml()` を呼んで `PageOutputColor` フィーチャの宣言を検証し、結果を `C:\VirtualPrintDemo\printer-capabilities.xml` に保存

> Windows の `Add-PrinterPort` は、`-PrinterHostAddress` を指定せずパスを `Name` に渡すと **Local Port Monitor** にそのファイルパスを登録します (`Standard TCP/IP` ではない)。ここが本実装の鍵で、これにより印刷データがそのファイルに PDF として書き出されます。

## 6. アンインストーラ (`scripts\Uninstall-VirtualPrinter.ps1`)

- 常駐プロセスを `Stop-Process -Id` (PID 指定) で停止
- `Remove-Printer` → `Remove-PrinterPort` (新ポート `spool.pdf` と旧ポート `spool.xps` の両方を best-effort で削除)
- `HKLM\…\Run\VirtualPrintDemoWatcher` を削除
- `C:\Program Files\VirtualPrintDemo\` を削除
- 過去の MSIX 試作版がインストールされていた場合 (`Get-AppxPackage VirtualPrintDemo*` / 自己署名証明書) も best-effort で削除

`C:\VirtualPrintDemo\` 配下の出力 PNG / PDF / ログは**残します**。誤って消したくない印刷結果がある場合に備えるためです。

## 7. 拡張ポイント

| やりたいこと | 変更箇所 |
|---|---|
| ジョブ名を出力フォルダ名に反映したい | PDF メタデータ (`/Info` 辞書の `/Title`) から推定するか、別途 PrintTicket フックを実装 |
| 出力先を変えたい | `App.OutputRoot` を編集、または環境変数 / 設定ファイル化 |
| DPI を上げ下げしたい | `PdfToPngRenderer` の `RenderOptions(Dpi: ...)` を変更 |
| 並列ジョブを安全に扱いたい | ローカルファイルポートを複数作成し、それぞれを別 `SpoolWatcher` で監視 |
| JPEG / WebP で出したい | `SKEncodedImageFormat` を `Jpeg` / `Webp` に切り替え、quality を調整 |
| カラーをモノクロに切り替えたい | `Set-PrintConfiguration -Color $false`、または PDFium 出力を `SKColorType.Gray8` に変換 |

## 8. 既知の制約

- ローカルファイルポートは単一パスに上書き書き込みされるため、**同時印刷ジョブ**を投げると後勝ちで一部失敗の可能性あり (実用上ほぼ問題ないが、エンタープライズ用途では独自ポートモニタを推奨)
- ジョブ名は Print To PDF からポートへ伝わらないため、出力フォルダ名は `<日時>_PrintJob` 固定 (タイムスタンプで識別)
- レンダラ自体はヘッドレス可能だが、GUI モード / `BitmapFrame` 利用箇所が WPF を伴うため、現状は対話的セッション前提 (= サービス化不可)
- 配布先 PC に .NET 8 Desktop Runtime が必要 (`--self-contained true` に切り替えて配布物に同梱することも可能)
- 暗号化 PDF を `--render` に渡すと PDFium がパスワードを要求して失敗する
