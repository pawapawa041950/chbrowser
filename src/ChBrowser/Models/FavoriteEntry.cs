using System;
using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace ChBrowser.Models;

/// <summary>
/// お気に入りエントリの基底。フォルダ・板・スレの 3 種類を <c>type</c> 判別子で区別する。
/// <see cref="FavoritesData"/> のツリー構造を成す。
/// </summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "type")]
[JsonDerivedType(typeof(FavoriteFolder), typeDiscriminator: "folder")]
[JsonDerivedType(typeof(FavoriteBoard),  typeDiscriminator: "board")]
[JsonDerivedType(typeof(FavoriteThread), typeDiscriminator: "thread")]
public abstract record FavoriteEntry
{
    /// <summary>UI 操作 (移動・削除・名前変更) で対象を一意に特定するための GUID。</summary>
    public Guid Id { get; init; } = Guid.NewGuid();

    /// <summary>追加日時 (UTC)。デフォルトの並びは降順 (新しいものが上)。</summary>
    public DateTimeOffset AddedAt { get; init; } = DateTimeOffset.UtcNow;
}

/// <summary>フォルダ。子に folder/board/thread を任意深さでネスト可能。</summary>
public sealed record FavoriteFolder : FavoriteEntry
{
    /// <summary>表示名。ユーザーが編集可能。</summary>
    public string Name { get; init; } = "";

    /// <summary>子エントリ。順序は配列順をそのまま使う (UI 並び替えで動かす)。</summary>
    public IReadOnlyList<FavoriteEntry> Children { get; init; } = Array.Empty<FavoriteEntry>();
}

/// <summary>板へのお気に入り。クリックで通常の板表示に新規タブで開く。</summary>
public sealed record FavoriteBoard : FavoriteEntry
{
    public string Host          { get; init; } = "";
    public string DirectoryName { get; init; } = "";

    /// <summary>表示名。bbsmenu.json が無くなっても表示できるよう保存しておく。</summary>
    public string BoardName     { get; init; } = "";
}

/// <summary>スレへのお気に入り。クリックで対象スレを新規タブで開く。
/// subject.txt から消えても (落ちても) 表示は維持する (詳細は 5.8)。</summary>
public sealed record FavoriteThread : FavoriteEntry
{
    public string Host          { get; init; } = "";
    public string DirectoryName { get; init; } = "";
    public string ThreadKey     { get; init; } = "";

    /// <summary>追加時のスレタイトル。subject.txt から消えても表示できるよう保存しておく。</summary>
    public string Title         { get; init; } = "";

    /// <summary>追加時の板名 (お気に入りディレクトリ展開時の「板」列に出すため)。</summary>
    public string BoardName     { get; init; } = "";
}

/// <summary>favorites.json 全体。<see cref="Root"/> がトップレベルエントリ列。</summary>
public sealed record FavoritesData
{
    public int Version { get; init; } = 1;
    public IReadOnlyList<FavoriteEntry> Root { get; init; } = Array.Empty<FavoriteEntry>();
}

/// <summary>お気に入り関連の固定値。ストレージ層と UI 層 (MainViewModel) で共有するため Models 配下に置く。</summary>
public static class FavoriteDefaults
{
    /// <summary>「上ボタン」バー (= Chrome ブックマークバー風の固定 UI) が中身を映す
    /// お気に入りルート直下フォルダの名前。<see cref="ChBrowser.ViewModels.MainViewModel.TopButtonsFolderName"/>
    /// もここを参照する。</summary>
    public const string TopButtonsFolderName = "上ボタン";
}
