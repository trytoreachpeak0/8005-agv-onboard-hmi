using System.Text.RegularExpressions;

namespace SQCD.Agv.UnitTests;

/// <summary>
/// 这张表回答一个问题，而这次返工证明它必须被写下来并守住：**在 v2 上，
/// <c>OnboardController._fatalFault</c> 到底约束什么？**（8005-agv-onboard-hmi#171 审查返工）
/// </summary>
/// <remarks>
/// <para>
/// <b>答案：只有「控制器自己发布的快照」，也就是显示。一个物理动作都不在里面。</b>
/// </para>
/// <para>
/// <c>_fatalFault</c> 在 <c>OnboardController.cs</c> 的读点有五个，其中四个在 <c>SubmitScanAsync</c>
/// 那条 MVP 流程上——取消操作 token、两次 <c>ThrowIfFatalFaultLatched</c>、上报确认后不回 idle——
/// 而 v2 下 <c>MainViewModel.SubmitAsync</c> 走 <c>_wireToGateSubmitter</c>，那条路一次都不会被调用。
/// 第五个是 <c>PublishCore</c>，它只把快照改写成 <c>Faulted</c>。
/// </para>
/// <para>
/// <b>这张表的第一版只写在一句注释里，主语还写窄了</b>：当时写的是「那一条开锁路径不经过控制器」，
/// 而事实是「v2 的业务路径都不经过控制器」。主语一换，横幅、扫码入口、复位的在途判据三处同时倒，
/// 审查找到的正好是这三处。所以它现在是可执行的：**新增一个开门点而不在下表登记，这里就红**，
/// 作者必须回答「它受不受锁存约束」。
/// </para>
/// <para>
/// <b>第四个开门点今天唯一被挡住的方式，是 <c>MainViewModel.ApplyWireToGatePresentationCore</c>
/// 里那个把 9 个恢复入口置 false 的 <c>if</c>。</b> 也就是说那个 <c>if</c> 承担着一项安全职责。
/// 它那里有一条注释指回本文件；删它之前先读那条注释。**那九个入口自己的写入路径由
/// <see cref="RecoveryEntryWriteSiteArchitectureTests"/> 守着**（onboard-hmi#176），包括那个
/// <c>if</c> 的 <c>return;</c>——少了它，下面的正常分支会把入口写回来。
/// </para>
/// <para>
/// <b>本文件每一条守卫守不到什么，逐条登记在
/// <c>RecoveryEntryWriteSiteArchitectureTests.GuardLimits</c> 那张表里</b>（onboard-hmi#176 的范围补充）。
/// 这一族**没有一条是行为判据**：它们问的都是源码里有没有某个 token、某个形状，所以换一个语义等价、
/// token 不同的写法它们就全盲，而那一刻没有任何东西会红。读本文件任何一条之前，先去那张表看它的限度。
/// </para>
/// </remarks>
public sealed class FatalFaultScopeArchitectureTests
{
    /// <summary>
    /// 允许直接碰具体 Modbus 客户端的两个地方：它自己，和把依赖装配起来的 <c>App</c>。
    /// </summary>
    private static readonly string[] ModbusClientCallers =
    [
        "src/SQCD.Agv.Infrastructure/ModbusTcpIoModuleClient.cs",
        "src/SQCD.Agv.Wpf/App.xaml.cs"
    ];

    /// <summary>门实际被打开的地方，以及它受不受 <c>_fatalFault</c> 约束。</summary>
    /// <param name="Path">调用点所在文件，仓库相对路径。</param>
    /// <param name="GuardedByFatalFault">
    /// 调用之前是否有 <c>ThrowIfFatalFaultLatched</c>。
    /// </param>
    /// <param name="Why">这一条为什么是这个答案——读者不必自己去推。</param>
    private sealed record UnlockSite(string Path, bool GuardedByFatalFault, string Why);

