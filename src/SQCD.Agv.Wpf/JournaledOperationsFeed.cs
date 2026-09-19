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
/// Reads run one at a time, in the order they were asked for, so a slow read can never publish an
/// older state over a newer one. A failed read is logged and leaves the display as it was: a side
/// mark is information for the operator, not something a journal hiccup should fault the vehicle for.
/// </para>
/// </remarks>
internal sealed class JournaledOperationsFeed(
    IWireToGateJournal journal,
    Action<WireToGateRecoveryState> publish,
    IAppLogger logger)
{
    private readonly object _sync = new();
    private Task _tail = Task.CompletedTask;

    /// <summary>Queues one read behind the ones already asked for; the returned task never faults.</summary>
    public Task RefreshAsync()
    {
        lock (_sync)
        {
            _tail = ReadAfterAsync(_tail);
            return _tail;
        }
    }

    private async Task ReadAfterAsync(Task previous)
    {
        await previous.ConfigureAwait(false);
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
