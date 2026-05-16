using System;
using System.Collections.Generic;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using ChBrowser.Models;
using ChBrowser.Services.Llm;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace ChBrowser.ViewModels;

/// <summary>
/// AI チャットウィンドウ (<see cref="ChBrowser.Views.AiChatWindow"/>) の ViewModel。
/// スレッド単位で開かれ、OpenAI 互換 API と function calling で会話する。
///
/// <para><b>Agent ループ</b>: ユーザ送信ごとに以下を繰り返す。
/// (1) 履歴 + ツール定義を <see cref="LlmClient.ChatStreamAsync"/> に投げる。
/// (2) 応答に tool_calls があれば、各ツールを <see cref="ThreadToolset.Execute"/> で実行し、
///     結果を <c>role:"tool"</c> メッセージとして履歴に追加 → もう一度 (1) に戻る。
/// (3) tool_calls なし (= テキスト応答) なら、それを最終回答として確定し、ループ終了。
/// ループ上限は設けない (= LLM が tool_calls を返さなくなるまで何ラウンドでも回す)。
/// 想定外に長引いた場合はウィンドウを閉じれば中断できる。</para>
///
/// <para><b>表示は 1 ユーザ送信 = 1 バブル</b>。Agent が何ラウンド回ろうとも UI 上はひとつのバブルに
/// まとまる (= ユーザから見て「複数回返答があった」感が出ないようにする)。各ラウンドの本文 +
/// 各ツール呼び出しの表示マーカー (<c>&lt;tool-call&gt;...&lt;/tool-call&gt;</c>) を 1 つの displayBuffer に
/// 累積し、Markdown レンダラがそれを単一の HTML に変換する。
/// 履歴 (LLM へ送る側) は OpenAI 仕様通りラウンドごとの assistant / tool メッセージで構成し、UI 用の
/// マーカーは混ぜない。</para>
/// </summary>
public sealed partial class AiChatViewModel : ObservableObject
{
    /// <summary>markdown 再レンダの最小間隔 (ms)。ストリーミング中、これより短い間隔の delta は
    /// バッファに溜めるだけにして再レンダ回数を抑える (= 長文応答での O(n^2) 再パースを緩和)。</summary>
    private const long RenderThrottleMs = 80;

    /// <summary>テキスト応答が "&lt;think&gt; だけで本文無し" だったときに自動再試行する最大回数。
    /// これを超えても本文が出てこなければエラーとして諦める。</summary>
    private const int MaxThinkOnlyRetries = 2;

    private readonly LlmClient      _llmClient;
    private readonly LlmSettings    _settings;
    /// <summary>スレッドにアタッチされたチャットなら ThreadToolset を保持し、スレッド読み取りツールを提示する。
    /// アタッチされていない (= スレッド非依存) チャットでは null で、その場合 thread 系ツールは提示しない。</summary>
    private readonly ThreadToolset? _threadToolset;
    /// <summary>plan-revise パターンの実体。チャット単位の状態 (= タスク一覧) を持つので
    /// VM が所有する。Thread ツールと並べて LLM に提示する。</summary>
    private readonly PlanToolset    _planToolset = new();
    /// <summary>チャット内で起きた全ての出来事 (ユーザ発言 / アシスタント本文 / 思考 / ツール結果) を
    /// 保管する archive。プロンプトには軽量サマリしか流さない代わりに、必要なら LLM が
    /// list_archive / recall_archive で原文を引き戻せる。</summary>
    private readonly ChatArchive   _archive = new();
    /// <summary>API に投げる全履歴。先頭は必ず system (= ツール案内入りプロンプト)。</summary>
    private readonly List<LlmChatMessage> _history = new();
    /// <summary>tool_call_id → archive エントリ id (= ToolCall 種別) の対応。
    /// プルーニング後のプレースホルダや、recall ガイダンスで参照する。</summary>
    private readonly Dictionary<string, string> _toolCallIdToArchiveId = new(StringComparer.Ordinal);

    /// <summary>次ラウンドの送信時に末尾に挿入するレビュー指示の待機列。
    /// <c>(kind, payload)</c> の形で、kind = "task_complete" / "final_answer"。
    /// <see cref="BuildTrimmedHistoryForSend"/> が消費する (= 1 度だけ送られる)。
    /// SendAsync の冒頭でクリアし、各 SendAsync 内で再蓄積される。</summary>
    private readonly List<(string Kind, string Payload)> _pendingReviewPrompts = new();

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
    /// <summary>アシスタント応答バブルの生成 (= ユーザ送信の最初の表示更新時)。</summary>
    public event Action? AssistantMessageStarted;
    /// <summary>アシスタント応答バブルの中身を更新 (引数は markdown を HTML 化したもの)。</summary>
    public event Action<string>? AssistantHtmlUpdated;
    /// <summary>アシスタント応答バブルの確定 (= 全ラウンド終了)。</summary>
    public event Action? AssistantMessageFinished;
    /// <summary>エラーバブルを 1 件追加。</summary>
    public event Action<string>? ErrorAdded;

    public AiChatViewModel(
        LlmClient      llmClient,
        LlmSettings    settings,
        string         systemPrompt,
        string         threadTitle,
        ThreadToolset? threadToolset)
    {
        _llmClient     = llmClient;
        _settings      = settings;
        _threadToolset = threadToolset;
        ThreadTitle    = string.IsNullOrEmpty(threadTitle) ? "(無題のスレッド)" : threadTitle;

        // system プロンプト (= ツール案内入り) を履歴の先頭に固定。以降 user/assistant/tool が積まれる。
        _history.Add(new LlmChatMessage("system", systemPrompt));

        SendCommand = new AsyncRelayCommand(SendAsync, () => !string.IsNullOrWhiteSpace(InputText));

        // LLM API 未設定なら、送信前に気づけるよう最初に案内を出しておく。
        if (string.IsNullOrWhiteSpace(_settings.ApiUrl) || string.IsNullOrWhiteSpace(_settings.Model))
            StatusMessage = "LLM API が未設定です。設定 → AI で API URL とモデル名を設定してください。";
    }

