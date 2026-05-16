# 設計検討の歴史

> Virtual Print Demo が現在の「ローカルファイルポート + 常駐ウォッチャ」方式に至るまでに、複数の方式を検討・試作しました。このドキュメントはその全記録です。「なぜこれを使わなかったのか」を後から振り返れるようにすることが目的です。

## 0. 要件 (出発点)

- Windows 標準の印刷ダイアログから印刷先プリンタとして選べる
- 印刷ジョブを画像化し、`C:\VirtualPrintDemo\` に **PNG** で保存
- 複数ページは**ページごとに別ファイル**
- 言語は **C# など Windows 親和性の高いもの**
- 配布の障壁はなるべく低く

参考: <https://learn.microsoft.com/ja-jp/windows-hardware/drivers/devapps/msix-manifest-specification-print-support-virtual-printer>

## 1. 検討候補 (初期)

| 候補 | 概要 | 想定言語 | 評価 |
|---|---|---|---|
| **v3 XPSDrv ドライバ** | 自前の XPSDrv プリンタドライバ。プリンタクラス INF を書き、ドライバ DLL を作って WHQL 署名 | C/C++ | ❌ ドライバ署名と INF 開発が重い |
| **v4 ドライバ + Print Workflow App** | Microsoft 標準ドライバを使い、UWP/.NET の Print Workflow Foreground/Background Task を MSIX で連携 | C# (UWP/.NET) | △ 試作したが INF と PFN マッピングの壁 |
| **Print Support App (PSA)** | v4 ドライバ無し、HardwareID マッチで MSIX/UWP アプリが直接ジョブを受け取れる新仕様 | C# (WinUI/UWP) | △ Windows 11 限定、署名前提 |
| **ローカルファイルポート + 常駐ウォッチャ** | 標準ドライバ + Local Port Monitor でファイルへ XPS 書き出し、ファイル監視で後処理 | C# (WPF) | ✅ **採用** |
| **TCP プリンタ + 自前サーバ** | 標準ドライバ + RAW/9100 ポート、自前 TCP リスナで XPS 受信 | C# | △ 単一マシン内で TCP は冗長、ファイアウォール考慮も増える |

最終的に「v3/v4 ドライバの開発・署名コストを払わず、配布時のユーザー手間を最小化する」観点でローカルファイルポート案を採用しました。

## 2. 試作 1: MSIX + Print Workflow Foreground Task

最初の試作です。次のような構成にしました。

```
Microsoft XPS Class Driver (v4) を共有
    ↓
windows.printWorkflowForegroundTask 拡張を持つ MSIX
    ↓
WPF (.NET 8) でジョブを受け取り、XPS を PNG にレンダリング
```

### 実装したもの

- WPF (.NET 8) プロジェクト + `Microsoft.Windows.SDK.NET` で WinRT 射影
- `Package.appxmanifest` に `<uap:Extension Category="windows.printWorkflowForegroundTask">` を宣言
- 自己署名証明書 (`Create-SelfSignedCert.ps1`) と `Build-Msix.ps1` で **MSIX をビルド・署名**
- 自己署名 PFX を信頼ルートに登録 → MSIX を `Add-AppxPackage`
- `Add-Printer` で **Microsoft XPS Class Driver** を使ったプリンタを作成

### ぶつかった壁

#### 2.1 WinRT 射影の問題
.NET 8 + `Microsoft.Windows.SDK.NET` (10.0.19041 / 22621) のいずれを試しても、`Windows.Graphics.Printing.Workflow.*` 型が `internal` のままで C# から直接使えない状態でした。`dynamic` 経由で WinRT API を呼ぶ workaround で回避。

#### 2.2 印刷時の致命的失敗
プリンタには登録できて、印刷ダイアログにも出現したのですが、印刷を実行すると **「印刷できませんでした」エラー**で失敗。Print Workflow App は起動すらしませんでした。

#### 2.3 根本原因
Print Workflow App をプリンタキューに紐付けるには、

- **v4 ドライバ INF の `PrintWorkflowAppPackageFamilyName` ディレクティブ**、または
- **Print Support App (PSA) の HardwareID マッチ**

のいずれかが必要で、`windows.printWorkflowForegroundTask` 拡張**だけ**では特定のプリンタには結びつきません。Microsoft 標準の XPS Class Driver はそのどちらも提供しないため、Workflow が起動しなかったのです。

参考: <https://learn.microsoft.com/windows-hardware/drivers/print/print-workflow-applications>

### 試作 1 で得た学び

- v4 ドライバの INF を弄らない限り Microsoft 標準ドライバとの Workflow 連携は事実上不可能
- MSIX + 自己署名は配布の障壁にもなる (信頼ルートへの証明書登録が必須)
- WinRT 射影は .NET 8 では internal が残るので `dynamic` 経由が必要 — 知見として残しておく価値あり

## 3. 試作 2: Print Support App (PSA)

Windows 11 で導入された Print Support App は MSIX 配布の正式な解で、INF 不要・HardwareID マッチ。

しかし:

- Windows 11 (22H2 以降推奨) 限定 (要件: Windows 10 でも動作)
- ストア署名 or 同等のコード署名が事実上前提 (実用上)
- Microsoft XPS Class Driver は HardwareID を `PRINTENUM\Microsoft_XPS_Document_Writer` 等で持つが、外部から PSA を後付けで紐付けるのは設計上複雑

採用見送り。

## 4. 採用方式: ローカルファイルポート + 常駐ウォッチャ

「印刷先がファイルでよいなら、Local Port Monitor をそのまま使えばいい」という発想に立ち戻りました。

```
Microsoft XPS Class Driver
    ↓
