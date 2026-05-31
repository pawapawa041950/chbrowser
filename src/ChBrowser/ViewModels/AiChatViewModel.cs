using System;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using ChBrowser.Models;
using ChBrowser.Services.Agent;
using ChBrowser.Services.Llm;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace ChBrowser.ViewModels;

/// <summary>
/// AI チャットウィンドウ (<see cref="ChBrowser.Views.AiChatWindow"/>) の ViewModel。
/// 3 レイヤーエージェント (<see cref="NewAgentEngine"/>: Strategist / Worker / ToolRuntime) を駆動する。
///
/// <para>表示は <see cref="IAgentHost"/> 実装 (partial: <c>AiChatViewModel.AgentHost.cs</c>) 経由で
/// WebView2 シェル (ai-chat.html) へ。1 ユーザ送信 = 1 バブル。会話 (Strategist の履歴) と finding は
/// エンジンインスタンス内でターンを跨いで保持される (= ウィンドウを閉じない限り維持)。</para>
/// </summary>
public sealed partial class AiChatViewModel : ObservableObject
{
    /// <summary>markdown 再レンダの最小間隔 (ms)。host (AgentHost partial) が使用。</summary>
    private const long RenderThrottleMs = 80;

    private readonly LlmClient        _llmClient;
    /// <summary>セッション共有 archive (= ツール結果の原文置き場・finding の evidence 参照元)。</summary>
    private readonly ChatArchive      _archive = new();
    /// <summary>新エンジンに渡す共有ツール土台。<see cref="SwitchContext"/> で <see cref="AgentToolContext.Thread"/> を差し替える。</summary>
    private readonly AgentToolContext _agentCtx;
    private readonly IAgentEngine     _engine;
    /// <summary>スレッドにアタッチされたチャットなら ThreadToolset を保持。<see cref="SwitchContext"/> で差し替え可。</summary>
    private ThreadToolset?            _threadToolset;

    /// <summary>ウィンドウタイトルに出すスレタイトル。<see cref="SwitchContext"/> で更新可能。</summary>
    [ObservableProperty] private string _threadTitle = "";

    /// <summary>ヘッダ下に出す現在モードの説明文。<see cref="SwitchContext"/> で更新する。</summary>
    [ObservableProperty] private string _contextSubtitle = "";

    [ObservableProperty] private string _inputText = "";

    /// <summary>応答待ち / エラー等の一時ステータス。空ならステータス行は出さない。</summary>
    [ObservableProperty] private string _statusMessage = "";

    public IAsyncRelayCommand SendCommand { get; }

    // ---- Window (WebView2 シェル) が購読する表示更新イベント (すべて UI スレッド上で発火) ----
    /// <summary>ユーザ発言を 1 件追加 (プレーンテキスト)。</summary>
    public event Action<string>? UserMessageAdded;
    /// <summary>イベント駆動の表示更新 (= ai-chat.html のセマンティックプロトコル: begin / seg / trunc /
    /// section / sectionDone / plan / summary / notice / error / end)。引数は JSON 化される匿名オブジェクト。
    /// 旧「全 HTML を innerHTML 置換」方式は廃止し、<see cref="ChBrowser.Services.Llm.TranscriptStreamer"/> 経由で
    /// 確定ブロックは凍結・末尾だけ更新する追記式に刷新した。</summary>
    public event Action<object>? AssistantEvent;

    /// <summary>AI 向けの板/スレ使い分け説明 (ユーザ編集)。文脈プリアンブルに注入する。空なら注入しない。</summary>
    private readonly string _aiBoardGuide;