    partial void OnInputTextChanged(string value) => SendCommand.NotifyCanExecuteChanged();

    /// <summary>入力欄の内容を user メッセージとして履歴に積み、Agent ループを 1 回まわす。
    /// 実行中は <see cref="SendCommand"/> が CanExecute=false になり再入は防がれる。</summary>
    private async Task SendAsync()
    {
        var userText = (InputText ?? "").Trim();
        if (userText.Length == 0) return;

        InputText = "";
        UserMessageAdded?.Invoke(userText);
        _history.Add(new LlmChatMessage("user", userText));
        _archive.RecordUserMessage(userText);
        // 前回ユーザ送信で残った review 待機列を破棄 (= 各 user 送信は独立した文脈)。
        _pendingReviewPrompts.Clear();
        StatusMessage = ComposeStatus("AI が応答を生成中…");

        // toolset を結合して LLM に提示する。plan 系を先頭、続いて archive 系、(あれば) 最後に thread 系。
        // 順序は LLM が「まず計画 → 必要なら過去参照 → スレ読み取り」の優先度を読み取りやすくする狙い。
        // スレッド非依存のチャット (= _threadToolset null) では thread 系を提示しない。
        var toolDefs = new List<object>();
        toolDefs.AddRange(_planToolset.GetToolDefinitions());
        toolDefs.AddRange(_archive.GetToolDefinitions());
        if (_threadToolset is not null)
            toolDefs.AddRange(_threadToolset.GetToolDefinitions());

        // 1 ユーザ送信 = 1 アシスタントバブル。Agent のラウンド数によらず 1 バブルに統合する。
        // displayBuffer は「表示用」の累積で、各ラウンドの LLM テキスト + ラウンド間の <tool-call> マーカーを含む。
        // 履歴 (= _history) は OpenAI 仕様通りラウンドごとの assistant / tool メッセージで持つ (UI マーカー無し)。
        var displayBuffer  = new StringBuilder();
        var bubbleStarted  = false;
        var lastRenderTick = 0L;
        // 最終回答レビューを 1 回挟むためのフラグ。テキストのみ応答が来た最初の回はレビューを挟み、
        // 2 回目は accept する (= 無限ループ防止)。
        var finalReviewIssued = false;
        // think だけで本文 0 だった応答を再試行した回数。MaxThinkOnlyRetries に達したらエラー終了。
        var thinkOnlyRetries = 0;
        // displayBuffer 内で「最終回答が始まる位置」のインデックス。null の間は全体が折りたたまれ、
        // 値が入ると pre = 折りたたみ / post = 通常表示で分割される (= MarkdownRenderer がやる)。
        // post-review ラウンドの開始時に displayBuffer.Length をセットし、もしそのラウンドが tool_calls
        // だった (= 最終ではなかった) ら null に戻す。
        int? agentFinalBoundary = null;

        void EnsureBubble()
        {
            if (!bubbleStarted)
            {
                bubbleStarted = true;
                AssistantMessageStarted?.Invoke();
            }
        }

        void RenderDisplay(bool force)
        {
            EnsureBubble();
            var now = Environment.TickCount64;
            if (!force && now - lastRenderTick < RenderThrottleMs) return;
            lastRenderTick = now;
            // agent モード: サマリは現在何をしているかの 1 行、境界は最終回答開始位置。
            AssistantHtmlUpdated?.Invoke(MarkdownRenderer.ToHtml(
                displayBuffer.ToString(),
                BuildAgentWorkSummary(),
                agentFinalBoundary));
        }

        try
        {
            // ループ上限なし: LLM が tool_calls を返し続ける限り回り続ける。
            // ユーザがウィンドウを閉じる / API が失敗する / LLM がテキスト応答を返す、のどれかで抜ける。
            while (true)
            {
                // post-review ラウンドの開始時に boundary を設定 (= ここから最終回答テキストが流れる想定)。
                // もしこのラウンドが tool_calls を返した場合は後で null に戻す。
                if (finalReviewIssued && agentFinalBoundary is null)
                {
                    agentFinalBoundary = displayBuffer.Length;
                }
                // このラウンドの LLM テキストだけを切り出したいので、ラウンド開始時の累積長さを覚えておく。
                var roundStart = displayBuffer.Length;

                void OnDelta(string delta)
                {
                    displayBuffer.Append(delta);
                    RenderDisplay(force: false);
                }

                // 送信用にトリミング:
                //  - 過去 assistant メッセージから <think>...</think> を除去 (= 直近 1 ラウンドだけ残す)
                //  - 完了済タスクに紐づく thread-tool 結果を短いプレースホルダに差し替え
                // 詳細は BuildTrimmedHistoryForSend 参照。_history 本体は触らない (UI / findings 用)。
                var historyToSend = BuildTrimmedHistoryForSend();

                var result = await _llmClient
                    .ChatStreamAsync(_settings, historyToSend, OnDelta, toolDefs)
                    .ConfigureAwait(true);

                if (!result.Ok)
                {
                    RenderDisplay(force: true);
                    if (bubbleStarted) AssistantMessageFinished?.Invoke();
                    ErrorAdded?.Invoke(result.Error ?? "不明なエラー");
                    StatusMessage = "";
                    return;
                }

                // このラウンドが追加した生テキスト (= 履歴に保存する assistant.content)。
                var roundText = displayBuffer.ToString(roundStart, displayBuffer.Length - roundStart);

                // ---- (A) ツール呼び出しあり: 実行 → 履歴 + バブル両方に反映 → 次ラウンドへ ----
                if (result.ToolCalls.Count > 0)
                {
                    // このラウンドは最終回答ではなかったので、speculative にセットした境界は取り消す。
                    // (= テキストも tool_calls も同時に来るケース対応。流れた中間テキストも折りたたみ側に残る。)
                    agentFinalBoundary = null;

                    // ラウンドが <think> を開いたまま閉じずにツール呼び出しへ移ったケースを救う:
                    // 表示上は「ツール表示の前で think を閉じる」のが自然なので、displayBuffer 末尾に
                    // </think> を補う (= 履歴には反映しない、表示専用)。
                    if (HasUnclosedThink(roundText))
                        displayBuffer.Append("</think>");

                    _history.Add(new LlmChatMessage("assistant", roundText)
                    {
                        ToolCalls = result.ToolCalls,
                    });
                    // archive: このラウンドのアシスタント本文 (= text 部 + think 部) を残す。
                    _archive.RecordAssistantContent(roundText, taskId: null);

                    foreach (var tc in result.ToolCalls)
                    {
                        var label = FormatToolCallForStatus(tc);
                        StatusMessage = ComposeStatus($"ツール実行中: {label}");

                        // バブル内に「ツール呼び出し」の視覚的マーカーを差し込む。MarkdownRenderer が
                        // <tool-call> ... </tool-call> を専用スタイルで囲むので、本文と区別される。
                        // 中身は HTML としてそのまま出すので、ここでエスケープしておく。
                        displayBuffer.Append("\n\n<tool-call>")
                                     .Append(EscapeHtml(label))
                                     .Append("</tool-call>\n\n");

                        var toolResult = DispatchTool(tc.Name, tc.ArgumentsJson);
                        _history.Add(new LlmChatMessage("tool", toolResult)
                        {
                            ToolCallId = tc.Id,
                        });

                        // complete_task が呼ばれたら、次ラウンドの送信で「タスク完了レビュー」を挟むため待機列に積む。
                        // これにより LLM は「直前の finding は依頼を満たすに足るか / 残タスクは適切か」を毎タスク後に強制確認する。
                        if (tc.Name == "complete_task")
                        {
                            var completedTaskId = TryGetStringField(tc.ArgumentsJson, "id");
                            if (!string.IsNullOrEmpty(completedTaskId))
                                _pendingReviewPrompts.Add(("task_complete", completedTaskId!));
                        }

                        // archive: 原文を保管しておき、後で recall_archive 経由で引き戻せるようにする。
                        // archive 自身のツール (list/recall) や plan 系ツールは復元の対象として薄いので除外。
                        if (!IsArchivableToolCall(tc.Name)) continue;
                        var archiveId = _archive.RecordToolCall(tc.Name, tc.ArgumentsJson, toolResult, taskId: null);
                        _toolCallIdToArchiveId[tc.Id] = archiveId;
                    }

                    RenderDisplay(force: true);
                    StatusMessage = ComposeStatus("AI がツール結果を解釈中…");
                    continue;
                }

                // ---- (B) ツール呼び出しなし (= テキスト応答) ----
                //
                // (B-0) 本文 0 (= <think> しか無い / 完全に空) の応答チェック
                // リーズニングモデルが推論枠を think に使い切って本文を出せずに終わるケースがある。
                // この場合、ユーザ向けの本文を出すように直接指示してリトライする (= 最大 MaxThinkOnlyRetries 回)。
                if (IsThinkOnlyOrEmptyResponse(roundText))
                {
                    if (thinkOnlyRetries < MaxThinkOnlyRetries)
                    {
                        // 失敗した試行も履歴と archive に残す (= LLM が次ラウンドで「前回は本文出さなかった」
                        // を観測できる)。空文字は記録しない。
                        if (!string.IsNullOrEmpty(roundText))
                        {
                            _history.Add(new LlmChatMessage("assistant", roundText));
                            _archive.RecordAssistantContent(roundText, taskId: null);
                        }

                        // 視覚マーカー: バブル内に「再生成中」を入れる。境界もここで前に進め、
                        // 失敗した試行は agent-work 折りたたみの内側に押し込む。
                        displayBuffer.Append("\n\n<tool-call>本文を再生成中… (本文 0 のためリトライ)</tool-call>\n\n");
                        agentFinalBoundary = displayBuffer.Length;

                        _pendingReviewPrompts.Add(("think_only_retry", ""));
                        thinkOnlyRetries++;
                        RenderDisplay(force: true);
                        StatusMessage = ComposeStatus($"AI が本文を再生成中… (試行 {thinkOnlyRetries + 1}/{MaxThinkOnlyRetries + 1})");
                        continue;
                    }

                    // リトライ上限到達: ユーザに状況を出して諦める。
                    if (bubbleStarted) AssistantMessageFinished?.Invoke();
                    ErrorAdded?.Invoke($"AI が本文を出力できませんでした ({MaxThinkOnlyRetries + 1} 回試行)。Context Size を増やすか、別のモデルをお試しください。");
                    StatusMessage = "";
                    return;
                }

                // 最初のテキスト応答は「下書き」とみなし、最終回答レビューを 1 回挟む (= 無限ループ防止のため 1 回だけ)。
                // 2 回目以降のテキスト応答はそのまま最終回答として確定する。
                if (!finalReviewIssued && !string.IsNullOrEmpty(roundText))
                {
                    finalReviewIssued = true;

                    // 下書きを履歴と archive には残す (= レビュー側 LLM がこれを見て可否を判断するため)。
                    _history.Add(new LlmChatMessage("assistant", roundText));
                    _archive.RecordAssistantContent(roundText, taskId: null);

                    // displayBuffer は下書き分を巻き戻し、その場所に「最終回答の見直し中…」マーカーを置く。
                    // ストリーミングで一瞬下書きが見えた後、レビュー後の最終回答に差し替わる UX になる。
                    displayBuffer.Length = roundStart;
                    displayBuffer.Append("\n\n<tool-call>最終回答の見直し中…</tool-call>\n\n");
                    RenderDisplay(force: true);

                    _pendingReviewPrompts.Add(("final_answer", roundText));
                    StatusMessage = ComposeStatus("AI が最終回答を見直し中…");
                    continue;
                }

                // ---- 最終確定 ----
                if (!string.IsNullOrEmpty(roundText))
                {
                    _history.Add(new LlmChatMessage("assistant", roundText));
                    _archive.RecordAssistantContent(roundText, taskId: null);
                }

                RenderDisplay(force: true);
                if (bubbleStarted) AssistantMessageFinished?.Invoke();
                else
                {
                    // バブルがまだ無く (= delta が一度も来てない) かつテキストも空: 空応答。
                    if (roundText.Length == 0)
                        ErrorAdded?.Invoke("AI から空の応答が返りました");
                }

                StatusMessage = "";
                return;
            }
        }
        catch (Exception ex)
        {
            if (bubbleStarted) AssistantMessageFinished?.Invoke();
            ErrorAdded?.Invoke(ex.Message);
            StatusMessage = "";
        }
    }

