using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;

namespace SQCD.Agv.UnitTests;

/// <summary>
/// 九个恢复入口属性的**写入路径**守卫：只有两种写法可以写它们，第三种出现就红
/// （<c>trytoreachpeak0/8005-agv-onboard-hmi#176</c>，hmi#171 的跟进票）。
/// </summary>
/// <remarks>
/// <para>
/// <b>它承担的是一条别处成立不了的前提。</b> hmi#171 留下的行为判据
/// <c>BothRefreshPathsKeepTheRecoveryEntriesClosedWhileALatchStands</c> 断的是「严重安全故障锁存着的
/// 时候，那两条刷新路径走完，九个入口都关着」。**那条断言成立的前提是「只有那两条路径写这九个属性」**
/// ——而它自己证明不了这个前提：它只调那两个已知入口，新加第三条路径直接赋值，它一声不响。
/// 前提要写成约束并写明由谁承担，承担者就是本文件。
/// </para>
/// <para>
/// <b>为什么这九个属性值得一道结构守卫。</b> 其中补偿清空、修正装货、强制机械取出三个入口按下去会经
/// <c>WireToGateRecoveryVectorExecutor</c> 真的开仓门，而那个执行器直接持 <c>IIoModuleClient</c>、
/// 不经过 <c>OnboardController</c>，所以严重安全故障锁存在执行那一层拦不住它——**挡住它的是界面这一层**。
/// 全仓四个开门点各自受不受锁存约束，见 <see cref="FatalFaultScopeArchitectureTests"/> 那张表。
/// </para>
/// <para>
/// <b>判据的写法是这张票的全部难点，因为两条路径的结构不同。</b> 一条是函数式（九行各自
/// <c>AllowRecoveryEntry(...)</c>），一条是语句式（<c>if (RecoveryEntriesBlockedByFatalFault)</c>
/// 把九个置 false 然后 <c>return;</c>，正常分支直接取业务值）。语义等价，结构不同，而
/// **hmi#171 的注释一度把它写成「九个入口全部收到同一道闸门 AllowRecoveryEntry 后面」**。
/// 照那句话写出来的守卫只扫 <c>AllowRecoveryEntry(</c>，会把 early-return 那条判成「没有闸门」；
/// 反过来，有人把那个 <c>return;</c> 删掉改成直接赋值，那样的扫描器完全看不见。
/// 所以这里的判据是两条并列：**每个赋值点，要么右边是 <c>AllowRecoveryEntry(...)</c>，要么落在同一个
/// 成员里一道有效的 <c>RecoveryEntriesBlockedByFatalFault</c> early return 之后。**
/// </para>
/// <para>
/// <b>「同一个成员里」是判据的一部分，不是实现细节。</b> 守卫在 A 方法里、赋值在 B 方法里，正是这张票
/// 要抓的那种第三条路径。<see cref="TheGuardTellsAThirdWritePathFromACompliantOne"/> 的最后一个合成例专门
/// 验这一点：去掉成员边界，那一例会变绿。
/// </para>
/// <para>
/// <b>这一族守卫各自守不到什么，登记在 <see cref="GuardLimits"/> 那张表里</b>，由
/// <see cref="EveryGuardInThisFamilyDeclaresWhatItCannotSee"/> 守着：新增一条守卫而不写下它的限度，那里会红。
/// </para>
/// </remarks>
public sealed class RecoveryEntryWriteSiteArchitectureTests
{
    private const string ViewModelPath = "src/SQCD.Agv.Wpf/ViewModels/MainViewModel.cs";

    private const string BehaviouralTestPath =
        "tests/SQCD.Agv.WireToGateG2Tests/FatalFaultLatchViewModelTests.cs";

    private const string BehaviouralTestName =
        "BothRefreshPathsKeepTheRecoveryEntriesClosedWhileALatchStands";

    /// <summary>
    /// 真实源码里赋值点的条数。**写死是有意的**：这条数字是<see cref="TheScannerStillSeesTheRealWriteSites"/>
    /// 判断「扫描器还睁着眼」的判据之一，而扫描器变瞎时的默认输出正是「什么都没发现」，与「确实没有」
    /// 长得一模一样。改动九个入口的写入点之后，回来对一次这个数并说明变化。
    /// </summary>
    private const int ExpectedWriteSiteCount = 25;

    /// <summary>一个恢复入口：公开属性，以及它的 backing field（直接写字段一样是绕过）。</summary>
    private sealed record RecoveryEntry(string Property, string BackingField);

