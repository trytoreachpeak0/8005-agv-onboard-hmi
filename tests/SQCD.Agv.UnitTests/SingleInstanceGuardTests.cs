using SQCD.Agv.Infrastructure;

namespace SQCD.Agv.UnitTests;

/// <summary>
/// 车载端单例守卫（onboard-hmi#157）。
///
/// 这里所有「第二次抢」都刻意放到另一个线程上跑。Windows 的命名互斥体按**线程**记所有权并且
/// 可重入：同一个线程拿着另一个句柄再抢同一个名字会直接成功。真实场景里第二个实例是另一个
/// 进程、当然也是另一个线程，在本线程里抢第二次只会测出一个永远绿的假结论。
/// </summary>
public sealed class SingleInstanceGuardTests
{
    [Fact]
    public void NameCarriesTheVehicleIdentitySoTwoCarsDoNotShareOne()
    {
        Assert.Equal(
            @"Global\SQCD.Agv.Wpf-AGV-8005-01",
            SingleInstanceGuard.BuildName(SingleInstanceGuard.GlobalNamePrefix, "AGV-8005-01"));
    }

    [Fact]
    public void BackslashInTheVehicleIdentityIsReplaced()
    {
        // 反斜杠在内核对象名里是命名空间分隔符，留着它会把名字切成别的东西。
        Assert.Equal(
            @"Local\SQCD.Agv.Wpf-AGV_01",
            SingleInstanceGuard.BuildName(SingleInstanceGuard.SessionNamePrefix, @"AGV\01"));
    }

    [Fact]
    public void SecondInstanceOfTheSameVehicleIsToldNotToStart()
    {
        string name = UniqueName();
        using SingleInstanceGuard first = SingleInstanceGuard.AcquireByName(name);
        Assert.Equal(SingleInstanceOutcome.Acquired, first.Outcome);
        Assert.True(first.ShouldStart);

        Assert.Equal(SingleInstanceOutcome.AlreadyRunning, AcquireOnAnotherThread(name, out bool shouldStart));
        Assert.False(shouldStart);
    }

    [Fact]
    public void DifferentVehiclesDoNotBlockEachOther()
    {
        string suffix = Guid.NewGuid().ToString("N");
        using SingleInstanceGuard first = SingleInstanceGuard.AcquireByName(
            SingleInstanceGuard.BuildName(SingleInstanceGuard.SessionNamePrefix, "AGV-A-" + suffix));
        Assert.Equal(SingleInstanceOutcome.Acquired, first.Outcome);

        Assert.Equal(
            SingleInstanceOutcome.Acquired,
            AcquireOnAnotherThread(
                SingleInstanceGuard.BuildName(SingleInstanceGuard.SessionNamePrefix, "AGV-B-" + suffix),
                out _));
    }

    [Fact]
    public void NameIsFreeAgainAfterTheHolderShutsDownCleanly()
    {
        // 运维脚本 10-start-onboard-stack.ps1 的先停后起走的就是这条路：旧进程关掉窗口正常退出，
        // 新进程必须能拿到同一个名字。
        string name = UniqueName();
        SingleInstanceGuard first = SingleInstanceGuard.AcquireByName(name);
        Assert.Equal(SingleInstanceOutcome.Acquired, first.Outcome);
        first.Dispose();

        Assert.Equal(SingleInstanceOutcome.Acquired, AcquireOnAnotherThread(name, out _));
    }

    [Fact]
    public void NameIsFreeAgainAfterTheHolderIsKilled()
    {
        // 进程被 Kill 或崩溃时没人释放互斥体，内核把它标成 abandoned。抢它的人会收到
        // AbandonedMutexException——那个异常的含义是「所有权已经归你了」，不是「抢失败」。
        // 当成失败的话，一次崩溃就会留下一个谁都抢不到的名字，车再也起不来。
        string name = UniqueName();
        Thread holder = new(() =>
        {
            Mutex abandoned = new(initiallyOwned: false, name);
            abandoned.WaitOne(TimeSpan.Zero);
        });
        holder.Start();
        holder.Join();

        Assert.Equal(SingleInstanceOutcome.Acquired, AcquireOnAnotherThread(name, out bool shouldStart));
        Assert.True(shouldStart);
    }

    [Fact]
    public void GuardThatCannotBeCreatedLetsTheApplicationStart()
    {
        // 守卫是防误操作的，不是安全边界。它自己建不起来时放行启动：一辆正在跑单的车因为抢不到
        // 一个名字而起不来，代价远大于偶发的双开。这里把名字先让一个别的类型的内核对象占掉来
        // 触发——那是这条路上最现实的一种走法。
        string name = UniqueName();
        using Semaphore squatter = new(1, 1, name);

        using SingleInstanceGuard guard = SingleInstanceGuard.AcquireByName(name);

        Assert.Equal(SingleInstanceOutcome.Unavailable, guard.Outcome);
        Assert.True(guard.ShouldStart);
    }

    private static string UniqueName() =>
        SingleInstanceGuard.BuildName(SingleInstanceGuard.SessionNamePrefix, Guid.NewGuid().ToString("N"));

    private static SingleInstanceOutcome AcquireOnAnotherThread(string name, out bool shouldStart)
    {
        SingleInstanceOutcome outcome = SingleInstanceOutcome.Unavailable;
        bool start = false;
        Thread thread = new(() =>
        {
            using SingleInstanceGuard guard = SingleInstanceGuard.AcquireByName(name);
            outcome = guard.Outcome;
            start = guard.ShouldStart;
        });
        thread.Start();
        thread.Join();
        shouldStart = start;
        return outcome;
    }
}
