using System;
using System.Diagnostics;
using System.Threading;
using System.Windows.Threading;

namespace ChBrowser.Services.Logging;

/// <summary>UI スレッドの固まりを見張る (アプリ全体が重くなる原因の調査用)。別スレッドから一定間隔で UI スレッドに空の処理を
/// 最優先 (<see cref="DispatcherPriority.Send"/>) で頼み、返ってくるまでの時間が <see cref="Threshold"/> を超えたらログペインに出す。
/// UI スレッドが他の処理で塞がっていた時間がそのまま分かる。見張り自体の負荷はごく小さい (200ms ごとに 1 回空の処理)。</summary>
public static class UiStallWatchdog
{
    public static readonly TimeSpan Threshold = TimeSpan.FromMilliseconds(300);
    private static int _started;

    public static void Start(Dispatcher dispatcher)
    {
        if (Interlocked.Exchange(ref _started, 1) == 1) return;
        var t = new Thread(() => Run(dispatcher)) { IsBackground = true, Name = "UiStallWatchdog", Priority = ThreadPriority.AboveNormal };
        t.Start();
    }

    private static void Run(Dispatcher dispatcher)
    {
        var sw = new Stopwatch();
        while (!dispatcher.HasShutdownStarted)
        {
            sw.Restart();
            var op = dispatcher.BeginInvoke(DispatcherPriority.Send, new Action(() => { }));
            if (op.Wait(TimeSpan.FromSeconds(60)) == DispatcherOperationStatus.Aborted) return;
            var ms = sw.ElapsedMilliseconds;
            if (ms >= Threshold.TotalMilliseconds)
                LogService.Instance.Write($"[uiStall] UI スレッドが {ms} ms 応答しませんでした (この間アプリ全体の操作・描画が止まる)");
            Thread.Sleep(200);
        }
    }
}
