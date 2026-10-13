using System.Text.RegularExpressions;

namespace SQCD.Agv.UnitTests;

/// <summary>
/// The shape research 4.3 item 1 rests on (onboard-hmi#315, T2 acceptance "入口护栏"): every dispatcher entry point of
/// the screen-state snapshot is wrapped whole in <c>try/catch (Exception)</c>, and every log write on that path sits in a
/// <c>try/catch (Exception)</c> of its own.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why.</b> An exception escaping onto the WPF dispatcher is classified <c>UNHANDLED_UI_ERROR</c> and latches the
/// vehicle; one escaping a <c>PropertyChanged</c> handler also leaves <c>SetProperty</c>'s caller (an entry refresh that
/// writes a dozen properties) half done. The behaviour is tested on a real dispatcher in
/// <c>HmiScreenStateMonitorDispatcherTests</c>; this holds the shape, so the next entry point added is wrapped too.
/// </para>
/// <para>
/// <b>What counts as an entry point.</b> In <c>HmiScreenStateMonitor.cs</c>: every method named <c>On…</c>,
/// <c>TryStart</c> and <c>Dispose</c>; every <c>+=</c> there must subscribe one of the <c>On…</c> methods by name (no
/// lambda), and nothing there may queue a delegate onto the dispatcher (<c>BeginInvoke</c>, <c>InvokeAsync</c>,
/// <c>Dispatcher.Invoke</c>), since such a delegate would be an entry point this scan does not see. In
/// <c>App.xaml.cs</c>: <c>StartScreenState</c>, the wiring.
/// </para>
/// <para>
/// <b>What it cannot see.</b> It reads source text with comments and literals blanked, cuts methods by braces, and
/// checks the top-level statements of each body: declarations initialised to a constant, then one <c>try</c> whose last
/// catch is <c>catch (Exception …)</c>, then nothing. It does not follow calls: code that an entry point calls is covered
/// by that entry point's catch, which is the point. The writer's <c>Submit</c>/<c>Disable</c>, called from the dispatcher,
/// only post to a channel (<c>TryWrite</c>, which does not throw); the writer's own task is not on the dispatcher and
/// catches per request.
/// </para>
/// <para>
/// The scan helpers are internal: <c>OperatorRecordLogArchitectureTests</c> (onboard-hmi#317) holds the operator record
/// log to the same shape with the same code.
/// </para>
/// </remarks>
public sealed class HmiScreenStateIsolationArchitectureTests
{
    private static readonly Regex Subscription = new(@"\+=\s*(?<target>[^;]+);", RegexOptions.Compiled);

    [Fact]
    public void EveryDispatcherEntryPointIsWrappedWhole()
    {
        string monitor = Code("src", "SQCD.Agv.Wpf", "ScreenStateOutput", "HmiScreenStateMonitor.cs");
        string[] entries =
        [
            .. Regex.Matches(monitor, @"\bvoid\s+(?<name>On\w+)\s*\(").Select(match => match.Groups["name"].Value),
            "TryStart",
            "Dispose",
        ];
        Assert.Contains("OnViewModelPropertyChanged", entries);
        Assert.Contains("OnFlushTick", entries);
        Assert.Contains("OnHeartbeatTick", entries);

        List<string> offences = [.. entries.SelectMany(entry => WrappedWholeOffences(monitor, entry))];
        offences.AddRange(WrappedWholeOffences(Code("src", "SQCD.Agv.Wpf", "App.xaml.cs"), "StartScreenState"));

        Assert.True(offences.Count == 0, "Not wrapped whole in try/catch (Exception):\n" + string.Join("\n", offences));
    }

