using System.ComponentModel;
using System.Runtime.InteropServices;
using SQCD.Agv.Infrastructure;

namespace SQCD.Agv.UnitTests;

/// <summary>
/// 车载端单例守卫（onboard-hmi#173、#165）。
/// </summary>
/// <remarks>
/// <para>
/// <b>「另一个持有者」一律在另一个线程上。</b>Windows 命名互斥体按线程记所有权且可重入：在本线程里抢第二次
/// 永远抢得到，那样的用例只会测出一个恒绿的假结论。持有线程用事件同步，不靠时序。
/// </para>
/// <para>
/// <b>「另一个账户持有」在进程内造。</b>用一个拒绝所有人的 DACL 建互斥体：创建者的句柄照样拿到完整权限，
/// 而任何按名字再打开它的调用都得到拒绝访问——与 ssh 起的 session 0 进程打开交互用户建的名字是同一个结果
/// （#165 第 1 条的现场记录）。
/// </para>
/// <para>
/// 每个用例用自己的随机名字，不碰 <see cref="SingleInstanceGuard.MachineName"/>：本机或 CI 上可能正跑着
/// 一个真的车载端，测试去抢真名字会与它互相干扰。
/// </para>
/// </remarks>
public sealed class SingleInstanceGuardTests
{
    [Fact]
    public void AFreeNameIsAcquiredAndGivenBackOnDispose()
    {
        string name = UniqueName();
        using (SingleInstanceGuard first = SingleInstanceGuard.AcquireByName(name))
        {
            Assert.Equal(SingleInstanceOutcome.Acquired, first.Outcome);
            Assert.True(first.ShouldStart);
        }

        Assert.Equal(SingleInstanceOutcome.Acquired, OnAnotherThread(() => SingleInstanceGuard.AcquireByName(name)));
    }

    [Fact]
    public void ANameHeldByAnotherOwnerMeansAlreadyRunning()
    {
        string name = UniqueName();
        using Holder holder = Holder.Hold(name);

        Assert.Equal(SingleInstanceOutcome.AlreadyRunning, OnAnotherThread(() => SingleInstanceGuard.AcquireByName(name)));
    }

    [Fact]
    public void AnAbandonedNameIsAcquired()
    {
        // The previous holder was killed or crashed: nobody released it. Treating that as a failure would leave a
        // name nobody can take, and the vehicle would never start again (L2 and G3 kill the onboard to restart it).
        string name = UniqueName();
        using Holder holder = Holder.Hold(name);
        holder.ExitWithoutRelease();

        Assert.Equal(SingleInstanceOutcome.Acquired, OnAnotherThread(() => SingleInstanceGuard.AcquireByName(name)));
    }

    [Fact]
    public void ANameThatExistsButCannotBeOpenedMeansAlreadyRunning()
    {
        // #165: the field line treated this as "the guard is unavailable" and let a second client start on agv01.
        string name = UniqueName();
        using DeniedMutex denied = DeniedMutex.Create(name);

        SingleInstanceGuard guard = SingleInstanceGuard.AcquireByName(name);

        Assert.Equal(SingleInstanceOutcome.AlreadyRunning, guard.Outcome);
        Assert.False(guard.ShouldStart);
    }

    [Fact]
    public void ANameTakenByAnotherKindOfObjectIsUndeterminableAndStopsTheStart()
    {
        string name = UniqueName();
        using EventWaitHandle squatter = new(false, EventResetMode.ManualReset, name);

        SingleInstanceGuard guard = SingleInstanceGuard.AcquireByName(name);

        Assert.Equal(SingleInstanceOutcome.Undeterminable, guard.Outcome);
        Assert.False(guard.ShouldStart);
    }

    [Fact]
    public void OnlyAcquiredStarts()
    {
        // The failure direction is the whole point of #173: every outcome but Acquired refuses the start. A new outcome
        // added to the enum is caught here and has to be decided, instead of starting by default.
        foreach (SingleInstanceOutcome outcome in Enum.GetValues<SingleInstanceOutcome>())
        {
            SingleInstanceGuard guard = GuardWith(outcome);
            Assert.Equal(outcome == SingleInstanceOutcome.Acquired, guard.ShouldStart);
        }
    }

    [Fact]
    public void TheFieldLineNamesFollowItsOwnRule()
    {
        // 03027de BuildName: prefix + agvId trimmed, backslash replaced by an underscore.
        Assert.Equal(
            [@"Global\SQCD.Agv.Wpf-老厂前线新多仓位1", @"Local\SQCD.Agv.Wpf-老厂前线新多仓位1"],
            SingleInstanceGuard.FieldLineNames(" 老厂前线新多仓位1 "));
        Assert.Equal(@"Global\SQCD.Agv.Wpf-a_b", SingleInstanceGuard.BuildFieldLineName(SingleInstanceGuard.FieldLineGlobalPrefix, @"a\b"));
        Assert.Empty(SingleInstanceGuard.FieldLineNames("  "));
    }

