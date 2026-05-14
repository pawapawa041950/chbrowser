using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;
using ChBrowser.Controls;
using ChBrowser.Services.Render;
using ChBrowser.ViewModels;
using Microsoft.Web.WebView2.Core;

namespace ChBrowser.Views;

/// <summary>
/// スレッド単位の AI チャットウィンドウ。モードレスで開かれる (= スレを見ながらやり取りできる)。
/// バインディング元は <see cref="AiChatViewModel"/>。
///
/// 会話表示は WebView2 (ai-chat.html シェル) で行う。VM が公開する表示更新イベントを購読し、
/// それぞれを <see cref="CoreWebView2.PostWebMessageAsJson"/> でシェルに転送する。
/// markdown → HTML 変換は VM 側 (Markdig) で済んでいるので、ここはメッセージ中継に徹する。
///
/// シェルの準備 (= 'ready' メッセージ受信) 前に飛んできた表示更新は <see cref="_pending"/> に
/// 退避し、ready 受信時にまとめて流す。
/// </summary>
public partial class AiChatWindow : Window
{
    private readonly AiChatViewModel _vm;
    /// <summary>シェル (ai-chat.html) の 'ready' を受信済みか。受信前の post はキューに退避する。</summary>
    private bool _shellReady;
    /// <summary>ready 前に発生した表示更新メッセージ (JSON 文字列) の退避キュー。</summary>
    private readonly List<string> _pending = new();

    public AiChatWindow(AiChatViewModel vm, Window? owner)
    {
        _vm         = vm;
        DataContext = vm;
        Owner       = owner;

        InitializeComponent();

        // VM の表示更新イベントを購読 (すべて UI スレッド上で発火する)。
        _vm.UserMessageAdded         += OnUserMessageAdded;
        _vm.AssistantMessageStarted  += OnAssistantMessageStarted;
        _vm.AssistantHtmlUpdated     += OnAssistantHtmlUpdated;
        _vm.AssistantMessageFinished += OnAssistantMessageFinished;
        _vm.ErrorAdded               += OnErrorAdded;

        Loaded += async (_, _) =>
        {
            await InitWebViewAsync();
            InputBox.Focus();
        };
        Closed += OnWindowClosed;
    }

    /// <summary>WebView2 を初期化して ai-chat.html シェルをロードする。
    /// 共有 CoreWebView2Environment を使うため <see cref="WebView2Helper.EnsureCoreAsync"/> 経由で初期化する。</summary>
    private async Task InitWebViewAsync()
    {
        try
        {
            await WebView2Helper.EnsureCoreAsync(TranscriptView).ConfigureAwait(true);
            var core = TranscriptView.CoreWebView2;
            if (core is null) return;

            core.Settings.IsStatusBarEnabled            = false;
            core.Settings.AreDefaultContextMenusEnabled = true; // テキスト選択 / コピーは許可
            core.Settings.AreDevToolsEnabled            = false;
            core.WebMessageReceived += OnWebMessageReceived;
            core.NewWindowRequested += OnNewWindowRequested;

            core.NavigateToString(EmbeddedAssets.Read("ai-chat.html"));
            // 以降の表示更新は OnWebMessageReceived で 'ready' を受け取ってから流す (= _shellReady)。
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[AiChatWindow] WebView2 init failed: {ex.Message}");
        }
    }

    // ---- VM イベント → シェルへのメッセージ転送 ----

    private void OnUserMessageAdded(string text)      => PostToShell(new { type = "addUser",         text });
    private void OnAssistantMessageStarted()          => PostToShell(new { type = "startAssistant" });
    private void OnAssistantHtmlUpdated(string html)  => PostToShell(new { type = "updateAssistant", html });
    private void OnAssistantMessageFinished()         => PostToShell(new { type = "finishAssistant" });
    private void OnErrorAdded(string text)            => PostToShell(new { type = "addError",        text });

