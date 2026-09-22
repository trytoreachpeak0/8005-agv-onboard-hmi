using SQCD.Agv.Contracts;
using SQCD.Agv.Core;

namespace SQCD.Agv.Infrastructure;

public sealed class WireToGateSessionService : IAsyncDisposable
{
    /// <summary>
    /// ADR-cross-0027 的心跳节拍：车载上位机每 2 秒发一次 <c>Heartbeat</c>。
    /// </summary>
    /// <remarks>
    /// 默认值写在这里，出厂 <c>appsettings.json</c> 里再写一遍同一个数——现场不配也符合 ADR，
    /// 而配置文件里看得见它是多少。两处由 <c>ConfigurationTests</c> 钉在一起。
    /// </remarks>
    public static readonly TimeSpan DefaultHeartbeatInterval = TimeSpan.FromSeconds(2);

    /// <summary>
    /// ADR-cross-0027 的静默失联阈值：连续这么久没有收到属于当前会话的合法消息即判失联。
    /// </summary>
    /// <remarks>
    /// 判定本身在服务端（control-server#234），车载端不拿它做判断，只拿它算出心跳间隔的上限。
    /// 阈值与心跳一样是项目级统一配置，不按车设置，所以这里是常量而不是配置项。
    /// </remarks>
    public static readonly TimeSpan LivenessTimeout = TimeSpan.FromSeconds(6);

    /// <summary>
    /// 心跳间隔的上限，不含。
    /// </summary>
    /// <remarks>
    /// ADR 要求单次心跳丢失不构成失联：丢掉一条之后，下一条要在阈值用完之前到，
    /// 所以间隔必须严格小于阈值的一半。写成除法而不是 3 秒的字面量，是为了让这条推理留在代码里。
    /// </remarks>
    public static readonly TimeSpan MaximumHeartbeatInterval = LivenessTimeout / 2;

    private readonly WireToGateSessionClient _client;
    private readonly IWireToGateJournal _journal;
    private readonly IAppLogger _logger;
    private readonly TimeSpan _heartbeatInterval;
    private readonly TimeSpan _reconnectDelay = TimeSpan.FromSeconds(2);
    private readonly TimeProvider _timeProvider;
    private readonly CancellationTokenSource _stopping = new();
    private Task? _runLoop;
    private bool _disposed;

    public WireToGateSessionService(
        WireToGateSessionOptions options,
        IIoModuleClient ioModule,
        IWireToGateJournal journal,
        IAppLogger logger,
        IClock clock,
        IVehicleSafetySignalProvider vehicleSafetySignalProvider,
        OnboardAlarmBoard alarmBoard,
        SlotConfigurationActivationCoordinator activationCoordinator,
        TimeSpan ioSnapshotMaxAge,
        TimeSpan vehicleSafetyMaxAge,
        TimeSpan vehicleSafetyClockSkewTolerance,
        TimeSpan? heartbeatInterval = null,
        TimeProvider? timeProvider = null)
    {
        ArgumentNullException.ThrowIfNull(ioModule);
        ArgumentNullException.ThrowIfNull(journal);
        ArgumentNullException.ThrowIfNull(logger);
        ArgumentNullException.ThrowIfNull(clock);
        ArgumentNullException.ThrowIfNull(vehicleSafetySignalProvider);
        _heartbeatInterval = heartbeatInterval ?? DefaultHeartbeatInterval;
        if (_heartbeatInterval <= TimeSpan.Zero || _heartbeatInterval >= MaximumHeartbeatInterval)
        {
            throw new ArgumentOutOfRangeException(
                nameof(heartbeatInterval),
                _heartbeatInterval,
                "会话心跳间隔必须为正，且严格小于ADR-cross-0027静默失联阈值的一半。");
        }

        _timeProvider = timeProvider ?? TimeProvider.System;
        _journal = journal;
        _logger = logger;
        _client = new WireToGateSessionClient(
            options,
            ioModule,
            journal,
            clock,
            vehicleSafetySignalProvider,
            alarmBoard,
            activationCoordinator,
            ioSnapshotMaxAge,
            vehicleSafetyMaxAge,
            vehicleSafetyClockSkewTolerance);
        _client.StateChanged += OnClientStateChanged;
        _client.JourneyChanged += OnClientJourneyChanged;
        _client.ServerCommandReceived += OnClientServerCommandReceived;
    }

    public WireToGateSessionSnapshot Current => _client.Current;