    /// <summary>応答が「&lt;think&gt;...&lt;/think&gt; しか無い / 完全に空」かを判定する。
    /// 閉じている think は通常マッチで除去、ストリーミング途中の閉じ忘れ &lt;think&gt;...$ も末尾まで除去し、
    /// その結果空白だけ残ったら think-only とみなす。</summary>
    private static readonly Regex ClosedThinkStripRe =
        new(@"<think>.*?</think>", RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.Compiled);
    private static readonly Regex TrailingOpenThinkRe =
        new(@"<think>[\s\S]*$", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static bool IsThinkOnlyOrEmptyResponse(string content)
    {
        if (string.IsNullOrWhiteSpace(content)) return true;
        var t = ClosedThinkStripRe.Replace(content, "");
        t = TrailingOpenThinkRe.Replace(t, "");
        return string.IsNullOrWhiteSpace(t);
    }

    /// <summary>roundText (= 1 ラウンドが新規に書き加えた assistant 本文) の中で、
    /// 開いた &lt;think&gt; の数が閉じた &lt;/think&gt; の数より多いかを判定する。
    /// true なら表示上は閉じてからツール表示に移った方が自然なので、呼び出し側が
    /// displayBuffer に &lt;/think&gt; を補う。 </summary>
    private static bool HasUnclosedThink(string s)
    {
        // 簡易: case-insensitive で出現回数を数えるだけ (= 構造解析はしない)。
        // 入れ子はそもそも非対応 (= LLM が <think> を入れ子で出してくることは現実的に無い)。
        var opens  = CountOccurrences(s, "<think>",  StringComparison.OrdinalIgnoreCase);
        var closes = CountOccurrences(s, "</think>", StringComparison.OrdinalIgnoreCase);
        return opens > closes;
    }

    private static int CountOccurrences(string s, string needle, StringComparison cmp)
    {
        if (string.IsNullOrEmpty(s) || string.IsNullOrEmpty(needle)) return 0;
        int count = 0, idx = 0;
        while ((idx = s.IndexOf(needle, idx, cmp)) >= 0)
        {
            count++;
            idx += needle.Length;
        }
        return count;
    }

    /// <summary>HTML 出力に直接埋め込む文字列のエスケープ (= &amp;, &lt;, &gt;, " )。
    /// ツール名 / 引数 JSON にこれらが混ざっていても安全に表示する。</summary>
    private static string EscapeHtml(string s)
    {
        if (string.IsNullOrEmpty(s)) return "";
        return s.Replace("&", "&amp;")
                .Replace("<", "&lt;")
                .Replace(">", "&gt;")
                .Replace("\"", "&quot;");
    }

    /// <summary>tool 名で plan / archive / (任意) thread のいずれの toolset に dispatch するかを決める。
    /// 上から順に試し、thread toolset が無い (= スレッド非依存チャット) 場合は最後に error JSON を返す。</summary>
    private string DispatchTool(string name, string argumentsJson)
    {
        var planResult = _planToolset.TryExecute(name, argumentsJson);
        if (planResult is not null) return planResult;
        var archiveResult = _archive.TryExecute(name, argumentsJson);
        if (archiveResult is not null) return archiveResult;
        if (_threadToolset is not null) return _threadToolset.Execute(name, argumentsJson);
        // thread toolset が無いのに thread 系ツールを呼ばれた = LLM が手探りで叩いてきたケース。
        // 「使えない」を明示して次のラウンドで諦めさせる。
        return "{\"error\":\"ツール \\\"" + name.Replace("\"", "\\\"") + "\\\" はこのチャットでは利用できません (スレッドにアタッチされていません)\"}";
    }

    /// <summary>archive に残す価値があるツール呼び出しか。plan 系 (= 状態変更だけ) と archive 系
    /// (= recall は archive 自身の参照なのでループ蓄積を避ける) は除外する。
    /// 残るのは thread 読み取り系 (= 原文を含む大きな結果)。</summary>
    private static bool IsArchivableToolCall(string toolName) => toolName switch
    {
        "create_plan" or "complete_task" or "revise_plan" => false,
        "list_archive" or "recall_archive"                 => false,
        _                                                   => true,
    };

    /// <summary>ステータス文字列に「現在の plan 進捗」を前置きする。plan が無いときはそのまま返す。</summary>
    private string ComposeStatus(string baseMessage)
    {
        var planCount = _planToolset.Tasks.Count;
        if (planCount == 0) return baseMessage;
        var done = _planToolset.CompletedCount;
        return $"[計画 {done}/{planCount}] {baseMessage}";
    }

    /// <summary>「agent work 折りたたみバー」に表示する 1 行サマリ。
    /// 動作中は <see cref="StatusMessage"/> をそのまま使う (= 何をしているかが分かる)。
    /// StatusMessage が空 (= 一段落 or 完了) のときは plan / archive の集計値から完了サマリを作る。</summary>
    private string BuildAgentWorkSummary()
    {
        if (!string.IsNullOrEmpty(StatusMessage)) return StatusMessage;
        var planDone  = _planToolset.CompletedCount;
        var planTotal = _planToolset.Tasks.Count;
        var toolCalls = 0;
        foreach (var e in _archive.Entries) if (e.Kind == ArchiveKind.ToolCall) toolCalls++;

        if (planTotal > 0 && toolCalls > 0) return $"作業内容を表示 ({planDone}/{planTotal} タスク・{toolCalls} ツール呼び出し)";
        if (planTotal > 0)                  return $"作業内容を表示 ({planDone}/{planTotal} タスク)";
        if (toolCalls > 0)                  return $"作業内容を表示 ({toolCalls} ツール呼び出し)";
        return "作業内容を表示";
    }

    /// <summary>ステータス行 / バブル内マーカーに出すツール呼び出し表示。
    /// plan 系 (create_plan / complete_task / revise_plan) は引数 JSON を覗いて人間向け表示にする。
    /// それ以外は <c>name({args 短縮})</c>。 </summary>
    private static string FormatToolCallForStatus(LlmToolCall tc)
    {
        var args = tc.ArgumentsJson ?? "";

        // plan 系ツールは人間向けに整形 (= 引数 JSON 生表示は読みにくいため)。
        switch (tc.Name)
        {
            case "create_plan":
            case "revise_plan":
            {
                var n     = TryCountTasksInArgs(args);
                var label = tc.Name == "create_plan" ? "計画作成" : "計画修正";
                return n is int count ? $"{label} ({count} タスク)" : label;
            }
            case "complete_task":
            {
                var id  = TryGetStringField(args, "id");
                var idDisplay = string.IsNullOrEmpty(id) ? "?" : id;
                return $"タスク完了: {idDisplay}";
            }
        }

        const int max = 160;
        if (args.Length > max) args = args.Substring(0, max) + "…";
        return $"{tc.Name}({args})";
    }

    /// <summary>create_plan / revise_plan の args JSON から tasks 配列の長さを取り出す (= 失敗時 null)。</summary>
    private static int? TryCountTasksInArgs(string argsJson)
    {
        try
        {
            using var doc = JsonDocument.Parse(argsJson);
            if (doc.RootElement.ValueKind != JsonValueKind.Object) return null;
            if (!doc.RootElement.TryGetProperty("tasks", out var tasks)) return null;
            if (tasks.ValueKind != JsonValueKind.Array) return null;
            return tasks.GetArrayLength();
        }
        catch { return null; }
    }

    /// <summary>引数 JSON から指定キーの string 値を取り出す (= 失敗時 null)。</summary>
    private static string? TryGetStringField(string argsJson, string key)
    {
        try
        {
            using var doc = JsonDocument.Parse(argsJson);
            if (doc.RootElement.ValueKind != JsonValueKind.Object) return null;
            if (!doc.RootElement.TryGetProperty(key, out var el)) return null;
            return el.ValueKind == JsonValueKind.String ? el.GetString() : null;
        }
        catch { return null; }
    }

    // ============================================================================
    // history トリミング (= LLM への送信ペイロード削減)
    //
    // 何ラウンドも回るエージェントで context window を食い潰さないため、毎ラウンド
    // ChatStreamAsync に渡す直前に履歴を整形する。<see cref="_history"/> 本体は触らない
    // (= UI 表示や finding の参照は元のまま)。
    //
    // 削減ルール:
    //   (a) 過去 assistant の <think>...</think> を全削除
    //   (b) 直近 1 ラウンドの assistant だけは <think> を保持 (= LLM が思考の連続性を保ちやすい)
    //   (c) 完了済タスクに紐づく thread-tool 結果を「省略済」プレースホルダ JSON に差し替え
    //       (要点は plan.findings 経由で各ツール応答に同梱されているので情報損失は最小)
    //
    // ツール結果のプルーニングは「どの tool_call_id がどのタスクに属したか」の特定が必要。
    // LLM はそれを明示しないので、履歴を順に走査し
    // 「直前の plan マーカー (create_plan/revise_plan) または complete_task 以降の thread-tool
    //  呼び出しは、次に来た complete_task のタスクに属する」と推定する。
    // ============================================================================

    /// <summary><see cref="_history"/> を送信用にトリムした新リストを返す。</summary>
    private List<LlmChatMessage> BuildTrimmedHistoryForSend()
    {
        var callToTask = BuildToolCallToTaskMap();

        // どのタスクが「完了済」か (= 現在の plan 状態から判定)。
        var completedTaskIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var t in _planToolset.Tasks)
        {
            if (t.Status == PlanTaskStatus.Completed) completedTaskIds.Add(t.Id);
        }

        // 直近 assistant のインデックス (= ここの <think> は残す)。
        var lastAssistantIdx = -1;
        for (var i = _history.Count - 1; i >= 0; i--)
        {
            if (_history[i].Role == "assistant") { lastAssistantIdx = i; break; }
        }

        var trimmed = new List<LlmChatMessage>(_history.Count);
        for (var i = 0; i < _history.Count; i++)
        {
            var msg = _history[i];

            // (a) + (b) past assistant の <think> 除去 (直近は触らない)
            if (msg.Role == "assistant" && i != lastAssistantIdx)
            {
                var stripped = StripThinkBlocks(msg.Content);
                if (!ReferenceEquals(stripped, msg.Content))
                {
                    trimmed.Add(new LlmChatMessage(msg.Role, stripped)
                    {
                        ToolCalls  = msg.ToolCalls,
                        ToolCallId = msg.ToolCallId,
                    });
                    continue;
                }
            }

            // (c) 完了済タスクに属する thread-tool 結果のプレースホルダ差し替え
            if (msg.Role == "tool" &&
                msg.ToolCallId is { Length: > 0 } cid &&
                callToTask.TryGetValue(cid, out var owningTaskId) &&
                completedTaskIds.Contains(owningTaskId))
            {
                // 対応する archive id (= recall_archive で原文を引くキー) があれば添える。
                _toolCallIdToArchiveId.TryGetValue(cid, out var archiveId);
                trimmed.Add(new LlmChatMessage(msg.Role, BuildExpiredToolPlaceholder(owningTaskId, archiveId))
                {
                    ToolCallId = msg.ToolCallId,
                });
                continue;
            }

            trimmed.Add(msg);
        }

        // セッション状態スナップショット (= 現在の plan + archive 目録) を
        // 元の system メッセージの直後に挿入する。これにより LLM は毎ラウンド最新の作業状態を
        // 「自動的に」見られるため、list_archive を能動的に呼ばなくても recall の動機が生まれる。
        var stateMsg = BuildSessionStateMessage();
        if (stateMsg is not null)
        {
            var insertAt = 0;
            while (insertAt < trimmed.Count && trimmed[insertAt].Role == "system") insertAt++;
            trimmed.Insert(insertAt, stateMsg);
        }

        // レビュー指示の差し込み: 末尾 (= LLM が直前に見る位置) に system メッセージとして付ける。
        // 種類は task_complete (= complete_task 直後) と final_answer (= テキスト応答後の見直し)。
        // 1 度送ったらキューから消す (= 同じレビューを次ラウンドにも送らない)。
        if (_pendingReviewPrompts.Count > 0)
        {
            foreach (var (kind, payload) in _pendingReviewPrompts)
            {
                var reviewText = kind switch
                {
                    "task_complete"    => BuildTaskCompleteReviewPrompt(payload),
                    "final_answer"     => BuildFinalAnswerReviewPrompt(payload),
                    "think_only_retry" => BuildThinkOnlyRetryPrompt(),
                    _                  => null,
                };
                if (!string.IsNullOrEmpty(reviewText))
                    trimmed.Add(new LlmChatMessage("system", reviewText));
            }
            _pendingReviewPrompts.Clear();
        }

        return trimmed;
    }

