using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using ChBrowser.Models;

namespace ChBrowser.Services.Bbs;

/// <summary>書き込みを本物のブラウザ (WebView2) の投稿フォームで行う提供者 (4chan、決定 D40)。
///
/// 4chan の投稿先は Cloudflare の確認付きで、CAPTCHA はページの JS が差し込むため、HTTP では投稿できない。
/// そこで <see cref="Api.PostClient"/> はこの提供者には HTTP で送らず、<see cref="IBrowserPoster"/> (投稿窓) に任せる:
/// 投稿窓は <see cref="BrowserPostPageUrl"/> を開き、<see cref="BuildBrowserPostFill"/> の内容をフォームに入れ、
/// ユーザが確認・CAPTCHA を済ませて投稿ボタンを押すのを待つ。ページが移るたびに <see cref="ClassifyBrowserPostPage"/> で
/// 結果のページかを見て、結果が出たら閉じる。</summary>
public interface IBrowserPostProvider
{
    /// <summary>投稿窓の WebView2 のプロファイル名 (掲示板ごとに分け、確認の Cookie をアプリ再起動後も残す)。</summary>
    string BrowserProfileName { get; }

    /// <summary>投稿窓の題名 (例「4chan に投稿」)。</summary>
    string BrowserPostWindowTitle { get; }

    /// <summary>投稿フォームのあるページ (レス: スレのページ、スレ立て: 板のページ)。</summary>
    Uri BrowserPostPageUrl(PostRequest request);

    /// <summary>フォームに入れる内容。</summary>
    BrowserPostFill BuildBrowserPostFill(PostRequest request);

    /// <summary>投稿窓の案内に出す「ユーザが押すボタン」の名前 (4chan: 「Post」、ふたば: 「返信する」/「スレッドを立てる」)。</summary>
    string BrowserPostSubmitLabel(PostRequest request) => "Post";

    /// <summary>送信の前にユーザが済ませる確認があるか (4chan: CAPTCHA)。false なら案内は「内容を確かめて押す」だけ。</summary>
    bool BrowserPostHasVerification => true;

    /// <summary>ページ遷移後の URL と HTML から、投稿の結果のページかを判定する。結果のページでなければ null
    /// (フォームのページ・確認のページ等。投稿窓はそのまま待つ)。</summary>
    PostResult? ClassifyBrowserPostPage(Uri url, string html);

    /// <summary>投稿窓のすべてのページに読み込み前に差し込む JS (ページの JS が XHR で送る掲示板で、応答を拾って
    /// <c>chrome.webview.postMessage</c> で知らせる。ふたば)。null (既定) なら差し込まない。</summary>
    string? BrowserPostCaptureScript => null;

    /// <summary><see cref="BrowserPostCaptureScript"/> が送ったメッセージ (JSON) から結果を判定する。結果でなければ null。
    /// ここで得たエラーでは投稿窓はフォームを開き直さない (送信済みかもしれないので、ユーザが確かめる)。</summary>
    PostResult? ClassifyBrowserPostMessage(string messageJson) => null;
}

/// <summary>投稿フォームへの入力内容。</summary>
/// <param name="FormSelector">フォームの CSS セレクタ (例 <c>form[name="post"]</c>)。</param>
/// <param name="Fields">入れる項目 (項目名 → 値)。値が空の項目も入れる (前回の入力を残さないため)。</param>
/// <param name="FileFieldName">添付ファイルの項目名 (<c>upfile</c>)。添付が無ければ使わない。</param>
/// <param name="PrepareScript">入力の前にページで実行する JS (隠れているフォームを出す等)。null なら無し。</param>
/// <param name="FocusSelector">入力後に表示位置を合わせる要素 (CAPTCHA の欄等)。null ならフォーム。</param>
public sealed record BrowserPostFill(
    string FormSelector,
    IReadOnlyList<KeyValuePair<string, string>> Fields,
    string? FileFieldName,
    string? PrepareScript = null,
    string? FocusSelector = null);

/// <summary>ブラウザの投稿窓 (<see cref="IBrowserPostProvider"/> の書き込みを実行する UI)。
/// 実装は <see cref="Browser.BrowserPostWindow"/>。窓を閉じられたら <see cref="PostOutcome.Cancelled"/> を返す。</summary>
public interface IBrowserPoster
{
    Task<PostResult> PostAsync(IBrowserPostProvider provider, PostRequest request, CancellationToken ct);
}