    /// <summary>
    /// 全仓**经 <c>IIoModuleClient.PulseUnlockAsync</c> 的**开门点，四个（按文件三条，
    /// <c>OnboardController</c> 里有装卸与重开门两处）。绕开那个接口的路由
    /// <see cref="NothingUnderSrcTouchesTheConcreteModbusClientOutsideItsTwoAllowedSites"/> 收窄，两条合起来覆盖的是
    /// 「全仓开门点」。**改这张表之前先确认你改的是事实，不是为了让测试变绿。**
    /// </summary>
    private static readonly UnlockSite[] UnlockSites =
    [
        new(
            "src/SQCD.Agv.Application/OnboardController.cs",
            GuardedByFatalFault: true,
            "MVP 扫码装卸。受约束，但 v2 下 SubmitScanAsync 从不被调用，所以这条约束在 v2 上是空转的。"),
        new(
            "src/SQCD.Agv.Application/WireToGateSlotOperationExecutor.cs",
            GuardedByFatalFault: false,
            "服务端下发的 SlotOperationCommand。执行器直接持 IIoModuleClient，不经过 OnboardController"
            + "——这是结构，不是「今天恰好没有引用」。要让锁存挡住它，需要协议层的「车拒绝执行」结果"
            + "形状（onboard-hmi#84）。"),
        new(
            "src/SQCD.Agv.Application/WireToGateRecoveryVectorExecutor.cs",
            GuardedByFatalFault: false,
            "恢复向量：补偿清空、修正装货、强制机械取出。操作员自己在 HMI 上按出来的，同样绕过控制器。"
            + "今天唯一挡住它的是 MainViewModel.ApplyWireToGatePresentationCore 里故障态置 false 那一段"
            + "——那个 if 因此承担着一项安全职责。")
    ];

    /// <summary>
    /// 受约束的那一侧，守卫必须紧挨着开门。隔开的行数只是一个近似，但它守的形状是对的：
    /// 「先查锁存、然后立刻开门」。中间插进任何需要等待的东西，这里就该红，因为那时守卫查的
    /// 已经不是开门那一刻的状态。
    /// </summary>
    private const int GuardProximityLines = 10;