    [Fact]
    public void AFieldLineNameThatExistsStopsTheStartWithoutTakingTheMachineName()
    {
        string agvId = "t-" + Guid.NewGuid().ToString("N");
        string machineName = UniqueName();
        using Holder fieldLine = Holder.Hold(SingleInstanceGuard.BuildFieldLineName(SingleInstanceGuard.FieldLineSessionPrefix, agvId));

        (SingleInstanceOutcome outcome, string decidedBy) = OnAnotherThread(() =>
        {
            using SingleInstanceGuard guard = SingleInstanceGuard.Acquire(agvId, machineName, logger: null);
            return (guard.Outcome, guard.Name);
        });

        Assert.Equal(SingleInstanceOutcome.AlreadyRunning, outcome);
        Assert.StartsWith(SingleInstanceGuard.FieldLineSessionPrefix, decidedBy, StringComparison.Ordinal);
        // The v2 name was never taken: a later start, once the field line is gone, is not blocked by this one.
        Assert.Equal(SingleInstanceOutcome.Acquired, OnAnotherThread(() => SingleInstanceGuard.AcquireByName(machineName)));
    }

    [Fact]
    public void AFieldLineNameThatExistsButCannotBeOpenedStopsTheStart()
    {
        string agvId = "t-" + Guid.NewGuid().ToString("N");
        using DeniedMutex fieldLine = DeniedMutex.Create(
            SingleInstanceGuard.BuildFieldLineName(SingleInstanceGuard.FieldLineSessionPrefix, agvId));

        using SingleInstanceGuard guard = SingleInstanceGuard.Acquire(agvId, UniqueName(), logger: null);

        Assert.Equal(SingleInstanceOutcome.AlreadyRunning, guard.Outcome);
    }

    [Fact]
    public void WithNoOtherInstanceTheMachineNameIsTaken()
    {
        string agvId = "t-" + Guid.NewGuid().ToString("N");
        string machineName = UniqueName();

        using SingleInstanceGuard guard = SingleInstanceGuard.Acquire(agvId, machineName, logger: null);

        Assert.Equal(SingleInstanceOutcome.Acquired, guard.Outcome);
        Assert.Equal(machineName, guard.Name);
        Assert.Equal(SingleInstanceOutcome.AlreadyRunning, OnAnotherThread(() => SingleInstanceGuard.AcquireByName(machineName)));
    }

