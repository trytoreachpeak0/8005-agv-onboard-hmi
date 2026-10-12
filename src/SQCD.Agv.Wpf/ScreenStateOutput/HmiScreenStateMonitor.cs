using System.Collections.Specialized;
using System.ComponentModel;
using System.Windows.Input;
using System.Windows.Threading;
using SQCD.Agv.Core;
using SQCD.Agv.Wpf.ScreenState;
using SQCD.Agv.Wpf.ViewModels;

namespace SQCD.Agv.Wpf.ScreenStateOutput;

/// <summary>
/// The dispatcher side of the screen-state snapshot (research sections 4.1 and 4.3, onboard-hmi#315): notices that the
/// view model changed, builds a <see cref="HmiScreenState"/> at background priority, and hands it to the writer.
/// </summary>
/// <remarks>
/// <para>
/// <b>Thread model.</b> The view model is written on the WPF dispatcher thread, so the builder runs there: two
/// <see cref="DispatcherTimer"/>s at <see cref="DispatcherPriority.Background"/>, behind all input and rendering.
/// <see cref="OnFlushTick"/> builds when something changed since the last build (at most one flush interval late);
/// <see cref="OnHeartbeatTick"/> builds regardless, and the writer rewrites the current file with a new <c>writtenAt</c>.
/// The heartbeat runs on the dispatcher on purpose: when the dispatcher is stuck the heartbeat stops and
/// <c>writtenAt</c> stops advancing, which is how T3 tells "the screen is not responding". Change handlers
/// (<c>PropertyChanged</c>, <c>CollectionChanged</c>, <c>CanExecuteChanged</c>) only set a flag; they can be raised on any
/// thread. Serialising and writing happen on the writer's task (<see cref="HmiScreenStateFileWriter"/>), never here.
/// </para>
/// <para>
/// <b>Isolation (research 4.3, item 1).</b> An exception escaping onto the dispatcher is classified
/// <c>UNHANDLED_UI_ERROR</c> and latches the vehicle (<c>App.OnDispatcherUnhandledException</c>); one escaping a
/// <c>PropertyChanged</c> handler also stops <c>SetProperty</c>'s caller halfway through, leaving the entries half
/// refreshed. So every entry point here -- each method named <c>On…</c>, plus <see cref="TryStart"/> and
/// <see cref="Dispose"/> -- is wrapped whole in <c>try/catch (Exception)</c>, never rethrows, and on failure disables the
/// snapshot for the rest of the process and has the writer write <c>DISABLED</c>. The logging in that path is wrapped
/// once more: a logger can throw. <c>HmiScreenStateIsolationArchitectureTests</c> holds the shape.
/// </para>
/// <para>
/// <b>What it touches.</b> Event subscriptions on the view model, its collections, slot cards and commands, and the two
/// timers. No window, focus or input API; <c>HmiScreenStateOutputForbiddenCallArchitectureTests</c> scans for them.
/// Modal dialogs raise no event, so the flush tick compares the modal registry with what it read last.
/// </para>
/// </remarks>
internal sealed class HmiScreenStateMonitor : IDisposable
{
    private readonly MainViewModel _viewModel;
    private readonly Func<HmiScreenState> _build;
    private readonly IModalDialogRegistry _modals;
    private readonly IHmiScreenStateSink _sink;
    private readonly IAppLogger _logger;
    private readonly TimeProvider _time;
    private readonly Dispatcher _dispatcher;
    private readonly DispatcherTimer _flushTimer;
    private readonly DispatcherTimer _heartbeatTimer;
    private readonly Action<string>? _entryProbe;
    private IReadOnlyList<OpenModalDialog> _lastModals = [];
    private int _dirty;
    private int _disabled;

    /// <param name="entryProbe">Test seam: called first in every entry point with its name, so a test can make it throw.</param>
    internal HmiScreenStateMonitor(
        MainViewModel viewModel,
        Func<HmiScreenState> build,
        IModalDialogRegistry modals,
        IHmiScreenStateSink sink,
        IAppLogger logger,
        TimeProvider time,
        Dispatcher dispatcher,
        TimeSpan flushInterval,
        TimeSpan heartbeatInterval,
        Action<string>? entryProbe = null)
    {
        _viewModel = viewModel ?? throw new ArgumentNullException(nameof(viewModel));
        _build = build ?? throw new ArgumentNullException(nameof(build));
        _modals = modals ?? throw new ArgumentNullException(nameof(modals));
        _sink = sink ?? throw new ArgumentNullException(nameof(sink));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _time = time ?? throw new ArgumentNullException(nameof(time));
        _dispatcher = dispatcher ?? throw new ArgumentNullException(nameof(dispatcher));
        _entryProbe = entryProbe;
        _flushTimer = new DispatcherTimer(DispatcherPriority.Background, dispatcher) { Interval = flushInterval };
        _heartbeatTimer = new DispatcherTimer(DispatcherPriority.Background, dispatcher) { Interval = heartbeatInterval };
    }

