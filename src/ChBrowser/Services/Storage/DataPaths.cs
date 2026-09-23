using System;
using System.IO;

namespace ChBrowser.Services.Storage;

/// <summary>
/// アプリのデータルートと、その配下の標準ディレクトリ/ファイルパスを解決する。
/// **ポータブルアプリ**: 既定では exe と同じディレクトリ配下の <c>data/</c> を使う
/// (= exe ディレクトリごと別マシンに持っていけばそのまま動く)。設計書 §4.1 参照。
/// 環境変数 <c>CHBROWSER_DATA_DIR</c> を設定すると差し替え可能(開発時のテスト用)。
///
/// 5ch.io / bbspink.com は内部に多数のサブドメイン (hayabusa9.5ch.io 等) を持ち、
/// 板はそれぞれ異なる host にホストされる。このため板ディレクトリは
/// <c>data/&lt;rootDomain&gt;/&lt;directory_name&gt;/</c> 形式で配置する。
///
/// 単一ファイル exe (= <c>PublishSingleFile=true</c>) との共存:
///   <see cref="AppContext.BaseDirectory"/> は単一ファイル + <c>IncludeAllContentForSelfExtract=true</c>
///   の構成だと一時展開ディレクトリ (= <c>%LOCALAPPDATA%\Temp\.net\...</c>) を指してしまうため、
///   ポータブル運用 (= exe と同じ場所の <c>data/</c>) が壊れる。
///   <see cref="Environment.ProcessPath"/> は実際に起動した exe の絶対パスを返すので、
///   そのディレクトリを使うことでこの問題を回避する。
/// </summary>
public sealed class DataPaths
{
    public string Root { get; }

    public DataPaths(string? rootOverride = null)
    {
        Root = rootOverride
               ?? Environment.GetEnvironmentVariable("CHBROWSER_DATA_DIR")
               ?? Path.Combine(GetExeDirectory(), "data");
    }

    /// <summary>実際に起動した .exe が置かれているディレクトリの絶対パスを返す。
    /// <see cref="Environment.ProcessPath"/> (NET 6+) の dirname を採用、取得不能な
    /// 開発時のレアケースだけ <see cref="AppContext.BaseDirectory"/> へフォールバックする。</summary>
    private static string GetExeDirectory()
    {
        var exePath = Environment.ProcessPath;
        if (!string.IsNullOrEmpty(exePath))
        {
            var dir = Path.GetDirectoryName(exePath);
            if (!string.IsNullOrEmpty(dir)) return dir;
        }
        return AppContext.BaseDirectory;
    }

    public string AppDir          => EnsureDir(Path.Combine(Root, "app"));
    public string NgDir           => EnsureDir(Path.Combine(Root, "ng"));
    public string NgByBoardDir    => EnsureDir(Path.Combine(NgDir, "by_board"));
    public string DonguriDir      => EnsureDir(Path.Combine(Root, "donguri"));
    public string CacheImagesDir  => EnsureDir(Path.Combine(Root, "cache", "images"));
    /// <summary>動画本体ファイルのキャッシュディレクトリ (Phase 動画キャッシュ拡張)。
    /// 画像と同じ index.json (= CacheImagesDir 配下) で集約管理されるが、ファイル本体は
    /// 巨大になりがちなので物理的にディレクトリを分けて運用上の混乱を避ける。
    /// 動画サムネ (JPEG) は Image 同様 CacheImagesDir に置く。</summary>
    public string CacheVideosDir  => EnsureDir(Path.Combine(Root, "cache", "videos"));
    public string ThemesDir       => EnsureDir(Path.Combine(Root, "themes"));

    public string Root5chIo       => EnsureDir(Path.Combine(Root, "5ch.io"));
    public string RootBbspink     => EnsureDir(Path.Combine(Root, "bbspink.com"));

    public string BbsmenuJsonPath   => Path.Combine(Root5chIo, "bbsmenu.json");

    /// <summary>提供者ごとの板一覧キャッシュ。5ch は従来の <see cref="BbsmenuJsonPath"/> (= 互換)、
    /// それ以外は <c>data/&lt;最初の保存ルート&gt;/boardlist.&lt;拡張子&gt;</c>。</summary>
    public string BoardListCachePath(ChBrowser.Services.Bbs.IBbsProvider provider, string extension)
    {
        if (provider.Id == "5ch") return BbsmenuJsonPath;
        var root = provider.StorageRoots.Count > 0 ? provider.StorageRoots[0] : provider.Id;
        return Path.Combine(EnsureDir(Path.Combine(Root, root)), "boardlist." + extension.TrimStart('.'));
    }
    public string LayoutJsonPath    => Path.Combine(AppDir, "layout.json");
    /// <summary>板一覧ペインのツリーの開閉状態 (起動時に前回の状態を再現する)。</summary>
    public string BoardTreeJsonPath => Path.Combine(AppDir, "board_tree.json");
    public string FavoritesJsonPath => Path.Combine(AppDir, "favorites.json");

    /// <summary>アプリ全体設定 (Phase 11)。<see cref="ChBrowser.Models.AppConfig"/> の保存先。</summary>
    public string ConfigJsonPath    => Path.Combine(AppDir, "config.json");

    /// <summary>AI 向けの「各板/スレの使い分け説明」をユーザが自由に書くテキストファイル。
    /// AI チャットを開くと読み込まれ、Strategist / Worker の文脈に注入される (= 板選びの精度向上)。</summary>
    public string AiBoardGuidePath  => Path.Combine(AppDir, "ai-board-guide.txt");