    [Fact]
    public void TheMachineNameIsGlobalAndCarriesNoVehicleIdentity()
    {
        // A session-local name is invisible across sessions -- that is how agv01 let the second client through.
        Assert.StartsWith(@"Global\", SingleInstanceGuard.MachineName, StringComparison.Ordinal);
        Assert.DoesNotContain("{", SingleInstanceGuard.MachineName, StringComparison.Ordinal);
    }

    [Fact]
    public void TheOnboardAppAsksTheGuardBeforeAnythingThatTouchesTheServerOrTheIoModule()
    {
        // The order is part of the criterion (#173): the vehicle-safety projection starts polling the server as soon as
        // it is constructed, and the IO client holds the Modbus target as soon as it is constructed. A refused second
        // instance must never get that far, and the name must be let go only after the IO client has stopped. Read over
        // the code view, so a comment naming these does not count.
        string[] code = CSharpSourceLexer.Lex(File.ReadAllText(Path.Combine(
            ProtocolIdentityArchitectureTests.RepositoryRoot(), "src", "SQCD.Agv.Wpf", "App.xaml.cs"))).Code;
        string text = string.Join('\n', code);

        int guard = IndexOfOnce(text, "SingleInstanceGuard.Acquire(");
        int refusedReturn = IndexOfOnce(text, "Shutdown(ExitCodeNotStartedAnotherInstance);");
        int safetyProjection = IndexOfOnce(text, "new ControlServerVehicleSafetySignalProvider(");
        int ioClient = IndexOfOnce(text, "new ModbusTcpIoModuleClient(");
        int ioStopped = IndexOfOnce(text, "_ioModule?.DisposeAsync()");
        int nameReleased = IndexOfOnce(text, "_singleInstance?.Dispose();");

        Assert.True(guard < refusedReturn && refusedReturn < safetyProjection && refusedReturn < ioClient,
            "App.OnStartup must ask SingleInstanceGuard, and return when refused, before it constructs the vehicle-safety "
            + "projection or the Modbus IO client: a second instance must not poll the server or hold the IO module.");
        Assert.Matches(@"Shutdown\(ExitCodeNotStartedAnotherInstance\);\s*return;", text);
        Assert.True(ioStopped < nameReleased,
            "The single-instance name must be released after the IO client has stopped, not before: otherwise the next "
            + "instance can take the name while this one still writes the DO.");
    }

    private static int IndexOfOnce(string text, string token)
    {
        int first = text.IndexOf(token, StringComparison.Ordinal);
        Assert.True(first >= 0, $"'{token}' is not in App.xaml.cs any more: this check reads nothing. Update it to the new code.");
        Assert.True(
            text.IndexOf(token, first + token.Length, StringComparison.Ordinal) < 0,
            $"'{token}' appears more than once in App.xaml.cs: which one this check reads is ambiguous.");
        return first;
    }

    private static string UniqueName() => @"Local\SQCD.Agv.Tests.SingleInstance." + Guid.NewGuid().ToString("N");

    private static SingleInstanceOutcome OnAnotherThread(Func<SingleInstanceGuard> acquire) =>
        OnAnotherThread(() =>
        {
            using SingleInstanceGuard guard = acquire();
            return guard.Outcome;
        });

    private static T OnAnotherThread<T>(Func<T> work)
    {
        T result = default!;
        Exception? failure = null;
        Thread thread = new(() =>
        {
            try
            {
                result = work();
            }
            catch (Exception exception)
            {
                failure = exception;
            }
        });
        thread.Start();
        thread.Join();
        return failure is null ? result : throw new InvalidOperationException("The work on the other thread failed.", failure);
    }

    private static SingleInstanceGuard GuardWith(SingleInstanceOutcome outcome)
    {
        // The guard's constructor is private: reach each outcome the way production does.
        string name = UniqueName();
        return outcome switch
        {
            SingleInstanceOutcome.Acquired => SingleInstanceGuard.AcquireByName(name),
            SingleInstanceOutcome.AlreadyRunning => HeldGuard(name),
            SingleInstanceOutcome.Undeterminable => SquattedGuard(name),
            _ => throw new InvalidOperationException($"No way to reach {outcome}: decide whether it starts, then add it here."),
        };
    }

    private static SingleInstanceGuard HeldGuard(string name)
    {
        using Holder holder = Holder.Hold(name);
        return SingleInstanceGuard.AcquireByName(name);
    }

    private static SingleInstanceGuard SquattedGuard(string name)
    {
        using EventWaitHandle squatter = new(false, EventResetMode.ManualReset, name);
        return SingleInstanceGuard.AcquireByName(name);
    }

    /// <summary>Holds a named mutex on its own thread until disposed, or exits without releasing it.</summary>
    private sealed class Holder : IDisposable
    {
        private readonly ManualResetEventSlim _held = new();
        private readonly ManualResetEventSlim _release = new();
        private readonly Thread _thread;
        private bool _releaseOnExit = true;

        private Holder(string name)
        {
            _thread = new Thread(() =>
            {
                using Mutex mutex = new(initiallyOwned: false, name);
                mutex.WaitOne();
                _held.Set();
                _release.Wait();
                if (_releaseOnExit)
                {
                    mutex.ReleaseMutex();
                }
            });
        }

        public static Holder Hold(string name)
        {
            Holder holder = new(name);
            holder._thread.Start();
            holder._held.Wait();
            return holder;
        }

        public void ExitWithoutRelease()
        {
            _releaseOnExit = false;
            _release.Set();
            _thread.Join();
        }

        public void Dispose()
        {
            _release.Set();
            _thread.Join();
            _held.Dispose();
            _release.Dispose();
        }
    }

    /// <summary>A named mutex whose DACL denies everyone: it exists, and nobody can open it by name.</summary>
    private sealed class DeniedMutex : IDisposable
    {
        private readonly IntPtr _handle;
        private bool _disposed;

        private DeniedMutex(IntPtr handle) => _handle = handle;

        public static DeniedMutex Create(string name)
        {
            // D:(D;;GA;;;WD) -- deny GENERIC_ALL to Everyone.
            if (!ConvertStringSecurityDescriptorToSecurityDescriptorW("D:(D;;GA;;;WD)", 1, out IntPtr descriptor, IntPtr.Zero))
            {
                throw new Win32Exception(Marshal.GetLastWin32Error());
            }

            try
            {
                SecurityAttributes attributes = new()
                {
                    Length = Marshal.SizeOf<SecurityAttributes>(),
                    SecurityDescriptor = descriptor,
                    InheritHandle = 0,
                };
                IntPtr handle = CreateMutexW(ref attributes, initialOwner: false, name);
                if (handle == IntPtr.Zero)
                {
                    throw new Win32Exception(Marshal.GetLastWin32Error());
                }

                return new DeniedMutex(handle);
            }
            finally
            {
                _ = LocalFree(descriptor);
            }
        }

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            _ = CloseHandle(_handle);
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct SecurityAttributes
        {
            public int Length;
            public IntPtr SecurityDescriptor;
            public int InheritHandle;
        }

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = true)]
        private static extern IntPtr CreateMutexW(ref SecurityAttributes attributes, [MarshalAs(UnmanagedType.Bool)] bool initialOwner, string name);

        [DllImport("kernel32.dll", ExactSpelling = true, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool CloseHandle(IntPtr handle);

        [DllImport("advapi32.dll", CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool ConvertStringSecurityDescriptorToSecurityDescriptorW(
            string stringSecurityDescriptor,
            uint revision,
            out IntPtr securityDescriptor,
            IntPtr securityDescriptorSize);

        [DllImport("kernel32.dll", ExactSpelling = true)]
        private static extern IntPtr LocalFree(IntPtr memory);
    }
}