    internal bool IsDisabled => Volatile.Read(ref _disabled) != 0;

    /// <summary>
    /// Builds and starts a monitor on the calling (dispatcher) thread. Returns null, having logged why, if that fails:
    /// the vehicle starts without a snapshot rather than not at all.
    /// </summary>
    internal static HmiScreenStateMonitor? TryStart(
        MainViewModel viewModel,
        Func<HmiScreenState> build,
        IModalDialogRegistry modals,
        IHmiScreenStateSink sink,
        IAppLogger logger,
        TimeProvider time,
        Dispatcher dispatcher,
        TimeSpan flushInterval,
        TimeSpan heartbeatInterval,
        Action<string>? entryProbe = null)
    {
        HmiScreenStateMonitor? monitor = null;
        try
        {
            entryProbe?.Invoke(nameof(TryStart));
            monitor = new HmiScreenStateMonitor(
                viewModel, build, modals, sink, logger, time, dispatcher, flushInterval, heartbeatInterval, entryProbe);
            monitor.Subscribe();
            Interlocked.Exchange(ref monitor._dirty, 1);
            monitor._flushTimer.Tick += monitor.OnFlushTick;
            monitor._heartbeatTimer.Tick += monitor.OnHeartbeatTick;
            monitor._flushTimer.Start();
            monitor._heartbeatTimer.Start();
            return monitor;
        }
        catch (Exception failure)
        {
            if (monitor is not null)
            {
                monitor.Disable(nameof(TryStart), failure);
            }
            else
            {
                try
                {
                    sink.Disable(nameof(TryStart), failure.GetType().Name, time.GetLocalNow());
                }
                catch (Exception)
                {
                    // The sink is not supposed to throw; if it does there is nothing further to tell.
                }

                try
                {
                    logger.Write(LogSeverity.Warning, nameof(HmiScreenStateMonitor), "屏幕状态快照启动失败，本次运行不写快照。", failure);
                }
                catch (Exception)
                {
                    // The logger can throw (research 4.3, item 1).
                }
            }

            return null;
        }
    }

    public void Dispose()
    {
        try
        {
            Interlocked.Exchange(ref _disabled, 1);
            StopTimers();
            Unsubscribe();
        }
        catch (Exception)
        {
            // Shutting down: nothing to disable any more, and nothing may escape onto the dispatcher.
        }
    }

    private void Subscribe()
    {
        _viewModel.PropertyChanged += OnViewModelPropertyChanged;
        foreach (INotifyCollectionChanged collection in Collections())
        {
            collection.CollectionChanged += OnCollectionChanged;
        }

        foreach (LockerCardViewModel locker in _viewModel.Lockers)
        {
            locker.PropertyChanged += OnLockerPropertyChanged;
        }

        foreach (ICommand command in Commands())
        {
            command.CanExecuteChanged += OnCommandCanExecuteChanged;
        }
    }

    private void Unsubscribe()
    {
        _viewModel.PropertyChanged -= OnViewModelPropertyChanged;
        foreach (INotifyCollectionChanged collection in Collections())
        {
            collection.CollectionChanged -= OnCollectionChanged;
        }

        foreach (LockerCardViewModel locker in _viewModel.Lockers)
        {
            locker.PropertyChanged -= OnLockerPropertyChanged;
        }

        foreach (ICommand command in Commands())
        {
            command.CanExecuteChanged -= OnCommandCanExecuteChanged;
        }
    }

    private INotifyCollectionChanged[] Collections() =>
        [
            _viewModel.Logs,
            _viewModel.Lockers,
            _viewModel.WorklistItems,
            _viewModel.JourneyPlanLegs,
            _viewModel.RecoveryDemandChoices,
        ];

