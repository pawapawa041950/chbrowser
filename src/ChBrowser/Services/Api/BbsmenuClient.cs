using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using ChBrowser.Models;
using ChBrowser.Services.Storage;

namespace ChBrowser.Services.Api;

/// <summary>
/// bbsmenu.json (板一覧) の取得・保存・読み込み。
/// </summary>
public sealed class BbsmenuClient
{
    private const string BbsmenuUrl = "https://menu.5ch.io/bbsmenu.json";

    private readonly MonazillaClient _client;
    private readonly DataPaths       _paths;

    public BbsmenuClient(MonazillaClient client, DataPaths paths)
    {
        _client = client;
        _paths  = paths;
    }

    /// <summary>サーバから取得し、生 JSON をディスクに保存した上でパース結果を返す。</summary>
    public async Task<IReadOnlyList<BoardCategory>> FetchAndSaveAsync(CancellationToken ct = default)
    {
        using var resp = await _client.Http.GetAsync(BbsmenuUrl, ct).ConfigureAwait(false);
        resp.EnsureSuccessStatusCode();

        var bytes = await resp.Content.ReadAsByteArrayAsync(ct).ConfigureAwait(false);

        // 取得そのままバイト列で保存 (UTF-8 想定だが変換しない)
        await File.WriteAllBytesAsync(_paths.BbsmenuJsonPath, bytes, ct).ConfigureAwait(false);

        return ParseBytes(bytes);
    }

    /// <summary>ローカル保存済みの bbsmenu.json からパースする。未取得の場合は空配列。</summary>
    public async Task<IReadOnlyList<BoardCategory>> LoadFromDiskAsync(CancellationToken ct = default)
    {
        if (!File.Exists(_paths.BbsmenuJsonPath))
            return Array.Empty<BoardCategory>();

        var bytes = await File.ReadAllBytesAsync(_paths.BbsmenuJsonPath, ct).ConfigureAwait(false);
        return ParseBytes(bytes);
    }

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        Converters = { new FlexibleIntConverter() },
    };

    private static IReadOnlyList<BoardCategory> ParseBytes(byte[] bytes)
    {
        var dto = JsonSerializer.Deserialize<BbsmenuJsonDto>(bytes, JsonOpts)
                  ?? throw new InvalidDataException("bbsmenu.json のパースに失敗しました。");

        var categories = new List<BoardCategory>(dto.MenuList?.Count ?? 0);

        if (dto.MenuList is null) return categories;

        foreach (var menu in dto.MenuList)
        {
            if (menu.CategoryContent is null) continue;

            var boards = new List<Board>(menu.CategoryContent.Count);
            foreach (var entry in menu.CategoryContent)
            {
                if (string.IsNullOrEmpty(entry.Url) ||
                    string.IsNullOrEmpty(entry.BoardName) ||
                    string.IsNullOrEmpty(entry.DirectoryName))
                {
                    continue; // 不完全エントリはスキップ
                }

                boards.Add(new Board(
                    DirectoryName: entry.DirectoryName!,
                    BoardName:     entry.BoardName!,
                    Url:           entry.Url!,
                    CategoryName:  entry.CategoryName ?? menu.CategoryName ?? "",
                    CategoryOrder: entry.CategoryOrder ?? 0));
            }

            categories.Add(new BoardCategory(
                CategoryName:   menu.CategoryName ?? "(無名)",
                CategoryNumber: menu.CategoryNumber ?? 0,
                Boards:         boards));
        }

        return categories;
    }
}
