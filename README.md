# ChBrowser

Windows 用の 5ch.io 専用ブラウザ。

<!-- スクリーンショット (任意): docs/screenshots/ にファイルを置いて差し替え -->
<!-- ![スクリーンショット](docs/screenshots/main.png) -->

## 主な機能

- **ポータブル構成** 設定ファイルはexeのあるフォルダに保存
- **4 ペイン構成** かちゅ～しゃやJaneなどの一般的なレイアウトをデフォルトとし、ペインヘッダのドラッグでレイアウトを自由に変更可
- **スレ表示モード** レス順 / ツリー (重複あり) / ツリー (重複なし) を切替可
- **リッチスクロールバー** 今どきの「スレ内で返信の多いレス」「画像URLが張られてるレス」などがスクロールバー上に表示
- **どんぐり対応** — 通常どんぐりとメール認証どんぐりを切り替えて書き込み可能
- **生成AI画像対応** プロンプトなどの情報を取得できるものはポップアップで表示
- **ビューワー搭載** JaneXenoライクなビューワー搭載
- **(実験的)LLM連携** OpenAI互換APIでLLMと連携。板一覧、スレ一覧、スレッド内容についてチャットで聞ける

## 動作要件

- Windows 11 : 追加モジュールなしに動作可。
- (ソースからビルドする場合) : [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0)が必要

配布バイナリは .NET 8 ランタイム内包しており、初回起動時に %TEMP% への展開処理が走るため少々起動に時間がかかります (2 回目以降のキャッシュ済み起動は高速)。
Windows10でもWebView2を入れれば理論上動作しますが、動作確認をとっていません。

## ビルド

```pwsh
dotnet publish src/ChBrowser/ChBrowser.csproj `
    -c Release -r win-x64 --self-contained true `
    -p:PublishSingleFile=true `
    -p:IncludeAllContentForSelfExtract=true `
    -p:PublishReadyToRun=true
```

出力: `src/ChBrowser/bin/Release/net8.0-windows/win-x64/publish/ChBrowser.exe` (約 180MB)

ビルドオプションの方針:
- `PublishSingleFile=true` + `--self-contained true`: 1 つの EXE で配布、.NET ランタイムも同梱 (= 配布先 PC にインストール不要)
- `IncludeAllContentForSelfExtract=true`: 初回起動時にすべてのコンテンツを `%TEMP%` に展開 (= WPF / WebView2 のリフレクションが安定動作)
- `PublishReadyToRun=true`: 事前 AOT コンパイル。サイズが 30〜50% 増える代わりに、起動時の JIT コストが減って 2 回目以降の起動が速い
- 圧縮 (`EnableCompressionInSingleFile`) は付けない: サイズより起動速度を優先

## 実行 / データの保存場所

`ChBrowser.exe` を任意のフォルダに置いて実行すると、その横に `data/` フォルダが作られ、以下が全部そこに保存されます:

- `app/config.json` — アプリ設定
- `app/favorites.json` — お気に入り
- `app/layout.json` — ウィンドウ位置 / ペインレイアウト
- `5ch.io/<board>/` — subject.txt / *.dat / *.idx.json
- `cache/images/` — 画像キャッシュ
- `donguri/` — どんぐり Cookie / 推定 Lv 状態
- `ng/rules.json` — NG ルール
- `kakikomi.txt` — 書き込みログ (任意機能)
- `themes/default/` — `post.html` / `post.css` / 各ペイン CSS のユーザカスタマイズ用
(初期では配置されていません。本ディレクトリにないファイルは規定値で動作します)

別マシンへ移行する場合は `ChBrowser.exe` と `data/` フォルダだけコピーすれば設定込みで動きます。

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

本プロジェクトは[MIT License](https://github.com/pawapawa041950/chbrowser/blob/main/LICENSE)です。
