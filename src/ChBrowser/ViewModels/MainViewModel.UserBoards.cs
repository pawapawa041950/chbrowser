using System;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Windows;
using ChBrowser.Models;
using ChBrowser.Services.Bbs;

namespace ChBrowser.ViewModels;

/// <summary>ユーザが追加する 5ch 互換の板 (<see cref="UserBoardsProvider"/>): メニュー「板一覧 → 板を追加…」、
/// アドレスバーに知らないホストの板 / スレ URL を入れたときの「追加しますか」、板一覧ペインの「追加した板から外す」。</summary>
public sealed partial class MainViewModel
{
    private static readonly Regex BbsTitleRegex = new(@"^BBS_TITLE=(?<t>.+)$", RegexOptions.Multiline | RegexOptions.Compiled);

    /// <summary>メニュー「板を追加…」: 板の URL を入力してもらい、追加できたらその板を開く。</summary>
    public async Task PromptAddUserBoardAsync(Window? owner)
    {
        var input = ChBrowser.Views.InputDialog.Prompt(owner, "5ch 互換の板を追加",
            "板の URL (例: https://example.net/board/。スレの URL でも可)")?.Trim();
        if (string.IsNullOrEmpty(input)) return;
        if (await AddUserBoardAsync(input).ConfigureAwait(true) is { } board)
            await OpenBoardByUrlAsync(board.Host, board.DirectoryName).ConfigureAwait(true);
    }

    /// <summary>アドレスバーの URL が、どの掲示板のものでもないホストの 5ch 形式の板 / スレなら、追加するか聞く。
    /// 追加したら true (呼び出し側は同じ URL を開き直す)、追加しようとして失敗したら false (理由はステータスに出ている)、
    /// 聞かなかった (5ch 形式でない) / 追加しないと答えたら null。</summary>
    private async Task<bool?> OfferAddUserBoardAsync(string input)
    {
        if (!Uri.TryCreate(input.Trim(), UriKind.Absolute, out var uri)
            || FiveChCompatibleProvider.ParseBoardLocation(uri) is not { } loc
            || BbsRegistry.Resolve(loc.Host) is not null) return null;
        var answer = MessageBox.Show(Application.Current?.MainWindow!,
            $"{loc.Host} は対応している掲示板ではありません。\n板「{loc.DirectoryName}」を 5ch 互換の板として追加しますか？",
            "板を追加", MessageBoxButton.YesNo, MessageBoxImage.Question);
        if (answer != MessageBoxResult.Yes) return null;
        return await AddUserBoardAsync(input).ConfigureAwait(true) is not null;
    }

    /// <summary>板を確かめて追加する: スレ一覧 (subject.txt) が 5ch 形式で読めること、板名は SETTING.TXT の BBS_TITLE。
    /// 既存の掲示板の板・追加済みの板なら追加せずにその板を返す。読めなければ理由をステータスに出して null。</summary>
    public async Task<Board?> AddUserBoardAsync(string input)
    {
        if (!Uri.TryCreate(input.Trim(), UriKind.Absolute, out var uri)
            || FiveChCompatibleProvider.ParseBoardLocation(uri) is not { } loc)
        {
            StatusMessage = $"板の URL として読めません (https://ホスト/板名/ の形で入力してください): {input}";
            return null;
        }
        var users = UserBoardsProvider.Instance;
        var owner = BbsRegistry.Resolve(loc.Host);
        if (owner is not null && owner != users)
        {
            StatusMessage = $"{loc.Host} は {owner.DisplayName} の板なので、追加しなくても開けます";
            return ResolveBoard(owner.NormalizeHost(loc.Host), loc.DirectoryName, loc.DirectoryName);
        }
        var probe = new Board(loc.DirectoryName, loc.DirectoryName, users.BoardUrl(loc.Host, loc.DirectoryName), "", 0);
        if (users.Contains(loc.Host, loc.DirectoryName)) return probe;

        StatusMessage = $"{probe.Url} を確認中...";
        try
        {
            // スレ一覧が 5ch 形式で読めるか (空の板は通す)
            using (var resp = await _subjectClient.Http.GetAsync(users.ThreadListUrl(probe)).ConfigureAwait(true))
            {
                if (!resp.IsSuccessStatusCode)
                {
                    StatusMessage = $"板を追加できません: スレ一覧 (subject.txt) を取得できませんでした (HTTP {(int)resp.StatusCode})";
                    return null;
                }
                var bytes = await resp.Content.ReadAsByteArrayAsync().ConfigureAwait(true);
                if (bytes.Length > 0 && users.ParseThreadList(bytes).Count == 0)
                {
                    StatusMessage = "板を追加できません: スレ一覧 (subject.txt) が 5ch 形式ではありませんでした";
                    return null;
                }
            }

            // 板名 (SETTING.TXT の BBS_TITLE)。取れなければ板の dir 名
            var name = loc.DirectoryName;
            try
            {
                using var setting = await _subjectClient.Http.GetAsync(users.BoardInfoUrl(probe)!).ConfigureAwait(true);
                if (setting.IsSuccessStatusCode
                    && BbsTitleRegex.Match(users.TextEncoding.GetString(await setting.Content.ReadAsByteArrayAsync().ConfigureAwait(true))) is { Success: true } m)
                    name = System.Net.WebUtility.HtmlDecode(m.Groups["t"].Value.Trim());
            }
            catch (System.Net.Http.HttpRequestException) { }

            users.Add(new UserBoardsProvider.UserBoard(loc.Host, loc.DirectoryName, name));
            ApplyUserBoards();
            StatusMessage = $"板「{name}」({loc.Host}) を追加しました";
            return probe with { BoardName = name };
        }
        catch (Exception ex) when (ex is System.Net.Http.HttpRequestException or TaskCanceledException)
        {
            StatusMessage = $"板を追加できません: {ex.Message}";
            return null;
        }
    }

    /// <summary>追加した板から外す (ログは残す。同じ URL をまた追加すれば元どおり読める)。</summary>
    public void RemoveUserBoard(string host, string directoryName, string boardName)
    {
        if (!UserBoardsProvider.Instance.Remove(host, directoryName)) return;
        ApplyUserBoards();
        StatusMessage = $"板「{boardName}」を追加した板から外しました (取得済みのログは残っています)";
    }

    /// <summary>追加した板が変わったとき: 板一覧ペインの「追加した板」と、スレ本文のリンク判定 (ホストの一覧) を出し直す。</summary>
    private void ApplyUserBoards()
    {
        ApplyCategories(UserBoardsProvider.ProviderId, UserBoardsProvider.Instance.LocalBoardList ?? Array.Empty<BoardCategory>());
        ThreadConfigJson = BuildThreadConfigJson(CurrentConfig);
    }
}