    /// <summary>
    /// 九个恢复入口。<see cref="TheRegisteredEntriesAreExactlyWhatTheBehaviouralAssertionCovers"/>
    /// 把这张表钉在行为判据断言的那九个上，两边任何一侧漂移都会红。
    /// </summary>
    private static readonly RecoveryEntry[] RecoveryEntries =
    [
        new("CanRequestWireToGateRecovery", "_canRequestWireToGateRecovery"),
        new("CanRequestLoadCancellation", "_canRequestLoadCancellation"),
        new("CanRequestLoadCompensation", "_canRequestLoadCompensation"),
        new("CanRequestLoadCorrection", "_canRequestLoadCorrection"),
        new("CanRequestFaultCargoHandoff", "_canRequestFaultCargoHandoff"),
        new("CanRequestForcedMechanicalRecovery", "_canRequestForcedMechanicalRecovery"),
        new("CanRequestManualChargingReturn", "_canRequestManualChargingReturn"),
        new("CanConfirmForcedMechanicalRecovery", "_canConfirmForcedMechanicalRecovery"),
        new("CanSubmitHardwareRecoveryRecord", "_canSubmitHardwareRecoveryRecord")
    ];

    /// <summary>
    /// 今天真的写这九个属性的三个成员。**「两条刷新路径」是从操作员那一侧数的，赋值点落在三个方法里**：
    /// <c>RefreshForcedIsolationCore</c> 是另外两条共用的子过程，最后两个入口只在它里面写。
    /// </summary>
    private static readonly string[] WritingMembers =
    [
        "ApplyWireToGatePresentationCore",
        "RefreshForcedIsolationCore",
        "RefreshWireToGateInputStateCore"
    ];

    private static readonly Regex BlockCommentRegex = new(
        @"/\*.*?\*/",
        RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.Singleline);