    /// <summary>終了時に開いていたタブ一覧 (= スレ一覧タブ + スレタブの両方、それぞれ並び順を維持) の永続化先。
    /// 起動時に <see cref="ChBrowser.Models.AppConfig.RestoreOpenTabsOnStartup"/> が true なら
    /// このファイルを読み、保存されていた順番で各タブを再オープンする。</summary>
    public string OpenTabsJsonPath => Path.Combine(AppDir, "open_tabs.json");

    // NG ルールは Phase 13e で `data/ng/rules.json` の単一ファイルに統合 (= NgStorage 内で組み立て)。
    // 旧 `global.json` + `by_board/*.json` は NgStorage.LoadAndMigrate が自動で吸収 + 削除する。

    /// <summary>どんぐり/MonaTicket Cookie の永続化先 (Netscape 形式)。Phase 8。</summary>
    public string DonguriCookiesPath => Path.Combine(DonguriDir, "cookies.txt");

    /// <summary>どんぐりの推定 Lv・最終取得時刻などのメタを置く JSON。Phase 8。</summary>
    public string DonguriStateJsonPath => Path.Combine(DonguriDir, "state.json");

    /// <summary>提供者ごとの Cookie (エッヂの edge-token 等) の永続化先 (Netscape 形式)。
    /// <c>data/&lt;最初の保存ルート&gt;/cookies.txt</c>。5ch のどんぐりは <see cref="DonguriCookiesPath"/> で別管理。</summary>
    public string ProviderCookiesPath(ChBrowser.Services.Bbs.IBbsProvider provider)
    {
        var root = provider.StorageRoots.Count > 0 ? provider.StorageRoots[0] : provider.Id;
        return Path.Combine(EnsureDir(Path.Combine(Root, root)), "cookies.txt");
    }

    /// <summary>投稿者情報 (アイコン・プロフィール) の保存先 (<c>data/&lt;掲示板のルート&gt;/authors.json</c>)。</summary>
    public string AuthorProfilesPath(ChBrowser.Services.Bbs.IBbsProvider provider)
    {
        var root = provider.StorageRoots.Count > 0 ? provider.StorageRoots[0] : provider.Id;
        return Path.Combine(EnsureDir(Path.Combine(Root, root)), "authors.json");
    }

    /// <summary>reddit のログイン状態の表示用メモ (<c>data/reddit.com/auth.json</c>、ユーザー名と最終ログイン時刻だけ)。
    /// ログインの Cookie 自体は WebView2 の reddit 用プロファイルが持つ。</summary>
    public string RedditAuthPath => Path.Combine(EnsureDir(Path.Combine(Root, "reddit.com")), "auth.json");

    /// <summary>reddit の接続テスト (設定 → 認証) の結果を書き出すファイル。</summary>
    public string RedditProbeReportPath => Path.Combine(EnsureDir(Path.Combine(Root, "reddit.com")), "probe.txt");

    /// <summary>書き込み記録 (kakikomi.txt)。Jane Xeno フォーマット互換、UTF-8 (BOM なし) + CRLF、append-only。
    /// ユーザがメモ帳等で同時編集できるよう、書込時のみ open → 即 close する運用。</summary>
    public string KakikomiTxtPath => Path.Combine(Root, "kakikomi.txt");

    /// <summary>板の保存ディレクトリ。host から root domain を判定。</summary>
    public string BoardDir(string host, string directoryName)
    {
        var domain = ExtractRootDomain(host);
        return EnsureDir(Path.Combine(Root, domain, directoryName));
    }

    public string SubjectTxtPath(string host, string directoryName)
        => Path.Combine(BoardDir(host, directoryName), "_subject.txt");

    /// <summary>板の SETTING.TXT を置く場所。アンダースコア prefix は subject.txt と同方針
    /// (= dat ファイル群と並んでも視認しやすいよう板メタファイルにだけ印を付ける)。</summary>
    public string SettingTxtPath(string host, string directoryName)
        => Path.Combine(BoardDir(host, directoryName), "_SETTING.TXT");

    public string DatPath(string host, string directoryName, string threadKey)
        => Path.Combine(BoardDir(host, directoryName), threadKey + ".dat");

    public string IdxJsonPath(string host, string directoryName, string threadKey)
        => Path.Combine(BoardDir(host, directoryName), threadKey + ".idx.json");

    /// <summary>スナップショット方式 (reddit 等) のスレの補助情報 (外部 ID → アプリ内番号の対応表)。<c>doc/reddit-design.md</c> §3 B3。</summary>
    public string ThreadMetaPath(string host, string directoryName, string threadKey)
        => Path.Combine(BoardDir(host, directoryName), threadKey + ".meta.json");

    /// <summary>AI による NG 判定スコア (レス番号 → 1..5) の per-thread 保存先。</summary>
    public string AiNgScoresPath(string host, string directoryName, string threadKey)
        => Path.Combine(BoardDir(host, directoryName), threadKey + ".aing.json");

    /// <summary>"hayabusa9.5ch.io" → "5ch.io"、"mercury.bbspink.com" → "bbspink.com"。</summary>
    public static string ExtractRootDomain(string host)
    {
        var parts = host.Split('.');
        return parts.Length >= 2 ? $"{parts[^2]}.{parts[^1]}" : host;
    }

    private static string EnsureDir(string path)
    {
        Directory.CreateDirectory(path);
        return path;
    }
}
