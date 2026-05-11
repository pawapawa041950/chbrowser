using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using ChBrowser.Models;
using ChBrowser.Services.Storage;

namespace ChBrowser.ViewModels;

/// <summary>スレ本文中に貼られた 5ch.io / bbspink.com スレ URL ホバー時のプレビュー取得。
/// 既存タブ → ディスクキャッシュ → ネットワークの順で dat を取り、対象レス本文とタイトルを返す。</summary>
public sealed partial class MainViewModel
{
    public async Task<ThreadPreviewResult> LoadThreadPreviewAsync(string host, string dir, string key, int requestedPostNo)
    {
        try
        {
            var rootIn = DataPaths.ExtractRootDomain(host);
            foreach (var tab in ThreadTabs)
            {
                if (string.Equals(DataPaths.ExtractRootDomain(tab.Board.Host), rootIn, StringComparison.OrdinalIgnoreCase) &&
                    string.Equals(tab.Board.DirectoryName, dir, StringComparison.Ordinal) &&
                    string.Equals(tab.ThreadKey,           key, StringComparison.Ordinal))
                {
                    return ExtractPreview(tab.Posts, requestedPostNo);
                }
            }

            var board = ResolveBoard(host, dir, "");

            var local = await _datClient.LoadFromDiskAsync(board, key).ConfigureAwait(true);
            if (local is not null && local.Posts.Count > 0)
            {
                return ExtractPreview(local.Posts, requestedPostNo);
            }

            var result = await _datClient.FetchAsync(board, key).ConfigureAwait(true);
            if (result.Posts.Count == 0)
                return ThreadPreviewResult.Failure("dat 取得失敗");
            return ExtractPreview(result.Posts, requestedPostNo);
        }
        catch (Exception ex)
        {
            return ThreadPreviewResult.Failure(ex.Message);
        }
    }

    private static ThreadPreviewResult ExtractPreview(IReadOnlyList<Post> posts, int requestedPostNo)
    {
        if (posts.Count == 0) return ThreadPreviewResult.Failure("レスなし");
        var title = posts[0].ThreadTitle ?? "";
        var effectiveNo = requestedPostNo > 0 ? requestedPostNo : 1;
        if (effectiveNo < 1 || effectiveNo > posts.Count)
            return new ThreadPreviewResult(false, title, "", "", "", effectiveNo, $">>{effectiveNo} は存在しません");
        var p = posts[effectiveNo - 1];
        return new ThreadPreviewResult(true, title, p.Body, p.Name, p.DateText, p.Number, null);
    }
}

/// <summary>JS に返すスレプレビュー結果。<see cref="Ok"/>=false 時は <see cref="Error"/> を表示。</summary>
public sealed record ThreadPreviewResult(
    bool    Ok,
    string  Title,
    string  Body,
    string  Name,
    string  DateText,
    int     PostNumber,
    string? Error)
{
    public static ThreadPreviewResult Failure(string msg)
        => new(false, "", "", "", "", 0, msg);
}