    /// <summary>complete_task 直後に挟むレビュープロンプト。ユーザの依頼 / 直前完了タスクの finding /
    /// 残タスクを並べ、「このまま進めていいか / revise すべきか」の判断を促す。</summary>
    private string BuildTaskCompleteReviewPrompt(string completedTaskId)
    {
        var sb = new StringBuilder();
        sb.AppendLine("[task 完了レビュー — 次の行動を決める前に必ず以下を確認]");
        sb.AppendLine();

        sb.AppendLine("## ユーザの依頼 (最新のユーザメッセージ)");
        sb.Append("> ").AppendLine(EscapeQuoteLine(GetLatestUserMessage()));
        sb.AppendLine();

        var task = _planToolset.Tasks.FirstOrDefault(t => t.Id == completedTaskId);
        sb.AppendLine("## 直前に完了したタスク");
        if (task is not null)
        {
            sb.Append("- ").Append(task.Id).Append(": ").AppendLine(task.Description);
            if (!string.IsNullOrEmpty(task.Finding))
                sb.Append("    finding: ").AppendLine(task.Finding);
        }
        else
        {
            sb.Append("- id=\"").Append(completedTaskId).AppendLine("\" (plan から消えています — revise されたか不整合)");
        }
        sb.AppendLine();

        var pending = _planToolset.Tasks.Where(t => t.Status != PlanTaskStatus.Completed).ToList();
        sb.AppendLine("## 残タスク");
        if (pending.Count == 0)
        {
            sb.AppendLine("- (なし — 全タスク完了)");
        }
        else
        {
            foreach (var t in pending)
                sb.Append("- ").Append(t.Id).Append(": ").AppendLine(t.Description);
        }
        sb.AppendLine();

        sb.AppendLine("## いま確認すべきこと");
        sb.AppendLine("1. 上記 finding はユーザの依頼に対して意味のある進捗か?");
        sb.AppendLine("2. 残タスクで本当にユーザの期待に応えられるか? 抜け・過剰・順序ミスは無いか?");
        sb.AppendLine("3. 見直しが必要なら revise_plan を呼ぶ (不要タスクの削除 / 追加調査 / 順序変更)");
        sb.AppendLine("4. 問題なければそのまま次の行動 (= 残タスク実行 or 最終回答) に進む");
        sb.AppendLine("形式的に通り過ぎず、依頼文を読み直してから判断すること。");

        return sb.ToString();
    }

