using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text.Json;

namespace ChBrowser.Services.Storage;

/// <summary>板一覧ペインのツリーの開閉状態 (<c>data/app/board_tree.json</c>)。起動時に前回終了時の状態を再現するために使う。
/// 掲示板 (提供者) ノードは既定で開、カテゴリは既定で閉なので、それぞれ既定と違うものだけを持つ。</summary>
public sealed class BoardTreeState
{
    /// <summary>閉じている掲示板ノードの提供者 Id。</summary>
    public List<string> CollapsedProviders { get; set; } = new();
    /// <summary>開いているカテゴリ (<c>提供者Id + "/" + カテゴリ名</c>)。</summary>
    public List<string> ExpandedCategories { get; set; } = new();

    public static string CategoryKey(string providerId, string categoryName) => providerId + "/" + categoryName;
}

/// <summary><see cref="BoardTreeState"/> の読み書き。書き込みは <c>.tmp</c> 経由で置き換える。失敗してもアプリ動作は止めない。</summary>
public sealed class BoardTreeStateStorage
{
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented        = true,
        Encoder              = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,   // カテゴリ名 (日本語) をそのまま書く
    };

    private readonly string _path;

    public BoardTreeStateStorage(DataPaths paths) => _path = paths.BoardTreeJsonPath;

    /// <summary>ファイルが無い / 壊れている場合は空の状態 (= 既定の開閉)。</summary>
    public BoardTreeState Load()
    {
        try
        {
            if (!File.Exists(_path)) return new BoardTreeState();
            var state = JsonSerializer.Deserialize<BoardTreeState>(File.ReadAllBytes(_path), JsonOpts) ?? new BoardTreeState();
            state.CollapsedProviders ??= new();
            state.ExpandedCategories ??= new();
            return state;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[BoardTreeStateStorage] load failed: {ex.Message}");
            return new BoardTreeState();
        }
    }

    public void Save(BoardTreeState state)
    {
        try
        {
            var dir = Path.GetDirectoryName(_path);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            var tmp = _path + ".tmp";
            File.WriteAllBytes(tmp, JsonSerializer.SerializeToUtf8Bytes(state, JsonOpts));
            File.Move(tmp, _path, overwrite: true);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[BoardTreeStateStorage] save failed: {ex.Message}");
        }
    }
}
