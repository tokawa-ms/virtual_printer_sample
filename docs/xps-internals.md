# XPS / OpenXPS / OPC Piece Streaming 内部仕様

> Virtual Print Demo は Microsoft XPS Class Driver の出力を解釈する都合上、
> XPS の OPC 物理構造を深く触ります。このドキュメントはその仕様面の覚書です。

## 1. XPS の物理構造

XPS (XML Paper Specification) は **OPC (Open Packaging Conventions, ISO/IEC 29500-2)** を物理層に使う ZIP ベースのパッケージ形式です。OPC の役割は次の通り:

- パッケージ = ZIP アーカイブ
- 「パート」= ZIP 内の 1 エントリ ＋ MIME Content-Type
- パート間の関係は `_rels/<partname>.rels` (ルートは `_rels/.rels`) に XML で記述
- パッケージ全体の Content-Type 表は `[Content_Types].xml`

XPS は OPC の上に次の論理構造を定義します:

```
[Package root]
  └─relationship: http://schemas.microsoft.com/xps/2005/06/fixedrepresentation
      → FixedDocumentSequence.fdseq
           ├─DocumentReference → Documents/1/FixedDocument.fdoc
           │    └─PageContent  → Documents/1/Pages/1.fpage  (XAML)
           │    └─PageContent  → Documents/1/Pages/2.fpage
           │    └─ ...
           └─DocumentReference → Documents/2/FixedDocument.fdoc
                └─ ...
```

`*.fpage` は WPF の `FixedPage` XAML で、XPS 名前空間 `http://schemas.microsoft.com/xps/2005/06` を使います。

## 2. OpenXPS (.oxps) との違い

ECMA-388 で標準化された **OpenXPS** は構造はほぼ XPS と同一ですが、URI と MIME 型が異なります。

| 概念 | XPS (Microsoft 2005/06) | OpenXPS (ECMA-388) |
|---|---|---|
| 名前空間 (`*.fpage` 等) | `http://schemas.microsoft.com/xps/2005/06` | `http://schemas.openxps.org/oxps/v1.0` |
| Fixed Representation relationship | `http://schemas.microsoft.com/xps/2005/06/fixedrepresentation` | `http://schemas.openxps.org/oxps/v1.0/fixedrepresentation` |
| Print Ticket relationship | `…/xps/2005/06/printticket` | `…/oxps/v1.0/printticket` |
| Required Resource relationship | `…/xps/2005/06/required-resource` | `…/oxps/v1.0/required-resource` |
| Restricted Font relationship | `…/xps/2005/06/restricted-font` | `…/oxps/v1.0/restricted-font` |
| Discard Control relationship | `…/xps/2005/06/discard-control` | `…/oxps/v1.0/discard-control` |
| Story Fragments relationship | `…/xps/2005/06/storyfragments` | `…/oxps/v1.0/storyfragments` |
| Content-Type プレフィックス | `application/vnd.ms-package.xps-` | `application/vnd.ms-package.oxps-` |
| ファイル拡張子 | `.xps` | `.oxps` |

**WPF の `System.Windows.Xps.Packaging.XpsDocument` は OpenXPS をネイティブ対応しません。** `GetFixedDocumentSequence()` は legacy XPS の relationship type を検索するため、OpenXPS パッケージでは `null` を返します。

### 本実装の対処 (`NormalizeOpenXpsToXps`)

`XpsToPngRenderer` は、入力パッケージをまず `ZipFile.OpenRead` で覗き、`openxps.org` または `application/oxps` 文字列が見つかれば次の変換を全テキスト部分 (`.xml` / `.rels` / `.fdseq` / `.fdoc` / `.fpage` / `.dict` / `.xaml` / `[Content_Types].xml`) に適用します:

```text
http://schemas.openxps.org/oxps/v1.0/<rel-name>  →  http://schemas.microsoft.com/xps/2005/06/<rel-name>
http://schemas.openxps.org/oxps/v1.0             →  http://schemas.microsoft.com/xps/2005/06
application/vnd.ms-package.oxps-                 →  application/vnd.ms-package.xps-
application/oxps                                 →  application/vnd.ms-package.xps-fixeddocumentsequence+xml
```

`ZipArchive.Update` モードで書き戻し、その後通常の XPS として `XpsDocument` で開きます。

## 3. OPC Piece Streaming (重要)

OPC 仕様の §10.1.3 / §10.2 は、パッケージ書き込みの**インターリーブモード**を定義しています。これは「全パートのコンテンツを書き終えてから ZIP を閉じる」ことができない**ストリーミング書き込み**シナリオ向けで、各パートを複数の "piece" に分割して書きます。

### ZIP エントリの命名規則

```
<part-name>/[<n>].piece           ; n = 0, 1, 2, ... (非最終ピース)
<part-name>/[<n>].last.piece      ; 最終ピース (n は通し番号)
```

たとえば `[Content_Types].xml` パートが 13 ピースに分割されると次のエントリができます:

