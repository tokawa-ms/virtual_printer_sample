# Virtual Print Demo

[English README](README-en.md) · [日本語 README (このファイル)](README.md)

Windows 10 / 11 (x64 / **ARM64** 両対応) で動作する**仮想プリンタのサンプル実装**です。
Windows 標準の印刷ダイアログから "Virtual Print Demo" を選んで印刷すると、印刷ジョブを
ページごとに **PNG 画像** として `C:\VirtualPrintDemo\<日時>_PrintJob\page_NNN.png`
に保存します。

- 言語: **C# / WPF / .NET 8**
- 依存ドライバ: Windows 標準同梱の **Microsoft XPS Class Driver** のみ
- 配布形式: **MSIX 不要・コード署名不要**
- 動作確認: Windows 11 24H2 (x64) / .NET 8 Desktop Runtime

> **ライセンス**: [MIT](LICENSE) © 2026 tokawa-ms

---

## 目次

1. [アーキテクチャ概要](#アーキテクチャ概要)
2. [対応プラットフォーム](#対応プラットフォーム)
3. [前提ソフトウェア](#前提ソフトウェア)
4. [インストール手順](#インストール手順)
   - [x64 Windows でのインストール](#x64-windows-でのインストール)
   - [ARM64 Windows でのインストール](#arm64-windows-でのインストール)
5. [動作確認](#動作確認)
6. [アンインストール手順](#アンインストール手順)
7. [更新（再インストール）](#更新再インストール)
8. [トラブルシューティング](#トラブルシューティング)
9. [リポジトリ構成](#リポジトリ構成)
10. [詳細ドキュメント](#詳細ドキュメント)
11. [ライセンス](#ライセンス)

---

## アーキテクチャ概要

```
 ┌────────────────────┐                                                  ┌────────────────────┐
 │ 任意の Windows アプリ │  ─印刷─▶  Microsoft XPS Class Driver  ─XPS─▶ │ ローカルファイルポート │
 │ (メモ帳 / Edge 等)  │                                                  │ C:\VirtualPrintDemo │
 └────────────────────┘                                                  │  \.spool\spool.xps │
                                                                          └────────┬───────────┘
                                                                                   │
                                                                  FileSystemWatcher による検知
                                                                                   │
                                                                                   ▼
                                       ┌──────────────────────────────────────────────────────────┐
                                       │ VirtualPrinter.App.exe --watch (常駐 / HKLM\Run 自動起動) │
                                       │   1) ZIP 完了検知 (PK\x03\x04 + EOCD)                      │
                                       │   2) OPC ピース・ストリーミング再構築                       │
                                       │   3) OpenXPS → 正規 XPS 名前空間正規化                     │
                                       │   4) XpsDocument で読み込み → 300 DPI でページごとに描画    │
                                       └──────────────────────────────────────────────────────────┘
                                                                                   │
                                                                                   ▼
                                            C:\VirtualPrintDemo\<日時>_PrintJob\page_NNN.png
```

| 層 | 実体 | 役割 |
|---|---|---|
| プリンタキュー | `Microsoft XPS Class Driver` + ローカルファイルポート | 印刷ダイアログに登場、XPS をファイルへ書き出す |
| 常駐サービス | `VirtualPrinter.App.exe --watch` (WPF / .NET 8) | スプールファイルを検知して XPS → PNG に変換 |
| 出力 | `C:\VirtualPrintDemo\<timestamp>_PrintJob\page_NNN.png` | ページごとの 300 DPI PNG |

「常駐サービス」とは言っても Windows サービスではなく、`HKLM\…\Run` に登録した**ユーザーログオン時起動のウィンドウ無し常駐プロセス**です。詳細は [docs/architecture.md](docs/architecture.md) を参照してください。

---

## 対応プラットフォーム

| 項目 | 値 |
|---|---|
| OS | Windows 10 1809 以降 / Windows 11 |
| CPU | **x64 ネイティブ** および **ARM64 ネイティブ** |
| .NET | .NET 8 Desktop Runtime (ホスト CPU と同アーキ版) |
| 権限 | インストール / アンインストール時のみ **管理者** |

Install スクリプトはホストの `PROCESSOR_ARCHITECTURE` を判定し、`win-x64` / `win-arm64` のどちらの publish を行うかを自動で切り替えます。

---

## 前提ソフトウェア

| ソフトウェア | 用途 | 入手先 |
|---|---|---|
| .NET 8 SDK | リポジトリからビルドする場合 | <https://dotnet.microsoft.com/download/dotnet/8.0> |
| .NET 8 Desktop Runtime | バイナリ配布で受け取る場合 (ホストと同アーキ) | 同上 |
| Windows PowerShell 5.1 もしくは PowerShell 7+ | インストール / アンインストールスクリプト | Windows 同梱 |

`Microsoft XPS Class Driver` は Windows に標準同梱されているため、ドライバの追加導入は不要です。

---

## インストール手順

> インストール中、`Set-ExecutionPolicy Bypass` 相当でスクリプトを起動するため、PowerShell ウィンドウを **管理者** として開いてください。

### x64 Windows でのインストール

```powershell
# 1) リポジトリの取得
git clone https://github.com/tokawa-ms/virtual_printer_sample.git
cd virtual_printer_sample

# 2) ビルド (任意 — Install スクリプトが自動 publish するためスキップ可)
dotnet publish src\VirtualPrinter.App -c Release -r win-x64 --no-self-contained

# 3) インストール (管理者 PowerShell)
powershell -ExecutionPolicy Bypass -File scripts\Install-VirtualPrinter.ps1
```

スクリプトは次の処理を行います:

1. 既存の "Virtual Print Demo" プリンタ・ローカルポート・常駐プロセスを停止/削除
2. 必要なら `dotnet publish -r win-x64 --no-self-contained` を実行
3. publish 成果物を **`C:\Program Files\VirtualPrintDemo\`** にコピー
4. `HKLM\Software\Microsoft\Windows\CurrentVersion\Run\VirtualPrintDemoWatcher` を登録（全ユーザーログオン時に `VirtualPrinter.App.exe --watch` を自動起動）
5. ウォッチャを即時起動（再ログオン不要で印刷テスト可能）
6. ローカルファイルポート `C:\VirtualPrintDemo\.spool\spool.xps` を作成
7. `Microsoft XPS Class Driver` を使った **Virtual Print Demo** プリンタを登録

### ARM64 Windows でのインストール

ARM64 Windows でも手順はまったく同じです:

```powershell
git clone https://github.com/tokawa-ms/virtual_printer_sample.git
cd virtual_printer_sample
powershell -ExecutionPolicy Bypass -File scripts\Install-VirtualPrinter.ps1
```

Install スクリプトは内部で次のように動きます:

- `PROCESSOR_ARCHITECTURE = ARM64` を検出
- `dotnet publish -r win-arm64 --no-self-contained` を実行（ネイティブ ARM64 バイナリを生成）
- 生成された `bin\Release\net8.0-windows\win-arm64\publish\` を `C:\Program Files\VirtualPrintDemo\` に配置

実行時には `==> Target runtime: win-arm64` と表示されます。

#### x64 マシンでクロスビルドして配布する場合

開発機が x64 でも、`-r win-arm64` を指定すれば ARM64 用 EXE が生成できます。生成された publish フォルダをそのまま ARM64 機にコピーし、Install スクリプトを ARM64 機で実行すれば導入できます（スクリプトは既存の publish 成果物があれば再ビルドをスキップします）。

```powershell
# x64 開発機で
dotnet publish src\VirtualPrinter.App -c Release -r win-arm64 --no-self-contained
# ↓ src\VirtualPrinter.App\bin\Release\net8.0-windows\win-arm64\publish\ を ARM64 機へコピー
# ARM64 機で
powershell -ExecutionPolicy Bypass -File scripts\Install-VirtualPrinter.ps1
```

> ARM64 Windows では x64 バイナリも互換実行できますが、起動速度・描画性能のために**ネイティブ ARM64 publish を強く推奨**します。

---

## 動作確認

1. メモ帳、Edge、Word、PowerPoint など任意のアプリケーションを開く
2. `[ファイル] → [印刷]` で **Virtual Print Demo** を選択
3. 出力先フォルダを確認:
   - `C:\VirtualPrintDemo\<日時>_PrintJob\page_001.png`, `page_002.png`, ...
4. 必要に応じてログを確認:
   - `C:\VirtualPrintDemo\virtual-printer.log`

### スモークテスト（任意）

実プリンタを介さずに、ウォッチャ単体が動いているかをテストできます:

```powershell
# 3 ページの XPS を合成して .spool にドロップ → page_001..003.png を確認
powershell -ExecutionPolicy Bypass -File scripts\Test-Smoke.ps1

# OpenXPS 形式での正規化動作を確認
powershell -ExecutionPolicy Bypass -File scripts\Test-Smoke-OpenXps.ps1
```

---

## アンインストール手順

両アーキ共通です。**管理者 PowerShell** で:

```powershell
powershell -ExecutionPolicy Bypass -File scripts\Uninstall-VirtualPrinter.ps1
```

このスクリプトは次を行います:

1. 常駐ウォッチャプロセス (`VirtualPrinter.App.exe`) の停止
2. `Virtual Print Demo` プリンタの削除
3. ローカルファイルポートの削除
4. `HKLM\…\Run\VirtualPrintDemoWatcher` の削除
5. `C:\Program Files\VirtualPrintDemo\` の削除

> **保持されるもの**: 過去に出力した `C:\VirtualPrintDemo\<日時>_PrintJob\` 配下の PNG とログは消されません。完全に消したい場合は手動で `Remove-Item C:\VirtualPrintDemo -Recurse -Force` を実行してください。

---

## 更新（再インストール）

新しいバイナリに入れ替えるときは、必ず一度アンインストールしてから再インストールします:

```powershell
powershell -ExecutionPolicy Bypass -File scripts\Uninstall-VirtualPrinter.ps1
git pull   # コードを更新
dotnet publish src\VirtualPrinter.App -c Release -r win-x64   --no-self-contained   # ARM64 機なら -r win-arm64
powershell -ExecutionPolicy Bypass -File scripts\Install-VirtualPrinter.ps1
```

---

## トラブルシューティング

| 症状 | 対処 |
|---|---|
| 印刷したが PNG が出ない | `C:\VirtualPrintDemo\virtual-printer.log` を確認。失敗時の XPS は `C:\VirtualPrintDemo\.failed\` に保存される |
| 印刷ダイアログにプリンタが表示されない | 管理者で `Install-VirtualPrinter.ps1` を実行し直す。`==> Target runtime: ...` が ARM64 機で x64 のままなら publish キャッシュを `Remove-Item` する |
| `never became a complete XPS package` がログに出る | 30 秒以内に完結 ZIP がポートに書かれなかった (前置きデータのみ等)。スクリプトが該当ファイルを自動削除して次のジョブを待つので、通常は再印刷で復旧する |
| `Another watcher instance is already running` | 既にウォッチャが常駐している。再インストール時は Install スクリプトが自動で停止するので問題ない |

詳細は [docs/troubleshooting.md](docs/troubleshooting.md) を参照してください。

---

## リポジトリ構成

```
virtual_printer_sample/
├── VirtualPrinter.sln
├── src/VirtualPrinter.App/                WPF (.NET 8) 本体
│   ├── App.xaml(.cs)                       起動と引数ディスパッチ
│   ├── MainWindow.xaml(.cs)                管理用最小 UI
│   ├── Logger.cs                           追記式ファイルロガー
│   ├── Workflow/SpoolWatcher.cs            FileSystemWatcher・完了検知
│   ├── Rendering/XpsToPngRenderer.cs       XPS/OpenXPS/Piece → PNG (300 DPI)
│   └── Assets/                             プレースホルダーアイコン
├── scripts/
│   ├── Install-VirtualPrinter.ps1          管理者向けインストーラ
│   ├── Uninstall-VirtualPrinter.ps1        管理者向けアンインストーラ
│   ├── Generate-Assets.ps1                 アイコン (プレースホルダー) 生成
│   ├── Test-Smoke.ps1                      ウォッチャ単体テスト (XPS)
│   └── Test-Smoke-OpenXps.ps1              ウォッチャ単体テスト (OpenXPS)
├── docs/                                  詳細ドキュメント (下記)
├── LICENSE                                MIT
├── README.md                              本ファイル (日本語)
└── README-en.md                           英語版 README
```

---

## 詳細ドキュメント

| ドキュメント | 内容 |
|---|---|
| [docs/architecture.md](docs/architecture.md) | アーキテクチャ詳細、各コンポーネントの責務、起動モード |
| [docs/xps-internals.md](docs/xps-internals.md) | XPS / OpenXPS / OPC Piece Streaming の構造と本実装での対処方法 |
| [docs/design-history.md](docs/design-history.md) | 過去に検討・実装した方式 (MSIX + Print Workflow / Print Support App など) と採用 / 不採用の理由 |
| [docs/troubleshooting.md](docs/troubleshooting.md) | 詳細トラブルシューティングと診断手順 |

---

## ライセンス

[MIT License](LICENSE) © 2026 **tokawa-ms**
