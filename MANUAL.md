# ImageViewer - ゲームアセット管理ツール マニュアル

## 概要

ゲーム開発用アセット（画像・音声・フォント）を軽量にブラウズし、タグとメタデータで整理するWindows デスクトップアプリ。  
SQLite DB はアセットフォルダ内 `_db/` に保存されるため、フォルダごと移動・コピーしてもデータが維持される。

## 動作環境

- Windows 10/11
- .NET 8 Runtime

## インストール

```
cd image-viewer
dotnet build ImageViewer/ImageViewer.csproj -c Release
```

実行ファイル: `ImageViewer/bin/Release/net8.0-windows/ImageViewer.exe`

## 起動

```bash
# GUIで起動
ImageViewer.exe

# フォルダを指定して起動
ImageViewer.exe "D:\GameAssets"
```

フォルダのドラッグ＆ドロップでも開ける。

## 対応ファイル形式

| 種別 | 拡張子 |
|------|--------|
| 画像 | .png .jpg .jpeg .bmp .gif .tiff .tif .ico .webp .dds .wdp .jxr |
| 音声 | .wav .mp3 .ogg .flac .aac .wma |
| フォント | .ttf .otf |

## 画面構成

```
+--Toolbar----[Open][Grid/List][Sort▼]---[path]---------+
|               |  [検索... (Ctrl+F)]                     |
| Folder Tree   |  ファイル一覧（リスト or グリッド）      |
|               |  ───────────────────────                |
|  Tags         |  [1/42] [800x600] [120%] [file.png]    |
|  [env]        |  [sprite] [character]                   |
|  [character]  |  ┌──────────────────────┐               |
|               |  │   プレビュー          │               |
|               |  │   (画像/音声/フォント) │               |
|               |  └──────────────────────┘               |
+-[status]----------------------------------[Grid/BG]----+
```

## キーボードショートカット

### ナビゲーション

| キー | 動作 |
|------|------|
| J / ↓ | 次の画像 |
| K / ↑ | 前の画像 |
| Space | 次の画像 |
| Back | 前の画像 |
| PageDown | 10件送り（Grid: 20件） |
| PageUp | 10件戻し（Grid: 20件） |
| Home / End | 先頭 / 末尾 |
| Tab | フォルダツリーにフォーカス |
| Enter | 外部ビューアで開く |

### 表示

| キー | 動作 |
|------|------|
| G | グリッド / リスト切替 |
| B | プレビュー背景切替（暗 / チェッカーボード / 白 / 灰） |
| Mouse Wheel | ズーム（プレビュー上） |
| ドラッグ | パン（ズーム時） |
| ダブルクリック / F | フィット（ズームリセット） |
| + / - | ズームイン / アウト |

### 検索・フィルタ

| キー | 動作 |
|------|------|
| Ctrl+F | 検索ボックスにフォーカス |
| Escape | 検索・タグフィルタをクリア |
| (タグパネル) | クリックでタグフィルタのON/OFF（AND条件） |

### タグ・メタデータ

| キー | 動作 |
|------|------|
| T | タグ追加（複数選択時は一括） |
| Ctrl+A | 全選択 |
| Ctrl+M | メタデータ編集（タイプ・用途・ノート） |
| Ctrl+E | メタデータ付きカタログを JSON 出力 |
| 右クリック | タグ削除 / エクスプローラーで開く |

### その他

| キー | 動作 |
|------|------|
| Ctrl+O | フォルダを開く |
| Ctrl+D | 重複ファイル検出 |
| ? | ショートカットヘルプ表示 |

## 音声プレビュー

- リスト上の音声ファイルにマウスを乗せると自動再生（ホバー再生）
- 終端に達すると自動ループ（先頭に戻って繰り返し再生）
- マウスが別アイテムに移動するか、リストから離れると停止
- AudioPanel の Play/Stop ボタンでも操作可能
- 対応形式は OS にインストールされたコーデックに依存（OGG は別途コーデック要）

## フォントプレビュー

- .ttf / .otf を選択すると3サイズでサンプルテキストを表示
- Latin (AaBbCcDd 0123) + 英文 + 日本語（あいうえお 漢字）
- フォント名を情報バーに表示

## クラウドストレージ連携

アセットフォルダを Google Drive 等の同期対象に置くと、DB・サムネイル・設定が自動同期される。

### 排他制御