    public WireToGateJourneySnapshot CurrentJourney => _client.CurrentJourney;

    public IWireToGateJournal Journal => _journal;

    public WireToGateSessionClient Client => _client;

    public event EventHandler<ValueChangedEventArgs<WireToGateSessionSnapshot>>? StateChanged;

    public event EventHandler<ValueChangedEventArgs<WireToGateJourneySnapshot>>? JourneyChanged;

    public event EventHandler<ValueChangedEventArgs<WireToGateServerCommand>>? ServerCommandReceived;

    /// <inheritdoc cref="WireToGateSessionClient.ClosedRecoverySessionHandler"/>
    public Func<WireToGateExceptionRecoverySessionSnapshot, CancellationToken, Task<bool>>?
        ClosedRecoverySessionHandler
    {
        get => _client.ClosedRecoverySessionHandler;
        set => _client.ClosedRecoverySessionHandler = value;
    }

    /// <inheritdoc cref="WireToGateSessionClient.FatalFaultLatched"/>
    public Func<bool>? FatalFaultLatched
    {
        get => _client.FatalFaultLatched;
        set => _client.FatalFaultLatched = value;
    }

    public Task<string> SendSublotSubmittedAsync(
        string operationSessionId,
        string stationId,
        long worklistRevision,
        string sublot,
        string entryMethod,
        string operatorId,
        string verificationMethod,
        DateTimeOffset verifiedAt,
        CancellationToken cancellationToken = default) =>
        _client.SendSublotSubmittedAsync(
            operationSessionId,
            stationId,
            worklistRevision,
            sublot,
            entryMethod,
            operatorId,
            verificationMethod,
            verifiedAt,
            cancellationToken);

    public Task<string> SendOperationProgressAsync(
        string slotOperationAttemptId,
        string phase,
        IReadOnlyList<int> activeUnlockSlots,
        IReadOnlyList<int> completedSlots,
        DateTimeOffset? observedAt = null,
        CancellationToken cancellationToken = default) =>
        _client.SendOperationProgressAsync(
            slotOperationAttemptId,
            phase,
            activeUnlockSlots,
            completedSlots,
            observedAt,
            cancellationToken);

    public Task<string> SendRecoveryOperationProgressAsync(
        string slotOperationAttemptId,
        string phase,
        IReadOnlyList<int> activeUnlockSlots,
        IReadOnlyList<int> completedSlots,
        DateTimeOffset? observedAt = null,
        CancellationToken cancellationToken = default) =>
        _client.SendRecoveryOperationProgressAsync(
            slotOperationAttemptId,
            phase,
            activeUnlockSlots,
            completedSlots,
            observedAt,
            cancellationToken);

    public Task<string> SendOperationResultAsync(
        string deduplicationKey,
        string messageId,
        WireToGateOperationResultPayload payload,
        CancellationToken cancellationToken = default) =>
        _client.SendOperationResultAsync(deduplicationKey, messageId, payload, cancellationToken);

    public Task<string> SendRecoveryOperationResultAsync(
        string deduplicationKey,
        string messageId,
        WireToGateOperationResultPayload payload,
        CancellationToken cancellationToken = default) =>
        _client.SendRecoveryOperationResultAsync(deduplicationKey, messageId, payload, cancellationToken);

    public Task<string> SendSlotOperationResumeRejectedAsync(
        string deduplicationKey,
        string messageId,
        string resumeCommandMessageId,
        SlotOperationCommandRejectedPayload payload,
        CancellationToken cancellationToken = default) =>
        _client.SendSlotOperationResumeRejectedAsync(
            deduplicationKey,
            messageId,
            resumeCommandMessageId,
            payload,
            cancellationToken);

    public Task<string> ResendOperationResultAsync(
        string deduplicationKey,
        CancellationToken cancellationToken = default) =>
        _client.ResendOperationResultAsync(deduplicationKey, cancellationToken);

    public Task<string> SendSlotOperationRejectedAsync(
        string deduplicationKey,
        string messageId,
        string commandMessageId,
        SlotOperationCommandRejectedPayload payload,
        CancellationToken cancellationToken = default) =>
        _client.SendSlotOperationRejectedAsync(
            deduplicationKey,
            messageId,
            commandMessageId,
            payload,
            cancellationToken);

