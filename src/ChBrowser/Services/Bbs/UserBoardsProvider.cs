using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using ChBrowser.Models;

namespace ChBrowser.Services.Bbs;

/// <summary>ユーザが追加した 5ch 互換の板 (メニュー「板一覧 → 板を追加…」/ アドレスバーに知らないホストの URL)。
/// 読み書きは 5ch 互換の共通部分 (<see cref="FiveChCompatibleProvider"/>) のまま、ホストと板一覧だけを追加した板から決める。
///
/// <list type="bullet">
/// <item><description>追加した板は <c>data/app/user_boards.json</c> に保存する (起動時に <see cref="Initialize"/> で読む)。</description></item>
/// <item><description>板一覧はネットワークから取らず、追加した板をホストごとのカテゴリに並べる (<see cref="LocalBoardList"/>)。</description></item>
/// <item><description>保存ルートはホストそのもの (<c>data/&lt;host&gt;/</c>)。任意のホストなので、末尾 2 つ (<c>co.jp</c> 等) にまとめると別のサイトと混ざるため。</description></item>
/// <item><description>登録の順番は既存の掲示板の後ろ (既存の掲示板のホストは既存の掲示板が持つ。追加の時点でも断る)。</description></item>
/// </list></summary>
public sealed class UserBoardsProvider : FiveChCompatibleProvider
{
    public const string ProviderId = "userboards";

    /// <summary>登録簿に入れる唯一のインスタンス。</summary>
    public static UserBoardsProvider Instance { get; } = new();

    private UserBoardsProvider() { }

    /// <summary>追加した板 1 つ。</summary>
    public sealed record UserBoard(string Host, string DirectoryName, string Name);

    private readonly object _lock = new();
    private List<UserBoard> _boards = new();
    private HashSet<string> _hosts = new(StringComparer.OrdinalIgnoreCase);
    private string? _path;

    public override string Id          => ProviderId;
    public override string DisplayName => "追加した板";

    /// <summary>保存してある追加した板を読む (起動時に 1 回。読めなければ空のまま)。</summary>
    public void Initialize(string path)
    {
        List<UserBoard>? loaded = null;
        var readable = true;
        try
        {
            if (File.Exists(path))
            {
                var bytes = File.ReadAllBytes(path);
                var start = bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF ? 3 : 0;   // 手で編集して BOM 付きで保存された場合
                loaded = JsonSerializer.Deserialize<List<UserBoard>>(bytes.AsSpan(start));
            }
        }
        catch (Exception ex) when (ex is JsonException or IOException)
        {
            readable = false;
            ChBrowser.Services.Logging.LogService.Instance.Write($"[UserBoards] 追加した板の一覧を読めません (このファイルは上書きしません): {ex.Message}");
        }
        lock (_lock)
        {
            _path = readable ? path : null;   // 読めなかったファイルを、空の一覧で上書きして消さない
            SetBoards(loaded?.Where(b => !string.IsNullOrEmpty(b.Host) && !string.IsNullOrEmpty(b.DirectoryName)) ?? Enumerable.Empty<UserBoard>());
        }
    }

    /// <summary>追加した板 (追加した順)。</summary>
    public IReadOnlyList<UserBoard> Boards { get { lock (_lock) return _boards.ToArray(); } }

    /// <summary>板を追加して保存する。既に追加してあれば名前だけ更新する。</summary>
    public void Add(UserBoard board)
    {
        board = board with { Host = board.Host.ToLowerInvariant() };
        lock (_lock)
        {
            var list = _boards.Where(b => !Same(b, board.Host, board.DirectoryName)).ToList();
            var at   = _boards.FindIndex(b => Same(b, board.Host, board.DirectoryName));
            if (at >= 0) list.Insert(at, board); else list.Add(board);
            SetBoards(list);
            Save();
        }
    }

    /// <summary>板を外して保存する (ログは消さない)。外したら true。</summary>
    public bool Remove(string host, string directoryName)
    {
        lock (_lock)
        {
            if (!_boards.Any(b => Same(b, host, directoryName))) return false;
            SetBoards(_boards.Where(b => !Same(b, host, directoryName)).ToList());
            Save();
            return true;
        }
    }

    /// <summary>追加した板か。</summary>
    public bool Contains(string host, string directoryName)
    {
        lock (_lock) return _boards.Any(b => Same(b, host, directoryName));
    }

    private static bool Same(UserBoard b, string host, string dir)
        => string.Equals(b.Host, host, StringComparison.OrdinalIgnoreCase) && string.Equals(b.DirectoryName, dir, StringComparison.Ordinal);

    private void SetBoards(IEnumerable<UserBoard> boards)
    {
        _boards = boards.ToList();
        _hosts  = new HashSet<string>(_boards.Select(b => b.Host), StringComparer.OrdinalIgnoreCase);
    }

    private void Save()
    {
        if (_path is null) return;
        try
        {
            var tmp = _path + ".tmp";
            File.WriteAllBytes(tmp, JsonSerializer.SerializeToUtf8Bytes(_boards, new JsonSerializerOptions { WriteIndented = true }));
            File.Move(tmp, _path, overwrite: true);
        }
        catch (IOException ex)
        {
            ChBrowser.Services.Logging.LogService.Instance.Write($"[UserBoards] 追加した板の一覧を保存できません: {ex.Message}");
        }
    }

    // ---- 掲示板としての情報 (ホストと板一覧は追加した板から) ----

    public override bool OwnsHost(string host)
    {
        lock (_lock) return _hosts.Contains(host);
    }

    public override IReadOnlyList<string> StorageRoots { get { lock (_lock) return _hosts.ToArray(); } }
    public override IReadOnlyList<string> HostSuffixes => StorageRoots;
    public override string DefaultHost { get { lock (_lock) return _boards.Count > 0 ? _boards[0].Host : ""; } }

    /// <summary>保存ルートはホストそのもの。</summary>
    public override string? StorageRootOf(string host) => OwnsHost(host) ? host.ToLowerInvariant() : null;

    public override string? BoardListEmptyHint => "まだありません (メニューの 板一覧 → 板を追加、またはアドレスバーに板の URL)";

    public override string? BoardListUrl => null;
    public override string  BoardListCacheExtension => "";
    public override IReadOnlyList<BoardCategory> ParseBoardList(byte[] bytes) => Array.Empty<BoardCategory>();

    /// <summary>追加した板を、ホストごとのカテゴリ (追加した順) に並べる。</summary>
    public override IReadOnlyList<BoardCategory>? LocalBoardList
    {
        get
        {
            var order = 0;
            return Boards.GroupBy(b => b.Host, StringComparer.OrdinalIgnoreCase)
                .Select((g, i) => new BoardCategory(g.Key, i + 1,
                    g.Select(b => new Board(b.DirectoryName, b.Name, BoardUrl(b.Host, b.DirectoryName), g.Key, ++order)).ToArray(), Id))
                .ToList();
        }
    }
}