    /// <summary>テキストのみ応答 (= 最終回答ドラフト) が来た直後に 1 度だけ挟むレビュープロンプト。
    /// ユーザの依頼に対して下書きが十分か、追加調査が必要か、を確認させる。
    /// 不要なら下書きをそのまま (or 微修正して) 再出力する。</summary>
    private string BuildFinalAnswerReviewPrompt(string draftAnswer)
    {
        var sb = new StringBuilder();
        sb.AppendLine("[最終回答レビュー — ユーザに出す前のチェックポイント]");
        sb.AppendLine();

        sb.AppendLine("## ユーザの依頼 (最新のユーザメッセージ)");
        sb.Append("> ").AppendLine(EscapeQuoteLine(GetLatestUserMessage()));
        sb.AppendLine();

        sb.AppendLine("## あなたが直前に書いた下書き回答");
        sb.AppendLine("```");
        sb.AppendLine(draftAnswer);
        sb.AppendLine("```");
        sb.AppendLine();

        // 残タスクが残っているなら強めに警告
        var pending = _planToolset.Tasks.Where(t => t.Status != PlanTaskStatus.Completed).ToList();
        if (pending.Count > 0)
        {
            sb.AppendLine("## 注意: plan に未完了タスクが残っています");
            foreach (var t in pending)
                sb.Append("- ").Append(t.Id).Append(": ").AppendLine(t.Description);
            sb.AppendLine("本当にこれらをスキップして回答していい状況か? 必要なら revise_plan で外す、または実行してから回答する。");
            sb.AppendLine();
        }

        sb.AppendLine("## いま確認すべきこと");
        sb.AppendLine("1. 下書きはユーザの依頼に正面から答えているか? 質問の核心を外していないか?");
        sb.AppendLine("2. 抜けや誤りや推測 (= スレに無いことを書いている) は無いか?");
        sb.AppendLine("3. 追加調査が必要なら revise_plan で新タスクを立て、調査してから再度回答に戻る");
        sb.AppendLine("4. 問題なければ最終回答を出力する。下書きをそのまま再掲してよい (= 同じテキストでも構わない)。");
        sb.AppendLine("5. レビュー結果として何か出力すること (空応答にしない)。");

        return sb.ToString();
    }