    /// <summary>类体里一个块成员的收尾：缩进四格的单独一个 <c>}</c>。</summary>
    private static readonly Regex MemberBlockEndRegex = new(
        @"^    \}\s*$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    /// <summary>
    /// 类体里一个表达式体成员或字段声明的收尾：缩进四格、当行以 <c>;</c> 结束。没有它，
    /// <c>internal void RefreshWireToGateInputState() =&gt; RunOnUiThread(...);</c> 会和它下面那个
    /// <c>Core</c> 方法并成一段，成员名就取错了。
    /// </summary>
    private static readonly Regex MemberExpressionEndRegex = new(
        @"^    \S.*;\s*$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Regex MemberSignatureRegex = new(
        @"^    [A-Za-z].*?\b(?<name>\w+)\s*\(",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Regex FatalFaultGuardRegex = new(
        @"\bif\s*\(\s*RecoveryEntriesBlockedByFatalFault\s*\)",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Regex ReturnRegex = new(
        @"\breturn\s*;",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    /// <summary>
    /// 一次对某个入口的写。**不锚行首**，因为 <c>if (...) { CanRequestX = false; return; }</c> 这种单行
    /// 写法里赋值不在行首，而它是合法的；锚了行首就会看不见它，也看不见任何塞在别的语句后面的赋值。
    /// 代价是字段声明的初始化器（<c>private bool _canRequestX = true;</c>）也会被算成一个赋值点，
    /// 而那正好该红：那是一个默认开着的恢复入口。
    /// </summary>
    private static readonly Dictionary<string, Regex> AssignmentRegexes = RecoveryEntries
        .SelectMany(entry => new[] { entry.Property, entry.BackingField })
        .ToDictionary(
            token => token,
            token => new Regex(
                $@"(?<![\w.])(?:this\.)?{Regex.Escape(token)}\s*=(?![=>])",
                RegexOptions.Compiled | RegexOptions.CultureInvariant),
            StringComparer.Ordinal);

    /// <summary>
    /// 九个属性的写入点没有第三条路径。**这一条是本票的交付物**；它的判别力由
    /// <see cref="TheGuardTellsAThirdWritePathFromACompliantOne"/> 双向验，扫描器有没有睁着眼由
    /// <see cref="TheScannerStillSeesTheRealWriteSites"/> 验。
    /// </summary>
    [Fact]
    public void EveryWriteToARecoveryEntryInTheViewModelIsGuarded()
    {
        WriteSite[] unguarded = ScanViewModel().Where(site => !site.IsGuarded).ToArray();

        Assert.True(
            unguarded.Length == 0,
            "这些地方写了恢复入口，却既没经 AllowRecoveryEntry、也不在一道有效的 "
            + $"RecoveryEntriesBlockedByFatalFault early return 之后：{Environment.NewLine}"
            + string.Join(
                Environment.NewLine,
                unguarded.Select(site =>
                    $"  {ViewModelPath}:{site.Line}  {site.Member}  {site.Entry}    {site.Statement.Trim()}"))
            + $"{Environment.NewLine}严重安全故障锁存着的时候，这条路径会把入口放回来，而其中三个按下去会真的开仓门。"
            + "要么把右边包进 AllowRecoveryEntry，要么在这个成员开头加上那道 early return。");
    }

    /// <summary>
    /// 扫描器自己还睁着眼。**今晚（2026-09-21）两次栽在同一形状上：工具跑了、输出正常、结论完全反了**
    /// ——注入没落到文件上、探针锚点没插进去，两次全量都绿。**工具没生效时的默认输出几乎总是
    /// 「什么都没发现」，和「确实没有」长得一模一样**，所以这一条要求扫描器拿出它真的看见了的东西。
    /// </summary>
    /// <remarks>
    /// 四项一起才够：数量对得上（漏扫会掉数）、九个入口每个都至少被看见两次（某个名字打错会掉到零）、
    /// 成员集合恰好是那三个（缩进格式一变，成员切分就不准，而那会悄悄改变「同一个成员里」这个判据）、
    /// **两种写法各自都被认出来过**（只认得函数式那种，正是票面点名的那个陷阱）。
    /// </remarks>
    [Fact]
    public void TheScannerStillSeesTheRealWriteSites()
    {
        WriteSite[] sites = ScanViewModel();

        Assert.True(
            sites.Length == ExpectedWriteSiteCount,
            $"扫描器在 {ViewModelPath} 里找到 {sites.Length} 个恢复入口赋值点，登记的是 "
            + $"{ExpectedWriteSiteCount} 个。少了多半是扫描器瞎了（改名、改格式、正则失配），"
            + "多了是真的加了写入点——两种都要回到这张表来说明。");

        foreach (RecoveryEntry entry in RecoveryEntries)
        {
            Assert.True(
                sites.Count(site => site.Entry == entry.Property) >= 2,
                $"{entry.Property} 在源码里只被看见 {sites.Count(site => site.Entry == entry.Property)} 次。"
                + "九个入口每个至少在两条路径上被写，看见不到两次说明这个名字已经对不上源码了。");
        }

        string[] members = sites.Select(site => site.Member).Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal).ToArray();
        Assert.True(
            members.SequenceEqual(WritingMembers, StringComparer.Ordinal),
            $"写这九个属性的成员变了。源码里：{string.Join(", ", members)}；"
            + $"表里：{string.Join(", ", WritingMembers)}。"
            + "新增一个成员就要回答：它凭什么可以写这些入口，锁存期间它写的是什么。");

        Assert.Contains(sites, site => site.ViaAllowRecoveryEntry);
        Assert.Contains(sites, site => site.UnderFatalFaultEarlyReturn && !site.ViaAllowRecoveryEntry);
    }

    /// <summary>
    /// 这道守卫在错的写法上红、在对的写法上不红——**两个答案不同，它才算有判别力**。
    /// </summary>
    /// <remarks>
    /// <para>
    /// hmi#171 交付那批守卫时一条常驻反向验证都没有，判别力靠一次性注入证在 PR 正文里。那证明的是
    /// 「当天有判别力」，不是「明天扫描器变瞎时会有人知道」。本票的守卫不继承那个毛病。
    /// </para>
    /// <para>
    /// 七个反例是**七种真的会发生的错法**，不是凑数：第三条路径（这张票的起因）、early return 被删掉、
    /// 守卫被行注释掉、守卫被块注释包起来、绕过属性直接写 backing field、
    /// <b>守卫块里少了 <c>return;</c></b>（那时后面的正常分支照样会把入口写回来，而块内那九个 false
    /// 看起来完全正确），以及守卫在上一个成员里、赋值在下一个成员里。
    /// </para>
    /// </remarks>
    [Fact]
    public void TheGuardTellsAThirdWritePathFromACompliantOne()
    {
        // 合规一：函数式，右边包进 AllowRecoveryEntry。
        AssertCompliant(
            """
                private void RefreshWireToGateInputStateCore()
                {
                    CanRequestLoadCompensation = AllowRecoveryEntry(_canRequest?.Invoke() == true);
                }
            """,
            expectedSites: 1);

        // 合规二：语句式，early return 之后直接取业务值。块内那个 false 也算在守卫范围里。
        AssertCompliant(
            """
                private void ApplyWireToGatePresentationCore()
                {
                    if (RecoveryEntriesBlockedByFatalFault)
                    {
                        CanRequestLoadCompensation = false;
                        return;
                    }

                    CanRequestLoadCompensation = _canRequest?.Invoke() == true;
                }
            """,
            expectedSites: 2);

        // 合规三：单行 early return，没有块。语义一样，形状不同——判据认的是语义。
        AssertCompliant(
            """
                private void ApplyWireToGatePresentationCore()
                {
                    if (RecoveryEntriesBlockedByFatalFault) { CanRequestLoadCompensation = false; return; }

                    CanRequestLoadCompensation = _canRequest?.Invoke() == true;
                }
            """,
            expectedSites: 2);

        // 反例一：第三条路径，谁都不经过。**这张票的起因就是这一种。**
        AssertViolates(
            """
                private void RefreshSomethingNewCore()
                {
                    CanRequestLoadCompensation = _canRequest?.Invoke() == true;
                }
            """,
            expectedSites: 1,
            expectedViolations: 1);

        // 反例二：early return 被删掉，正常分支原样留着。扫 AllowRecoveryEntry( 的守卫看不见这一种。
        AssertViolates(
            """
                private void ApplyWireToGatePresentationCore()
                {
                    CanRequestLoadCompensation = _canRequest?.Invoke() == true;
                }
            """,
            expectedSites: 1,
            expectedViolations: 1);

        // 反例三：守卫被行注释掉。注释里的那个 false 也不该被数成一个合规赋值点，所以只有一个违规。
        AssertViolates(
            """
                private void ApplyWireToGatePresentationCore()
                {
                    // if (RecoveryEntriesBlockedByFatalFault)
                    // {
                    //     CanRequestLoadCompensation = false;
                    //     return;
                    // }
                    CanRequestLoadCompensation = _canRequest?.Invoke() == true;
                }
            """,
            expectedSites: 1,
            expectedViolations: 1);

        // 反例四：守卫被块注释包起来。hmi#171 那批守卫只剥行注释，块注释里的调用会被当成代码读。
        AssertViolates(
            """
                private void ApplyWireToGatePresentationCore()
                {
                    /*
                    if (RecoveryEntriesBlockedByFatalFault)
                    {
                        CanRequestLoadCompensation = false;
                        return;
                    }
                    */
                    CanRequestLoadCompensation = _canRequest?.Invoke() == true;
                }
            """,
            expectedSites: 1,
            expectedViolations: 1);

        // 反例五：绕过属性直接写 backing field。属性是 private set，所以这是类内唯一的另一条写法。
        AssertViolates(
            """
                private void RefreshSomethingNewCore()
                {
                    _canRequestLoadCompensation = true;
                }
            """,
            expectedSites: 1,
            expectedViolations: 1);

        // 反例六：守卫块里少了 return;。块内九个 false 看起来完全正确，而正常分支紧接着把它们写回来
        // ——**整段都不合规，包括块内那一条**，因为它已经不构成一道 early return 了。
        AssertViolates(
            """
                private void ApplyWireToGatePresentationCore()
                {
                    if (RecoveryEntriesBlockedByFatalFault)
                    {
                        CanRequestLoadCompensation = false;
                    }

                    CanRequestLoadCompensation = _canRequest?.Invoke() == true;
                }
            """,
            expectedSites: 2,
            expectedViolations: 2);

        // 反例七：守卫在上一个成员里，赋值在下一个成员里。**这一条验的是「同一个成员」那半个判据**：
        // 去掉成员边界，第二个赋值点会被上面那道守卫"保护"，于是这一例变绿——而它正是第三条路径。
        WriteSite[] acrossMembers = Scan(ClassBody(
            """
                private void ApplyWireToGatePresentationCore()
                {
                    if (RecoveryEntriesBlockedByFatalFault)
                    {
                        CanRequestLoadCompensation = false;
                        return;
                    }
                }

                private void RefreshSomethingNewCore()
                {
                    CanRequestLoadCompensation = _canRequest?.Invoke() == true;
                }
            """));
        Assert.Equal(2, acrossMembers.Length);
        Assert.True(
            acrossMembers[0].IsGuarded,
            "守卫块内那一条被判成不合规，而它就在 early return 里。");
        Assert.False(
            acrossMembers[1].IsGuarded,
            "另一个成员里的赋值被上一个成员的守卫放行了——「同一个成员里」这半个判据没有生效。");
    }

    /// <summary>
    /// 登记的九个入口，与行为判据 <c>BothRefreshPathsKeepTheRecoveryEntriesClosedWhileALatchStands</c>
    /// 断言的那九个**互相钉住**。
    /// </summary>
    /// <remarks>
    /// 两张清单各自都会过期，而它们过期的方式不一样：本文件漏一个，那个入口的写入路径就没人看；
    /// 行为测试漏一个，锁存期间它是不是真的关着就没人验。钉在一起之后，**任何一侧单独漂移都会红**，
    /// 改的人必须同时回答两个问题。<b>它守不到的是两边一起漏掉</b>——新增第十个恢复入口而两张表都不登记，
    /// 这里和那里都不会响。那一半今天没有承担者，见 <see cref="GuardLimits"/>。
    /// </remarks>
    [Fact]
    public void TheRegisteredEntriesAreExactlyWhatTheBehaviouralAssertionCovers()
    {
        string[] lines = StripComments(ReadRepositoryFile(BehaviouralTestPath));
        int start = Array.FindIndex(lines, line => line.Contains(BehaviouralTestName, StringComparison.Ordinal));
        Assert.True(
            start >= 0,
            $"{BehaviouralTestPath} 里找不到 {BehaviouralTestName}。本文件承担的正是那条测试的前提，"
            + "它改名或被删掉，这里要先知道——别把这一条改成找得到就行。");

        int end = Array.FindIndex(lines, start, line => MemberBlockEndRegex.IsMatch(line));
        Assert.True(end > start, $"{BehaviouralTestName} 的方法体没有正常结束——扫描器自己瞎了。");

        string[] asserted = lines[start..(end + 1)]
            .SelectMany(line => Regex.Matches(line, @"Assert\.False\(viewModel\.(?<name>\w+)\)")
                .Select(match => match.Groups["name"].Value))
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToArray();
        string[] registered = RecoveryEntries.Select(entry => entry.Property)
            .Order(StringComparer.Ordinal).ToArray();

        Assert.True(
            asserted.SequenceEqual(registered, StringComparer.Ordinal),
            "行为判据断言的恢复入口与本文件登记的对不上。"
            + $"{Environment.NewLine}那条测试里：{string.Join(", ", asserted)}"
            + $"{Environment.NewLine}本文件表里：{string.Join(", ", registered)}"
            + $"{Environment.NewLine}这两张表必须一起改：一张管「锁存期间它真的关着」，"
            + "另一张管「除了那两条路径没人写它」。");
    }

    /// <summary>
    /// 这一族守卫，每一条都写下了它**守不到什么**。
    /// </summary>
    /// <remarks>
    /// <para>
    /// hmi#176 的范围补充要的就是这张表：不要只在被问到的那一条上写限度，逐条写出来。
    /// 理由是这一族守卫**绝大多数守的是写法不是行为**——源码里有没有某个 token、某个形状，
    /// 而不是运行起来会怎样。写法守卫有一类固定的失效方式：<b>换一个语义等价、token 不同的写法，
    /// 它就全盲</b>，而那一刻没有任何东西会红。把限度写在各自旁边，下一个人读到那条守卫时就读到了
    /// 它撑不住的那一半，不必自己去推。
    /// </para>
    /// <para>
    /// <b>为什么它是一条测试而不是一段文档</b>：一份写一次就不再被碰的限度清单，下一条守卫加进来时
    /// 没有任何东西提醒作者来补。反射对齐之后，新增一条 <c>[Fact]</c> 而不登记就红。
    /// </para>
    /// </remarks>
    [Fact]
    public void EveryGuardInThisFamilyDeclaresWhatItCannotSee()
    {
        string[] actual = new[]
            {
                typeof(FatalFaultScopeArchitectureTests),
                typeof(RecoveryEntryWriteSiteArchitectureTests)
            }
            .SelectMany(type => type.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly))
            .Where(method => method.GetCustomAttributes<FactAttribute>(inherit: true).Any())
            .Select(method => $"{method.DeclaringType!.Name}.{method.Name}")
            .Order(StringComparer.Ordinal)
            .ToArray();
        string[] registered = GuardLimits.Select(limit => limit.Guard)
            .Order(StringComparer.Ordinal).ToArray();

        Assert.True(
            actual.SequenceEqual(registered, StringComparer.Ordinal),
            "这一族守卫的清单变了，而每一条都要写下它守不到什么。"
            + $"{Environment.NewLine}代码里：{string.Join(", ", actual.Except(registered, StringComparer.Ordinal))} 未登记"
            + $"{Environment.NewLine}表里：  {string.Join(", ", registered.Except(actual, StringComparer.Ordinal))} 已不存在"
            + $"{Environment.NewLine}新增一条守卫就要回答：它守的是写法还是行为，换成什么写法它会全盲。");

        Assert.All(GuardLimits, limit => Assert.False(
            string.IsNullOrWhiteSpace(limit.CannotSee),
            $"{limit.Guard}（{limit.Kind}）登记了，却没有写下它守不到什么——那一栏空着等于没登记。"));

        // 这一族**没有一条是行为判据**，这本身是要读者知道的事实：锁存真的挡住了入口，靠的是
        // FatalFaultLatchViewModelTests 那一侧，不是这里的任何一条。哪天这里长出一条行为断言，
        // 这里会红，那时把这句话和 GuardKind 一起改。
        Assert.All(GuardLimits, limit => Assert.True(
            limit.Kind is GuardKind.SourceShape or GuardKind.SelfCheck,
            $"{limit.Guard} 登记成了 {limit.Kind}，而这一族至今全是源码级守卫。"));
        Assert.Contains(GuardLimits, limit => limit.Kind == GuardKind.SourceShape);
        Assert.Contains(GuardLimits, limit => limit.Kind == GuardKind.SelfCheck);
    }

    /// <summary>守卫守的是运行起来的行为，还是源码长什么样，还是另一条守卫自己。</summary>
    private enum GuardKind
    {
        /// <summary>守源码的形状：换一个语义等价、token 不同的写法，它就全盲。</summary>
        SourceShape,

        /// <summary>守另一条守卫的判别力或视力，不直接守产品。</summary>
        SelfCheck
    }

    /// <summary>一条守卫，以及它按构造看不见的东西。</summary>
    private sealed record GuardLimit(string Guard, GuardKind Kind, string CannotSee);

    /// <summary>
    /// 这一族守卫的限度表。**这里每一条都是实读那条守卫的判据写出来的**，不是从它的名字推的。
    /// </summary>
    private static readonly GuardLimit[] GuardLimits =
    [
        new(
            "FatalFaultScopeArchitectureTests.EveryUnlockCallSiteInTheProductIsRegistered",
            GuardKind.SourceShape,
            "只认得文本 `.PulseUnlockAsync(`。经委托、方法组或反射调用同一个成员它看不见；绕开 "
            + "IIoModuleClient、自己 new TcpClient 直接写开锁线圈更看不见（那一半由 "
            + "NothingUnderSrcTouchesTheConcreteModbusClientOutsideItsTwoAllowedSites 部分收窄，而那条自己也承认它"
            + "挡的只是「照着现成客户端再用一次」）。它也没有合成反向验证：没有造一个未登记的开门点确认它会红。"),
        new(
            "FatalFaultScopeArchitectureTests.EverySiteRegisteredAsGuardedReallyChecksTheLatchBeforeUnlocking",
            GuardKind.SourceShape,
            "只问「开门前十行内有没有 ThrowIfFatalFaultLatched 这个 token」。守卫被包进一个恒假的 if、"
            + "或者那个方法本身被改成空实现，它照样绿——token 在就算数。它的判别力由 "
            + "TheseGuardsStillReportRedOnSyntheticViolations 验，那是这一族里唯一有合成验证的产品守卫。"),
        new(
            "FatalFaultScopeArchitectureTests.TheUnguardedSitesCannotSeeTheControllerAtAll",
            GuardKind.SourceShape,
            "判据是「文件文本里不出现 OnboardController」。把控制器包一层接口或别名注入进那两个执行器，"
            + "它一个字都看不见；反过来在那两个文件里写一句提到这个类名的注释，它会误红（文件里已注明，方向是保守的）。"
            + "没有合成反向验证。"),
        new(
            "FatalFaultScopeArchitectureTests.NothingUnderSrcTouchesTheConcreteModbusClientOutsideItsTwoAllowedSites",
            GuardKind.SourceShape,
            "只认得类名 ModbusTcpIoModuleClient 这个 token。本仓的 Modbus 是手写在裸 TcpClient 上的，"
            + "所以一个新文件自己连 IO 模块、直接写开锁线圈，它看不见（文件里已注明）。没有合成反向验证。"),
        new(
            "FatalFaultScopeArchitectureTests.TheInFlightQueryIsDerivedFromTheSerialisationGate",
            GuardKind.SourceShape,
            "只要求表达式体里出现 _operationGate 这个 token。`_operationGate != null` 这类含同一个 token 的"
            + "错写法照样过（文件里已注明）。要升级成行为断言，缺的是一个能在持门期间回调的挂点。没有合成反向验证。"),
        new(
            "FatalFaultScopeArchitectureTests.TheInFlightAggregateReadsBothExecutorsWithAnOr",
            GuardKind.SourceShape,
            "**hmi#176 范围补充点名的那一条。** 它守的是写法不是行为：把两个执行器换成两个同源字段，它仍然绿。"
            + "S2 那条链上三个真实环节——App 接线、HasSlotWorkInFlight 有没有把两个执行器都或进去的运行时结果、"
            + "恢复向量执行器的运行时行为——一个都没有行为判据，因为构造 WireToGateBusinessService 要一个真 "
            + "WireToGateSessionService，只有重 G2 夹具里才有。"),
        new(
            "FatalFaultScopeArchitectureTests.TheseGuardsStillReportRedOnSyntheticViolations",
            GuardKind.SelfCheck,
            "它只验 GuardedWithin 这一个判定函数（四个合成例：没守卫、守卫被注释掉、守卫就在上一行、守卫隔太远）。"
            + "同文件另外四条产品守卫的判定一条都没被合成验证过。"),
        new(
            "FatalFaultScopeArchitectureTests.TheScannerStillFindsUnlockCallsInTheProductSources",
            GuardKind.SelfCheck,
            "只断言「找得到开门点，且其中含两个已知文件」，不断言条数。扫描器漏掉第三处它不会响"
            + "——而漏扫正是这类工具最常见的坏法。"),
        new(
            "FatalFaultScopeArchitectureTests.ThatAggregateGuardSeparatesRightFromWrong",
            GuardKind.SelfCheck,
            "验的是 AssertAggregateIsAnOrOfBothExecutors 这个判定函数对四种错写法都红、对正确写法不红。"
            + "它不验「源码里真的抓到了那个表达式体」——那一半在 TheInFlightAggregateReadsBothExecutorsWithAnOr 里，"
            + "靠 AggregateBody 找不到就红。"),
        new(
            "RecoveryEntryWriteSiteArchitectureTests.EveryWriteToARecoveryEntryInTheViewModelIsGuarded",
            GuardKind.SourceShape,
            "**它守的是写入路径的形状，不是锁存真的挡住了入口。** 把 AllowRecoveryEntry 掏空成 "
            + "`=> offeredByBusiness`、或把 RecoveryEntriesBlockedByFatalFault 改成恒假，这条照样绿"
            + "——那一半由行为判据 BothRefreshPathsKeepTheRecoveryEntriesClosedWhileALatchStands 接住，"
            + "两条是互补的，任何一条单独都不够。它另外看不见：经反射或 XAML 侧写入属性；"
            + "赋值行里带字符串字面量而字面量中含 `//`（剥注释是逐行切分）；以及九个之外的新入口。"),
        new(
            "RecoveryEntryWriteSiteArchitectureTests.TheScannerStillSeesTheRealWriteSites",
            GuardKind.SelfCheck,
            "确认扫描器在真实源码上有输出、条数对得上、九个名字都还命中、成员切分没跑偏、两种写法都认得出。"
            + "它保证不了扫描器对一种**将来才出现的写法**仍然准——那种写法会表现为条数变化，需要人来判。"),
        new(
            "RecoveryEntryWriteSiteArchitectureTests.TheGuardTellsAThirdWritePathFromACompliantOne",
            GuardKind.SelfCheck,
            "七个合成反例覆盖的是七种**已经想到的**错法。想不到的错法不在里面；这条测试的价值上限就是那份清单。"),
        new(
            "RecoveryEntryWriteSiteArchitectureTests.TheRegisteredEntriesAreExactlyWhatTheBehaviouralAssertionCovers",
            GuardKind.SourceShape,
            "钉住的是两张清单彼此一致，**不是它们完整**。新增第十个恢复入口而两边都不登记，这里和行为判据都不会响。"
            + "那一半今天没有承担者：要守住它，得先有一个「什么算一个恢复入口」的机器可判的定义"
            + "（例如按 XAML 绑定面或按一个标记接口枚举），本票没有做。"),
        new(
            "RecoveryEntryWriteSiteArchitectureTests.EveryGuardInThisFamilyDeclaresWhatItCannotSee",
            GuardKind.SelfCheck,
            "只保证每条守卫**有**一行限度说明，且那一行非空。**它判断不了那行说明是不是对的、是不是已经过期**"
            + "——守卫的判据改了而限度那一栏没跟着改，这里不会红。那需要人读。"),
    ];

    /// <summary>一个恢复入口的赋值点，以及它落在哪一类合规写法里。</summary>
    private sealed record WriteSite(
        int Line,
        string Entry,
        string Member,
        string Statement,
        bool ViaAllowRecoveryEntry,
        bool UnderFatalFaultEarlyReturn)
    {
        public bool IsGuarded => ViaAllowRecoveryEntry || UnderFatalFaultEarlyReturn;
    }

    /// <summary>类体里被 <see cref="MemberBlockEndRegex"/>／<see cref="MemberExpressionEndRegex"/> 切出来的一段。</summary>
    private sealed record MemberSpan(string Name, int Start, int End);

    private static WriteSite[] ScanViewModel() => Scan(ReadRepositoryFile(ViewModelPath));

    /// <summary>
    /// 判据本体，**一个纯函数**：输入一份类体源码，输出每个恢复入口赋值点与它是否受守卫。
    /// 写成纯函数是为了让 <see cref="TheGuardTellsAThirdWritePathFromACompliantOne"/> 能直接喂合成源码——
    /// hmi#171 那批守卫做不到双向自检，正是因为它们的判据和「去磁盘上读 src 目录」焊在一起。
    /// </summary>
    private static WriteSite[] Scan(string source)
    {
        string[] lines = StripComments(source);
        List<WriteSite> sites = [];
        foreach (MemberSpan member in SplitIntoMembers(lines))
        {
            int[] guards = FindFatalFaultEarlyReturns(lines, member);
            for (int index = member.Start; index <= member.End; index++)
            {
                foreach (RecoveryEntry entry in RecoveryEntries)
                {
                    if (!AssignmentRegexes[entry.Property].IsMatch(lines[index])
                        && !AssignmentRegexes[entry.BackingField].IsMatch(lines[index]))
                    {
                        continue;
                    }

                    string statement = ReadStatement(lines, index, member.End);
                    sites.Add(new WriteSite(
                        index + 1,
                        entry.Property,
                        member.Name,
                        statement,
                        statement.Contains("AllowRecoveryEntry(", StringComparison.Ordinal),
                        // `<=` 而不是 `<`：单行 early return 里，那句 false 与守卫在同一行。
                        Array.Exists(guards, guard => guard <= index)));
                }
            }
        }

        return [.. sites];
    }

    /// <summary>
    /// 剥掉注释，行号不变。**块注释先剥**：hmi#171 那批守卫只剥行注释，于是一段 <c>/* */</c> 里
    /// 调用形状的文字会被当成代码读——那是反过来的假绿，而这里两种都剥，于是把守卫藏进任何一种注释都会红。
    /// </summary>
    private static string[] StripComments(string source)
    {
        string normalised = source.Replace("\r\n", "\n", StringComparison.Ordinal);
        string withoutBlocks = BlockCommentRegex.Replace(
            normalised,
            match => new string('\n', match.Value.Count(character => character == '\n')));
        return [.. withoutBlocks.Split('\n').Select(line => line.Split("//", 2)[0])];
    }

    /// <summary>
    /// 把类体切成成员。**「同一个成员里」是判据的一部分**，所以这个切分的准确性由
    /// <see cref="TheScannerStillSeesTheRealWriteSites"/> 的成员集合断言盯着：格式一变它就会红。
    /// </summary>
    private static MemberSpan[] SplitIntoMembers(string[] lines)
    {
        List<MemberSpan> members = [];
        int start = 0;
        for (int index = 0; index < lines.Length; index++)
        {
            if (!MemberBlockEndRegex.IsMatch(lines[index]) && !MemberExpressionEndRegex.IsMatch(lines[index]))
            {
                continue;
            }

            members.Add(new MemberSpan(NameOf(lines, start, index), start, index));
            start = index + 1;
        }

        if (start < lines.Length)
        {
            members.Add(new MemberSpan(NameOf(lines, start, lines.Length - 1), start, lines.Length - 1));
        }

        return [.. members];
    }

    private static string NameOf(string[] lines, int start, int end)
    {
        for (int index = start; index <= end; index++)
        {
            Match match = MemberSignatureRegex.Match(lines[index]);
            if (match.Success)
            {
                return match.Groups["name"].Value;
            }
        }

        return "(unnamed)";
    }

    /// <summary>
    /// 成员里每一道**有效的** <c>RecoveryEntriesBlockedByFatalFault</c> early return 的起始行。
    /// </summary>
    /// <remarks>
    /// <b>「有效」的全部内容是那个 <c>return;</c>。</b> 没有它，块里把九个置 false 之后控制流会往下走，
    /// 正常分支紧接着按业务值把它们写回来——而那个块看起来完全正确。这种错法在
    /// <see cref="TheGuardTellsAThirdWritePathFromACompliantOne"/> 的反例六里。
    /// </remarks>
    private static int[] FindFatalFaultEarlyReturns(string[] lines, MemberSpan member)
    {
        List<int> guards = [];
        for (int index = member.Start; index <= member.End; index++)
        {
            if (!FatalFaultGuardRegex.IsMatch(lines[index]))
            {
                continue;
            }

            int blockEnd = FindBlockEnd(lines, index, member.End);
            int scanEnd = blockEnd < 0 ? index : blockEnd;
            for (int inner = index; inner <= scanEnd; inner++)
            {
                if (ReturnRegex.IsMatch(lines[inner]))
                {
                    guards.Add(index);
                    break;
                }
            }
        }

        return [.. guards];
    }

    private static int FindBlockEnd(string[] lines, int headerLine, int limit)
    {
        int depth = 0;
        bool opened = false;
        for (int index = headerLine; index <= limit; index++)
        {
            foreach (char character in lines[index])
            {
                if (character == '{')
                {
                    depth++;
                    opened = true;
                }
                else if (character == '}')
                {
                    depth--;
                    if (opened && depth <= 0)
                    {
                        return index;
                    }
                }
            }
        }

        return -1;
    }

    /// <summary>赋值语句全文，跨行读到 <c>;</c> 为止——<c>AllowRecoveryEntry(</c> 常常在下一行。</summary>
    private static string ReadStatement(string[] lines, int start, int limit)
    {
        StringBuilder builder = new();
        for (int index = start; index <= limit; index++)
        {
            builder.Append(lines[index].Trim()).Append(' ');
            if (lines[index].TrimEnd().EndsWith(';'))
            {
                break;
            }
        }

        return builder.ToString();
    }

    /// <summary>把一段成员源码包成一个类体，让合成例走的是与真实源码同一条缩进假设。</summary>
    private static string ClassBody(string members) =>
        $"public sealed class Synthetic{Environment.NewLine}{{{Environment.NewLine}{members}{Environment.NewLine}}}{Environment.NewLine}";

    private static void AssertCompliant(string members, int expectedSites)
    {
        WriteSite[] sites = Scan(ClassBody(members));
        Assert.Equal(expectedSites, sites.Length);
        Assert.All(sites, site => Assert.True(
            site.IsGuarded,
            $"合规写法被判成了违规（第 {site.Line} 行，{site.Member}）：{site.Statement.Trim()}"));
    }

    /// <summary>
    /// **两个数都要对。** 只断言违规条数，一个把注释也读成代码的扫描器可以靠多数出几个「合规」赋值点
    /// 蒙混过去；<paramref name="expectedSites"/> 同时钉住「它到底看见了几处写」。
    /// </summary>
    private static void AssertViolates(string members, int expectedSites, int expectedViolations)
    {
        WriteSite[] sites = Scan(ClassBody(members));
        Assert.Equal(expectedSites, sites.Length);
        Assert.Equal(expectedViolations, sites.Count(site => !site.IsGuarded));
    }

    private static string ReadRepositoryFile(string relativePath) => File.ReadAllText(Path.Combine(
        ProtocolIdentityArchitectureTests.RepositoryRoot(),
        relativePath.Replace('/', Path.DirectorySeparatorChar)));
}
