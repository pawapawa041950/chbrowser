using System;
using System.Diagnostics;
using System.Text.Json;
using System.Windows;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;

namespace ChBrowser.Services.WebView2;

/// <summary>WebView2 から届く postMessage の共通ルーティングヘルパ (Phase 23 で抽出)。
/// 4 ペイン (favorites / board-list / thread-list / thread-view) の <c>WebMessageReceived</c> ハンドラが
/// 共通で必要とする 4 系統 (shortcut / gesture / gestureProgress / gestureEnd / bridgeReady) を
/// このクラスに集約し、各ペインの UserControl は自分固有のメッセージタイプの switch だけを書けばよくなる。</summary>
public static class WebMessageBridge
{
    /// <summary>WebMessage を JSON として読んで (type, ルート要素) を返す。</summary>
    public static (string Type, JsonElement Root) TryParseMessage(CoreWebView2WebMessageReceivedEventArgs e)
    {
        try
        {
            var json = e.WebMessageAsJson;
            if (string.IsNullOrEmpty(json)) return ("", default);
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement.Clone();
            var type = root.TryGetProperty("type", out var typeProp) ? (typeProp.GetString() ?? "") : "";
            return (type, root);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[WebMessage] parse failed: {ex.Message}");
            return ("", default);
        }
    }

    /// <summary>4 共通系統 (shortcut / gesture / gestureProgress / gestureEnd / bridgeReady) を dispatch する。
    /// 加えて debugLog (= JS 側からの汎用デバッグ出力) を LogService に橋渡しする。
    /// これらが消費されたら true を返す (呼出元はその時点で return)。</summary>
    public static bool TryDispatchCommonMessage(object? sender, string type, JsonElement payload, string category)
    {
        if (type == "shortcut" || type == "gesture")
        {
            DispatchFromWebView(payload, category);
            return true;
        }
        if (type == "gestureProgress" || type == "gestureEnd")
        {
            RouteGestureProgress(payload, type, category);
            return true;
        }
        if (type == "bridgeReady")
        {
            PushBindingsTo(sender, category);
            return true;
        }
        if (type == "debugLog")
        {
            var msg = payload.TryGetProperty("message", out var mp) ? (mp.GetString() ?? "") : "";
            if (!string.IsNullOrEmpty(msg))
                ChBrowser.Services.Logging.LogService.Instance.Write($"[js/{category}] {msg}");
            return true;
        }
        return false;
    }

    /// <summary>shortcut / gesture メッセージを ShortcutManager にルーティング。</summary>
    private static void DispatchFromWebView(JsonElement payload, string category)
    {
        if (Application.Current is not App app || app.ShortcutManager is not { } mgr) return;
        var descriptor = payload.TryGetProperty("descriptor", out var dp) ? dp.GetString() : null;
        if (!string.IsNullOrEmpty(descriptor)) mgr.Dispatch(category, descriptor);
    }

    /// <summary>ジェスチャー進捗 (gestureProgress / gestureEnd) をルーティング。</summary>
    private static void RouteGestureProgress(JsonElement payload, string type, string category)
    {
        if (Application.Current is not App app || app.ShortcutManager is not { } mgr) return;
        if (type == "gestureEnd")
        {
            mgr.NotifyGestureProgress(null, null);
            return;
        }
        var value = payload.TryGetProperty("value", out var vp) ? vp.GetString() : "";
        mgr.NotifyGestureProgress(category, value ?? "");
    }

    /// <summary>bridgeReady 受信時、対象 WebView だけに setShortcutBindings を direct push する。</summary>
    private static void PushBindingsTo(object? sender, string category)
    {
        if (Application.Current is not App app || app.ShortcutManager is not { } mgr) return;
        if (sender is not Microsoft.Web.WebView2.Wpf.WebView2 wv || wv.CoreWebView2 is null) return;
        var map = mgr.GetBindingsForCategory(category);
        var json = JsonSerializer.Serialize(new { type = "setShortcutBindings", bindings = map });
        try { wv.CoreWebView2.PostWebMessageAsJson(json); }
        catch (Exception ex) { Debug.WriteLine($"[WebMessage] PushBindingsTo failed: {ex.Message}"); }
    }
}