    [Fact]
    public void EveryHandlerCatchDisablesTheSnapshotUnderItsOwnName()
    {
        string monitor = Code("src", "SQCD.Agv.Wpf", "ScreenStateOutput", "HmiScreenStateMonitor.cs");
        string[] handlers = [.. Regex.Matches(monitor, @"\bvoid\s+(?<name>On\w+)\s*\(").Select(match => match.Groups["name"].Value)];
        Assert.NotEmpty(handlers);

        string[] offences =
        [
            .. handlers.Where(handler => !Regex.IsMatch(
                Body(monitor, handler)!,
                @"catch\s*\(\s*Exception\s+(?<v>\w+)\s*\)\s*\{\s*Disable\(\s*nameof\(\s*" + handler + @"\s*\)\s*,\s*\k<v>\s*\)\s*;\s*\}\s*\}\s*$"))
        ];

        Assert.True(offences.Length == 0, "These handlers' catch does not end in Disable(nameof(itself), …): " + string.Join(", ", offences));
    }

    [Fact]
    public void SubscriptionsAreTheOnMethodsByNameAndNothingIsQueuedOntoTheDispatcher()
    {
        string monitor = Code("src", "SQCD.Agv.Wpf", "ScreenStateOutput", "HmiScreenStateMonitor.cs");
        string[] targets = [.. Subscription.Matches(monitor).Select(match => match.Groups["target"].Value.Trim())];
        Assert.NotEmpty(targets);

        string[] offences =
        [
            .. targets
                .Where(target => !Regex.IsMatch(target, @"^(\w+\.)?On\w+$"))
                .Select(target => $"subscription to {target}"),
            .. Regex.Matches(monitor, @"\b(BeginInvoke|InvokeAsync)\b|\bDispatcher\.Invoke\b|\b_dispatcher\.Invoke\b")
                .Select(match => $"dispatcher queueing: {match.Value}"),
        ];

        Assert.True(offences.Length == 0, string.Join("\n", offences));
    }

    /// <summary>Research 4.3 item 1: the logger can throw, so each log write on the dispatcher path has its own catch.</summary>
    [Fact]
    public void EveryLogWriteOnTheDispatcherPathIsWrappedAgain()
    {
        string monitor = Code("src", "SQCD.Agv.Wpf", "ScreenStateOutput", "HmiScreenStateMonitor.cs");
        string app = Body(Code("src", "SQCD.Agv.Wpf", "App.xaml.cs"), "StartScreenState")!;
        string wiring = Code("src", "SQCD.Agv.Wpf", "ScreenStateOutput", "HmiScreenStateWiring.cs");

        string[] offences =
        [
            .. UnwrappedLogWrites(monitor).Select(index => $"HmiScreenStateMonitor.cs at offset {index}"),
            .. UnwrappedLogWrites(app).Select(index => $"App.StartScreenState at offset {index}"),
            // Called from App.StartScreenState's try today; wrapped on its own anyway (second review L2).
            .. UnwrappedLogWrites(wiring).Select(index => $"HmiScreenStateWiring.cs at offset {index}"),
        ];
        Assert.NotEmpty(Regex.Matches(wiring, @"logger\.Write\s*\("));

        Assert.NotEmpty(Regex.Matches(monitor, @"logger\.Write\s*\("));
        Assert.True(offences.Length == 0, "Log writes outside their own try/catch (Exception): " + string.Join(", ", offences));
    }