```
[Content_Types].xml/[0].piece
[Content_Types].xml/[1].piece
[Content_Types].xml/[2].piece
...
[Content_Types].xml/[12].piece
[Content_Types].xml/[13].last.piece
```

### Microsoft XPS Class Driver の挙動

Microsoft XPS Class Driver を経由してプリンタキューに XPS を流し込むと、**ほぼ常に piece streaming 形式**で書き出されます。これはプリンタが進捗的に処理を始められるようにするための仕様準拠の動きです。

### `System.IO.Packaging.ZipPackage` の挙動

.NET の `ZipPackage` は OPC §10.1.3 のピース再構築を**自動で行いません**。そのため次の現象が起きます:

- パッケージは「開ける」(コンストラクタは例外を投げない)
- しかし論理的な `[Content_Types].xml` や `_rels/.rels` パートが見つからない
- 結果 `XpsDocument.GetFixedDocumentSequence()` が `null`
- 呼び出し側は「XPS なのにシーケンスが無い」と困惑する

> 本実装で実際に遭遇した症状で、エラー文言は `XPS has no fixed document sequence.` でした。

### 本実装の対処 (`ReassembleOpcPieces`)

レンダリング前に次の処理を行います。

1. `ZipFile.OpenRead` で全エントリを走査
2. 正規表現 `^(.+)/\[(\d+)\](\.last)?\.piece$` でピースエントリを判定
3. パート名 (キャプチャ 1) ごとにピース番号 (キャプチャ 2) を昇順に並べた `SortedDictionary<int, byte[]>` に格納
4. ピース以外のエントリ (非ストリーミングな単一パート) はそのまま `List<(name, data)>` へ
5. `<path>.reassembled` に新しい ZIP を作成して、ピース部分は連結バイト列として 1 エントリで書き、その他はそのまま書く
6. 元のファイルを `File.Move(overwrite: true)` で置き換え

これにより `[Content_Types].xml` や `Documents/1/Pages/1.fpage` が単一エントリで存在する素直な XPS になり、`XpsDocument` が問題なく開けます。

## 4. ZIP の完了検知

ローカルファイルポートに書き出されるスプールファイルは、書き込み途中で `FileSystemWatcher` のイベントを発生させます。Microsoft XPS Class Driver は次のような挙動を見せることがあります:

- 本ジョブの前に **2 バイトの先行データ** (`0D 0A`) を書き出してから接続を閉じ、その後本物の XPS を別の書込みで送信
- 本物の書込みは数 MB をブロック単位で流し込む (ピース単位で複数 flush)

このため単純な「サイズが N ミリ秒変化しなければ完了」では失敗します。本実装は **ZIP の完結性そのもの** を完了の基準にしています。

### 検出条件 (`SpoolWatcher.WaitForStableAsync`)

すべて満たすときに「完了」と判定:

1. **ZIP ローカルヘッダー** (`50 4B 03 04`) がファイル先頭にある
2. **EOCD シグネチャ** (`50 4B 05 06`) が末尾 64 KiB の中にある
3. ファイルサイズが直近 1500 ms 変化していない
4. `FileShare.None` で排他オープンできる (= 書込みハンドルが解放されている)

タイムアウト (既定 30 秒) を超えても完結しない場合は、対象ファイルを削除して次のイベントを待ちます。これにより 2 バイトの先行データだけが残ったまま延々と再処理されることを防いでいます。

EOCD は ZIP 仕様上、最後の 22 〜 65557 バイトのどこかにあります。本実装では十分余裕をとって末尾 64 KiB をスキャンしています。

## 5. レンダリングパイプラインの順序

`XpsToPngRenderer.RenderCore` が行う前処理は決まった順序で実行されます。

```
[input XPS file]
    │
    ├─① ReassembleOpcPieces      (piece-streamed なら単一エントリ化)
    │
    ├─② NormalizeOpenXpsToXps    (OpenXPS なら legacy XPS に正規化)
    │
    ▼
[normalized XPS file]
    │
    ▼
new XpsDocument(file, FileAccess.Read)
    │
    ▼
seq.References → docRef.GetDocument() → pageRef.GetPageRoot()
    │
    ▼
RenderTargetBitmap (300 DPI) → PngBitmapEncoder → page_NNN.png
```

①と②は独立した変換で、両方が同時に必要な場合もあります (例: piece-streamed な OpenXPS パッケージ)。

## 6. 参考リンク

- ISO/IEC 29500-2 (OPC) - <https://www.iso.org/standard/71691.html>
- ECMA-388 (OpenXPS) - <https://ecma-international.org/publications-and-standards/standards/ecma-388/>
- Microsoft Docs / XPS Document Format - <https://learn.microsoft.com/windows/win32/printdocs/xps-document-format>
- Microsoft Docs / Print Spooler Local Port Monitor - <https://learn.microsoft.com/windows-hardware/drivers/print/local-port-monitor>
- ZIP File Format Specification (PKWARE APPNOTE.TXT)
