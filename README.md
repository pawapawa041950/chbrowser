# ChBrowser

Windows 用の 5ch.io 専用ブラウザ。WPF + .NET 8 + WebView2 で実装。

<!-- スクリーンショット (任意): docs/screenshots/ にファイルを置いて差し替え -->
<!-- ![スクリーンショット](docs/screenshots/main.png) -->

## 主な機能

- **4 ペイン構成** (お気に入り / 板一覧 / スレ一覧 / スレ表示) — ペインヘッダのドラッグでレイアウト変更可
- **スレ表示モード** — レス順 / ツリー (重複あり) / ツリー (重複なし) を切替
- **どんぐり (acorn) 認証 + 書き込み機能** — メール認証 Cookie の自動更新、書込ログ (`kakikomi.txt`) 出力
- **画像 / 動画 / YouTube サムネイルのインライン表示** — クリックでアプリ内画像ビューアに遷移、AI 生成画像メタデータ (Stable Diffusion infotext / ComfyUI workflow) の自動抽出表示
- **x.com / pixiv URL の自動展開** — fxtwitter API / pixiv ajax 経由でサムネ画像 URL に解決
- **NG ルール** — 名前 / ID / ワッチョイ / 本文 × 部分一致 / 正規表現、グローバルまたは板単位、有効期限付き、連鎖あぼーん対応
- **ショートカット / マウスジェスチャー** — カテゴリ別 (全体 / スレ表示 / スレ一覧 / 板一覧 / お気に入り) にカスタマイズ可
- **テーマ** — レス HTML テンプレ (`post.html`) と各ペインの CSS をユーザが編集可

## 動作要件

- Windows 10 1809 以降 (x64)
- [WebView2 Runtime](https://developer.microsoft.com/en-us/microsoft-edge/webview2/) — Windows 11 では標準同梱
- (ソースからビルドする場合) [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0)

配布バイナリは self-contained / single-file 形式なので .NET 8 ランタイムを別途インストールする必要はありません。

## ダウンロード

[Releases](../../releases) ページから最新の `ChBrowser.exe` を取得してください。

## ビルド

```pwsh
dotnet publish src/ChBrowser/ChBrowser.csproj `
    -c Release -r win-x64 --self-contained true `
    -p:PublishSingleFile=true `
    -p:IncludeAllContentForSelfExtract=true `
    -p:EnableCompressionInSingleFile=true
```

出力: `src/ChBrowser/bin/Release/net8.0-windows/win-x64/publish/ChBrowser.exe`

## 実行 / データの保存場所

ポータブル運用が前提です。`ChBrowser.exe` を任意のフォルダに置いて実行すると、その横に `data/` フォルダが作られ、以下が全部そこに保存されます:

- `app/config.json` — アプリ設定
- `app/favorites.json` — お気に入り
- `app/layout.json` — ウィンドウ位置 / ペインレイアウト
- `5ch.io/<board>/` — subject.txt / *.dat / *.idx.json
- `cache/images/` — 画像キャッシュ
- `donguri/` — どんぐり Cookie / 推定 Lv 状態
- `ng/rules.json` — NG ルール
- `kakikomi.txt` — 書き込みログ (任意機能)
- `themes/default/` — `post.html` / `post.css` / 各ペイン CSS のユーザカスタマイズ用 (設定画面 → デザイン → 「既定 CSS / HTML を生成」で展開)

別マシンへ移行する場合は `ChBrowser.exe` と `data/` フォルダごとコピーすれば設定込みで動きます。

開発時は環境変数 `CHBROWSER_DATA_DIR` でデータディレクトリを差し替え可能です。

## ディレクトリ構成

```
src/ChBrowser/
├── Models/          ドメインモデル (Post / Board / NgRule 等)
├── ViewModels/      MVVM ビューモデル (CommunityToolkit.Mvvm)
├── Views/           WPF ビュー (Window / Dialog / Pane / Settings パネル)
│   └── Panes/       4 ペイン (Favorites / BoardList / ThreadList / ThreadDisplay)
├── Services/        ビジネスロジック (Api / Storage / Image / Ng / Donguri / Shortcuts / Render / Theme / Logging / Url / WebView2)
├── Controls/        WPF カスタムコントロール (PaneLayoutPanel / WebView2Helper / SearchBoxToggle 等)
├── Converters/      WPF IValueConverter
└── Resources/       WebView2 に流し込む HTML / CSS / JS と app.ico
```

## 免責事項

- 本ソフトウェアは 5ch.io / bbspink.com の API・仕様に依存します。サーバ側の変更で動作しなくなる可能性があります。
- 本ソフトウェアは非公式クライアントであり、5ch.net / 5ch.io 運営とは関係ありません。
- ご利用は自己責任でお願いします。

## ライセンス

(策定中。MIT ライクで非商用利用に限定する方向で検討中。)
