using SQCD.Agv.Wpf.ViewModels;

namespace SQCD.Agv.WireToGateG2Tests;

/// <summary>
/// The operator log of a view model a test constructs itself, oldest first, copied without tripping over an append.
/// The view-model tests read the log through here, never <c>viewModel.Logs</c> itself
/// (ViewModelLogsReadArchitectureTests); the G2 harness has its own <c>LogsSnapshot</c> for the view model it owns.
/// Call it again on each poll of a wait, and take a line found in it from the copy.
/// </summary>
/// <remarks>
/// <para>
/// These tests construct <see cref="MainViewModel"/> with a real controller and no WPF <c>Application</c>, so
/// <c>MainViewModel.RunOnUiThread</c> runs each append on the thread that raised it: the controller's
/// <c>StateChanged</c> reaches <c>AppendOperatorRecord</c> on the controller's thread, and a command's error path
/// reports its refusal on whatever thread the command finished on. A test enumerating <c>Logs</c> at that instant
/// fails with "Collection was modified" (onboard-hmi#307; the same race failed a G2 test once, onboard-hmi#285).
/// </para>
/// <para>
/// So the copy is taken the way the harness takes it: by index off a count read first -- indexing does not check the
/// collection's version -- and a read that still loses the race is retried: an index past a collection that shrank
/// throws <c>ArgumentOutOfRangeException</c>, and a slot read between an append raising the count and storing the line
/// comes back empty, which is turned into an <c>InvalidOperationException</c>.
/// </para>
/// <para>
/// <b>The indexed copy is deliberate; do not turn it into an enumeration</b> (<c>[.. viewModel.Logs]</c>,
/// <c>ToArray()</c>, <c>foreach</c>). Nothing would go red if it were: ViewModelLogsReadArchitectureTests only counts
/// that this file reads the log once, and no test races an append against this method. The tolerance is shown only
/// by the probe kept in <c>evidence/hmi307/</c> (onboard-hmi#307 review S2).
/// </para>
/// <para>
/// <b>What the retry cannot repair.</b> <c>TrimLogs</c> removes from the front once the log passes 300 lines; a removal
/// in the middle of a copy can shift a line out of it without any exception. Only
/// <c>OperatorRecordLogTests.TheScreenKeepsTheLast300LinesAndTheLogKeepsEveryOne</c> goes past 300, and it appends
/// on its own thread and reads only the count afterwards.
/// </para>
/// </remarks>
internal static class OperatorLogSnapshot
{
    public static LogLineViewModel[] Of(MainViewModel viewModel)
    {
        for (int attempt = 0; ; attempt++)
        {
            try
            {
                return Copy(viewModel);
            }
            catch (Exception exception)
                when (exception is InvalidOperationException or ArgumentOutOfRangeException
                    && attempt < 50)
            {
                Thread.Sleep(5);
            }
        }
    }

    private static LogLineViewModel[] Copy(MainViewModel viewModel)
    {
        System.Collections.ObjectModel.ObservableCollection<LogLineViewModel> lines = viewModel.Logs;
        int count = lines.Count;
        LogLineViewModel[] copy = new LogLineViewModel[count];
        for (int index = 0; index < count; index++)
        {
            copy[index] = lines[index]
                ?? throw new InvalidOperationException("the operator log was read in the middle of an append");
        }

        return copy;
    }
}
