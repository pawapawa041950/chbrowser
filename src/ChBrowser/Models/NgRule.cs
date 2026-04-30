using System;
using System.Collections.Generic;

namespace ChBrowser.Models;

/// <summary>NG 機能 (Phase 13) の 1 ルール。スコープ情報をルール自身が持つ
/// (= 空 BoardHost / BoardDirectory はグローバル、入っていれば板単位)。</summary>
public sealed record NgRule
{
    /// <summary>ルール識別子 (UI の編集 / 削除 / トグルで対象を特定するため)。</summary>
    public Guid Id { get; init; } = Guid.NewGuid();

    /// <summary>適用する板の host (= bbsmenu に登録された板の Host)。空文字列 = グローバル。</summary>
    public string BoardHost { get; init; } = "";

    /// <summary>適用する板の directory_name。空文字列 = グローバル。
    /// BoardHost と BoardDirectory のどちらか片方だけ設定された状態は不正 (= 空白扱い)。</summary>
    public string BoardDirectory { get; init; } = "";

    /// <summary>"name" / "id" / "watchoi" / "word"。NgService 側でこの値に応じて判定対象フィールドを切り替える。</summary>
    public string Target { get; init; } = "word";

    /// <summary>"literal" (部分一致) または "regex" (正規表現)。</summary>
    public string MatchKind { get; init; } = "literal";

    /// <summary>マッチパターン (literal は単純文字列、regex は正規表現)。</summary>
    public string Pattern { get; init; } = "";

    /// <summary>個別の有効/無効トグル。false にすれば判定から外れる (削除はしない)。
    /// 正規表現バリデーション失敗時もこの値が false になる (= 自動無効化)。</summary>
    public bool Enabled { get; init; } = true;

    /// <summary>ルール作成日時 (ソート用)。</summary>
    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;

    /// <summary>有効期限。null は無期限。<see cref="DateTimeOffset.UtcNow"/> &gt; ExpiresAt なら NgService で skip される。</summary>
    public DateTimeOffset? ExpiresAt { get; init; }
}

/// <summary>NG ルール集合のシリアライズ単位。グローバル / 板単位の JSON ファイルがこの形で保存される。</summary>
public sealed record NgRuleSet
{
    public int Version { get; init; } = 1;
    public IReadOnlyList<NgRule> Rules { get; init; } = Array.Empty<NgRule>();
}
