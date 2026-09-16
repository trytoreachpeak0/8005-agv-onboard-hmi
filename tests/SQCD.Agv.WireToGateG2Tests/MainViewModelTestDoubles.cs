using SQCD.Agv.Core;

namespace SQCD.Agv.WireToGateG2Tests;

/// <summary>
/// 只走「快照 → 界面状态」这一段的测试要用的两个替身。原来是
/// <see cref="MainViewModelJourneyTests"/> 的私有嵌套类，第二个界面测试类出现时提到这里，
/// 免得同一份样板抄两遍。
/// </summary>
internal sealed class InertRuleGateway : IRuleGateway
{
    public bool IsConnected => false;

    public VisitContext? CurrentVisit => null;

    public event EventHandler<ValueChangedEventArgs<bool>>? ConnectionChanged;

    public event EventHandler<ValueChangedEventArgs<VisitContext?>>? VisitChanged;

    public Task StartAsync(CancellationToken applicationStopping)
    {
        ConnectionChanged?.Invoke(this, new ValueChangedEventArgs<bool>(false));
        VisitChanged?.Invoke(this, new ValueChangedEventArgs<VisitContext?>(null));
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

    public Task<ScanAuthorization> VerifyScanAsync(
        ScanVerificationRequest request,
        CancellationToken cancellationToken) =>
        throw new NotSupportedException();

    public Task<bool> ReportOperationAsync(
        OperationResult result,
        CancellationToken cancellationToken) =>
        throw new NotSupportedException();

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}

internal sealed class SilentLogger : IAppLogger
{
    public event EventHandler<LogEntryEventArgs>? EntryWritten;

    public void Write(
        LogSeverity severity,
        string source,
        string message,
        Exception? exception = null)
    {
        EntryWritten?.Invoke(
            this,
            new LogEntryEventArgs(new LogEntry(DateTimeOffset.Now, severity, source, message)));
    }
}