    public Task<ExceptionRecoverySessionOpenedPayload> RequestExceptionRecoverySessionAsync(
        string messageId,
        ExceptionRecoverySessionRequestedPayload payload,
        CancellationToken cancellationToken = default) =>
        _client.RequestExceptionRecoverySessionAsync(messageId, payload, cancellationToken);

    public Task<RecoveryActionAcceptedPayload> SubmitRecoveryActionAsync(
        string messageId,
        RecoveryActionSubmittedPayload payload,
        CancellationToken cancellationToken = default) =>
        _client.SubmitRecoveryActionAsync(messageId, payload, cancellationToken);

    public Task<HardwareRecoveryRecordResultPayload> SubmitHardwareRecoveryRecordAsync(
        string messageId,
        HardwareRecoveryRecordSubmittedPayload payload,
        CancellationToken cancellationToken = default) =>
        _client.SubmitHardwareRecoveryRecordAsync(messageId, payload, cancellationToken);

    public Task<LoadCancellationAuthorizationPayload> RequestLoadCancellationStartAsync(
        string messageId,
        LoadCancellationStartRequestedPayload payload,
        CancellationToken cancellationToken = default) =>
        _client.RequestLoadCancellationStartAsync(messageId, payload, cancellationToken);

    public Task<string> RequestLoadCompensationAsync(
        string messageId,
        LoadCompensationRequestedPayload payload,
        CancellationToken cancellationToken = default) =>
        _client.RequestLoadCompensationAsync(messageId, payload, cancellationToken);

    public Task<string> RequestLoadCorrectionAsync(
        string messageId,
        LoadCorrectionRequestedPayload payload,
        CancellationToken cancellationToken = default) =>
        _client.RequestLoadCorrectionAsync(messageId, payload, cancellationToken);

    public Task<string> SendLoadCancellationResultAsync(
        string deduplicationKey,
        string messageId,
        LoadCancellationResultPayload payload,
        CancellationToken cancellationToken = default) =>
        _client.SendLoadCancellationResultAsync(
            deduplicationKey,
            messageId,
            payload,
            cancellationToken);

    public Task<string> SendLoadCompensationResultAsync(
        string deduplicationKey,
        string messageId,
        LoadCompensationResultPayload payload,
        CancellationToken cancellationToken = default) =>
        _client.SendLoadCompensationResultAsync(
            deduplicationKey,
            messageId,
            payload,
            cancellationToken);

    public Task<string> SendLoadCorrectionResultAsync(
        string deduplicationKey,
        string messageId,
        LoadCorrectionResultPayload payload,
        CancellationToken cancellationToken = default) =>
        _client.SendLoadCorrectionResultAsync(
            deduplicationKey,
            messageId,
            payload,
            cancellationToken);

    public Task<string> SendFaultCargoRecoveryResultAsync(
        string deduplicationKey,
        string messageId,
        FaultCargoRecoveryResultPayload payload,
        CancellationToken cancellationToken = default) =>
        _client.SendFaultCargoRecoveryResultAsync(
            deduplicationKey,
            messageId,
            payload,
            cancellationToken);

    public Task<string> SendForcedMechanicalRecoveryResultAsync(
        string deduplicationKey,
        string messageId,
        ForcedMechanicalRecoveryResultPayload payload,
        CancellationToken cancellationToken = default) =>
        _client.SendForcedMechanicalRecoveryResultAsync(
            deduplicationKey,
            messageId,
            payload,
            cancellationToken);

    public Task<ManualChargingReturnToServiceResultPayload> RequestManualChargingReturnToServiceAsync(
        string messageId,
        ManualChargingReturnToServiceRequestedPayload payload,
        CancellationToken cancellationToken = default) =>
        _client.RequestManualChargingReturnToServiceAsync(messageId, payload, cancellationToken);

    public Task<string> SendPreDepartureSafetyCheckResultAsync(
        string preDepartureSafetyCheckId,
        string outcome,
        DateTimeOffset observedAt,
        long safetyStateVersion,
        DateTimeOffset validUntil,
        WireToGateSafetySummaryPayload safety,
        CancellationToken cancellationToken = default) =>
        _client.SendPreDepartureSafetyCheckResultAsync(
            preDepartureSafetyCheckId,
            outcome,
            observedAt,
            safetyStateVersion,
            validUntil,
            safety,
            cancellationToken);

    public Task<bool> PublishSafetyStateSnapshotAsync(
        long safetyStateVersion,
        CancellationToken cancellationToken = default) =>
        _client.PublishSafetyStateSnapshotAsync(safetyStateVersion, cancellationToken);

