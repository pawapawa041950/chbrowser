using System;
using System.Collections.Generic;

namespace ChBrowser.Services.Media;

/// <summary>サムネ表示用 URL の取得が失敗したパターンの種類。
/// Tracker のキー側に併用されて (url, kind) の組合せで失敗状態を記憶する。</summary>
public enum MediaAcquisitionKind
{
    /// <summary>画像 (直リンク) の取得失敗。HEAD で 4xx / GET で 404 等。</summary>
    Image         = 0,
    /// <summary>動画本体ファイルのダウンロード失敗 (HTTP エラー / ネットワーク / SaveAsync 失敗)。</summary>
    VideoDownload = 1,
    /// <summary>動画サムネ抽出失敗 (canvas tainted / CORS proxy 失敗 / loadeddata エラー)。</summary>
    VideoThumb    = 2,
    /// <summary>SNS (x.com / pixiv 等) async 展開失敗 (媒体無し / login 要求 / API ダウン)。</summary>
    SnsExpand     = 3,
}

/// <summary>サムネ表示用 URL の「セッション中の取得失敗」を一元管理するトラッカ。
///
/// <para>役割: 一度失敗した URL に対して、ユーザの明示クリックがあるまで自動再試行を抑制することで
/// 帯域とサーバ負荷を節約する。Kind ごとに別の failure 状態を持つので、例えば
/// 「画像 GET 失敗」と「動画サムネ抽出失敗」は独立に管理される。</para>
///
/// <para>スコープ: アプリ全体 (= シングルトン)。アプリ再起動でクリア。
/// ユーザが明示的に retry した時 (= スロットクリック等) は <see cref="ResetAll"/> で全 Kind をクリアする。</para>
///
/// <para>スレッド安全性: 内部 dictionary + HashSet を単一 lock で守る (= 操作頻度は低いので
/// 細かい lock 粒度は不要)。</para></summary>
public sealed class MediaAcquisitionTracker
{
    /// <summary>Kind → 失敗 URL set。
    /// 全 Kind 分予め埋めて、null チェックを省略する。</summary>
    private readonly Dictionary<MediaAcquisitionKind, HashSet<string>> _byKind;
    private readonly object _lock = new();

    public MediaAcquisitionTracker()
    {
        _byKind = new Dictionary<MediaAcquisitionKind, HashSet<string>>();
        foreach (MediaAcquisitionKind k in Enum.GetValues<MediaAcquisitionKind>())
        {
            _byKind[k] = new HashSet<string>(StringComparer.Ordinal);
        }
    }

    /// <summary>指定 URL + Kind を失敗として記憶する。</summary>
    public void MarkFailed(string url, MediaAcquisitionKind kind)
    {
        if (string.IsNullOrEmpty(url)) return;
        lock (_lock) { _byKind[kind].Add(url); }
    }

    /// <summary>指定 URL + Kind が失敗済か。</summary>
    public bool IsFailed(string url, MediaAcquisitionKind kind)
    {
        if (string.IsNullOrEmpty(url)) return false;
        lock (_lock) { return _byKind[kind].Contains(url); }
    }

    /// <summary>指定 URL の特定 Kind だけ失敗状態をクリア。</summary>
    public void Reset(string url, MediaAcquisitionKind kind)
    {
        if (string.IsNullOrEmpty(url)) return;
        lock (_lock) { _byKind[kind].Remove(url); }
    }

    /// <summary>指定 URL の全 Kind の失敗状態をクリア。
    /// ユーザの明示クリック (= スロット click / playMedia 等) で「全部リセットして再試行したい」意図に対応。</summary>
    public void ResetAll(string url)
    {
        if (string.IsNullOrEmpty(url)) return;
        lock (_lock)
        {
            foreach (var set in _byKind.Values) set.Remove(url);
        }
    }
}