    private static readonly Regex UnlockCallRegex = new(
        @"\.PulseUnlockAsync\s*\(",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    [Fact]
    public void EveryUnlockCallSiteInTheProductIsRegistered()
    {
        string[] found = ScanUnlockSites().Select(site => site.Path).Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToArray();
        string[] registered = UnlockSites.Select(site => site.Path)
            .Order(StringComparer.Ordinal)
            .ToArray();

        Assert.True(
            found.SequenceEqual(registered, StringComparer.Ordinal),
            "门被打开的地方变了，而这张表决定了「严重安全故障锁存到底挡得住什么」。"
            + $"{Environment.NewLine}源码里：{string.Join(", ", found)}"
            + $"{Environment.NewLine}表里：  {string.Join(", ", registered)}"
            + $"{Environment.NewLine}新增一处就要回答：它受不受 _fatalFault 约束，为什么。");
    }

    /// <summary>
    /// 登记为受约束的那些，每一次开门之前都真的有守卫。**这一条防的是「表说受约束、代码里其实
    /// 没有」**——那种漂移不会有任何别的东西报警，而它的表现就是横幅说了一件不成立的事。
    /// </summary>
    [Fact]
    public void EverySiteRegisteredAsGuardedReallyChecksTheLatchBeforeUnlocking()
    {
        foreach (UnlockSite registered in UnlockSites.Where(site => site.GuardedByFatalFault))
        {
            SourceUnlock[] calls = ScanUnlockSites()
                .Where(call => call.Path == registered.Path)
                .ToArray();

            Assert.True(calls.Length > 0, $"{registered.Path} 登记为开门点，却一次都没开门。");
            Assert.All(calls, call => Assert.True(
                call.GuardedWithin(GuardProximityLines),
                $"{call.Path}:{call.Line} 开门之前 {GuardProximityLines} 行内没有 "
                + "ThrowIfFatalFaultLatched。表里说这一处受严重安全故障锁存约束，而代码里不是了。"));
        }
    }

    /// <summary>
    /// 不受约束的那两处，**不受约束是结构决定的，不是今天恰好如此**：那两个执行器对
    /// <c>OnboardController</c> 零引用，所以锁存在那条路上没有任何可以生效的地方。
    /// </summary>
    /// <remarks>
    /// <para>
    /// 这一条是把「这是结构，不是今天恰好没有引用」那句注释变成可执行的。哪天有人给执行器接上
    /// 控制器，这里会红——那不一定是错的，但它是一次要写下来的架构变化，不该悄悄发生。
    /// </para>
    /// <para>
    /// <b>判据是文本包含，不是符号引用</b>：在那两个文件里写一句提到 <c>OnboardController</c> 的
    /// 注释，这里也会红。这个近似的方向是保守的（宁可多红一次让人来看），而换成真正的符号分析要
    /// 引入编译器 API——本仓的其它源码级门禁都没走那条路。知道它会这样，别在那两个文件里顺手写
    /// 这个类名。
    /// </para>
    /// </remarks>
    [Fact]
    public void TheUnguardedSitesCannotSeeTheControllerAtAll()
    {
        string root = ProtocolIdentityArchitectureTests.RepositoryRoot();
        foreach (UnlockSite registered in UnlockSites.Where(site => !site.GuardedByFatalFault))
        {
            string source = File.ReadAllText(Path.Combine(
                root, registered.Path.Replace('/', Path.DirectorySeparatorChar)));

            Assert.False(
                source.Contains("OnboardController", StringComparison.Ordinal),
                $"{registered.Path} 现在引用了 OnboardController。表里说这一处不受严重安全故障锁存"
                + "约束，而那条理由是「它根本看不到控制器」——理由没了，结论要重新判，不是照旧。");
        }
    }

    /// <summary>
    /// 这张表只看得见**经 <c>IIoModuleClient.PulseUnlockAsync</c> 的**开门。绕开那个接口、直接
    /// 拿具体 Modbus 客户端写寄存器的代码，它一个字都看不见——所以这一条把那个类型的使用收窄到两处。
    /// </summary>
    /// <remarks>
    /// <para>
    /// <c>IIoModuleClient</c>（<c>src/SQCD.Agv.Core/Ports.cs</c>）上能让门动的只有
    /// <c>PulseUnlockAsync</c> 一个成员，其余是连接状态、快照与等待。所以「扫 <c>PulseUnlockAsync</c>」
    /// 覆盖了这个接口的全部开门面；缺的那一半正是这一条守的：**没有人从接口下面钻过去**。
    /// </para>
    /// <para>
    /// <b>它守的没有标题曾经声称的那么多，名字已经按实际收窄。</b>这个仓的 Modbus 是手写在裸
    /// <c>TcpClient</c> 上的（<c>ModbusTcpIoModuleClient.cs</c> 没有任何 Modbus 库依赖），所以
    /// **一个新文件自己 <c>new TcpClient()</c> 连 IO 模块、直接写开锁线圈，这一条看不见**
    /// （审查，判据路条目 8）。它挡的是「照着现成的客户端再用一次」，不是「所有到达仓门的路」。
    /// </para>
    /// <para>
    /// 这一条是 8005-agv-onboard-hmi#171 审查之后补的，理由就是这张票本身的教训：
    /// **一个过度声称的表，和一句过度声称的横幅，坏法完全一样**——读的人据此做了一个它撑不住的
    /// 判断。上面那张表如果只扫 <c>PulseUnlockAsync</c> 却自称「全仓开门点」，就是在重犯。
    /// </para>
    /// </remarks>
    [Fact]
    public void NothingUnderSrcTouchesTheConcreteModbusClientOutsideItsTwoAllowedSites()
    {
        string root = ProtocolIdentityArchitectureTests.RepositoryRoot();
        string[] offenders = Directory
            .EnumerateFiles(Path.Combine(root, "src"), "*.cs", SearchOption.AllDirectories)
            .Select(path => Path.GetRelativePath(root, path).Replace('\\', '/'))
            .Where(relative => !ModbusClientCallers.Contains(relative, StringComparer.Ordinal))
            .Where(relative => File
                .ReadAllText(Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar)))
                .Contains("ModbusTcpIoModuleClient", StringComparison.Ordinal))
            .Order(StringComparer.Ordinal)
            .ToArray();