    public Task<string> SendSafetyStateChangedAsync(
        long safetyStateVersion,
        DateTimeOffset observedAt,
        WireToGateSafetySummaryPayload safety,
        IReadOnlyList<int> affectedSlots,
        CancellationToken cancellationToken = default) =>
        _client.SendSafetyStateChangedAsync(
            safetyStateVersion,
            observedAt,
            safety,
            affectedSlots,
            cancellationToken);

    public Task RejectServerCommandAsync(
        WireToGateServerCommand command,
        string reasonCode,
        CancellationToken cancellationToken = default) =>
        _client.RejectServerCommandAsync(command, reasonCode, cancellationToken);

    public Task<string> SendDurableAsync(
        string messageType,
        string deduplicationKey,
        string messageId,
        string? correlationId,
        object payload,
        CancellationToken cancellationToken = default) =>
        _client.SendDurableAsync(
            messageType,
            deduplicationKey,
            messageId,
            correlationId,
            payload,
            cancellationToken);

    public void Start()
    {
        ThrowIfDisposed();
        _runLoop ??= Task.Run(() => RunAsync(_stopping.Token));
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _client.StateChanged -= OnClientStateChanged;
        _client.JourneyChanged -= OnClientJourneyChanged;
        _client.ServerCommandReceived -= OnClientServerCommandReceived;
        _stopping.Cancel();
        if (_runLoop is not null)
        {
            try
            {
                await _runLoop.ConfigureAwait(false);
            }
            catch (Exception exception)
            {
                _logger.Write(LogSeverity.Warning, nameof(WireToGateSessionService), "上层会话后台任务退出异常。", exception);
            }
        }

        await _client.DisposeAsync().ConfigureAwait(false);
        await _journal.DisposeAsync().ConfigureAwait(false);
        _stopping.Dispose();
        GC.SuppressFinalize(this);
    }

    private void OnClientStateChanged(object? sender, ValueChangedEventArgs<WireToGateSessionSnapshot> args) =>
        StateChanged?.Invoke(this, args);

    private void OnClientJourneyChanged(object? sender, ValueChangedEventArgs<WireToGateJourneySnapshot> args) =>
        JourneyChanged?.Invoke(this, args);

    private void OnClientServerCommandReceived(object? sender, ValueChangedEventArgs<WireToGateServerCommand> args) =>
        ServerCommandReceived?.Invoke(this, args);

    private async Task RunAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await _client.ConnectAndRecoverAsync(stoppingToken).ConfigureAwait(false);
                _logger.Write(
                    LogSeverity.Information,
                    nameof(WireToGateSessionService),
                    $"上层会话已建立：generation={_client.Current.SessionGeneration}，readiness={_client.Current.Readiness}。");
                // 节拍按「上一条心跳发出的时刻」推进，不是「上一条心跳处理完的时刻」。
                // SendHeartbeatAsync 要等 HeartbeatAck 回来，等完再定时，往返时间就会累加到下一次
                // 间隔上——服务端应答慢一点，2 秒的节拍就漂到 2 秒加往返，而阈值只有 6 秒。
                long lastHeartbeatSentAt = _timeProvider.GetTimestamp();
                while (!stoppingToken.IsCancellationRequested)
                {
                    TimeSpan due = _heartbeatInterval - _timeProvider.GetElapsedTime(lastHeartbeatSentAt);
                    if (due > TimeSpan.Zero)
                    {
                        await Task.Delay(due, _timeProvider, stoppingToken).ConfigureAwait(false);
                    }

                    lastHeartbeatSentAt = _timeProvider.GetTimestamp();
                    await _client.SendHeartbeatAsync(stoppingToken).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception exception)
            {
                try
                {
                    await _client.DisconnectAsync().ConfigureAwait(false);
                }
                catch (Exception disconnectException)
                {
                    _logger.Write(
                        LogSeverity.Warning,
                        nameof(WireToGateSessionService),
                        "清理失效的上层会话连接时发生异常，安全状态已切换为离线。",
                        disconnectException);
                }

                _logger.Write(
                    LogSeverity.Warning,
                    nameof(WireToGateSessionService),
                    $"上层会话不可用：{exception.Message}。将在{_reconnectDelay.TotalSeconds:0}秒后重连。");
            }

            try
            {
                await Task.Delay(_reconnectDelay, _timeProvider, stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }

    private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(_disposed, this);
}