    /// <summary>think-only な応答が来たときに次ラウンドへ強制的に「本文を出してください」と指示するプロンプト。
    /// <see cref="BuildFinalAnswerReviewPrompt"/> よりも強く、簡潔に振る舞いを矯正する目的。</summary>
    private string BuildThinkOnlyRetryPrompt()
    {
        var sb = new StringBuilder();
        sb.AppendLine("[本文出力リトライ — 前回応答に本文がありませんでした]");
        sb.AppendLine();
        sb.AppendLine("あなたの前回の応答は <think>...</think> ブロックの中身しか無く、ユーザに見える本文 (= <think> の外側のテキスト) がありませんでした。");
        sb.AppendLine("推論枠を think に使い切ったか、思考の途中で出力が止まった可能性があります。");
        sb.AppendLine();
        sb.AppendLine("## 今すぐやること");
        sb.AppendLine("- <think> を **使わずに** 直接ユーザ向けの最終回答テキストを出力する。");
        sb.AppendLine("- これまでの plan と findings から分かった結論を、短くてもいいので明確に述べる。");
        sb.AppendLine("- スレ内に十分な情報が無いと判断したなら「スレ内には書かれていない」と書いて構わない。");
        sb.AppendLine("- 推測ではなく、これまで取得した情報に基づいて書くこと。");
        sb.AppendLine("- 今回はツールも plan も呼ばない。テキスト本文のみを出力する。");
        return sb.ToString();
    }

