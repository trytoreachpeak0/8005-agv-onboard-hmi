using System.Windows.Threading;
using SQCD.Agv.Core;
using SQCD.Agv.Infrastructure;
using SQCD.Agv.Wpf.ScreenState;
using SQCD.Agv.Wpf.ViewModels;

namespace SQCD.Agv.Wpf.ScreenStateOutput;

/// <summary>
/// Whether the screen-state snapshot starts, and starting it (onboard-hmi#315). <c>App.StartScreenState</c> calls this;
/// the tests call the same method, so the switch that keeps everything off by default is under test (review S2).
/// </summary>
/// <remarks>
/// <para>
/// <b>Off means nothing.</b> With the switch off nothing is built: no writer task, no timer, no subscription on the view
/// model, no file. <c>HmiScreenStateWiringTests</c> holds that.
/// </para>
/// <para>
/// <b>Invalid parameters never stop the HMI</b> (review S4). They are not part of <c>OnboardSettings.Validate</c>; here
/// they are logged once and the snapshot does not start, and the vehicle starts as it would without it.
/// </para>
/// </remarks>
internal static class HmiScreenStateWiring
{
    /// <summary>What was started; the caller disposes the monitor, then the writer.</summary>
    internal sealed record Started(HmiScreenStateFileWriter Writer, HmiScreenStateMonitor? Monitor);

    /// <summary>Starts the snapshot on the calling (dispatcher) thread if it is switched on and its parameters are valid.</summary>
    /// <param name="session">The session, null without one: the accepted safety state and its change event (onboard-hmi#314).</param>
    internal static Started? Start(
        ScreenStateSettings screenState,
        LogSettings logging,
        MainViewModel viewModel,
        IModalDialogRegistry modals,
        IAppLogger logger,
        Dispatcher dispatcher,
        IHmiScreenStateFileSystem? files = null,
        TimeProvider? time = null,
        IHmiScreenStateSessionSource? session = null)
    {
        ArgumentNullException.ThrowIfNull(screenState);
        if (!screenState.Enabled)
        {
            return null;
        }

        if (screenState.Problem() is { } problem)
        {
            try
            {
                logger.Write(
                    LogSeverity.Warning,
                    nameof(HmiScreenStateWiring),
                    $"屏幕状态快照已打开但参数无效，本次运行不启动快照，车载端照常运行：{problem}");
            }
            catch (Exception)
            {
                // The logger can throw (research 4.3, item 1); not starting the snapshot is the whole answer anyway.
            }

            return null;
        }

        HmiScreenStateOutputOptions options = HmiScreenStateOutputOptions.From(screenState, logging);
        TimeProvider clock = time ?? TimeProvider.System;
        HmiScreenStateFileWriter writer = new(options, files ?? HmiScreenStateFileSystem.Instance, clock, logger);
        HmiScreenStateMonitor? monitor = HmiScreenStateMonitor.TryStart(
            viewModel,
            () => HmiScreenStateBuilder.Build(viewModel, modals, session?.AcceptedSafety),
            modals,
            writer,
            logger,
            clock,
            dispatcher,
            options.FlushInterval,
            options.HeartbeatInterval,
            session: session);
        return new Started(writer, monitor);
    }
}