    /// <summary>
    /// The application starts the snapshot only through <c>HmiScreenStateWiring.Start</c>, handing it its own
    /// <c>screenState</c> and <c>logging</c> sections (second review N2): that method holds the switch and the parameter
    /// check, and <c>HmiScreenStateWiringTests</c> tests it, so a wiring that bypassed it, or passed it other settings,
    /// would leave both untested. Nothing under <c>src/</c> but the wiring starts a monitor or builds a writer.
    /// </summary>
    [Fact]
    public void TheApplicationStartsTheSnapshotOnlyThroughTheWiringWithItsOwnSettings()
    {
        string startScreenState = Body(Code("src", "SQCD.Agv.Wpf", "App.xaml.cs"), "StartScreenState")!;
        Assert.Matches(@"\bHmiScreenStateWiring\.Start\(\s*settings\.ScreenState\s*,\s*settings\.Logging\s*,", startScreenState);
        Assert.Single(Regex.Matches(startScreenState, @"\bHmiScreenStateWiring\.Start\("));

        string source = Path.Combine(ProtocolIdentityArchitectureTests.RepositoryRoot(), "src");
        string[] offences =
        [
            .. Directory.GetFiles(source, "*.cs", SearchOption.AllDirectories)
                .Where(file => Path.GetFileName(file) != "HmiScreenStateWiring.cs")
                // Only files that name either type can start or build one; the lexer refuses raw strings, which other
                // files under src/ use, and a file that names a type and uses a raw string still fails loudly here.
                .Where(file => File.ReadAllText(file) is var text
                    && (text.Contains("HmiScreenStateMonitor", StringComparison.Ordinal)
                        || text.Contains("HmiScreenStateFileWriter", StringComparison.Ordinal)))
                .SelectMany(file => StartsOrBuilds(
                    string.Join("\n", CSharpSourceLexer.Lex(File.ReadAllText(file)).Code),
                    Path.GetFileName(file)))
        ];

        Assert.Contains("App.xaml.cs", Directory.GetFiles(source, "*.cs", SearchOption.AllDirectories)
            .Where(file => File.ReadAllText(file).Contains("HmiScreenStateMonitor", StringComparison.Ordinal))
            .Select(Path.GetFileName));
        Assert.True(offences.Length == 0, "Only HmiScreenStateWiring may start a monitor or build a writer:\n" + string.Join("\n", offences));
    }

    [Theory]
    [InlineData("var monitor = HmiScreenStateMonitor.TryStart(vm, build);")]
    [InlineData("_screenStateWriter = new HmiScreenStateFileWriter(options, files, time, logger);")]
    [InlineData("HmiScreenStateFileWriter writer = new(options, files, time, logger);")]
    public void StartingOrBuildingOutsideTheWiringIsCaught(string code) =>
        Assert.NotEmpty(StartsOrBuilds(code, "App.xaml.cs"));

    private static IEnumerable<string> StartsOrBuilds(string code, string file) =>
        Regex.Matches(
                code,
                @"\bHmiScreenStateMonitor\.TryStart\s*\(|\bnew\s+HmiScreenStateFileWriter\s*\(|\bHmiScreenStateFileWriter\??\s+\w+\s*=\s*new\s*\(")
            .Select(match => $"{file}: {match.Value}");

    /// <summary>The checks themselves, on shapes they must refuse, so a broken pattern does not turn them into a pass.</summary>
    [Theory]
    [InlineData("void OnX(object s, EventArgs a) { _dirty = 1; }")]
    [InlineData("void OnX(object s, EventArgs a) { try { _dirty = 1; } catch (IOException f) { Disable(nameof(OnX), f); } }")]
    [InlineData("void OnX(object s, EventArgs a) { try { _dirty = 1; } catch (Exception f) { Disable(nameof(OnX), f); } _dirty = 2; }")]
    [InlineData("void OnX(object s, EventArgs a) { _dirty = Read(); try { } catch (Exception f) { Disable(nameof(OnX), f); } }")]
    public void AnEntryPointNotWrappedWholeIsCaught(string code) =>
        Assert.NotEmpty(WrappedWholeOffences(code, "OnX"));

    [Theory]
    [InlineData("void OnX(object s, EventArgs a) { try { _dirty = 1; } catch (Exception f) { Disable(nameof(OnX), f); } }")]
    [InlineData("static T? TryStart() { T? m = null; try { m = new(); return m; } catch (Exception f) { return null; } }")]
    public void AWrappedEntryPointPasses(string code) =>
        Assert.Empty(WrappedWholeOffences(code, code.Contains("TryStart", StringComparison.Ordinal) ? "TryStart" : "OnX"));

    [Theory]
    [InlineData("{ _logger.Write(a, b, c); }", 1)]
    [InlineData("{ try { _logger.Write(a, b, c); } catch (IOException) { } }", 1)]
    [InlineData("{ try { x(); } catch (Exception e) { _logger.Write(a, b, c, e); } }", 1)]
    [InlineData("{ try { _logger.Write(a, b, c); } catch (Exception) { } }", 0)]
    [InlineData("{ try { y(); } catch (Exception e) { try { logger.Write(a, b, c, e); } catch (Exception) { } } }", 0)]
    public void LogWritesAreCheckedForTheirOwnCatch(string code, int unwrapped) =>
        Assert.Equal(unwrapped, UnwrappedLogWrites(code).Length);