    /// <summary>履歴の中で最も新しいユーザメッセージの本文を返す。無ければ空文字。</summary>
    private string GetLatestUserMessage()
    {
        for (var i = _history.Count - 1; i >= 0; i--)
        {
            if (_history[i].Role == "user") return _history[i].Content ?? "";
        }
        return "";
    }

    /// <summary>引用ブロック ("> ...") 内に複数行を入れるための加工。各改行を "> " で接続して
    /// 引用が途切れないようにする。長すぎる文は適度に短縮 (= 1000 字キャップ)。</summary>
    private static string EscapeQuoteLine(string s)
    {
        if (string.IsNullOrEmpty(s)) return "";
        const int maxLen = 1000;
        var t = s.Length > maxLen ? s.Substring(0, maxLen) + "…" : s;
        return t.Replace("\r\n", "\n").Replace("\n", "\n> ");
    }

    /// <summary>session state スナップショットに載せる archive エントリの上限。
    /// これより古いものは「省略 — list_archive で取得可」と注記する。</summary>
    private const int MaxArchiveBriefsInSessionState = 30;

    /// <summary>毎ラウンド冒頭に挿入する「現在の作業状態」メッセージを組み立てる。
    /// plan も archive も空ならスキップ (= null を返す)。
    /// 中身: (1) plan の全タスクと finding、(2) archive 目録 (新しい順、上位 N 件)。</summary>
    private LlmChatMessage? BuildSessionStateMessage()
    {
        var hasPlan    = _planToolset.Tasks.Count > 0;
        var hasArchive = _archive.Entries.Count > 0;
        if (!hasPlan && !hasArchive) return null;

        var sb = new StringBuilder();
        sb.AppendLine("[セッション状態スナップショット — 毎ラウンド自動更新]");

        if (hasPlan)
        {
            sb.AppendLine();
            sb.Append("## 現在の計画 (")
              .Append(_planToolset.CompletedCount).Append('/').Append(_planToolset.Tasks.Count)
              .AppendLine(" 完了)");
            foreach (var t in _planToolset.Tasks)
            {
                var mark = t.Status == PlanTaskStatus.Completed ? "✓" : "○";
                sb.Append("- ").Append(mark).Append(' ').Append(t.Id).Append(": ").Append(t.Description);
                if (t.Status == PlanTaskStatus.Completed && !string.IsNullOrEmpty(t.Finding))
                {
                    sb.AppendLine();
                    sb.Append("    finding: ").Append(t.Finding);
                }
                sb.AppendLine();
            }
        }

        if (hasArchive)
        {
            sb.AppendLine();
            sb.Append("## 参照可能な過去 (archive, 新しい順、上位 ")
              .Append(MaxArchiveBriefsInSessionState).AppendLine(" 件)");
            sb.AppendLine("- 原文を見たいときは recall_archive(id=\"aN\") を呼ぶ");
            sb.AppendLine("- 検索・絞り込みは list_archive(filter?) で。");

            var total = _archive.Entries.Count;
            var newestFirst = _archive.Entries.Reverse().Take(MaxArchiveBriefsInSessionState);
            foreach (var e in newestFirst)
            {
                sb.Append("- ").Append(e.Id).Append(": ");
                switch (e.Kind)
                {
                    case ArchiveKind.ToolCall:
                    {
                        sb.Append("tool_call ").Append(e.ToolName).Append('(');
                        var args = e.ToolArgs ?? "";
                        if (args.Length > 60) args = args.Substring(0, 60) + "…";
                        sb.Append(args).Append(')');
                        if (!string.IsNullOrEmpty(e.TaskId)) sb.Append(" [task=").Append(e.TaskId).Append(']');
                        break;
                    }
                    case ArchiveKind.AssistantText:
                        sb.Append("text \"").Append(BuildBriefForState(e.Content)).Append('"');
                        break;
                    case ArchiveKind.AssistantThink:
                        sb.Append("think \"").Append(BuildBriefForState(e.Content)).Append('"');
                        break;
                    case ArchiveKind.UserMessage:
                        sb.Append("user \"").Append(BuildBriefForState(e.Content)).Append('"');
                        break;
                }
                sb.AppendLine();
            }

            if (total > MaxArchiveBriefsInSessionState)
            {
                sb.Append("- ... (古い ").Append(total - MaxArchiveBriefsInSessionState).AppendLine(" 件は省略 — list_archive で取得可)");
            }
        }

        return new LlmChatMessage("system", sb.ToString());
    }

