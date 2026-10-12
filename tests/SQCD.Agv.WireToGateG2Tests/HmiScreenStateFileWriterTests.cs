using System.Globalization;
using System.IO;
using System.Text.Json;
using System.Windows.Threading;
using SQCD.Agv.Application;
using SQCD.Agv.Core;
using SQCD.Agv.Wpf;
using SQCD.Agv.Wpf.ScreenState;
using SQCD.Agv.Wpf.ScreenStateOutput;
using SQCD.Agv.Wpf.ViewModels;
using Xunit;
using static SQCD.Agv.WireToGateG2Tests.HmiScreenStateMonitorDispatcherTests;
using static SQCD.Agv.WireToGateG2Tests.MultiDemandViewModelTests;

namespace SQCD.Agv.WireToGateG2Tests;

/// <summary>
/// The screen-state writer (onboard-hmi#315, T2 acceptance): the timeline grows only with content, stays bounded in
/// size and age without touching anybody else's file, the current file is always whole, and a failing disk reaches
/// neither the business nor the screen.
/// </summary>
/// <remarks>No <c>IntegrationSlice</c>/<c>ProtocolVector</c> trait: a diagnostic output, it proves no protocol vector.</remarks>
public sealed class HmiScreenStateFileWriterTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 12, 10, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task TheTimelineGrowsOnlyWhenTheContentChanges()
    {
        using TemporaryDirectory directory = new();
        HmiScreenState state = await AState();
        await using HmiScreenStateFileWriter writer = new(
            Options(directory.Path), HmiScreenStateFileSystem.Instance, TimeProvider.System, new RecordingLogger());

        for (int round = 0; round < 5; round++)
        {
            // A later build time and a heartbeat are not a content change.
            writer.Submit(state with { BuiltAt = state.BuiltAt.AddSeconds(round) }, heartbeat: round % 2 == 0);
            Assert.True(await writer.DrainAsync());
        }

        Assert.Single(TimelineLines(directory.Path));
        Assert.Equal(1, Current(directory.Path).GetProperty("contentRevision").GetInt64());

        writer.Submit(state with { SelectedRecoveryDemandChoice = "TD-002" }, heartbeat: false);
        writer.Submit(state with { SelectedRecoveryDemandChoice = "TD-002" }, heartbeat: false);
        Assert.True(await writer.DrainAsync());

        string[] lines = TimelineLines(directory.Path);
        Assert.Equal(2, lines.Length);
        JsonElement current = Current(directory.Path);
        Assert.Equal(2, current.GetProperty("contentRevision").GetInt64());
        Assert.Equal("ACTIVE", current.GetProperty("status").GetString());
        Assert.Equal("TD-002", current.GetProperty("state").GetProperty("selectedRecoveryDemandChoice").GetString());
    }

    /// <summary>
    /// Research M5: a departure countdown run through its whole cycle, rebuilt every second the way the screen redraws,
    /// adds a line only when its tier (or caption) changes, never one per second.
    /// </summary>
    [Fact]
    public async Task ACountdownCycleAddsALinePerTierNotPerSecond()
    {
        using TemporaryDirectory directory = new();
        ManualClock clock = new(Now);
        await using OnboardController controller = Controller();
        MainViewModel viewModel = await ViewModel(controller, clock);
        viewModel.UpdateWireToGateJourney(Journey(Now + TimeSpan.FromMinutes(2)));
        viewModel.ScanText = "TYPED-BY-OPERATOR-7781";
        await using HmiScreenStateFileWriter writer = new(
            Options(directory.Path), HmiScreenStateFileSystem.Instance, TimeProvider.System, new RecordingLogger());

        List<(StationDepartureCountdownTier Tier, string Label)> shown = [];
        const int seconds = 150;
        for (int second = 0; second <= seconds; second++)
        {
            shown.Add((viewModel.StationDepartureCountdownTier, viewModel.StationDepartureCountdownLabel));
            writer.Submit(HmiScreenStateBuilder.Build(viewModel, new ModalDialogRegistry()), heartbeat: second % 5 == 0);
            Assert.True(await writer.DrainAsync());
            clock.Advance(TimeSpan.FromSeconds(1));
            viewModel.RefreshStationDepartureCountdown();
        }

        int changes = 1 + shown.Zip(shown.Skip(1)).Count(pair => pair.First != pair.Second);
        Assert.Contains(StationDepartureCountdownTier.Expired, shown.Select(item => item.Tier));
        Assert.True(changes >= 4, $"the countdown did not run through its tiers: {changes}");
        Assert.Equal(changes, TimelineLines(directory.Path).Length);
        // Failure definition 5: neither the worklist's sublot nor what is in the scan box reaches either file.
        string written = string.Join('\n', TimelineLines(directory.Path))
            + File.ReadAllText(Path.Combine(directory.Path, HmiScreenStateFileWriter.CurrentFileName));
        Assert.DoesNotContain("SUBLOT-001", written, StringComparison.Ordinal);
        Assert.DoesNotContain("TYPED-BY-OPERATOR-7781", written, StringComparison.Ordinal);
        Assert.Contains("\"hasText\":true", written, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ADayFileStopsAtItsSizeCapAndTheCurrentFileKeepsGoing()
    {
        using TemporaryDirectory directory = new();
        HmiScreenState state = await AState();
        RecordingLogger logger = new();
        const long cap = 16 * 1024;
        await using HmiScreenStateFileWriter writer = new(
            Options(directory.Path) with { TimelineMaxFileBytes = cap },
            HmiScreenStateFileSystem.Instance,
            TimeProvider.System,
            logger);

        const int rounds = 200;
        for (int round = 1; round <= rounds; round++)
        {
            writer.Submit(state with { ProgramVersion = $"v{round}" }, heartbeat: false);
            Assert.True(await writer.DrainAsync());
        }

        string timeline = Directory.GetFiles(directory.Path, "hmi-state-*.ndjson").Single();
        long length = new FileInfo(timeline).Length;
        Assert.InRange(length, cap / 2, cap);
        Assert.True(TimelineLines(directory.Path).Length < rounds);
        Assert.Equal(rounds, Current(directory.Path).GetProperty("contentRevision").GetInt64());
        Assert.Single(logger.Entries, entry => entry.Source == nameof(HmiScreenStateFileWriter) && entry.Message.Contains("大小上限", StringComparison.Ordinal));
    }

    /// <summary>
    /// Expired timeline files go; the technical log's expired <c>agv-*.log</c> beside them and any name that is not
    /// exactly <c>hmi-state-yyyyMMdd.ndjson</c> stay.
    /// </summary>
    [Fact]
    public async Task RetentionDeletesOnlyExpiredTimelineFiles()
    {
        using TemporaryDirectory directory = new();
        DateOnly today = DateOnly.FromDateTime(DateTime.Now);
        string[] timelines = [.. Enumerable.Range(0, 9).Select(step => Timeline(today.AddDays(-5 * step)))];
        string[] others =
        [
            $"agv-{today.AddDays(-40):yyyyMMdd}.log",
            $"agv-{today.AddDays(-35):yyyyMMdd}.log",
            $"hmi-state-{today.AddDays(-40):yyyyMMdd}.ndjson.bak",
            $"hmi-state-{today.AddDays(-40):yyyyMMdd}x.ndjson",
            $"control.err.log",
        ];
        foreach (string name in timelines.Concat(others))
        {
            await File.WriteAllTextAsync(Path.Combine(directory.Path, name), "{}\n", TestContext.Current.CancellationToken);
        }

        await using HmiScreenStateFileWriter writer = new(
            Options(directory.Path), HmiScreenStateFileSystem.Instance, TimeProvider.System, new RecordingLogger());
        writer.Submit(await AState(), heartbeat: false);
        Assert.True(await writer.DrainAsync());

        string[] left = [.. Directory.GetFiles(directory.Path).Select(Path.GetFileName).OfType<string>()];
        Assert.DoesNotContain(Timeline(today.AddDays(-40)), left);
        Assert.DoesNotContain(Timeline(today.AddDays(-35)), left);
        foreach (string kept in timelines.Where(name => name != Timeline(today.AddDays(-40)) && name != Timeline(today.AddDays(-35))))
        {
            Assert.Contains(kept, left);
        }

        foreach (string other in others)
        {
            Assert.Contains(other, left);
        }
    }

    /// <summary>
    /// A clock that jumped months ahead does not make every real timeline file look expired: the purge's "now" is held
    /// to the newest file found plus the process's uptime.
    /// </summary>
    [Fact]
    public async Task AClockJumpedAheadDeletesNothing()
    {
        using TemporaryDirectory directory = new();
        DateOnly today = DateOnly.FromDateTime(DateTime.Now);
        string[] timelines = [.. Enumerable.Range(0, 4).Select(step => Timeline(today.AddDays(-5 * step)))];
        foreach (string name in timelines)
        {
            await File.WriteAllTextAsync(Path.Combine(directory.Path, name), "{}\n", TestContext.Current.CancellationToken);
        }

        await using HmiScreenStateFileWriter writer = new(
            Options(directory.Path),
            HmiScreenStateFileSystem.Instance,
            new ShiftedTimeProvider(TimeSpan.FromDays(150)),
            new RecordingLogger());
        writer.Submit(await AState(), heartbeat: false);
        Assert.True(await writer.DrainAsync());

        string[] left = [.. Directory.GetFiles(directory.Path).Select(Path.GetFileName).OfType<string>()];
        Assert.All(timelines, name => Assert.Contains(name, left));
    }

    /// <summary>
    /// Readers never see half a document: thousands of reads while the writer keeps replacing the file, each one parsed.
    /// A read may find the file locked for an instant (that is retried by any reader); it may never find it partial.
    /// </summary>
    [Fact]
    public async Task AConcurrentReaderAlwaysReadsAWholeDocument()
    {
        using TemporaryDirectory directory = new();
        HmiScreenState state = await AState();
        await using HmiScreenStateFileWriter writer = new(
            Options(directory.Path), HmiScreenStateFileSystem.Instance, TimeProvider.System, new RecordingLogger());
        writer.Submit(state, heartbeat: true);
        Assert.True(await writer.DrainAsync());
        string current = Path.Combine(directory.Path, HmiScreenStateFileWriter.CurrentFileName);

        using CancellationTokenSource stop = new();
        Task writing = Task.Run(async () =>
        {
            for (int round = 0; !stop.IsCancellationRequested; round++)
            {
                writer.Submit(state with { ProgramVersion = $"v{round}" }, heartbeat: true);
                await writer.DrainAsync();
            }
        }, TestContext.Current.CancellationToken);

        int parsed = 0;
        int locked = 0;
        while (parsed < 3_000)
        {
            byte[] bytes;
            try
            {
                bytes = await File.ReadAllBytesAsync(current, TestContext.Current.CancellationToken);
            }
            catch (IOException)
            {
                locked++;
                continue;
            }

            using JsonDocument document = JsonDocument.Parse(bytes);
            Assert.Equal("ACTIVE", document.RootElement.GetProperty("status").GetString());
            parsed++;
        }

        stop.Cancel();
        await writing;
        long revision = Current(directory.Path).GetProperty("contentRevision").GetInt64();
        Assert.True(revision > 10, $"the writer barely wrote during the reads: revision {revision}, locked {locked}");
    }

    /// <summary>
    /// A read-only directory or a full disk: the screen and the business carry on (nothing escapes onto a real
    /// dispatcher, the snapshot is not even disabled), and the technical log gets one line, not one per write, plus one
    /// when writing works again.
    /// </summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task AFailingDiskReachesNeitherTheScreenNorTheBusiness(bool readOnly)
    {
        await using OnboardController controller = Controller();
        MainViewModel viewModel = await ViewModel(controller);
        FailingFileSystem files = new(readOnly ? new UnauthorizedAccessException("read-only") : new IOException("There is not enough space on the disk."));
        RecordingLogger logger = new();
        using TemporaryDirectory directory = new();
        await using HmiScreenStateFileWriter writer = new(Options(directory.Path), files, TimeProvider.System, logger);
        using DispatcherThread thread = new();
        int unhandled = 0;
        thread.Dispatcher.Invoke(() => thread.Dispatcher.UnhandledException += (_, args) =>
        {
            Interlocked.Increment(ref unhandled);
            args.Handled = true;
        });
        HmiScreenStateMonitor? monitor = thread.Dispatcher.Invoke(() => HmiScreenStateMonitor.TryStart(
            viewModel,
            () => HmiScreenStateBuilder.Build(viewModel, new ModalDialogRegistry()),
            new ModalDialogRegistry(),
            writer,
            logger,
            TimeProvider.System,
            thread.Dispatcher,
            TimeSpan.FromMilliseconds(20),
            TimeSpan.FromMilliseconds(60)));
        Assert.NotNull(monitor);

        await WaitAsync(() => files.Attempts >= 10, "repeated write attempts");
        int records = thread.Dispatcher.Invoke(() => viewModel.Logs.Count);
        _ = thread.Dispatcher.BeginInvoke(() => viewModel.ReportOperatorRejection("SUBLOT_NOT_IN_WORKLIST"));
        await WaitAsync(() => thread.Dispatcher.Invoke(() => viewModel.Logs.Count) == records + 1, "the operator record");
        files.Recover();
        await WaitAsync(() => File.Exists(Path.Combine(directory.Path, HmiScreenStateFileWriter.CurrentFileName)), "a write after recovery");
        bool disabled = monitor.IsDisabled;
        thread.Dispatcher.Invoke(monitor.Dispose);

        Assert.Equal(0, Volatile.Read(ref unhandled));
        Assert.False(disabled);
        Assert.True(thread.Dispatcher.Invoke(() => viewModel.HasWarning));
        (LogSeverity Severity, string Source, string Message)[] lines =
            [.. logger.Entries.Where(entry => entry.Source == nameof(HmiScreenStateFileWriter))];
        Assert.Equal(2, lines.Length);
        Assert.Equal(LogSeverity.Warning, lines[0].Severity);
        Assert.Contains("写盘失败", lines[0].Message, StringComparison.Ordinal);
        Assert.Contains("已恢复", lines[1].Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// After <see cref="HmiScreenStateFileWriter.Disable"/> the current file says <c>DISABLED</c>, which entry point and
    /// which exception type, carries no state, and nothing submitted afterwards is written.
    /// </summary>
    [Fact]
    public async Task ADisabledSnapshotWritesOneDisabledDocumentAndThenNothing()
    {
        using TemporaryDirectory directory = new();
        HmiScreenState state = await AState();
        await using HmiScreenStateFileWriter writer = new(
            Options(directory.Path), HmiScreenStateFileSystem.Instance, TimeProvider.System, new RecordingLogger());
        writer.Submit(state, heartbeat: false);
        Assert.True(await writer.DrainAsync());

        writer.Disable("OnFlushTick", nameof(InvalidOperationException), Now);
        writer.Submit(state with { ProgramVersion = "after" }, heartbeat: true);
        await writer.CompleteAsync(TimeSpan.FromSeconds(10));

        JsonElement current = Current(directory.Path);
        Assert.Equal("DISABLED", current.GetProperty("status").GetString());
        Assert.Equal("OnFlushTick", current.GetProperty("disabledEntry").GetString());
        Assert.Equal(nameof(InvalidOperationException), current.GetProperty("disabledReason").GetString());
        Assert.Equal(JsonValueKind.Null, current.GetProperty("state").ValueKind);
        Assert.Equal(2, current.GetProperty("contentRevision").GetInt64());
        string[] lines = TimelineLines(directory.Path);
        Assert.Equal(2, lines.Length);
        Assert.Contains("\"DISABLED\"", lines[^1], StringComparison.Ordinal);
    }

    private static string Timeline(DateOnly day) => HmiScreenStateFileWriter.TimelineFileName(day);

    private static async Task<HmiScreenState> AState()
    {
        await using OnboardController controller = Controller();
        MainViewModel viewModel = await ViewModel(controller);
        return HmiScreenStateBuilder.Build(viewModel, new ModalDialogRegistry());
    }

    private static JsonElement Current(string directory)
    {
        using JsonDocument document = JsonDocument.Parse(
            File.ReadAllBytes(Path.Combine(directory, HmiScreenStateFileWriter.CurrentFileName)));
        return document.RootElement.Clone();
    }

    private static string[] TimelineLines(string directory) =>
        [.. Directory.GetFiles(directory, "hmi-state-*.ndjson").SelectMany(File.ReadAllLines).Where(line => line.Length > 0)];

    private static WireToGateJourneySnapshot Journey(DateTimeOffset deadlineAt) => new(
        null,
        new WireToGateCurrentStopWorklist(
            "ST-01",
            1,
            null,
            deadlineAt,
            [new WireToGateWorklistItem(
                "11111111-1111-1111-1111-111111111111",
                "TD-001",
                "SUBLOT-001",
                "WIRE_TO_GATE",
                "PICKUP",
                1)],
            new string('a', 64)),
        null,
        Now);

    /// <summary>The system clock moved by a fixed amount, as a vehicle PC's clock is after an RTC jump.</summary>
    private sealed class ShiftedTimeProvider(TimeSpan shift) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => base.GetUtcNow() + shift;
    }

    /// <summary>Every write throws until <see cref="Recover"/>; reads answer as an empty directory.</summary>
    private sealed class FailingFileSystem(Exception failure) : IHmiScreenStateFileSystem
    {
        private int _attempts;
        private volatile bool _recovered;

        public int Attempts => Volatile.Read(ref _attempts);

        public void Recover() => _recovered = true;

        public void CreateDirectory(string path) => Guard(() => HmiScreenStateFileSystem.Instance.CreateDirectory(path));

        public void WriteAllBytes(string path, byte[] bytes) => Guard(() => HmiScreenStateFileSystem.Instance.WriteAllBytes(path, bytes));

        public void Replace(string source, string destination) => Guard(() => HmiScreenStateFileSystem.Instance.Replace(source, destination));

        public void AppendAllBytes(string path, byte[] bytes) => Guard(() => HmiScreenStateFileSystem.Instance.AppendAllBytes(path, bytes));

        public long LengthOf(string path) => HmiScreenStateFileSystem.Instance.LengthOf(path);

        public IReadOnlyList<string> FileNamesIn(string directory) => HmiScreenStateFileSystem.Instance.FileNamesIn(directory);

        public void Delete(string path) => Guard(() => HmiScreenStateFileSystem.Instance.Delete(path));

        private void Guard(Action action)
        {
            if (!_recovered)
            {
                Interlocked.Increment(ref _attempts);
                throw failure;
            }

            action();
        }
    }
}
