using System;
using System.Collections.Generic;
using System.Text;
using System.Threading.Tasks;
using ChBrowser.Models;
using ChBrowser.Services.Llm;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace ChBrowser.ViewModels;

/// <summary>
/// AI チャットウィンドウ (<see cref="ChBrowser.Views.AiChatWindow"/>) の ViewModel。
/// スレッド単位で開かれ、システムプロンプトにそのスレの内容を丸ごと含めた状態で
/// OpenAI 互換 API と会話する。応答は SSE ストリーミングで受け取り、markdown を逐次 HTML 化して
/// イベントで Window (= WebView2 シェル) に流す。
///
/// 会話表示は WPF のコレクションではなく WebView2 上で行うため、VM は「表示更新イベント」を公開し、
/// Window がそれを購読して WebView2 へ post する構成にしている。
/// </summary>
public sealed partial class AiChatViewModel : ObservableObject
{
    /// <summary>markdown 再レンダの最小間隔 (ms)。ストリーミング中、これより短い間隔の delta は
    /// バッファに溜めるだけにして再レンダ回数を抑える (= 長文応答での O(n^2) 再パースを緩和)。</summary>
    private const long RenderThrottleMs = 80;

    private readonly LlmClient   _llmClient;
    private readonly LlmSettings _settings;
    /// <summary>API に投げる全履歴。先頭は必ず system (= スレ内容入りプロンプト)。</summary>
    private readonly List<LlmChatMessage> _history = new();

    /// <summary>ウィンドウタイトルに出すスレタイトル。</summary>
    public string ThreadTitle { get; }

    [ObservableProperty] private string _inputText = "";

    /// <summary>応答待ち / エラー等の一時ステータス。空ならステータス行は出さない。</summary>
    [ObservableProperty] private string _statusMessage = "";

    public IAsyncRelayCommand SendCommand { get; }

    // ---- Window (WebView2 シェル) が購読する表示更新イベント ----
    // すべて UI スレッド上で発火する (= SendAsync が UI スレッドで動き、ChatStreamAsync が
    // ConfigureAwait(true) で継続を UI スレッドに戻すため)。
    /// <summary>ユーザ発言を 1 件追加 (プレーンテキスト)。</summary>
    public event Action<string>? UserMessageAdded;
    /// <summary>アシスタント応答バブルの生成 (= 最初の delta 到着時)。</summary>
    public event Action? AssistantMessageStarted;
    /// <summary>アシスタント応答バブルの中身を更新 (引数は markdown を HTML 化したもの)。</summary>
    public event Action<string>? AssistantHtmlUpdated;
    /// <summary>アシスタント応答バブルの確定 (= ストリーミング終了)。</summary>
    public event Action? AssistantMessageFinished;
    /// <summary>エラーバブルを 1 件追加。</summary>
    public event Action<string>? ErrorAdded;

    public AiChatViewModel(LlmClient llmClient, LlmSettings settings, string systemPrompt, string threadTitle)
    {
        _llmClient  = llmClient;
        _settings   = settings;
        ThreadTitle = string.IsNullOrEmpty(threadTitle) ? "(無題のスレッド)" : threadTitle;

        // system プロンプト (= スレ内容入り) を履歴の先頭に固定。以降 user/assistant が積まれる。
        _history.Add(new LlmChatMessage("system", systemPrompt));

        SendCommand = new AsyncRelayCommand(SendAsync, () => !string.IsNullOrWhiteSpace(InputText));

        // LLM API 未設定なら、送信前に気づけるよう最初に案内を出しておく。
        if (string.IsNullOrWhiteSpace(_settings.ApiUrl) || string.IsNullOrWhiteSpace(_settings.Model))
            StatusMessage = "LLM API が未設定です。設定 → AI で API URL とモデル名を設定してください。";
    }

    partial void OnInputTextChanged(string value) => SendCommand.NotifyCanExecuteChanged();

    /// <summary>入力欄の内容を user メッセージとして履歴に積み、全履歴を LLM に投げて応答をストリーミング表示する。
    /// 実行中は <see cref="SendCommand"/> が CanExecute=false になり再入は防がれる。</summary>
    private async Task SendAsync()
    {
        var userText = (InputText ?? "").Trim();
        if (userText.Length == 0) return;

        InputText = "";
        UserMessageAdded?.Invoke(userText);
        _history.Add(new LlmChatMessage("user", userText));
        StatusMessage = "AI が応答を生成中…";

        var buffer          = new StringBuilder(); // 受信済み markdown の累積
        var assistantStarted = false;
        var lastRenderTick   = 0L;

        // delta 到着ごとに呼ばれる。最初の delta でバブルを生成し、以降は throttle して再レンダ。
        void OnDelta(string delta)
        {
            buffer.Append(delta);
            if (!assistantStarted)
            {
                assistantStarted = true;
                AssistantMessageStarted?.Invoke();
            }
            var now = Environment.TickCount64;
            if (now - lastRenderTick >= RenderThrottleMs)
            {
                lastRenderTick = now;
                AssistantHtmlUpdated?.Invoke(MarkdownRenderer.ToHtml(buffer.ToString()));
            }
        }

        try
        {
            var result = await _llmClient.ChatStreamAsync(_settings, _history, OnDelta).ConfigureAwait(true);

            // throttle で取りこぼした末尾分を含めて、受信済み全文をここで最終レンダする。
            var finalText = buffer.ToString();
            if (assistantStarted)
            {
                AssistantHtmlUpdated?.Invoke(MarkdownRenderer.ToHtml(finalText));
                AssistantMessageFinished?.Invoke();
                _history.Add(new LlmChatMessage("assistant", finalText));
            }
            else if (result.Ok)
            {
                // 成功したが 1 つも delta が来なかった (= 空応答)。空バブルを出して確定しておく。
                AssistantMessageStarted?.Invoke();
                AssistantHtmlUpdated?.Invoke(MarkdownRenderer.ToHtml(finalText));
                AssistantMessageFinished?.Invoke();
                _history.Add(new LlmChatMessage("assistant", finalText));
            }

            StatusMessage = "";
            // ストリーミングが途中で失敗した場合は、ここまでの部分応答 (上で確定済み) に加えてエラーも出す。
            if (!result.Ok)
                ErrorAdded?.Invoke(result.Error ?? "不明なエラー");
        }
        catch (Exception ex)
        {
            if (assistantStarted) AssistantMessageFinished?.Invoke();
            ErrorAdded?.Invoke(ex.Message);
            StatusMessage = "";
        }
    }
}
