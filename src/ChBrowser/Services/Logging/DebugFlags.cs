namespace ChBrowser.Services.Logging;

/// <summary>デバッグ用フラグ。設定画面「全般」のデバッグチェックボックス
/// (<see cref="ChBrowser.Models.AppConfig.DebugDisableRecovery"/>) と同期し、ON の間だけ
/// (1) 「スレ表示真っ白」現象の分析ログを出力し、(2) バグ発生時の自動リカバリ
/// (ProcessFailed→Reload / 内部 reload→resync) を止める。
///
/// <para>AppConfig 適用 (<c>MainViewModel.ApplyConfig</c>) のたびに更新される。値変化時即時反映。
/// 通常運用では false (= 設定画面の説明文どおり「通常は使用しない」)。</para>
///
/// <para>ProcessFailed は worker thread から来ることがあるので volatile で公開する。</para></summary>
public static class DebugFlags
{
    /// <summary>true の間だけ分析ログを出力し、自動リカバリを無効化する。</summary>
    public static volatile bool DisableRecoveryAndLog;
}