    internal static IEnumerable<string> WrappedWholeOffences(string code, string method)
    {
        string? body = Body(code, method);
        if (body is null)
        {
            yield return $"{method}: not found";
            yield break;
        }

        string inner = body[1..^1];
        int tryAt = Regex.Match(inner, @"\btry\s*\{").Index;
        if (!Regex.IsMatch(inner, @"\btry\s*\{"))
        {
            yield return $"{method}: no try";
            yield break;
        }

        string before = inner[..tryAt];
        if (!Regex.IsMatch(before, @"^\s*([\w?<>\[\]]+\s+\w+\s*=\s*(null|false|true|0|default)\s*;\s*)*$"))
        {
            yield return $"{method}: code before the try: {before.Trim()}";
        }

        int position = Matching(inner, inner.IndexOf('{', tryAt));
        string lastCatch = string.Empty;
        while (true)
        {
            Match next = Regex.Match(inner[(position + 1)..], @"^\s*(catch\b[^{]*|finally\s*)\{");
            if (!next.Success)
            {
                break;
            }

            lastCatch = next.Groups[1].Value;
            position = Matching(inner, position + 1 + next.Length - 1);
        }

        if (!Regex.IsMatch(lastCatch, @"^catch\s*\(\s*Exception(\s+\w+)?\s*\)\s*$"))
        {
            yield return $"{method}: the last catch is not catch (Exception …): '{lastCatch.Trim()}'";
        }

        string after = inner[(position + 1)..];
        if (after.Trim().Length > 0)
        {
            yield return $"{method}: code after the try/catch: {after.Trim()}";
        }
    }

    /// <summary>Offsets of logger writes not inside a <c>try</c> block whose catches include <c>catch (Exception …)</c>.</summary>
    internal static int[] UnwrappedLogWrites(string code)
    {
        List<(int Start, int End)> guarded = [];
        foreach (Match tryMatch in Regex.Matches(code, @"\btry\s*\{"))
        {
            int open = tryMatch.Index + tryMatch.Length - 1;
            int close = Matching(code, open);
            int position = close;
            bool catchesAll = false;
            while (true)
            {
                Match next = Regex.Match(code[(position + 1)..], @"^\s*(catch\b[^{]*|finally\s*)\{");
                if (!next.Success)
                {
                    break;
                }

                catchesAll |= Regex.IsMatch(next.Groups[1].Value, @"^catch\s*\(\s*Exception(\s+\w+)?\s*\)\s*$");
                position = Matching(code, position + 1 + next.Length - 1);
            }

            if (catchesAll)
            {
                guarded.Add((open, close));
            }
        }

        return
        [
            .. Regex.Matches(code, @"\b\w*logger\.Write\s*\(", RegexOptions.IgnoreCase)
                .Select(match => match.Index)
                .Where(index => !guarded.Any(block => index > block.Start && index < block.End))
        ];
    }

    /// <summary>The body of the named method, braces included; null when there is none.</summary>
    internal static string? Body(string code, string method)
    {
        Match header = Regex.Match(code, @"\b" + method + @"\s*\([^;{]*?\)\s*\{");
        if (!header.Success)
        {
            return null;
        }

        int open = header.Index + header.Length - 1;
        return code[open..(Matching(code, open) + 1)];
    }

    private static int Matching(string code, int open)
    {
        int depth = 0;
        for (int index = open; index < code.Length; index++)
        {
            depth += code[index] switch { '{' => 1, '}' => -1, _ => 0 };
            if (depth == 0)
            {
                return index;
            }
        }

        throw new InvalidOperationException("unbalanced braces");
    }

    internal static string Code(params string[] relativePath) =>
        string.Join(
            "\n",
            CSharpSourceLexer.Lex(File.ReadAllText(Path.Combine([ProtocolIdentityArchitectureTests.RepositoryRoot(), .. relativePath]))).Code);
}
