using System.Collections.Generic;
using System.Threading;
using ChBrowser.Models;

namespace ChBrowser.Services.Llm;

/// <summary>LLM への同時リクエスト数の枠 (LLM プロファイル単位)。NG 判定 AI と AI 翻訳が同じプロファイルを使うときは
/// 同じ枠を取り合うので、LLM サーバに投げる数は合計でもプロファイルの同時実行数 (<see cref="LlmProfile.Concurrency"/>) まで。
///
/// <para>同時実行数の設定が変わったら、次に枠を取るときから新しい上限の枠を使う
/// (実行中の要求は取った枠に返すので、古い枠はそのまま終わる)。</para></summary>
public static class LlmConcurrency
{
    private static readonly Dictionary<string, (int Size, SemaphoreSlim Gate)> _gates = new();
    private static readonly object _lock = new();

    /// <summary>この接続の枠。プロファイル Id 単位 (Id が無い接続は URL + モデル名で区別)。
    /// 取った <see cref="SemaphoreSlim"/> は破棄しないこと (共有している)。</summary>
    public static SemaphoreSlim Gate(LlmSettings s)
    {
        var key  = !string.IsNullOrEmpty(s.ProfileId) ? s.ProfileId : $"{s.ApiUrl}|{s.Model}";
        var size = System.Math.Clamp(s.Concurrency, 1, LlmProfile.MaxConcurrency);
        lock (_lock)
        {
            if (_gates.TryGetValue(key, out var g) && g.Size == size) return g.Gate;
            var gate = new SemaphoreSlim(size);
            _gates[key] = (size, gate);
            return gate;
        }
    }
}