    /// <summary>session state の brief 用に、空白を畳んで先頭 60 字に切った文字列を返す。</summary>
    private static string BuildBriefForState(string content)
    {
        var c = Regex.Replace(content ?? "", @"\s+", " ").Trim();
        return c.Length > 60 ? c.Substring(0, 60) + "…" : c;
    }

    /// <summary>履歴を順に走査し、thread-tool の <c>tool_call_id</c> → 属するタスク id を引く辞書を作る。
    /// 推定ルール: 「直前の plan マーカー (create_plan / revise_plan) 以降に呼ばれた thread-tool は、
    /// 次に来た complete_task のタスクに属する」。plan 自体のツール呼び出しは辞書に入れない
    /// (= 永久に保持する)。complete_task に至らないままセッションが終わった呼び出しは未帰属で残る。</summary>
    private Dictionary<string, string> BuildToolCallToTaskMap()
    {
        var result  = new Dictionary<string, string>(StringComparer.Ordinal);
        var pending = new List<string>(); // 次の complete_task に属する候補

        foreach (var msg in _history)
        {
            if (msg.Role != "assistant" || msg.ToolCalls is not { Count: > 0 } tcs) continue;
            foreach (var tc in tcs)
            {
                switch (tc.Name)
                {
                    case "create_plan":
                    case "revise_plan":
                        // 新規 plan が立てられたら、それまでに溜まっていた未帰属呼び出しは捨てる
                        // (= LLM 自身が plan を仕切り直したので、それらは省いて構わない)。
                        pending.Clear();
                        break;
                    case "complete_task":
                    {
                        var taskId = TryGetStringField(tc.ArgumentsJson, "id");
                        if (!string.IsNullOrEmpty(taskId))
                        {
                            foreach (var id in pending) result[id] = taskId!;
                        }
                        pending.Clear();
                        break;
                    }
                    default:
                        // thread-tool 呼び出し (get_posts 等): 候補に積む
                        if (!string.IsNullOrEmpty(tc.Id)) pending.Add(tc.Id);
                        break;
                }
            }
        }
        return result;
    }

    /// <summary>assistant content から <c>&lt;think&gt;...&lt;/think&gt;</c> ブロックを全削除する。
    /// 非貪欲マッチ + Singleline (= \n を跨ぐ)。<think> を含まない場合は元の参照をそのまま返す
    /// (= 呼び出し側で参照同一性で「変更なし」を判定できる)。</summary>
    private static readonly Regex ThinkBlockRe =
        new(@"<think>.*?</think>", RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.Compiled);
    private static string StripThinkBlocks(string content)
    {
        if (string.IsNullOrEmpty(content)) return content;
        if (content.IndexOf("<think>", StringComparison.OrdinalIgnoreCase) < 0) return content;
        var replaced = ThinkBlockRe.Replace(content, "");
        // 連続する空行を 1 つに圧縮 (= <think> 抜きで残った空白がだらしないので整える)。
        return Regex.Replace(replaced, @"\n{3,}", "\n\n").Trim();
    }

    /// <summary>プルーニングされた tool 結果に差し替えるプレースホルダ JSON。
    /// task id と (あれば) archive id を明示し、要点は plan.findings、原文が要れば recall_archive、
    /// という導線を LLM に示す。</summary>
    private static string BuildExpiredToolPlaceholder(string taskId, string? archiveId)
    {
        return JsonSerializer.Serialize(new
        {
            omitted      = true,
            owning_task  = taskId,
            archive_id   = archiveId,
            note         = archiveId is null
                ? "このツール結果は完了済タスクのため省略されました。要点は plan の当該タスクの finding を参照してください。"
                : $"このツール結果は完了済タスクのため省略されました。要点は plan の finding を参照。原文が必要なら recall_archive(id=\"{archiveId}\") で再取得できます。",
        }, new JsonSerializerOptions
        {
            Encoder       = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
            WriteIndented = false,
        });
    }
}