    /// <summary>表示更新メッセージをシェルに送る。ready 前なら退避キューに積む。</summary>
    private void PostToShell(object message)
    {
        var json = JsonSerializer.Serialize(message);
        if (_shellReady && TranscriptView.CoreWebView2 is { } core)
        {
            try { core.PostWebMessageAsJson(json); }
            catch (Exception ex) { Debug.WriteLine($"[AiChatWindow] post failed: {ex.Message}"); }
        }
        else
        {
            _pending.Add(json);
        }
    }

    /// <summary>シェル (ai-chat.html) からのメッセージ受信。
    /// 'ready' でキューを流し、'openExternal' でリンクを既定ブラウザに渡す。</summary>
    private void OnWebMessageReceived(object? sender, CoreWebView2WebMessageReceivedEventArgs e)
    {
        try
        {
            using var doc = JsonDocument.Parse(e.WebMessageAsJson);
            var type = doc.RootElement.TryGetProperty("type", out var t) ? t.GetString() : null;

            if (type == "ready")
            {
                _shellReady = true;
                if (TranscriptView.CoreWebView2 is { } core)
                {
                    foreach (var json in _pending)
                    {
                        try { core.PostWebMessageAsJson(json); }
                        catch (Exception ex) { Debug.WriteLine($"[AiChatWindow] flush failed: {ex.Message}"); }
                    }
                }
                _pending.Clear();
                return;
            }

            if (type == "openExternal")
            {
                var url = doc.RootElement.TryGetProperty("url", out var u) ? u.GetString() : null;
                OpenExternal(url);
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[AiChatWindow] message handling failed: {ex.Message}");
        }
    }

    /// <summary>WebView2 内からの新規ウィンドウ要求は出さず、外部ブラウザに委譲する (= 安全策)。
    /// 通常のリンクは JS 側で openExternal に変換済みなので、ここに来るのは想定外経路。</summary>
    private void OnNewWindowRequested(object? sender, CoreWebView2NewWindowRequestedEventArgs e)
    {
        e.Handled = true;
        OpenExternal(e.Uri);
    }

    /// <summary>http/https の URL を OS 既定ブラウザで開く。それ以外は無視。</summary>
    private static void OpenExternal(string? url)
    {
        if (string.IsNullOrEmpty(url)) return;
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)) return;
        if (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps) return;
        try
        {
            Process.Start(new ProcessStartInfo { FileName = url, UseShellExecute = true });
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[AiChatWindow] open external failed: {ex.Message}");
        }
    }

    private void OnWindowClosed(object? sender, EventArgs e)
    {
        _vm.UserMessageAdded         -= OnUserMessageAdded;
        _vm.AssistantMessageStarted  -= OnAssistantMessageStarted;
        _vm.AssistantHtmlUpdated     -= OnAssistantHtmlUpdated;
        _vm.AssistantMessageFinished -= OnAssistantMessageFinished;
        _vm.ErrorAdded               -= OnErrorAdded;

        if (TranscriptView.CoreWebView2 is { } core)
        {
            core.WebMessageReceived -= OnWebMessageReceived;
            core.NewWindowRequested -= OnNewWindowRequested;
        }
        TranscriptView.Dispose();
    }

    /// <summary>入力欄のキー処理: Enter で送信、Shift+Enter は通常の改行として通す。
    /// IME 変換確定の Enter は <see cref="KeyEventArgs.Key"/> が <see cref="Key.ImeProcessed"/> になり
    /// <see cref="Key.Enter"/> 判定に引っかからないので、自然に「送信せず変換確定」になる (= 誤送信防止)。</summary>
    private void InputBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;
        if ((Keyboard.Modifiers & ModifierKeys.Shift) == ModifierKeys.Shift) return; // Shift+Enter = 改行

        e.Handled = true;
        if (_vm.SendCommand.CanExecute(null))
            _vm.SendCommand.Execute(null);
    }
}
