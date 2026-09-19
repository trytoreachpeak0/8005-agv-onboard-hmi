using SQCD.Agv.Core;

namespace SQCD.Agv.Wpf;

/// <summary>
/// Reads the journal's recovery state for the display, and never writes it (batch 7-13,
/// <c>8005-agv-onboard-hmi#134</c>).
/// </summary>
/// <remarks>
/// <para>
/// The worklist's side marks and the load correction's target come from the operation contexts the
/// journal already keeps -- the armed <c>OperationContext</c> and <c>LastCompletedLoadOperationContext</c>,
/// both carrying a <c>demandId</c> and its <c>slots</c>. They survive a restart, which is what lets a
/// row keep its side after one. The recovery state is written only through
/// <c>IWireToGateJournal.UpdateRecoveryStateAsync</c> by the business service; this only reads.
/// </para>
/// <para>
/// <b>Reads run one at a time and are merged</b>: while one is running, everything asked for behind it
/// becomes a single further read, which starts after the last of those requests and therefore reads the
/// latest. Every operator event and every journey snapshot asks for a refresh, and
/// <c>SqliteWireToGateJournal</c> has one semaphore for the whole journal -- one read per request would
/// keep taking that lock while the executor is writing its checkpoints, for a display that only ever
/// wants the newest state (PR #143 review). A failed read is logged and leaves the display as it was: a
/// side mark is information for the operator, not something a journal hiccup should fault the vehicle for.
/// </para>
/// </remarks>
internal sealed class JournaledOperationsFeed(
    IWireToGateJournal journal,
    Action<WireToGateRecoveryState> publish,
    IAppLogger logger)
{
    private readonly object _sync = new();
    private Task _tail = Task.CompletedTask;
    private bool _queued;

    /// <summary>
    /// Asks for a read of the latest state. While one read is running at most one more is queued, and a
    /// request that arrives while that one waits joins it instead of adding another. The returned task
    /// completes when the read that covers this request has published, and never faults.
    /// </summary>
    public Task RefreshAsync()
    {
        lock (_sync)
        {
            if (_queued)
            {
                return _tail;
            }

            _queued = true;
            _tail = ReadAfterAsync(_tail);
            return _tail;
        }
    }

    private async Task ReadAfterAsync(Task previous)
    {
        await previous.ConfigureAwait(false);
        lock (_sync)
        {
            // Cleared before the read, not after: a request arriving from here on is not covered by this
            // read and queues the next one, so nothing asked for goes unread.
            _queued = false;
        }

        try
        {
            publish(await journal.ReadRecoveryStateAsync().ConfigureAwait(false));
        }
        catch (Exception exception)
        {
            // Including the journal disposed at shutdown: nothing here may fault the vehicle.
            logger.Write(
                LogSeverity.Warning,
                nameof(JournaledOperationsFeed),
                "读取日志里的操作上下文失败，清单项的侧与修正对象暂按上一次显示。",
                exception);
        }
    }
}