    private ICommand[] Commands() =>
        [_viewModel.ScannerSubmitCommand, _viewModel.ManualSubmitCommand, _viewModel.ClearLogsCommand];

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs args)
    {
        try
        {
            _entryProbe?.Invoke(nameof(OnViewModelPropertyChanged));
            Interlocked.Exchange(ref _dirty, 1);
        }
        catch (Exception failure)
        {
            Disable(nameof(OnViewModelPropertyChanged), failure);
        }
    }

    private void OnCollectionChanged(object? sender, NotifyCollectionChangedEventArgs args)
    {
        try
        {
            _entryProbe?.Invoke(nameof(OnCollectionChanged));
            Interlocked.Exchange(ref _dirty, 1);
        }
        catch (Exception failure)
        {
            Disable(nameof(OnCollectionChanged), failure);
        }
    }

    private void OnLockerPropertyChanged(object? sender, PropertyChangedEventArgs args)
    {
        try
        {
            _entryProbe?.Invoke(nameof(OnLockerPropertyChanged));
            Interlocked.Exchange(ref _dirty, 1);
        }
        catch (Exception failure)
        {
            Disable(nameof(OnLockerPropertyChanged), failure);
        }
    }

    private void OnCommandCanExecuteChanged(object? sender, EventArgs args)
    {
        try
        {
            _entryProbe?.Invoke(nameof(OnCommandCanExecuteChanged));
            Interlocked.Exchange(ref _dirty, 1);
        }
        catch (Exception failure)
        {
            Disable(nameof(OnCommandCanExecuteChanged), failure);
        }
    }

    private void OnFlushTick(object? sender, EventArgs args)
    {
        try
        {
            _entryProbe?.Invoke(nameof(OnFlushTick));
            if (IsDisabled)
            {
                StopTimers();
                return;
            }

            bool modalsChanged = ModalsChanged();
            if (Interlocked.Exchange(ref _dirty, 0) == 0 && !modalsChanged)
            {
                return;
            }

            _sink.Submit(_build(), heartbeat: false);
        }
        catch (Exception failure)
        {
            Disable(nameof(OnFlushTick), failure);
        }
    }

    private void OnHeartbeatTick(object? sender, EventArgs args)
    {
        try
        {
            _entryProbe?.Invoke(nameof(OnHeartbeatTick));
            if (IsDisabled)
            {
                StopTimers();
                return;
            }

            ModalsChanged();
            Interlocked.Exchange(ref _dirty, 0);
            _sink.Submit(_build(), heartbeat: true);
        }
        catch (Exception failure)
        {
            Disable(nameof(OnHeartbeatTick), failure);
        }
    }

    /// <summary>Whether the open dialogs differ from the last read; remembers this read. Dialogs are compared by reference.</summary>
    private bool ModalsChanged()
    {
        IReadOnlyList<OpenModalDialog> open = _modals.Read();
        bool changed = open.Count != _lastModals.Count
            || open.Where((dialog, index) => !ReferenceEquals(dialog, _lastModals[index])).Any();
        _lastModals = open;
        return changed;
    }

    private void StopTimers()
    {
        _flushTimer.Stop();
        _heartbeatTimer.Stop();
    }

    /// <summary>
    /// Stops the snapshot for the rest of the process and has the writer write <c>DISABLED</c>. Called from the catch of
    /// every entry point, on whatever thread raised it; never throws.
    /// </summary>
    private void Disable(string entry, Exception failure)
    {
        bool first = false;
        try
        {
            first = Interlocked.Exchange(ref _disabled, 1) == 0;
            if (first)
            {
                Unsubscribe();
                if (_dispatcher.CheckAccess())
                {
                    StopTimers();
                }

                // The type name only: the message can carry what the failing code had in hand (HmiScreenStateDocument).
                _sink.Disable(entry, failure.GetType().Name, _time.GetLocalNow());
            }
        }
        catch (Exception)
        {
            // Off-dispatcher the timers stop themselves at their next tick; the flag is already set.
        }

        if (!first)
        {
            return;
        }

        try
        {
            _logger.Write(
                LogSeverity.Warning,
                nameof(HmiScreenStateMonitor),
                $"屏幕状态快照在 {entry} 出错，本次运行停用快照（文件标记为 DISABLED），界面与业务不受影响。",
                failure);
        }
        catch (Exception)
        {
            // The logger can throw (research 4.3, item 1); the snapshot is already disabled.
        }
    }
}