        Assert.True(
            offenders.Length == 0,
            "这些文件直接碰到了具体的 Modbus 客户端，而开门点那张表只看得见经 "
            + $"IIoModuleClient.PulseUnlockAsync 的开门：{string.Join(", ", offenders)}"
            + $"{Environment.NewLine}要么改走接口，要么把这个文件加进 ModbusClientCallers 并说明"
            + "为什么它不会绕过那张表。");
    }

    /// <summary>
    /// 两个执行器的 <c>HasOperationInFlight</c> 读的必须是它们**自己用来串行化的那道门**，不是一个
    /// 放在旁边的字段（8005-agv-onboard-hmi#171 审查 S2 的硬要求）。
    /// </summary>
    /// <remarks>
    /// <para>
    /// 严重安全故障的复位复核拿这个属性当「现在有没有在开门」。**一个平行维护的字段漂移起来，
    /// 表现正好是复位该被挡住的那一刻说「没在跑」**——也就是 S2 的失败场景原样重来。
    /// </para>
    /// <para>
    /// <b>这条守的是运行时测试守不住的那一半，而那一半是实测出来的。</b> 把属性改成
    /// <c>Volatile.Read(ref _activeOperation) is not null</c>——一个看起来完全合理的平行来源——
    /// <c>HasOperationInFlightIsTrueWhileTheExecutorIsRunningAndFalseAfterwards</c> 照样绿，因为
    /// 在 <c>ExecuteAsync</c> 这一条路径上两者恰好一致。
    /// </para>
    /// <para>
    /// <b>它守的是写法，不是行为。</b><c>_operationGate != null</c> 同样含那个 token，照样能过。
    /// 退到源码这一层是因为造不出便宜的观察点，不是因为行为差异不存在——**差异在哪是实读过的**：
    /// <c>SettleInterruptedAsync</c>（持门结算中断的 attempt）与 <c>AbortOperationAsync</c>
    /// （把门当 barrier 等当前操作结束）都持门而**从不写 <c>_activeOperation</c>**，那个字段只在
    /// <c>ExecuteAsync</c> 与 <c>ResumeAsync</c> 里写。所以那两条路径上「门被持有而
    /// <c>_activeOperation</c> 为 null」是真的会发生的。
    /// </para>
    /// <para>
    /// <b>要把这条升级成行为断言，缺的是一个能挂住那两条路径的挂点</b>：现有夹具用的是真
    /// <c>SqliteWireToGateJournal</c>，没有可以在持门期间回调的替身。下一个人要做这件事，
    /// 从那里下手，别把这条当洁癖删掉。（<c>WireToGateRecoveryVectorExecutor</c> 那一侧根本没有
    /// <c>_activeOperation</c> 这样的字段，所以那边今天没有平行来源，这条对它是冗余的守卫。）
    /// </para>
    /// </remarks>
    [Theory]
    [InlineData("src/SQCD.Agv.Application/WireToGateSlotOperationExecutor.cs")]
    [InlineData("src/SQCD.Agv.Application/WireToGateRecoveryVectorExecutor.cs")]
    public void TheInFlightQueryIsDerivedFromTheSerialisationGate(string path)
    {
        string root = ProtocolIdentityArchitectureTests.RepositoryRoot();
        string source = File.ReadAllText(
            Path.Combine(root, path.Replace('/', Path.DirectorySeparatorChar)));
        Match declaration = Regex.Match(
            source,
            @"public\s+bool\s+HasOperationInFlight\s*=>(?<body>[^;]*);",
            RegexOptions.CultureInvariant | RegexOptions.Singleline);

        Assert.True(
            declaration.Success,
            $"{path} 里找不到 HasOperationInFlight。复位复核靠它判「现在有没有在开门」；"
            + "这道守卫锚在它上面，改名之前先把守卫一起改，别让它悄悄变瞎。");
        Assert.Contains("_operationGate", declaration.Groups["body"].Value, StringComparison.Ordinal);
    }

    /// <summary>
    /// 这一批守卫**自己会不会报红**，用合成源码验，不靠「我当时注入过一次」。
    /// </summary>
    /// <remarks>
    /// <para>
    /// 第一版这四条守卫一条常驻反向验证都没有：判别力是靠一次性注入证的，证据写在 PR 正文里。
    /// 那证明的是「今天有判别力」，**不是「明天扫描器变瞎时会有人知道」**——而扫描器变瞎正是
    /// 这一类工具最常见的坏法（审查，判据路条目 9）。
    /// </para>
    /// <para>
    /// 同一个 PR 里 <c>LocalFailureCodeRegistryArchitectureTests</c> 做对了（合成坏例＋合成不该叫的
    /// 例），说明这不是不会做，是建守卫的默认动作里漏了这一步。
    /// </para>
    /// </remarks>
    [Fact]
    public void TheseGuardsStillReportRedOnSyntheticViolations()
    {
        // 一、开门前没有守卫 → GuardedWithin 说不通过。
        string[] unguarded =
        [
            "            SetActiveOperation(operation);",
            "            ThrowIfExternalSafetyNotReady();",
            "            await _ioModule.PulseUnlockAsync(slotIndex, operationToken).ConfigureAwait(false);"
        ];
        Assert.False(
            new SourceUnlock("synthetic/Unguarded.cs", 3, unguarded).GuardedWithin(GuardProximityLines),
            "开门前没有守卫，这条守卫却说通过了。");

        // 二、守卫被注释掉 → 同样不通过。这一条是判据路条目 7 指出的那个洞。
        string[] commentedOut =
        [
            "            // ThrowIfFatalFaultLatched(operationToken);  // 锁存判断上移到调用方了",
            "            await _ioModule.PulseUnlockAsync(slotIndex, operationToken).ConfigureAwait(false);"
        ];
        Assert.False(
            new SourceUnlock("synthetic/CommentedOut.cs", 2, commentedOut).GuardedWithin(GuardProximityLines),
            "守卫只剩注释，这条守卫却说通过了。");

        // 三、守卫真的在 → 通过。没有这一条，上面两条可以被一个恒 false 的实现满足。
        string[] guarded =
        [
            "            ThrowIfFatalFaultLatched(operationToken);",
            "            await _ioModule.PulseUnlockAsync(slotIndex, operationToken).ConfigureAwait(false);"
        ];
        Assert.True(
            new SourceUnlock("synthetic/Guarded.cs", 2, guarded).GuardedWithin(GuardProximityLines),
            "守卫就在上一行，这条守卫却说不通过。");

        // 四、隔太远 → 不通过：守卫要紧挨着开门，中间插进等待就不再是开门那一刻的状态。
        List<string> tooFar = ["            ThrowIfFatalFaultLatched(operationToken);"];
        tooFar.AddRange(Enumerable.Repeat("            await SomethingThatWaits();", GuardProximityLines + 1));
        tooFar.Add("            await _ioModule.PulseUnlockAsync(slotIndex, operationToken).ConfigureAwait(false);");
        Assert.False(
            new SourceUnlock("synthetic/TooFar.cs", tooFar.Count, [.. tooFar]).GuardedWithin(GuardProximityLines),
            $"守卫隔了超过 {GuardProximityLines} 行，这条守卫却说通过了。");
    }

    /// <summary>
    /// 扫描器自己没瞎：真实源码里确实能找到开门点，而且找得到的数量与表一致。
    /// </summary>
    [Fact]
    public void TheScannerStillFindsUnlockCallsInTheProductSources()
    {
        SourceUnlock[] calls = ScanUnlockSites();

        Assert.NotEmpty(calls);
        Assert.Contains(calls, call => call.Path == "src/SQCD.Agv.Application/OnboardController.cs");
        Assert.Contains(
            calls,
            call => call.Path == "src/SQCD.Agv.Application/WireToGateSlotOperationExecutor.cs");
    }

    private static SourceUnlock[] ScanUnlockSites()
    {
        string root = ProtocolIdentityArchitectureTests.RepositoryRoot();
        string sourceRoot = Path.Combine(root, "src");
        List<SourceUnlock> calls = [];
        foreach (string path in Directory.EnumerateFiles(sourceRoot, "*.cs", SearchOption.AllDirectories))
        {
            string relative = Path.GetRelativePath(root, path).Replace('\\', '/');
            string[] lines = File.ReadAllLines(path);
            for (int index = 0; index < lines.Length; index++)
            {
                if (!UnlockCallRegex.IsMatch(lines[index]))
                {
                    continue;
                }

                calls.Add(new SourceUnlock(relative, index + 1, lines));
            }
        }

        return [.. calls];
    }

    private sealed record SourceUnlock(string Path, int Line, string[] Lines)
    {
        /// <summary>
        /// 开门之前的若干行里有没有守卫。**注释里的那一行不算**——把守卫注释掉、在旁边写一句
        /// 「锁存判断上移到调用方了」，第一版的扫描器照样放行，而表仍然声称这一处受约束
        /// （审查，判据路条目 7）。同一个 PR 里 <c>LocalFailureCodeRegistryArchitectureTests</c>
        /// 剥了注释，这里没剥，两张表标准不一致，而不一致的那一边是松的那一边。
        /// </summary>
        public bool GuardedWithin(int proximity)
        {
            int start = Math.Max(0, Line - 1 - proximity);
            return Lines[start..(Line - 1)]
                .Select(line => line.Split("//", 2)[0])
                .Any(code => code.Contains("ThrowIfFatalFaultLatched", StringComparison.Ordinal));
        }
    }

    /// <summary>
    /// 复位复核问的「有没有在途装卸」是两个执行器的并，而**聚合本身**（那个 <c>||</c>）此前没有
    /// 任何判据：两侧各自的行为测试都在，但把 <c>||</c> 写成 <c>&amp;&amp;</c>、或漏掉一侧，两条都不会红
    /// （审查，判据路条目 1）。
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>这是源码级守卫，不是行为测试，限度写在这里。</b>要造一条真正分辨它的行为用例，
    /// 得让一侧忙、另一侧闲，而 <c>WireToGateBusinessService</c> 的构造要一个真
    /// <c>WireToGateSessionService</c>，只有重 G2 夹具里才有——成本远高于这条守卫，收益是同一个。
    /// 它守的是写法不是行为：有人把两个执行器换成两个同源的字段，这条仍然绿。
    /// </para>
    /// <para>
    /// 按 onboard-hmi#176 的要求，它自带双向自检（见下一条），不继承本文件那批守卫
    /// 「只证明今天有判别力」的毛病。
    /// </para>
    /// </remarks>
    [Fact]
    public void TheInFlightAggregateReadsBothExecutorsWithAnOr()
    {
        string root = ProtocolIdentityArchitectureTests.RepositoryRoot();
        string source = File.ReadAllText(
            Path.Combine(root, "src", "SQCD.Agv.Wpf", "WireToGateBusinessService.cs"));

        AssertAggregateIsAnOrOfBothExecutors(AggregateBody(source));
    }

    /// <summary>上一条在错误的写法上确实会红，且在正确的写法上不红——两个答案不同才算有判别力。</summary>
    [Fact]
    public void ThatAggregateGuardSeparatesRightFromWrong()
    {
        // 正确：不该红。
        AssertAggregateIsAnOrOfBothExecutors(
            "_executor.HasOperationInFlight || _vectorExecutor.HasOperationInFlight");

        // 四种错法，每一种都该红。
        foreach (string wrong in new[]
        {
            "_executor.HasOperationInFlight",                                          // 漏掉恢复向量那一侧
            "_vectorExecutor.HasOperationInFlight",                                    // 漏掉仓位命令那一侧
            "_executor.HasOperationInFlight && _vectorExecutor.HasOperationInFlight",   // 或写成了与
            "false"                                                                    // 整个判据被掏空
        })
        {
            Assert.ThrowsAny<Xunit.Sdk.XunitException>(
                () => AssertAggregateIsAnOrOfBothExecutors(wrong));
        }
    }

    private static void AssertAggregateIsAnOrOfBothExecutors(string body)
    {
        Assert.Contains("_executor.HasOperationInFlight", body, StringComparison.Ordinal);
        Assert.Contains("_vectorExecutor.HasOperationInFlight", body, StringComparison.Ordinal);
        Assert.Contains("||", body, StringComparison.Ordinal);
        Assert.DoesNotContain("&&", body, StringComparison.Ordinal);
    }

    /// <summary>
    /// <c>HasSlotWorkInFlight</c> 表达式体的源码，注释已剥掉——判据写在注释里不算数，
    /// 这正是 onboard-hmi#176 要求别继承的那个洞之一。
    /// </summary>
    private static string AggregateBody(string source)
    {
        string code = Regex.Replace(
            Regex.Replace(source, @"//[^\n]*", string.Empty),
            @"/\*.*?\*/", string.Empty, RegexOptions.Singleline);

        Match match = Regex.Match(
            code,
            @"bool\s+HasSlotWorkInFlight\s*=>(?<body>[^;]*);",
            RegexOptions.Singleline);
        Assert.True(match.Success, "源码里找不到 HasSlotWorkInFlight 的表达式体——扫描器自己瞎了。");
        return match.Groups["body"].Value;
    }
}
