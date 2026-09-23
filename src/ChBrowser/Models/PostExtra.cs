using System.Collections.Generic;

namespace ChBrowser.Models;

/// <summary>レスの拡張情報 (<see cref="Post.Ext"/>)。5ch 系の dat 行には無い、掲示板固有の構造化データを持つ
/// (<c>doc/reddit-design.md</c> §3 B1)。番号列付きログ (<see cref="Services.Api.NumberedLogFormat"/>) の 8 列目に JSON で保存され、
/// スレ表示 (thread.js) へは <c>post.ext</c> として届く。</summary>
/// <param name="ExternalId">掲示板側の投稿 ID (reddit: <c>t1_xxx</c> / <c>t3_xxx</c>、4chan・ふたば: 投稿番号)。</param>
/// <param name="ParentNumber">親レスのアプリ内番号。本文のアンカーとは別の「構造上の返信先」で、ツリー・返信数・連鎖 NG・
/// 自分への返信検知はアンカーとこの値の和で辿る (<c>doc/reddit-design.md</c> §3 B11)。親がスレ本体 (トップレベル) なら null。</param>
/// <param name="ParentName">親レスの投稿者名 (返信先表示 <c>↳ u/xxx</c> 用。保存時点の値)。</param>
/// <param name="Depth">木の深さ (0 = トップレベル)。</param>
/// <param name="Score">評価値 (reddit の score)。初見時の値 (追随しない)。</param>
/// <param name="CreatedEpoch">投稿時刻 (epoch 秒)。</param>
/// <param name="EditedEpoch">編集時刻 (epoch 秒)。未編集なら null。</param>
/// <param name="Permalink">そのレスを指す URL (ブラウザで開く用)。</param>
/// <param name="Attachments">添付ファイル (画像・動画・ギャラリー)。</param>
/// <param name="MyVote">取得時点での自分の評価 (1 = 賛成、-1 = 反対、0 / null = なし。reddit の <c>likes</c>)。<see cref="Score"/> はこれを含んだ値。
/// アプリから評価した後の状態は idx.json (<see cref="ThreadIndex.MyVotes"/>) が持ち、表示はそちらを優先する。</param>
public sealed record PostExtra(
    string?  ExternalId   = null,
    long?    ParentNumber = null,
    string?  ParentName   = null,
    int?     Depth        = null,
    long?    Score        = null,
    long?    CreatedEpoch = null,
    long?    EditedEpoch  = null,
    string?  Permalink    = null,
    IReadOnlyList<PostAttachment>? Attachments = null,
    int?     MyVote       = null);

/// <summary>レスの添付ファイル 1 件。<see cref="FileName"/> はアンカー規則の attachment 種別 (ふたばの <c>&gt;xxx.png</c>) の解決にも使う。</summary>
/// <param name="Url">本体の URL。</param>
/// <param name="ThumbUrl">サムネイルの URL (無ければ null)。</param>
/// <param name="FileName">元ファイル名 / 表示名。</param>
/// <param name="Kind">"image" / "video" / "file"。</param>
public sealed record PostAttachment(
    string  Url,
    string? ThumbUrl = null,
    string? FileName = null,
    long?   Size     = null,
    int?    Width    = null,
    int?    Height   = null,
    string  Kind     = "image");
