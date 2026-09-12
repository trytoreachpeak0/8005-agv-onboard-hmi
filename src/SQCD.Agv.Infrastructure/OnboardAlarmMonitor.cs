using System.IO;
using SQCD.Agv.Core;

namespace SQCD.Agv.Infrastructure;

/// <summary>
/// 车载端告警的生产者：按固定节拍、以及任何一个相关事件发生时重新求值，把结果整份放上告警板；服务端手上
/// 的那份与告警板不一致时，报一份新快照。
/// </summary>
/// <remarks>
/// <para>
/// <b>发不发由比对决定，不由「这一轮变没变」决定。</b>比的是会话客户端记下的「服务端最近 ack 的那一份」的
/// 内容：内容不一样、上一次没发成功，都会在下一轮补上，不需要单独的重试逻辑。完整握手自己报一份当下的全量，
/// 报完两边就一致，这里什么都不用做。
/// </para>
/// <para>
/// <b>只比内容，不比会话代。</b>续传中断恢复的那种重连不重新握手、也不重报告警快照，服务端手上仍是上一代
/// ack 过的那一份；服务端看板只看这台车在不在线和它最近那一份快照，不要求快照与会话同代。内容没变就再报一份，
/// 只会让续传连接上多出一条与服务端现有事实完全相同的快照。
/// </para>
/// <para>
/// 旧模式（不走 WIRE_TO_GATE）也跑：本机界面照样要显示告警，只是没有会话可以报，<c>client</c> 为 <c>null</c>。
/// </para>
/// </remarks>
public sealed class OnboardAlarmMonitor : IAsyncDisposable
{
    private readonly OnboardAlarmBoard _board;
    private readonly Func<OnboardAlarmInputs> _readInputs;
    private readonly WireToGateSessionClient? _client;
    private readonly IAppLogger _logger;
    private readonly TimeSpan _interval;
    private readonly SemaphoreSlim _wake = new(0, 1);
    private readonly SemaphoreSlim _evaluationGate = new(1, 1);
    private readonly CancellationTokenSource _stopping = new();
    private Task? _loop;
    private string? _lastFailure;
    private bool _disposed;

    public OnboardAlarmMonitor(
        OnboardAlarmBoard board,
        Func<OnboardAlarmInputs> readInputs,
        WireToGateSessionClient? client,
        IAppLogger logger,
        TimeSpan interval)
    {
        _board = board ?? throw new ArgumentNullException(nameof(board));
        _readInputs = readInputs ?? throw new ArgumentNullException(nameof(readInputs));
        _client = client;
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(interval, TimeSpan.Zero);
        _interval = interval;
    }

    /// <summary>告警板上的内容变了。在监视器的后台线程上触发。</summary>
    public event EventHandler<ValueChangedEventArgs<OnboardAlarmSnapshot>>? AlarmsChanged;

    public void Start()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        _loop ??= Task.Run(() => RunAsync(_stopping.Token));
    }

    /// <summary>请后台尽快再求值一轮。多次请求合并成一轮，不排队。</summary>
    public void RequestEvaluation()
    {
        if (_disposed)
        {
            return;
        }

        try
        {
            if (_wake.CurrentCount == 0)
            {
                _wake.Release();
            }
        }
        catch (SemaphoreFullException)
        {
        }
        catch (ObjectDisposedException)
        {
        }
    }

    /// <summary>求值一轮；需要时报一份快照。</summary>
    public async Task EvaluateOnceAsync(CancellationToken cancellationToken = default)
    {
        await _evaluationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            IReadOnlyList<AlarmEntry> alarms = OnboardAlarmEvaluator.Evaluate(_readInputs());
            if (_board.ReplaceAll(alarms))
            {
                AlarmsChanged?.Invoke(this, new ValueChangedEventArgs<OnboardAlarmSnapshot>(_board.Peek()));
            }

            if (_client is null || !ServerNeedsANewSnapshot(_client))
            {
                return;
            }

            try
            {
                if (await _client.PublishAlarmSnapshotAsync(cancellationToken).ConfigureAwait(false))
                {
                    _lastFailure = null;
                }
            }
            catch (Exception exception) when (
                exception is IOException or TimeoutException or InvalidDataException or InvalidOperationException)
            {
                // 一秒一轮，同一个原因只记一次，否则断线那几秒会把日志刷满。
                if (!string.Equals(_lastFailure, exception.Message, StringComparison.Ordinal))
                {
                    _lastFailure = exception.Message;
                    _logger.Write(
                        LogSeverity.Warning,
                        nameof(OnboardAlarmMonitor),
                        "告警快照暂未报上服务端，下一轮再报。",
                        exception);
                }
            }
        }
        finally
        {
            _evaluationGate.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _stopping.Cancel();
        if (_loop is not null)
        {
            try
            {
                await _loop.ConfigureAwait(false);
            }
            catch (Exception exception)
            {
                _logger.Write(LogSeverity.Warning, nameof(OnboardAlarmMonitor), "告警监视后台任务退出异常。", exception);
            }
        }

        _stopping.Dispose();
        _wake.Dispose();
        _evaluationGate.Dispose();
    }

    private bool ServerNeedsANewSnapshot(WireToGateSessionClient client)
    {
        WireToGateSessionSnapshot session = client.Current;
        if (!session.Connected || session.SessionGeneration is null)
        {
            return false;
        }

        OnboardAlarmPublication? acknowledged = client.LastAcknowledgedAlarmSnapshot;
        return acknowledged is null
            || !acknowledged.Alarms.SequenceEqual(_board.Peek().Alarms);
    }

    private async Task RunAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await EvaluateOnceAsync(stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception exception)
            {
                _logger.Write(LogSeverity.Warning, nameof(OnboardAlarmMonitor), "告警求值失败，下一轮再试。", exception);
            }

            try
            {
                await _wake.WaitAsync(_interval, stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }
}
