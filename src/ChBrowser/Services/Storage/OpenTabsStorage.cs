using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text.Json;

namespace ChBrowser.Services.Storage;

/// <summary>1 件のスレ一覧タブ識別情報。
/// 「板タブ」と「お気に入りフォルダタブ」を <see cref="Kind"/> で区別する:
///   "board"           → <see cref="Host"/> + <see cref="DirectoryName"/> で板を特定
///   "favoritesFolder" → <see cref="FolderId"/> でフォルダ Guid (= 仮想ルートなら Guid.Empty) を特定
/// 不要側のフィールドは null。JSON にもそのまま null として書き出される (= 単純な 1 record で扱う方針)。</summary>
public sealed record OpenThreadListTabEntry(
    string  Kind,
    string? Host,
    string? DirectoryName,
    string? FolderId);

/// <summary>1 件のスレタブ識別情報。Title はタブ open までの placeholder 用 (= dat 取得完了で正しい値に置き換わる)。</summary>
public sealed record OpenThreadTabEntry(
    string Host,
    string DirectoryName,
    string Key,
    string Title);

/// <summary>1 枚のスレ表示ペインの保存情報 (複数ペイン化, Phase 4)。
/// <see cref="PaneKey"/> はレイアウト (layout.json) の leaf キーと対応する。
/// <see cref="SelectedIndex"/> はこのペインで選択中だったタブの <see cref="Tabs"/> 内インデックス (無選択は -1)。</summary>
public sealed record OpenThreadPaneEntry(
    string                      PaneKey,
    int                         SelectedIndex,
    List<OpenThreadTabEntry>    Tabs);

/// <summary>1 枚のスレ一覧ペインの保存情報 (複数ペイン化)。スレ表示の <see cref="OpenThreadPaneEntry"/> の一覧版。
/// <see cref="Tabs"/> は板タブ / お気に入りフォルダタブの並び (= <see cref="OpenThreadListTabEntry"/>)。</summary>
public sealed record OpenThreadListPaneEntry(
    string                          PaneKey,
    int                             SelectedIndex,
    List<OpenThreadListTabEntry>    Tabs);

/// <summary>open_tabs.json のシリアライズ用ルート。スレ一覧タブ → スレタブ (ペイン別) の順番で記録 / 復元する。
///
/// <para>v2 (複数ペイン化) で <see cref="ThreadPanes"/> / <see cref="ActiveThreadPaneKey"/> を追加。
/// 旧 v1 ファイルはこれらが null になるので、フラットな <see cref="ThreadTabs"/> から単一ペインとして復元する
/// (= 後方互換)。v2 保存時は <see cref="ThreadTabs"/> は空のまま <see cref="ThreadPanes"/> に書き出す。</para></summary>
public sealed class OpenTabsData
{
    public int Version { get; init; } = 1;
    public List<OpenThreadListTabEntry> ThreadListTabs { get; init; } = new();
    public List<OpenThreadTabEntry>     ThreadTabs     { get; init; } = new();

    /// <summary>スレ表示ペイン別のタブ振り分け (v2+)。null は旧フォーマット (= ThreadTabs を見る)。</summary>
    public List<OpenThreadPaneEntry>?   ThreadPanes         { get; init; }
    /// <summary>復元時にアクティブにするスレ表示ペインのキー (v2+)。</summary>
    public string?                      ActiveThreadPaneKey { get; init; }

    /// <summary>スレ一覧ペイン別のタブ振り分け (v3+)。null は旧フォーマット (= ThreadListTabs を見る)。</summary>
    public List<OpenThreadListPaneEntry>? ThreadListPanes         { get; init; }
    /// <summary>復元時にアクティブにするスレ一覧ペインのキー (v3+)。</summary>
    public string?                        ActiveThreadListPaneKey { get; init; }
}

/// <summary>
/// 終了時に開いていたタブ一覧 (= スレ一覧タブ + スレタブ、それぞれ並び順を維持) を
/// <c>data/app/open_tabs.json</c> に読み書きする。
/// 失敗時はアプリを止めず空データを返す (= 復元はオプショナル機能、なくても通常起動に支障なし)。
/// </summary>
public sealed class OpenTabsStorage
{
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented        = true,
    };

    private readonly string _path;

    public OpenTabsStorage(DataPaths paths)
    {
        _path = paths.OpenTabsJsonPath;
    }

    /// <summary>ファイルが無い・壊れている場合は空データ。例外を呼び元に伝えない (= 起動を止めない)。</summary>
    public OpenTabsData Load()
    {
        if (!File.Exists(_path)) return new OpenTabsData();
        try
        {
            using var fs = File.OpenRead(_path);
            return JsonSerializer.Deserialize<OpenTabsData>(fs, JsonOpts) ?? new OpenTabsData();
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[OpenTabsStorage] load failed: {ex.Message}");
            return new OpenTabsData();
        }
    }

    /// <summary>スレ一覧ペイン別 + スレ表示ペイン別の振り分けを保存する。v3 フォーマット。
    /// 例外時はログだけ吐いて飲み込む — 保存失敗で終了処理を止めないため。</summary>
    public void Save(IReadOnlyList<OpenThreadListPaneEntry> threadListPanes,
                     IReadOnlyList<OpenThreadPaneEntry>     threadPanes,
                     string?                                activeThreadListPaneKey,
                     string?                                activeThreadPaneKey)
    {
        try
        {
            var data = new OpenTabsData
            {
                Version                 = 3,
                ThreadListPanes         = new List<OpenThreadListPaneEntry>(threadListPanes),
                ThreadPanes             = new List<OpenThreadPaneEntry>(threadPanes),
                ActiveThreadListPaneKey = activeThreadListPaneKey,
                ActiveThreadPaneKey     = activeThreadPaneKey,
            };
            // 部分書き込みを避けるため .tmp に書いてから rename。
            var tmp = _path + ".tmp";
            using (var fs = File.Create(tmp))
            {
                JsonSerializer.Serialize(fs, data, JsonOpts);
            }
            if (File.Exists(_path)) File.Delete(_path);
            File.Move(tmp, _path);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[OpenTabsStorage] save failed: {ex.Message}");
        }
    }
}
