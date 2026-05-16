# トラブルシューティング

> Virtual Print Demo が想定通り動かないときの診断手順とよくある事例集です。
> アーキテクチャ全体は [architecture.md](architecture.md)、XPS の内部仕様は [xps-internals.md](xps-internals.md) を併読してください。

## 1. まず確認するもの

### 1.1 ログファイル

```
C:\VirtualPrintDemo\virtual-printer.log
```

`Info` / `Err` の 2 種類で書かれており、ウォッチャ起動・ジョブ検知・前処理・ページ書き出しのすべてが追記されます。問題の切り分けはほぼ常にここから始めます。

### 1.2 失敗ファイル

完了検知に成功したが、後段のレンダリングで失敗した XPS は次の場所に保全されます。

```
C:\VirtualPrintDemo\.failed\failed_<日時>.xps
```

このファイルは `--render` モードで再現できます:

```powershell
& 'C:\Program Files\VirtualPrintDemo\VirtualPrinter.App.exe' --render `
  'C:\VirtualPrintDemo\.failed\failed_20260516_191848719.xps'
```

ログ末尾に `Reassembled OPC piece-streamed package.` や `Normalized OpenXPS package to legacy XPS.` の有無を確認すると、どの前処理が走ったかが分かります。

### 1.3 常駐プロセスの存在確認

```powershell
Get-Process VirtualPrinter.App -ErrorAction SilentlyContinue |
  Format-Table Id, ProcessName, StartTime, MainModule
```

`--watch` モードのプロセスが 1 つだけ動いていることを確認します (`Mutex` 制御により 2 つ目以降は即終了)。

### 1.4 プリンタとポートの存在確認

```powershell
Get-Printer     -Name 'Virtual Print Demo'   | Format-List Name, PortName, DriverName, Shared, Published
Get-PrinterPort -Name 'C:\VirtualPrintDemo\.spool\spool.xps' | Format-List
```

`DriverName` が `Microsoft XPS Class Driver`、`PortName` がローカルファイルポート (`C:\VirtualPrintDemo\.spool\spool.xps`) になっていれば OK。

## 2. 症状別

### 印刷したが PNG が出ない

| 確認順 | アクション |
|---|---|
| 1 | `virtual-printer.log` の末尾を読む |
| 2 | `Processing spool '...'` のログが**ない**なら → スプールに XPS が届いていない。プリンタとポートの設定を確認 |
| 3 | `Processing spool '...'` のログは**ある**が `Render failed` で終わっている → `.failed/` のファイルを `--render` で再現してエラー詳細を取得 |
| 4 | それでも分からない場合は、新規ジョブのログとともに [Issues](https://github.com/tokawa-ms/virtual_printer_sample/issues) へ |

### 印刷ダイアログにプリンタが出ない

```powershell
# プリンタが本当に未登録なら ↓ で確認
Get-Printer | Where-Object Name -like '*Virtual*'