- フォルダを開くと `_db/.lock` ファイルが作成される（マシン名・PID・タイムスタンプ）
- 15秒ごとのハートビートでタイムスタンプを更新
- 別マシンで同じフォルダを開こうとすると警告ダイアログ（強制オープン可）
- アプリ終了時にロック自動解放。クラッシュ時は45秒後に自動失効

### 注意事項

- 同時に2台から書き込み操作を行わないこと（SQLite 破損リスク）
- アプリを閉じてから同期完了を待つのが安全
- `_db/` だけをシンボリックリンクで同期対象にすれば、大容量アセット本体は同期しない

## データ保存先

アプリが管理するファイルは全てアセットフォルダ内の `_db/` に格納される。外部には何も作らない。

```
<アセットフォルダ>/
  _db/
    assets.db              # SQLite DB（タグ・メタデータ・ハッシュ）
    thumbs/                # サムネイルキャッシュ（PNG）
    viewer-settings.json   # ウィンドウ設定
    asset-catalog.json     # Ctrl+E で出力されるカタログ
    .lock                  # 排他ロックファイル（起動中のみ）
```

## ライブ更新

フォルダを開いている間、ファイルの追加・削除・リネームを自動検出して一覧を更新する（FileSystemWatcher、500msデバウンス）。手動でフォルダを再オープンする必要はない。

## ソート

ツールバーの ComboBox で切替:
- **名前**: パスのアルファベット順（デフォルト）
- **サイズ**: ファイルサイズ降順
- **更新日時**: 更新日時の新しい順
- **拡張子**: 拡張子順 → 名前順

## タグ運用

- タグは自由テキスト。使用中のタグが左下パネルに表示される
- 複数タグのフィルタは AND 条件
- Shift/Ctrl+クリックで複数選択 → T キーで一括タグ付け
- タグが全アセットから外れると自動削除

## メタデータ

Ctrl+M でアセット個別に設定:

| フィールド | 説明 | 選択肢例 |
|-----------|------|----------|
| タイプ | アセットの種類 | texture, sprite, spritesheet, audio_sfx, audio_bgm, font, ui, vfx, tileset |
| 用途 | 使用先 | character, environment, ui, effect, ambient, system |
| ノート | 自由記述 | — |

## 重複検出

Ctrl+D で実行。先頭64KBのSHA256ハッシュ＋ファイルサイズで判定。  
ハッシュは初回計算後 DB に永続化される（2回目以降は即座に結果表示）。

## MCP サーバー（LLM連携）

アセット DB を LLM から直接操作するための MCP (Model Context Protocol) サーバー。

### セットアップ

```bash
cd mcp-server
npm install
```

### Claude Code に接続

プロジェクトまたはユーザーの `.claude/settings.json`:

```json
{
  "mcpServers": {
    "asset-mgr": {
      "command": "node",
      "args": ["D:/develop/tools/image-viewer/mcp-server/index.mjs", "D:/GameAssets"]
    }
  }
}
```

`D:/GameAssets` はアセットフォルダの実際のパスに置き換える。

### Codex CLI に接続

```bash
codex mcp add asset-mgr -- node D:/develop/tools/image-viewer/mcp-server/index.mjs D:/GameAssets
```

### 提供ツール

| ツール | 説明 |
|--------|------|
| search_assets | 名前・タグ・タイプ・用途・拡張子で検索 |
| get_asset_details | 単一アセットの全情報取得 |
| set_metadata | タイプ・用途・ノートを設定 |
| add_tag / remove_tag | タグの追加・削除 |
| bulk_tag | パスパターンで一括タグ付け |
| list_tags | 使用中タグ一覧（件数付き） |
| list_asset_types | 使用中タイプ・用途の一覧 |
| get_catalog | 全カタログ JSON 出力 |
| stats | DB 統計情報 |

### Godot 連携の例

LLM に対して:

```
asset-mgr の get_catalog で全アセットを取得し、
各 texture タイプのアセットに対して Godot の .import ファイルを生成してください。
sprite タイプはフィルタなし・ミップマップなしで設定してください。
```

LLM が MCP 経由でカタログを読み取り、適切な Godot リソース定義を生成する。

### 一括メタデータ割り振りの例

```
asset-mgr の bulk_tag で textures/character 以下に "character" タグを付けてください。
次に search_assets で character タグのある .png を検索し、
全てに set_metadata で asset_type=sprite, usage=character を設定してください。
```
