using System.Reflection;
using System.Text.RegularExpressions;
using static SQCD.Agv.UnitTests.CSharpSourceLexer;

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
/// 所以这里的判据是两条并列：**每一次写，要么是右边整个就是一次 <c>AllowRecoveryEntry(...)</c> 的简单
/// 赋值，要么落在同一个成员里一道有效的 <c>RecoveryEntriesBlockedByFatalFault</c> early return 之后**
/// （守卫块之内只许写 <c>false</c>）。第三种合规写法是属性自己的 setter 把 <c>value</c> 写进自己的字段。
/// </para>
/// <para>
/// <b>「一次写」不只是 <c>=</c>。</b> 第一版只认简单赋值，审查在真实文件上加了一个
/// <c>SetProperty(ref _field, true, ...)</c> 的新成员，五条守卫全绿——而这个文件里每个 setter 都是这个
/// 形状，下一个人照着抄就是它。现在认四种：简单赋值、复合赋值、解构赋值、以 <c>ref</c>／<c>out</c>
/// 传出 backing field。「有效的 early return」也有三个条件，见 <see cref="FindFatalFaultEarlyReturns"/>。
/// 仍然看不见的写法登记在 <see cref="GuardLimits"/> 里，其中一种按当前结果钉在合成例里。
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
    /// 真实源码里 setter 以外的写入点条数（7 + 9 + 7 + 2）。**写死是有意的**：这条数字是
    /// <see cref="TheScannerStillSeesTheRealWriteSites"/> 判断「扫描器还睁着眼」的判据之一，而扫描器变瞎时的
    /// 默认输出正是「什么都没发现」，与「确实没有」长得一模一样。
    /// **它变了的时候先别改它**：第三条写入路径落在核心守卫盲区里时，这个数是唯一会响的东西。
    /// 先按那条测试报错里的问题逐行回答，确认新写入点挡得住锁存，再改。
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

    /// <summary>
    /// 守卫头，**必须在方法体顶层**：缩进正好 8 格、紧接着就是 <c>if</c>。这就排除了 <c>else if</c>、
    /// 嵌在另一个块或 lambda 里的守卫——见 <see cref="FindFatalFaultEarlyReturns"/>。
    /// </summary>
    private static readonly Regex TopLevelFatalFaultGuardRegex = new(
        @"^        if\s*\(\s*RecoveryEntriesBlockedByFatalFault\s*\)",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    /// <summary>从给定位置起正好是一句 <c>return;</c>（<c>\G</c> 锚在 <c>Match(line, position)</c> 的起点）。</summary>
    private static readonly Regex ReturnRegex = new(
        @"\Greturn\s*;",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    /// <summary>
    /// 对一个恢复入口的**一次写**，四种形状（hmi#176 审查中等 1 之后）：
    /// </summary>
    /// <remarks>
    /// <list type="number">
    /// <item><b>简单与复合赋值</b>：<c>=</c>、<c>|=</c>、<c>&amp;=</c>、<c>^=</c>、<c>??=</c> 等。
    /// 不锚行首：<c>if (...) { CanX = false; return; }</c> 这种单行写法里赋值不在行首。</item>
    /// <item><b>解构赋值</b>：<c>(CanX, CanY) = (...)</c>。</item>
    /// <item><b>以 <c>ref</c>／<c>out</c> 传出 backing field</b>：<c>SetProperty(ref _canX, true, nameof(CanX))</c>。
    /// <b>这是最该防的一种</b>：这个文件里每一个 setter 都是 <c>SetProperty(ref _field, value)</c>，下一个人
    /// 加第三条路径时照着现有写法抄一份，就是这个形状——第一版的守卫恰好看不见它。</item>
    /// </list>
    /// <para>
    /// 字段声明的初始化器（<c>private bool _canX = true;</c>）也会被算成一次写，而那正好该红：那是一个
    /// 默认开着的恢复入口。
    /// </para>
    /// </remarks>
    private static readonly Dictionary<string, Regex[]> WriteRegexes = RecoveryEntries
        .SelectMany(entry => new[] { entry.Property, entry.BackingField })
        .ToDictionary(
            token => token,
            token =>
            {
                string name = $@"(?:this\.)?{Regex.Escape(token)}";
                return new[]
                {
                    new Regex(
                        $@"(?<![\w.]){name}\s*(?:\?\?|<<|>>>|>>|[|&^+\-*/%])?=(?![=>])",
                        RegexOptions.Compiled | RegexOptions.CultureInvariant),
                    new Regex(
                        $@"\((?=[^()]*(?<![\w.]){name}\b)[^()]*,[^()]*\)\s*=(?![=>])",
                        RegexOptions.Compiled | RegexOptions.CultureInvariant),
                    new Regex(
                        $@"\b(?:ref|out)\s+{name}\b",
                        RegexOptions.Compiled | RegexOptions.CultureInvariant)
                };
            },
            StringComparer.Ordinal);

    /// <summary>守卫块内唯一合规的写：<c>CanX = false;</c>。</summary>
    private static readonly Dictionary<string, Regex> FalseRegexes = RecoveryEntries
        .SelectMany(entry => new[] { entry.Property, entry.BackingField })
        .ToDictionary(
            token => token,
            token => new Regex(
                $@"(?<![\w.])(?:this\.)?{Regex.Escape(token)}\s*=\s*false\s*;",
                RegexOptions.Compiled | RegexOptions.CultureInvariant),
            StringComparer.Ordinal);

    /// <summary>函数式写法的开头：简单赋值、右边以 <c>AllowRecoveryEntry(</c> 起头。括号闭合之后的判断在 <see cref="IsWrapped"/>。</summary>
    private static readonly Dictionary<string, Regex> WrappedHeadRegexes = RecoveryEntries
        .SelectMany(entry => new[] { entry.Property, entry.BackingField })
        .ToDictionary(
            token => token,
            token => new Regex(
                $@"(?<![\w.])(?:this\.)?{Regex.Escape(token)}\s*=\s*AllowRecoveryEntry\(",
                RegexOptions.Compiled | RegexOptions.CultureInvariant),
            StringComparer.Ordinal);

    /// <summary>
    /// 属性自己的 setter：把 <c>value</c> 原样写进自己的 backing field，不多也不少。
    /// <c>SetProperty(ref _canX, true)</c> 或 <c>SetProperty(ref _canX, value || x)</c> 不算。
    /// </summary>
    private static readonly Dictionary<string, Regex> OwnSetterRegexes = RecoveryEntries.ToDictionary(
        entry => entry.BackingField,
        entry => new Regex(
            $@"\bref\s+(?:this\.)?{Regex.Escape(entry.BackingField)}\s*,\s*value\s*[,)]"
            + $@"|(?<![\w.])(?:this\.)?{Regex.Escape(entry.BackingField)}\s*=\s*value\s*;",
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
    /// <para>
    /// 五项一起才够：setter 以外的写入点条数对得上（漏扫会掉数）、九个入口每个都至少被看见两次（某个名字
    /// 打错会掉到零）、写入成员恰好是那三个（缩进格式一变，成员切分就不准，而那会悄悄改变「同一个成员里」
    /// 这个判据）、**每个入口恰好一个自己的 setter 被认出来**（属性声明行认不出时，九个 setter 会整批变成
    /// 违规或整批消失）、**函数式与语句式两种写法各自都被认出来过**（只认得函数式那种，正是票面点名的那个陷阱）。
    /// hmi#181 换用 <see cref="CSharpSourceLexer"/> 后再加两项：视图模型里每个成员都切得出名字，且没有一个以续行
    /// 记号开头（深度与命名两项拦不住深度 1 上的错切，hmi#162 第四轮复审）。
    /// </para>
    /// <para>
    /// <b>条数那一项的报错措辞是判据的一部分。</b> 第三条写入路径出现、而核心守卫恰好落在它的盲区里时
    /// （见 <see cref="GuardLimits"/>），**唯一会红的就是这一项**。第一版的报错说「回到这张表来说明」，
    /// 等于在教人改掉那个数让它闭嘴，而改完之后核心守卫是唯一防线、它又判那条新路径合规（审查意见）。
    /// 所以消息里先问问题、后给改数的许可，顺序不能倒。
    /// </para>
    /// </remarks>
    [Fact]
    public void TheScannerStillSeesTheRealWriteSites()
    {
        WriteSite[] sites = ScanViewModel();
        WriteSite[] writes = sites.Where(site => site.Kind != WriteKind.OwnSetter).ToArray();

        Assert.True(
            writes.Length == ExpectedWriteSiteCount,
            $"扫描器在 {ViewModelPath} 里找到 {writes.Length} 个恢复入口写入点（setter 以外），登记的是 "
            + $"{ExpectedWriteSiteCount} 个。**先别改这个数。**"
            + $"{Environment.NewLine}少了：多半是扫描器瞎了（改名、改格式、正则失配），修扫描器，不是改数。"
            + $"{Environment.NewLine}多了：先回答三个问题——多出来的是哪个成员里的哪一行；锁存期间它写进去的是什么；"
            + "它凭什么挡得住锁存（经 AllowRecoveryEntry，还是在方法体顶层一道无条件的 early return 之后）。"
            + "EveryWriteToARecoveryEntryInTheViewModelIsGuarded 判它合规不等于它真有闸门，那条守卫的盲区列在 GuardLimits 里。"
            + $"{Environment.NewLine}逐行读过、确认它挡得住之后，才改 ExpectedWriteSiteCount，并在提交说明里写下是哪一行。"
            + $"{Environment.NewLine}{string.Join(Environment.NewLine, writes.Select(site => $"  {site.Line}  {site.Member}  {site.Kind}  {site.Statement.Trim()}"))}");

        foreach (RecoveryEntry entry in RecoveryEntries)
        {
            int seen = writes.Count(site => site.Entry == entry.Property);
            Assert.True(
                seen >= 2,
                $"{entry.Property} 在源码里只被看见 {seen} 次。"
                + "九个入口每个至少在两条路径上被写，看见不到两次说明这个名字已经对不上源码了。");

            WriteSite[] setters = sites
                .Where(site => site.Entry == entry.Property && site.Kind == WriteKind.OwnSetter)
                .ToArray();
            Assert.True(
                setters.Length == 1 && setters[0].Member == entry.Property,
                $"{entry.Property} 应该恰好有一处被认作它自己的 setter，实际 {setters.Length} 处。"
                + "认不出 setter，说明属性声明行的识别坏了——那时属性块里的写会被算到别的成员名下。");
        }

        string[] members = writes.Select(site => site.Member).Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal).ToArray();
        Assert.True(
            members.SequenceEqual(WritingMembers, StringComparer.Ordinal),
            $"写这九个属性的成员变了。源码里：{string.Join(", ", members)}；"
            + $"表里：{string.Join(", ", WritingMembers)}。"
            + "新增一个成员就要回答：它凭什么可以写这些入口，锁存期间它写的是什么。");

        Assert.Contains(writes, site => site.Kind == WriteKind.Wrapped);
        Assert.Contains(writes, site => site.Kind == WriteKind.UnderGuard);

        // 成员切分本身（hmi#181）：每个成员都有名字，且没有一个以续行记号开头。后一项是 hmi#162 第四轮复审的教训：
        // 深度 1 上切错的成员两半都有名字、深度照样归零，只有「后半截以 || ? : . 开头」这一点露馅。
        MemberSpan[] spans = MemberSpans(Lex(ReadRepositoryFile(ViewModelPath)));
        Assert.True(
            !spans.Any(span => span.Name == Unnamed),
            $"{ViewModelPath} 里有成员切不出名字，第 "
            + $"{string.Join(", ", spans.Where(span => span.Name == Unnamed).Select(span => span.Start + 1))} 行。"
            + "写入点按成员名判「同一个成员里」，名字取不出来这条判据就失准；先让 CSharpSourceLexer 认得这个形状。");
        MemberSpan[] tails = [.. spans.Where(span => ContinuationStartRegex.IsMatch(span.Text))];
        Assert.True(
            tails.Length == 0,
            $"{ViewModelPath} 里有成员以续行记号开头，第 "
            + $"{string.Join(", ", tails.Select(span => $"{span.Start + 1}（{span.Name}）"))} 行：那是上一个成员被切断的后半截，"
            + "它和前半截都不会被当作它本来的那个成员检查。先修 CSharpSourceLexer.MemberSpans。");
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
    /// 反例分三组，每一个都是**真会发生的错法**：第一版就认得的七种（第三条路径、early return 被删、守卫被
    /// 行注释或块注释掉、直接写 backing field、守卫块里少了 <c>return;</c>、守卫在上一个成员里）；审查找到、
    /// 第一版**看不见**的写法（照着现有 setter 抄的 <c>SetProperty(ref ...)</c>、复合赋值、解构赋值、
    /// setter 自己写死值、<c>AllowRecoveryEntry(...) || x</c>）；审查找到、第一版**判错**的守卫形状
    /// （嵌在条件里、<c>else if</c>、塞进 lambda、return 被套进内层 if、不带花括号的守卫头、守卫块里写 true）。
    /// </para>
    /// <para>
    /// <b>最后一个例子是一个已知盲区，按当前结果钉住的</b>：守卫之后把写入放进 lambda 延迟执行，它判合规。
    /// 钉住它不是认可它，是让下一个改进扫描器的人**知道自己改进了**——那时这一例会失败，把它挪进反例即可。
    /// </para>
    /// </remarks>
    [Fact]
    public void TheGuardTellsAThirdWritePathFromACompliantOne()
    {
        // 合规一：函数式，右边整个就是一次 AllowRecoveryEntry。
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

        // 合规三：单行 early return。语义一样，形状不同——判据认的是语义。
        AssertCompliant(
            """
                private void ApplyWireToGatePresentationCore()
                {
                    if (RecoveryEntriesBlockedByFatalFault) { CanRequestLoadCompensation = false; return; }

                    CanRequestLoadCompensation = _canRequest?.Invoke() == true;
                }
            """,
            expectedSites: 2);

        // 合规四：不带花括号、嵌入语句就是 return;。
        AssertCompliant(
            """
                private void ApplyWireToGatePresentationCore()
                {
                    if (RecoveryEntriesBlockedByFatalFault) return;

                    CanRequestLoadCompensation = _canRequest?.Invoke() == true;
                }
            """,
            expectedSites: 1);

        // 合规五：属性自己的 setter 把 value 原样写进自己的字段——这个文件里九个 setter 全是这个形状。
        AssertCompliant(
            """
                public bool CanRequestLoadCompensation
                {
                    get => _canRequestLoadCompensation;
                    private set => SetRecoveryEntry(ref _canRequestLoadCompensation, value);
                }
            """,
            expectedSites: 1);

        // ---- 第一组：第一版就认得的错法 ----

        // 反例：第三条路径，谁都不经过。**这张票的起因就是这一种。**
        AssertViolates(
            """
                private void RefreshSomethingNewCore()
                {
                    CanRequestLoadCompensation = _canRequest?.Invoke() == true;
                }
            """,
            expectedSites: 1,
            expectedViolations: 1);

        // 反例：early return 被删掉，正常分支原样留着。扫 AllowRecoveryEntry( 的守卫看不见这一种。
        AssertViolates(
            """
                private void ApplyWireToGatePresentationCore()
                {
                    CanRequestLoadCompensation = _canRequest?.Invoke() == true;
                }
            """,
            expectedSites: 1,
            expectedViolations: 1);

        // 反例：守卫被行注释掉。注释里的那个 false 也不该被数成一个写入点，所以只有一个。
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

        // 反例：守卫被块注释包起来。hmi#171 那批守卫只剥行注释，块注释里的调用会被当成代码读。
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

        // 反例：绕过属性、直接给 backing field 赋值。
        AssertViolates(
            """
                private void RefreshSomethingNewCore()
                {
                    _canRequestLoadCompensation = true;
                }
            """,
            expectedSites: 1,
            expectedViolations: 1);

        // 反例：守卫块里少了 return;。块内那个 false 看起来完全正确，而正常分支紧接着把它写回来
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

        // ---- 第二组：第一版看不见的写法（审查中等 1、疑 3） ----

        // 反例：**照着文件里现有 setter 抄出来的第三条路径**。这是最可能出现的形状，第一版恰好看不见它。
        AssertViolates(
            """
                private void RefreshSomethingNewCore()
                {
                    SetProperty(ref _canRequestLoadCorrection, true, nameof(CanRequestLoadCorrection));
                }
            """,
            expectedSites: 1,
            expectedViolations: 1);

        // 反例：复合赋值，右边还包着闸门——`CanX |= AllowRecoveryEntry(b)` 等于「旧值 或 闸门」。
        AssertViolates(
            """
                private void RefreshSomethingNewCore()
                {
                    CanRequestLoadCompensation |= AllowRecoveryEntry(_canRequest?.Invoke() == true);
                }
            """,
            expectedSites: 1,
            expectedViolations: 1);

        // 反例：解构赋值，一行写两个入口。
        AssertViolates(
            """
                private void RefreshSomethingNewCore()
                {
                    (CanRequestLoadCompensation, CanRequestLoadCorrection) = (true, true);
                }
            """,
            expectedSites: 2,
            expectedViolations: 2);

        // 反例：属性自己的 setter 写死了值，不是 value。
        AssertViolates(
            """
                public bool CanRequestLoadCompensation
                {
                    get => _canRequestLoadCompensation;
                    private set => SetRecoveryEntry(ref _canRequestLoadCompensation, true);
                }
            """,
            expectedSites: 1,
            expectedViolations: 1);

        // 反例：闸门只是右边的一部分。第一版只问「语句里含 AllowRecoveryEntry(」，这一例它判合规。
        AssertViolates(
            """
                private void RefreshWireToGateInputStateCore()
                {
                    CanRequestLoadCorrection = AllowRecoveryEntry(false) || _business;
                }
            """,
            expectedSites: 1,
            expectedViolations: 1);

        // ---- 第三组：第一版判错的守卫形状（审查中等 2） ----

        // 反例：守卫嵌在条件里。_x 为假时守卫根本不执行，后面的写照样把入口打开。
        AssertViolates(
            """
                private void ApplyWireToGatePresentationCore()
                {
                    if (_x)
                    {
                        if (RecoveryEntriesBlockedByFatalFault)
                        {
                            CanRequestLoadCompensation = false;
                            return;
                        }
                    }

                    CanRequestLoadCompensation = _canRequest?.Invoke() == true;
                }
            """,
            expectedSites: 2,
            expectedViolations: 2);

        // 反例：写成 else if。前一个分支走了，守卫就不执行。
        AssertViolates(
            """
                private void ApplyWireToGatePresentationCore()
                {
                    if (_x)
                    {
                        HasWarning = true;
                    }
                    else if (RecoveryEntriesBlockedByFatalFault)
                    {
                        CanRequestLoadCompensation = false;
                        return;
                    }

                    CanRequestLoadCompensation = _canRequest?.Invoke() == true;
                }
            """,
            expectedSites: 2,
            expectedViolations: 2);

        // 反例：守卫塞进 lambda——那里的 return 只退出 lambda。
        AssertViolates(
            """
                private void ApplyWireToGatePresentationCore()
                {
                    RunOnUiThread(() =>
                    {
                        if (RecoveryEntriesBlockedByFatalFault)
                        {
                            CanRequestLoadCompensation = false;
                            return;
                        }
                    });

                    CanRequestLoadCompensation = _canRequest?.Invoke() == true;
                }
            """,
            expectedSites: 2,
            expectedViolations: 2);

        // 反例：return 被套进守卫块里的内层 if，不再是无条件的。
        AssertViolates(
            """
                private void ApplyWireToGatePresentationCore()
                {
                    if (RecoveryEntriesBlockedByFatalFault)
                    {
                        CanRequestLoadCompensation = false;
                        if (_y)
                        {
                            return;
                        }
                    }

                    CanRequestLoadCompensation = _canRequest?.Invoke() == true;
                }
            """,
            expectedSites: 2,
            expectedViolations: 2);

        // 反例：**审查最刁的那一例**。守卫头不带花括号、嵌入语句不是 return，于是那个 false 与后面的写全都
        // 无条件执行；后面再有一个无关的 if 块带着 return。第一版会一路找到那个无关块，把它的 return 当成守卫的。
        AssertViolates(
            """
                private void ApplyWireToGatePresentationCore()
                {
                    if (RecoveryEntriesBlockedByFatalFault) HasWarning = true;
                    CanRequestLoadCompensation = false;
                    if (_y)
                    {
                        return;
                    }

                    CanRequestLoadCompensation = _canRequest?.Invoke() == true;
                }
            """,
            expectedSites: 2,
            expectedViolations: 2);

        // 反例：守卫块本身是一道 early return，但它在块里把入口打开了。块内只许写 false。
        AssertViolates(
            """
                private void ApplyWireToGatePresentationCore()
                {
                    if (RecoveryEntriesBlockedByFatalFault)
                    {
                        CanRequestLoadCompensation = true;
                        return;
                    }
                }
            """,
            expectedSites: 1,
            expectedViolations: 1);

        // 反例：守卫在上一个成员里，赋值在下一个成员里。**这一条验的是「同一个成员」那半个判据**：
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

        // ---- hmi#181：逐行读源码看不见的写法。换用 CSharpSourceLexer 之前这三类里前两类全绿 ----

        // 反例：跨行赋值。名字在一行、`= true;` 在下一行，逐行找写入时两行各自都不像一次写。
        AssertViolates(
            """
                private void ReopenCompensation()
                {
                    CanRequestLoadCompensation
                        = true;
                }
            """,
            expectedSites: 1,
            expectedViolations: 1);

        // 反例：同一行前面有一个含 `//` 的字符串。按「行里第一个 //」剥注释会把后半行连同写入一起剥掉。
        AssertViolates(
            """
                private void ReopenCompensation()
                {
                    string link = "onboard://recovery"; CanRequestLoadCompensation = link.Length > 0;
                }
            """,
            expectedSites: 1,
            expectedViolations: 1);

        // 回归（两边都红）：换行的表达式体属性里写入口。**这一类在本守卫里本来就红**：它没有闭包，逐个写入点判定，
        // 旧的按缩进切分把这个属性并进相邻成员，写入仍落在某个成员里，既不是 AllowRecoveryEntry 包着的写、也不在
        // 守卫之后，照样判 Unguarded。hmi#162 那边同名的问题是错切让闭包跟不进无名成员，这里没有闭包，那个失效形状
        // 不存在（hmi#181 票面前提据此更正）。换用 CSharpSourceLexer 之后变的只是报错里的成员名，变成它自己的名字；
        // 之前取决于放在哪里（真实文件上实测：写入成员前 → RefreshWireToGateInputStateCore，写入成员后 →
        // OnVehicleSafetySignalChanged，写成单行 → (unnamed)）。下面断言钉住的正是这个名字。
        WriteSite[] expressionBodied = Scan(ClassBody(
            """
                private bool ReopenCompensation =>
                    CanRequestLoadCompensation = true;

                private void RefreshWireToGateInputStateCore()
                {
                    CanRequestLoadCompensation = AllowRecoveryEntry(_canRequest?.Invoke() == true);
                }
            """));
        Assert.Equal(2, expressionBodied.Length);
        Assert.Equal("ReopenCompensation", expressionBodied[0].Member);
        Assert.False(expressionBodied[0].IsGuarded);
        Assert.Equal("RefreshWireToGateInputStateCore", expressionBodied[1].Member);
        Assert.True(expressionBodied[1].IsGuarded);

        // 反例：守卫块里跨行把入口打开。块内「只许写 false」按单行数，会数出 0 次写、0 次 false 而判合规；
        // 计数要延伸到这次写所在语句的 `;`。
        AssertViolates(
            """
                private void ApplyWireToGatePresentationCore()
                {
                    if (RecoveryEntriesBlockedByFatalFault)
                    {
                        CanRequestLoadCompensation
                            = true;
                        return;
                    }
                }
            """,
            expectedSites: 1,
            expectedViolations: 1);

        // 反例（hmi#181 审查 S1）：守卫块里跨行的 ref／out／解构写入，开头的 `ref`、`out`、`(` 在入口名字的上一行。
        // 本票第一版从名字所在行的行首截取计数文本，把它们截在外面，数出 0 次写、0 次 false 而判合规——四种都是。
        foreach (string splitWrite in new[]
        {
            "SetProperty(ref\n                _canRequestLoadCompensation, HasPhysicallyUnknownSlots, nameof(CanRequestLoadCompensation));",
            "(_,\n                CanRequestLoadCompensation) = (0, true);",
            "(\n                CanRequestLoadCompensation, _) = (true, 0);",
            "_ = bool.TryParse(\"true\", out\n                _canRequestLoadCompensation);",
        })
        {
            AssertViolates(
                "    private void ApplyWireToGatePresentationCore()\n    {\n        if (RecoveryEntriesBlockedByFatalFault)\n        {\n"
                + $"            {splitWrite}\n            return;\n        }}\n    }}",
                expectedSites: 1,
                expectedViolations: 1);
        }

        // 反例（审查 S2，基分支上就有）：同一行上第二次写。按行分类时，这一行只要有一次合规写，整行放行。
        AssertViolates(
            """
                private void RefreshWireToGateInputStateCore()
                {
                    CanRequestLoadCompensation = AllowRecoveryEntry(_canRequest?.Invoke() == true); CanRequestLoadCompensation |= HasPhysicallyUnknownSlots;
                }
            """,
            expectedSites: 2,
            expectedViolations: 1);
        AssertViolates(
            """
                public bool CanRequestLoadCompensation
                {
                    get => _canRequestLoadCompensation;
                    private set { SetProperty(ref _canRequestLoadCompensation, value); _canRequestLoadCompensation |= HasPhysicallyUnknownSlots; }
                }
            """,
            expectedSites: 2,
            expectedViolations: 1);

        // 合规：守卫块里跨行写 false 仍是合规的，不因为跨行被误判。
        AssertCompliant(
            """
                private void ApplyWireToGatePresentationCore()
                {
                    if (RecoveryEntriesBlockedByFatalFault)
                    {
                        CanRequestLoadCompensation
                            = false;
                        return;
                    }
                }
            """,
            expectedSites: 1);

        // ---- 已知盲区，按当前结果钉住 ----

        // 守卫之后把写入放进 lambda 延迟执行：lambda 真正跑的时候，锁存可能已经立起来了，而守卫只在
        // 这个方法进入时查了一次。扫描器按行号判它在 early return 之后，于是**判合规**。这是错的，但要识别它
        // 需要知道哪些代码会被推迟执行，那是控制流分析。改进扫描器之后这一例会失败——那时把它挪进反例。
        AssertCompliant(
            """
                private void ApplyWireToGatePresentationCore()
                {
                    if (RecoveryEntriesBlockedByFatalFault)
                    {
                        return;
                    }

                    RunOnUiThread(() => CanRequestLoadCompensation = _canRequest?.Invoke() == true);
                }
            """,
            expectedSites: 1);

        // 同一语句内对同一入口的第二次写（hmi#181 定向核对 E1）：setter 里先按合规形状 SetProperty(ref _x, value)，
        // 同一条语句里再 `_x |= y`。一条语句对一个入口只判一次，第一次的合规形状掩护了第二次，于是**判合规**。
        // 基分支同样看不见；同形状还有 `AllowRecoveryEntry(...)` 实参里放 lambda 写同一入口。**不追**：扫描护栏第二轮
        // 仍有绕法就停下，统一判据、收窄声称（票面「不做」，hmi#162 与 cs#262 的教训）——拆到「一次写一判」要的是
        // 表达式级的分析，那是另一个量级。这一例失败时说明扫描器变强了，把它挪进反例。
        AssertCompliant(
            """
                public bool CanRequestLoadCorrection
                {
                    get => _canRequestLoadCorrection;
                    private set => _ = SetProperty(ref _canRequestLoadCorrection, value) | (_canRequestLoadCorrection |= HasPhysicallyUnknownSlots);
                }
            """,
            expectedSites: 1);
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
        Lexed lexed = Lex(ReadRepositoryFile(BehaviouralTestPath));
        MemberSpan[] spans = [.. MemberSpans(lexed).Where(span => span.Name == BehaviouralTestName)];
        Assert.True(
            spans.Length == 1,
            $"{BehaviouralTestPath} 里找到 {spans.Length} 个 {BehaviouralTestName}。本文件承担的正是那条测试的前提，"
            + "它改名或被删掉，这里要先知道——别把这一条改成找得到就行。");

        string[] asserted = lexed.Code[spans[0].Start..(spans[0].End + 1)]
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
            + "两条是互补的，任何一条单独都不够。"
            + "**「early return 之后」是按行号与缩进近似的，不是控制流分析**：守卫头要在缩进 8 格的方法体顶层、"
            + "return 要在守卫块第一层，这挡住了嵌套 if、else if、lambda 里的守卫、内层 return、不带花括号的假守卫"
            + "（审查中等 2 的五种变异，合成反例都在）；但**守卫之后把写入放进 lambda 或本地函数延迟执行，它判合规**，"
            + "**写在 AllowRecoveryEntry(...) 实参里的 lambda 同样判合规**（整句被当成一次包着的写）"
            + "（合成例最后一条按当前结果钉着），格式没经过 dotnet format 时缩进近似也会失准。"
            + "写入的形状认得简单与复合赋值、解构赋值、以 ref／out 传出 backing field（审查中等 1），"
            + "hmi#181 起经 CSharpSourceLexer 读、按成员的代码文本匹配，并以**语句**为分类单位（上一个 `;`／`{`／`}` 之后到"
            + "下一个 `;`）：字符串里的 `//` 不再截断一行；名字与 `=`、`ref`／`out` 与字段、解构的括号与名字分在两行也认得出；"
            + "不同语句各自判定。**同一语句内对同一入口的多次写只判一次，第一次的合规形状会掩护后面的**"
            + "（`private set => _ = SetProperty(ref _x, value) | (_x |= y);`、`AllowRecoveryEntry(... Task.Run(() => CanX |= y) ...)`，"
            + "合成例钉着前一种；基分支同样看不见，按扫描护栏「第二轮仍有绕法就停」没有追）。语句边界是按这三个字符往回找的，"
            + "不是语法分析。"
            + "它看不见的写法：经反射或 XAML 双向绑定写入、在别的文件里写（今天九个属性都是 private set 且类不是 partial，"
            + "所以别的文件写不进来——那是今天的状态，不是这条守卫保证的）；写另一个实例的入口（`other.CanX = true`，"
            + "形状里排除了点号前缀）；九个之外的新入口。词法层自己的限度：同一行两个成员共用这一行、用转义写的标识符"
            + "认不出、原始字符串拒读（大声失败）。"),
        new(
            "RecoveryEntryWriteSiteArchitectureTests.TheScannerStillSeesTheRealWriteSites",
            GuardKind.SelfCheck,
            "确认扫描器在真实源码上有输出：setter 以外的写入点条数对得上、九个名字都还命中、每个入口恰好认出一个"
            + "自己的 setter、成员切分没跑偏、两种写法都认得出。**第三条写入路径落在核心守卫盲区里时，唯一会红的就是"
            + "它的条数那一项**——所以它的报错先问「多出来的那一行凭什么挡得住锁存」，再许可改数。它保证不了扫描器对一种"
            + "将来才出现的写法仍然准，也拦不住有人不读报错直接改数。"),
        new(
            "RecoveryEntryWriteSiteArchitectureTests.TheGuardTellsAThirdWritePathFromACompliantOne",
            GuardKind.SelfCheck,
            "合成反例覆盖的是**已经想到的**错法（第一版的七种、审查补的十一种、hmi#181 补的逐行读法看不见的四种、"
            + "hmi#181 审查补的跨行 ref／out／解构四种与同一行第二次写两种），"
            + "想不到的不在里面；这条测试的价值上限"
            + "就是那份清单。最后一例是按当前结果钉住的已知盲区（守卫后的 lambda 延迟写入被判合规），它失败时说明扫描器"
            + "变强了，不是变坏了。"),
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

    /// <summary>一个赋值点落在哪一类写法里。</summary>
    private enum WriteKind
    {
        /// <summary>简单赋值，右边**整个**就是一次 <c>AllowRecoveryEntry(...)</c> 调用（函数式）。</summary>
        Wrapped,

        /// <summary>落在同一个成员里一道有效的 early return 之后，或是它块内的一句 <c>= false;</c>（语句式）。</summary>
        UnderGuard,

        /// <summary>属性自己的 setter 把 <c>value</c> 写进自己的 backing field。</summary>
        OwnSetter,

        /// <summary>以上都不是。</summary>
        Unguarded
    }

    /// <summary>一个恢复入口的写入点，以及它落在哪一类写法里。</summary>
    private sealed record WriteSite(int Line, string Entry, string Member, string Statement, WriteKind Kind)
    {
        public bool IsGuarded => Kind != WriteKind.Unguarded;
    }

    /// <summary>一道有效的 early return：守卫头所在行，与它管住的那一块的最后一行。</summary>
    private sealed record EarlyReturn(int Header, int BlockEnd);

    private static WriteSite[] ScanViewModel() => Scan(ReadRepositoryFile(ViewModelPath));

    /// <summary>
    /// 判据本体，**一个纯函数**：输入一份类体源码，输出每个恢复入口写入点与它落在哪一类写法里。
    /// 写成纯函数是为了让 <see cref="TheGuardTellsAThirdWritePathFromACompliantOne"/> 能直接喂合成源码——
    /// hmi#171 那批守卫做不到双向自检，正是因为它们的判据和「去磁盘上读 src 目录」焊在一起。
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>源码经 <see cref="CSharpSourceLexer"/> 读（hmi#181）。</b>第一版逐行用正则近似 C#：按「行里第一个
    /// <c>//</c>」剥注释，于是 <c>"onboard://x"; CanX = true;</c> 的写入连同后半行一起被剥掉；按缩进四格的
    /// <c>}</c>／<c>;</c> 切成员，于是换行的表达式体成员并进相邻成员、名字取错；逐行找写入，于是名字在一行、
    /// <c>= true;</c> 在下一行的赋值两行都不像一次写。现在注释与字面量由词法层抹成等长空白（行号不变），成员由
    /// <see cref="CSharpSourceLexer.MemberSpans"/> 切。
    /// </para>
    /// <para>
    /// <b>跨行不是一条新规则，是同一组写法换了一个读的范围。</b>写入形状（<see cref="WriteRegexes"/>）不再逐行匹配，
    /// 而是对整个成员的代码文本匹配，<c>\s*</c> 本来就跨得过换行；命中位置再折回行号。票面要的是「统一判据，不追加
    /// 禁用写法清单」。
    /// </para>
    /// <para>
    /// <b>分类的单位是语句，不是行</b>（hmi#181 审查 S1、S2）。一次写归到它所在的那条语句（<see cref="StatementStart"/>
    /// 到下一个 <c>;</c>），一条语句对一个入口判一次，行号取入口名字所在的行。按行分类有两个洞：同一行上
    /// <c>CanX = AllowRecoveryEntry(...); CanX |= y;</c> 的第二次写被第一次的合规放行（基分支上就有）；按行首截取计数文本时，
    /// 上一行的 <c>ref</c>／<c>out</c>／解构的 <c>(</c> 被截在外面，守卫块里那样的写被判合规（本票第一版引入）。
    /// 真实源码上每条语句只写一个入口一次，条数 25 不变。
    /// </para>
    /// </remarks>
    private static WriteSite[] Scan(string source)
    {
        Lexed lexed = Lex(source);
        string[] lines = lexed.Code;
        List<WriteSite> sites = [];
        foreach (MemberSpan member in MemberSpans(lexed))
        {
            EarlyReturn[] guards = FindFatalFaultEarlyReturns(lines, member);
            string text = string.Join('\n', lines[member.Start..(member.End + 1)]);
            int[] lineStarts = LineStarts(text);
            foreach (RecoveryEntry entry in RecoveryEntries)
            {
                // 这个入口的每一次写，归到它所在的那条语句；一条语句对一个入口只判一次，行号取第一次写时入口名字所在的行。
                SortedDictionary<int, (int NameAt, int End)> byStatement = [];
                foreach (Match match in WriteRegexes[entry.Property].Concat(WriteRegexes[entry.BackingField])
                    .SelectMany(regex => regex.Matches(text)))
                {
                    int start = StatementStart(text, match.Index);
                    int nameAt = match.Index + NameOffset(match.Value, entry);
                    int end = text.IndexOf(';', match.Index + match.Length);
                    end = end < 0 ? text.Length : end + 1;
                    byStatement[start] = byStatement.TryGetValue(start, out (int NameAt, int End) seen)
                        ? (Math.Min(seen.NameAt, nameAt), Math.Max(seen.End, end))
                        : (nameAt, end);
                }

                foreach ((int start, (int nameAt, int end)) in byStatement)
                {
                    int index = member.Start + LineOf(lineStarts, nameAt);
                    string statement = text[start..end];
                    sites.Add(new WriteSite(
                        index + 1,
                        entry.Property,
                        member.Name,
                        Regex.Replace(statement, @"\s+", " ").Trim(),
                        Classify(entry, member, statement, index, guards)));
                }
            }
        }

        return [.. sites.OrderBy(site => site.Line).ThenBy(site => site.Entry, StringComparer.Ordinal)];
    }

    /// <summary>
    /// 一条语句的起点：往回找到的第一个 <c>;</c>、<c>{</c> 或 <c>}</c> 之后（代码视图里字面量与注释已抹掉，里面的这些字符不算）。
    /// <c>ref</c>、<c>out</c>、解构的 <c>(</c> 在入口名字的上一行时，它们仍在这条语句里（hmi#181 审查 S1）。
    /// </summary>
    private static int StatementStart(string text, int position)
    {
        int boundary = position <= 0 ? -1 : text.LastIndexOfAny([';', '{', '}'], position - 1);
        return boundary + 1;
    }

    /// <summary>Where the entry's own name starts inside a write match (a deconstruction match starts at its <c>(</c>).</summary>
    private static int NameOffset(string matched, RecoveryEntry entry)
    {
        Match name = Regex.Match(
            matched,
            $@"(?<![\w.])(?:{Regex.Escape(entry.Property)}|{Regex.Escape(entry.BackingField)})\b",
            RegexOptions.CultureInvariant);
        return name.Success ? name.Index : 0;
    }

    private static int[] LineStarts(string text)
    {
        List<int> starts = [0];
        for (int index = 0; index < text.Length; index++)
        {
            if (text[index] == '\n')
            {
                starts.Add(index + 1);
            }
        }

        return [.. starts];
    }

    private static int LineOf(int[] lineStarts, int position)
    {
        int found = Array.BinarySearch(lineStarts, position);
        return found >= 0 ? found : ~found - 1;
    }

    /// <summary>
    /// 一个写入点属于哪一类。顺序有意义：先认 setter 本身，再认函数式，最后才看守卫——
    /// 守卫块内的写入只许是 <c>= false;</c>，块后的写入才被那道 early return 保护。
    /// </summary>
    /// <param name="statement">这次写所在的那一条语句，代码视图，可跨行（见 <see cref="StatementStart"/>）。</param>
    private static WriteKind Classify(
        RecoveryEntry entry,
        MemberSpan member,
        string statement,
        int index,
        EarlyReturn[] guards)
    {
        if (string.Equals(member.Name, entry.Property, StringComparison.Ordinal)
            && OwnSetterRegexes[entry.BackingField].IsMatch(statement))
        {
            return WriteKind.OwnSetter;
        }

        if (IsWrapped(statement, entry))
        {
            return WriteKind.Wrapped;
        }

        if (Array.Exists(guards, guard => guard.BlockEnd < index))
        {
            return WriteKind.UnderGuard;
        }

        // 守卫块之内：唯一合规的写法是把它关掉。`CanX = true; return;` 同样是一道 early return，
        // 而它恰好把入口打开了。这条语句里每一次写都得是 `= false;`，ref／解构这类写法一概不算。
        // 数的范围是整条语句而不是一行（hmi#181）：按行数，`CanX` 换行 `= true;` 会数出 0 次写、0 次 false 而判合规；
        // 从名字所在行的行首数，`SetProperty(ref` 换行 `_canX, …)` 的 ref 被截在外面，同样 0 对 0（审查 S1）。
        if (Array.Exists(guards, guard => guard.Header <= index && index <= guard.BlockEnd)
            && CountWrites(statement, entry) == FalseRegexes[entry.Property].Matches(statement).Count
                + FalseRegexes[entry.BackingField].Matches(statement).Count)
        {
            return WriteKind.UnderGuard;
        }

        return WriteKind.Unguarded;
    }

    private static int CountWrites(string line, RecoveryEntry entry) =>
        WriteRegexes[entry.Property].Concat(WriteRegexes[entry.BackingField])
            .Sum(regex => regex.Matches(line).Count);

    /// <summary>
    /// 右边**整个**是一次 <c>AllowRecoveryEntry(...)</c> 调用：那个括号一闭合，紧跟着就是 <c>;</c>。
    /// 只问「语句里含这个字符串」，<c>CanX = AllowRecoveryEntry(false) || business;</c> 也会过（审查疑 3）。
    /// 只认简单赋值 <c>=</c>：<c>CanX |= AllowRecoveryEntry(b)</c> 等于「旧值 或 闸门」，旧值会漏过闸门。
    /// </summary>
    private static bool IsWrapped(string statement, RecoveryEntry entry)
    {
        foreach (string token in new[] { entry.Property, entry.BackingField })
        {
            Match head = WrappedHeadRegexes[token].Match(statement);
            if (!head.Success)
            {
                continue;
            }

            int depth = 1;
            for (int position = head.Index + head.Length; position < statement.Length; position++)
            {
                if (statement[position] == '(')
                {
                    depth++;
                }
                else if (statement[position] == ')' && --depth == 0)
                {
                    return statement[(position + 1)..].TrimStart().StartsWith(';');
                }
            }
        }

        return false;
    }

    /// <summary>
    /// 成员里每一道**有效的** <c>RecoveryEntriesBlockedByFatalFault</c> early return。
    /// </summary>
    /// <remarks>
    /// <para>
    /// 「有效」是三件事，每一件都对应审查找到的一种骗过第一版的变异（hmi#176 审查中等 2）：
    /// </para>
    /// <list type="number">
    /// <item><b>守卫头在方法体的顶层</b>：缩进正好 8 格、以 <c>if</c> 开头。包进另一个 <c>if</c>、写成
    /// <c>else if</c>、塞进 <c>RunOnUiThread(() =&gt; { ... })</c>（那里的 return 只退出 lambda），都不算——
    /// 那时守卫后面的代码不一定被它挡住。</item>
    /// <item><b><c>return;</c> 在守卫块的第一层</b>：块里再套一层 <c>if (...) { return; }</c>，那个 return
    /// 不是无条件的。</item>
    /// <item><b>不带花括号的守卫头，唯一算数的嵌入语句是 <c>return;</c></b>。
    /// <c>if (RecoveryEntriesBlockedByFatalFault) HasWarning = true;</c> 之后的代码无条件执行；第一版会一路
    /// 找到后面某个无关的花括号块，把它的 return 当成守卫的。</item>
    /// </list>
    /// <para>
    /// 第一条靠的是缩进，也就是靠 <c>dotnet format</c>：它认的是「格式化过的顶层」，不是控制流意义上的
    /// 顶层。对一个正则扫描器，真正的控制流分析是质的跨越，本票没有做（登记在 <see cref="GuardLimits"/> 里）。
    /// </para>
    /// </remarks>
    private static EarlyReturn[] FindFatalFaultEarlyReturns(string[] lines, MemberSpan member)
    {
        List<EarlyReturn> guards = [];
        for (int index = member.Start; index <= member.End; index++)
        {
            Match header = TopLevelFatalFaultGuardRegex.Match(lines[index]);
            if (!header.Success)
            {
                continue;
            }

            int? blockEnd = GuardedBlockEnd(lines, index, header.Index + header.Length, member.End);
            if (blockEnd is int end)
            {
                guards.Add(new EarlyReturn(index, end));
            }
        }

        return [.. guards];
    }

    /// <summary>守卫头后面那条嵌入语句的最后一行——前提是它无条件地 return，否则 <c>null</c>。</summary>
    private static int? GuardedBlockEnd(string[] lines, int headerLine, int column, int limit)
    {
        int line = headerLine;
        int position = column;
        while (line <= limit)
        {
            while (position < lines[line].Length && char.IsWhiteSpace(lines[line][position]))
            {
                position++;
            }

            if (position < lines[line].Length)
            {
                break;
            }

            line++;
            position = 0;
        }

        if (line > limit)
        {
            return null;
        }

        if (lines[line][position] != '{')
        {
            return ReturnAt(lines[line], position) ? line : null;
        }

        int depth = 0;
        bool returns = false;
        for (; line <= limit; line++, position = 0)
        {
            for (; position < lines[line].Length; position++)
            {
                char character = lines[line][position];
                if (character == '{')
                {
                    depth++;
                }
                else if (character == '}')
                {
                    if (--depth == 0)
                    {
                        return returns ? line : null;
                    }
                }
                else if (depth == 1 && ReturnAt(lines[line], position))
                {
                    returns = true;
                }
            }
        }

        return null;
    }

    private static bool ReturnAt(string line, int position) =>
        (position == 0 || !(char.IsLetterOrDigit(line[position - 1]) || line[position - 1] == '_'))
        && ReturnRegex.Match(line, position).Success;

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