    public AiChatViewModel(
        LlmClient      llmClient,
        string         threadTitle,
        ThreadToolset? threadToolset,
        AppConfig      config,
        string         aiBoardGuide = "")
    {
        _llmClient      = llmClient;
        _threadToolset  = threadToolset;
        _aiBoardGuide   = aiBoardGuide ?? "";
        ThreadTitle     = string.IsNullOrEmpty(threadTitle) ? "(スレッド指定なし)" : threadTitle;
        ContextSubtitle = ComputeContextSubtitle(threadToolset);

        // 共有ツール土台 + 新 3 レイヤーエンジン。会話/finding はエンジン内で永続 (D8)。
        _agentCtx = new AgentToolContext(_threadToolset, _archive);
        _engine   = new NewAgentEngine(
            host:               this,
            ctx:                _agentCtx,
            llm:                _llmClient,
            strategistSettings: LlmSettings.StrategistFromConfig(config),
            workerSettings:     LlmSettings.WorkerFromConfig(config),
            contextPreamble:    BuildAgentContextPreamble(),
            allowParallel:      config.AllowParallelWorkers);

        SendCommand = new AsyncRelayCommand(SendAsync, () => !string.IsNullOrWhiteSpace(InputText));

        // モデル未設定なら、送信前に気づけるよう最初に案内を出す (Strategist 設定 = 空なら LLM 連携にフォールバック)。
        var s = LlmSettings.StrategistFromConfig(config);
        if (string.IsNullOrWhiteSpace(s.ApiUrl) || string.IsNullOrWhiteSpace(s.Model))
            StatusMessage = "AI モデルが未設定です。設定 → AI で API URL とモデル名を設定してください。";
    }

    partial void OnInputTextChanged(string value) => SendCommand.NotifyCanExecuteChanged();

    /// <summary>このチャットの context を別スレに切り替える。会話 (Strategist の履歴) と archive は維持し、
    /// 共有ツール土台のスレッドだけ差し替える (= 「前の会話を引き継いで別スレ質問できる」UX)。
    /// MainViewModel が SelectedThreadTab 変化 / attached スレタブ closure を観測して呼ぶ。</summary>
    public void SwitchContext(ThreadToolset newToolset, string newThreadTitle)
    {
        _threadToolset   = newToolset;
        _agentCtx.Thread = newToolset;   // 新エンジンの ToolRuntime はここを動的参照する
        ThreadTitle      = string.IsNullOrEmpty(newThreadTitle) ? "(スレッド指定なし)" : newThreadTitle;
        ContextSubtitle  = ComputeContextSubtitle(newToolset);

        StatusMessage = newToolset.HasAttached
            ? $"コンテキスト切替: 「{ThreadTitle}」にアタッチしました (これまでの会話は保持されています)"
            : "コンテキスト切替: スレッドに非アタッチになりました (横断ツールは引き続き使えます)";
    }

    /// <summary>ヘッダのサブタイトル文字列を toolset 状態から組み立てる。</summary>
    private static string ComputeContextSubtitle(ThreadToolset? toolset)
        => toolset is { HasAttached: true }
            ? "このスレッドを文脈に LLM と会話します (他スレも横断アクセス可)"
            : "スレッドに非アタッチ — 板やスレを横断して質問できます";

    /// <summary>新エンジン (Strategist / Worker) の system プロンプトに前置きする文脈 (= スレ attached 状況)。</summary>
    private string BuildAgentContextPreamble()
    {
        var attached = _threadToolset is { HasAttached: true };
        var sb = new StringBuilder();
        sb.Append("これは 5ch 専用ブラウザ「ChBrowser」内蔵の AI です。");
        sb.Append(attached
            ? $"現在「{ThreadTitle}」というスレッドを文脈にしています。"
            : "特定スレッドには非アタッチです。");
        sb.Append("板やスレッドはツールで横断的に参照できます。");
        if (!string.IsNullOrWhiteSpace(_aiBoardGuide))
        {
            sb.Append("\n\n# 板/スレの使い分け (ユーザ提供メモ)\n");
            sb.Append("関連スレを探すときの板選びにこのメモを優先的に参考にすること。板名は list_boards で実 URL に解決する。\n");
            sb.Append(_aiBoardGuide.Trim());
        }
        return sb.ToString();
    }

    /// <summary>入力欄の内容を 1 ターンとしてエンジンに渡す。実行中は <see cref="SendCommand"/> が
    /// CanExecute=false になり再入は防がれる。</summary>
    private async Task SendAsync()
    {
        var userText = (InputText ?? "").Trim();
        if (userText.Length == 0) return;

        InputText = "";
        UserMessageAdded?.Invoke(userText);   // ユーザ発言バブル
        await _engine.RunTurnAsync(userText, CancellationToken.None).ConfigureAwait(true);
    }

    /// <summary>HTML 出力に直接埋め込む文字列のエスケープ。host (AgentHost partial) が使用。</summary>
    private static string EscapeHtml(string s)
    {
        if (string.IsNullOrEmpty(s)) return "";
        return s.Replace("&", "&amp;")
                .Replace("<", "&lt;")
                .Replace(">", "&gt;")
                .Replace("\"", "&quot;");
    }
}