Local Port Monitor (port name = ファイルパス)
    ↓
C:\VirtualPrintDemo\.spool\spool.xps   (= XPS package がそのまま書き込まれる)
    ↓
FileSystemWatcher (常駐 WPF プロセス)
    ↓
XPS → PNG レンダリング (300 DPI / ページ)
```

メリット:

- **MSIX / 署名 / INF / WHQL 不要**
- Windows 10 1809 以降であればどの環境でも動作
- WPF / .NET 8 だけで完結 (C/C++ ドライバ開発不要)
- ARM64 ネイティブビルドも可能

デメリットと割り切り:

- ジョブ名がポートに伝わらない → 出力フォルダ名はタイムスタンプベース
- 同時印刷は単一ファイルに上書きされるため後勝ち (ヘビーな並列用途は想定外)
- 常駐プロセスが必要 (`HKLM\…\Run` に登録)

実装中に追加で発覚した 2 つの罠 (→ 解決済):

### 4.1 「2 バイトの前置きファイル」問題
Microsoft XPS Class Driver の Local Port パイプラインは、本ジョブの前に小さな前置きデータ (`0D 0A` の 2 バイト) を書き込むことがあります。当初の完了検知 (「サイズが N ms 不変なら完了」) では、この前置きを完成品と誤認していました。

**対策**: ZIP ローカルヘッダー (`PK\x03\x04`) と EOCD (`PK\x05\x06`) の両方を確認する厳格な完了検知に切替。詳細は [xps-internals.md](xps-internals.md#4-zip-の完了検知)。

### 4.2 OPC Piece Streaming 問題
1.1 MB の正常な XPS なのに `XpsDocument.GetFixedDocumentSequence()` が `null` を返す現象に遭遇。原因は **OPC piece streaming** (`[Content_Types].xml/[0].piece` のように 1 パートが ZIP 内で複数エントリに分割されている形式)。`.NET` の `System.IO.Packaging.ZipPackage` は自動再構築しないため、論理パートが「見えない」状態になっていた。

**対策**: レンダリング前にピース → 単一エントリへ再構築する変換を追加。詳細は [xps-internals.md](xps-internals.md#3-opc-piece-streaming-重要)。

## 5. 不採用方式の遺物について

`legacy-msix/` フォルダ (現リポジトリでは `.gitignore` 済) には MSIX 試作版の成果物 (`Package.appxmanifest`, `Build-Msix.ps1`, `Create-SelfSignedCert.ps1`, 自己署名 PFX, 生成済 MSIX 等) がローカル保管されています。**配布物には含めません**。

理由:
- 自己署名証明書の秘密鍵 (`.pfx`) を公開リポジトリに置くべきでない
- MSIX 版は今や全く使われない (起動経路もない)
- アンインストールスクリプトは「過去に MSIX 版を入れた環境」に対しては best-effort で削除を試みるが、現リポジトリ単体では MSIX を再構築しない

## 6. もしまた MSIX 方式に戻すなら

将来「やはり MSIX で配布したい」となった場合の最短ルート:

1. v4 ドライバを内製し、INF に `PrintWorkflowAppPackageFamilyName=<PFN>` を宣言する (= WHQL 署名前提)
2. または Windows 11 限定の **Print Support App (PSA)** として再設計。`<uap10:Extension Category="windows.printSupport">` で HardwareID をマッチ
3. 既存の `XpsToPngRenderer` (piece streaming / OpenXPS 対応済) はそのまま流用可能
4. ジョブ取得は `Windows.Graphics.Printing.Workflow.PrintWorkflowJobStartingEventArgs` / `PrintSupportSessionInfo` 経由
5. .NET 8 + WinRT の internal 問題は `Microsoft.Windows.CsWinRT` のカスタム射影でクリーンに解決できる

ただし、要件「全 Windows で動く・署名なし」を満たしたい限り、現在の方式が最良という結論は変わりません。

## 7. カラー対応のための PDF 移行 (v2.x)

§4 で採用した「Microsoft XPS Class Driver + Local Port Monitor」方式は配布性・互換性の点では理想的でしたが、運用していくうちに**カラーで印刷したジョブもすべてグレースケール PNG として出力される**という致命的な問題が判明しました。

### 7.1 原因の特定

姉妹プロジェクト `virtual_printer_sample_netprint` (ネットプリント観測ツール) で同一スプール仕様を再利用しようとした際、本問題を集中的に調査した結果、原因が以下であることが分かりました。

- **Microsoft XPS Class Driver は PrintCapabilities に `PageOutputColor` を宣言していない**
- PrintTicket で `psk:Color` を指定しても、ドライバ側が「カラー印刷をサポートしている」と認識しないため、Edge / Chrome / Word などのアプリケーションが**送信前にコンテンツをグレースケールへラスタライズ**してしまう
- スプールに到達した XPS の時点でカラー情報が失われており、後段のレンダラを変えても回復不可能

確認コマンド (PowerShell):

```powershell
[xml]$caps = (Get-Printer -Name 'Virtual Print Demo' | Get-PrintConfiguration -PrintTicketXml)
# PageOutputColor を持つ Feature を探す
([xml](Get-WmiObject -Class Win32_Printer -Filter "Name='Virtual Print Demo'").GetPrinterCapabilities().PrintCapabilities).
  SelectNodes("//*[local-name()='Feature' and contains(@name,'PageOutputColor')]")