# 再インストール (Administrator)
powershell -ExecutionPolicy Bypass -File scripts\Install-VirtualPrinter.ps1
```

旧 publish のキャッシュが残っていて意図しないアーキの bin がコピーされている可能性もあります。気になる場合:

```powershell
Remove-Item -Recurse -Force src\VirtualPrinter.App\bin, src\VirtualPrinter.App\obj
```

を実行してから再インストール。

### ログに `never became a complete XPS package`

スプールに書かれたデータが 30 秒以内に**完結 ZIP** (`PK\x03\x04` 開始かつ `PK\x05\x06` 終了) にならなかったことを意味します。
本実装は該当ファイルを自動的に削除して次のジョブを待ちます。通常の挙動です。

ただし、毎回のジョブで連続して発生する場合は以下を疑ってください:

- 期待と異なるプリンタドライバが Local Port に書き込んでいる (XPS 以外のデータ)
- ポートのパスがウォッチャの監視先と一致していない
- ウイルス対策ソフトが書込みをブロックしている

### ログに `XPS has no fixed document sequence` または `Central Directory corrupt`

最新版では発生しないはずです。発生した場合は次を確認してください:

- インストールされているバイナリが古い: `Get-FileHash 'C:\Program Files\VirtualPrintDemo\VirtualPrinter.App.dll'` を実行し、リポジトリの最新 publish 結果と比較
- `Uninstall-VirtualPrinter.ps1` → `dotnet publish` → `Install-VirtualPrinter.ps1` の手順で再インストール

### `Another watcher instance is already running` でウォッチャがすぐ終わる

シングルインスタンス制御 (`Mutex("Global\\VirtualPrintDemo.Watcher")`) が発動しただけで、害はありません。先行プロセスが正常に動いているか:

```powershell
Get-Process VirtualPrinter.App | Format-Table Id, StartTime
```

を確認してください。本当に動いていないのにメッセージだけ出ている場合は、孤立 Mutex の可能性があるためログオフ → ログオンするか、`Stop-Process -Id <PID>` で残骸を消して再起動します。

### ARM64 機なのに win-x64 がインストールされている

```powershell
Get-Process VirtualPrinter.App | ForEach-Object {
    Add-Type -TypeDefinition @'
using System; using System.Diagnostics;
public class P { public static string Arch(int pid) {
    return Process.GetProcessById(pid).MainModule.FileName; } }
'@
    [P]::Arch($_.Id)
}
```

で実体パスを確認し、`C:\Program Files\VirtualPrintDemo\VirtualPrinter.App.exe` の PE ヘッダ Machine タイプを `dumpbin /headers` または `file` 相当で確認します。次の PowerShell スニペットでも判別できます:

```powershell
$exe = 'C:\Program Files\VirtualPrintDemo\VirtualPrinter.App.exe'
$fs = [IO.File]::OpenRead($exe)
$br = New-Object IO.BinaryReader($fs)
$fs.Seek(0x3C, 'Begin') | Out-Null
$peOff = $br.ReadInt32()
$fs.Seek($peOff + 4, 'Begin') | Out-Null
$machine = $br.ReadUInt16()
$br.Dispose(); $fs.Dispose()
switch ($machine) {
    0x8664 { 'x64 (AMD64)' }
    0xAA64 { 'ARM64' }
    default { '0x{0:X4}' -f $machine }
}
```

`x64 (AMD64)` が返ってきたら、過去にクロスビルドした publish をそのまま使っていた可能性があります。`bin\` / `obj\` を消してから再インストールしてください。

## 3. 強制リセット手順

ありとあらゆる残骸を消したい場合:

```powershell
# 1. アンインストール
powershell -ExecutionPolicy Bypass -File scripts\Uninstall-VirtualPrinter.ps1

# 2. 出力ディレクトリも完全に消す (過去の PNG とログも消える点に注意)
Remove-Item C:\VirtualPrintDemo -Recurse -Force -ErrorAction SilentlyContinue

# 3. ビルドキャッシュを破棄
Remove-Item -Recurse -Force src\VirtualPrinter.App\bin, src\VirtualPrinter.App\obj

# 4. 再ビルド + 再インストール
dotnet publish src\VirtualPrinter.App -c Release -r win-x64 --no-self-contained  # or win-arm64
powershell -ExecutionPolicy Bypass -File scripts\Install-VirtualPrinter.ps1
```

## 4. 既知の制限事項

| 制約 | 詳細 | 対応策 |
|---|---|---|
| 同時印刷ジョブ | ローカルファイルポートは単一パスへの書き込みのため、ほぼ同時のジョブが衝突する可能性あり | 並列用途には自前ポートモニタが必要 |
| ジョブ名取得 | XPS Class Driver はジョブ名をポートへ伝えない | 出力フォルダ名はタイムスタンプベース。XPS 内の `Metadata/Job_PT.xml` に PrintTicket があり、そこから推定はできるが安定しない |
| サービス化不可 | WPF のレンダリング API は対話的セッションが必要 | `HKLM\…\Run` でユーザーログオン時起動 |
| 配布ランタイム | .NET 8 Desktop Runtime をホストにインストールしておく必要あり | csproj で `<SelfContained>true</SelfContained>` に切り替えれば同梱配布も可 |