# → XPS Class Driver では 0 件
```

### 7.2 検討した代替案

| 案 | 概要 | 評価 |
|---|---|---|
| **A. Print Workflow で PrintTicket を上書き** | v4 ドライバ + Workflow App で `psk:Color` を強制注入 | ❌ §2 で判明したとおり Workflow App は標準ドライバに紐付かない |
| **B. WinRT `Windows.Data.Pdf` で PDF をレンダ** | Print To PDF に切り替え、UWP 系 API で PNG 化 | △ 動くが MaxPage や解像度制御が貧弱、DPI 指定不可、フォント描画品質が落ちる |
| **C. Magick.NET + Ghostscript** | PDF → PNG をネイティブツールで実施 | ❌ Ghostscript の AGPL ライセンスが配布要件と相性悪い |
| **D. clawPDF などサードパーティ仮想プリンタ** | 既製の OSS 仮想プリンタを採用 | ❌ プロジェクトの趣旨 (自前で印刷経路を持つこと) に反する |
| **E. Microsoft Print To PDF + PDFium (PDFtoImage)** | OS 標準ドライバを Print To PDF に切替え、PDFium で 300 DPI カラー PNG 化 | ✅ **採用** |

### 7.3 採用方式: Print To PDF + PDFium

```
Microsoft Print To PDF (OS 標準, PageOutputColor=Color を宣言)
    ↓
Local Port Monitor (port name = ファイルパス)
    ↓
C:\VirtualPrintDemo\.spool\spool.pdf   (= 完全な PDF ファイル)
    ↓
FileSystemWatcher (常駐 WPF プロセス)
    ↓
PDFium (PDFtoImage NuGet) → 300 DPI カラー PNG
```

採用理由:

- **OS 標準ドライバのまま**で済む (追加ドライバ・MSIX・WHQL 署名すべて不要)
- Print To PDF は PrintCapabilities に **`psk:PageOutputColor` フィーチャ** と `psk:Color` オプションを明示宣言している ため、アプリ側がカラーで送信する
- PDFium は `PDFtoImage` NuGet 経由で win-x64 / win-arm64 両方のネイティブバイナリが揃う
- 入力が **PDF** に変わるため、`%PDF-` ヘッダ + `%%EOF` トレーラによる**自然な完了検知**が可能 (OPC piece streaming の問題は完全に消滅)

副次的な効果:

- §4.1 「2 バイト前置きファイル」問題は PDF パスにも残るが、`%PDF-` チェックで弾けるため対策コードがシンプルに
- §4.2 OPC Piece Streaming 問題はそもそも発生しない (XPS 経路を使わないため)
- レンダリングが WPF Visual API 非依存になり、将来的にはサービス化やヘッドレス運用も検討しやすい

### 7.4 残課題と既知の制約

- 出力ファイル名は引き続き `page_NNN.png` (タイムスタンプベースのジョブフォルダ内)。Print To PDF はジョブ名をポートに伝えないため、フォルダ名の改善は依然として課題
- PDFium はネイティブ DLL を伴うため、ARM64 機での動作確認は別途必要 (publish 出力に `pdfium.dll` が含まれることはビルド時に確認済)
- 旧 XPS パイプライン用に追加した `XpsToPngRenderer.cs` / OPC 再構築 / OpenXPS 正規化コードはすべて削除済。再評価する場合は git 履歴から取得する

### 7.5 参考: PrintCapabilities の比較

| ドライバ | `PageOutputColor` 宣言 | デフォルト | 備考 |
|---|---|---|---|
| Microsoft XPS Class Driver | ❌ なし | (アプリがグレースケールに前段ラスタライズ) | カラー情報がスプール到達時点で失われる |
| Microsoft Print To PDF | ✅ あり (Color / Monochrome) | Color | `Set-PrintConfiguration -Color $true` でデフォルト固定可能 |

この調査結果は姉妹プロジェクト [`virtual_printer_sample_netprint`](https://github.com/tokawa-ms/virtual_printer_sample_netprint) の検証ノートが出発点になっています。同プロジェクトでは更にネットプリント番号アップロードを実装していますが、本リポジトリは「カラー PNG 出力までで完結する最小構成」として PDF パイプラインの中核部分のみを採り込みました。
