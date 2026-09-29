using System.Text.RegularExpressions;

namespace SQCD.Agv.UnitTests;

/// <summary>
/// Where the session client's published readiness may come from: the server's SessionReadiness, or a lifecycle step that
/// names its own value -- never a copy of the session state read earlier (<c>trytoreachpeak0/8005-agv-control-server#380</c>).
/// </summary>
/// <remarks>
/// <para>
/// <b>The defect this pins.</b> After a mid-session safety message's ack, the waiter read <c>Current</c> and passed its
/// readiness to <c>Publish</c>. The receive loop applies the SessionReadiness the server appends after that ack on
/// another thread, so a read taken before it and written after put the old readiness back: CI real-rig run 36477303574,
/// where a cargo handoff's RECOVERY_REQUIRED was overwritten with READY and the fault cargo handoff entry never appeared.
/// The fix republishes through <c>PublishAcceptedVersions</c>, which reads the latest state and writes it under the lock
/// every publication takes.
/// </para>
/// <para>
/// <b>Why a text guard next to the behaviour tests.</b> <c>RecoveryVectorG2Tests.StaleReadinessRepublish</c> forces the
/// interleaving by holding the waiter where it reads the session clock, which is inside the publication. With the lock in
/// <c>Publish</c>, a republish that goes back to reading <c>Current</c> outside the lock is held there only after it
/// holds the lock -- the window it reopens, between its read and the lock, has no clock read to hold it at. Measured: with
/// both sites put back to the old read and the lock kept, those three tests stay green. This class is what goes red then.
/// </para>
/// <para>
/// <b>What it cannot see.</b> It reads text. A readiness value computed elsewhere and passed in under the name
/// <c>mappedReadiness</c>, a second class publishing session state, or a write to <c>_current</c> through something other
/// than <c>Volatile.Write(ref _current, ...)</c> would pass it. The guarantee rests on the construction -- one writer,
/// <c>PublishUnderGate</c>, reached only under the lock -- and this pins that construction's shape, not its semantics.
/// </para>
/// <para>
/// No <c>IntegrationSlice</c> trait: a cross-cutting guard, for the reason <see cref="IntegrationSliceTraitArchitectureTests"/> gives.
/// </para>
/// </remarks>
public sealed class SessionReadinessWriteSiteArchitectureTests
{
    private const string ClientPath = "src/SQCD.Agv.Infrastructure/WireToGateSessionClient.cs";

    /// <summary>
    /// The <c>Publish</c> calls in the client today: handshake start, session accepted, handshake failed, disconnect,
    /// dispose. Kept literal: a scanner that stops finding calls reports nothing, which looks exactly like nothing wrong.
    /// The server's readiness goes through <c>PublishServerReadiness</c>, a failed loop's finish through
    /// <c>PublishUnderGate</c> under the lock it holds for its generation check.
    /// </summary>
    private const int ExpectedPublishCalls = 5;

    [Fact]
    public void EveryPublishedReadinessIsTheServersOrALifecycleLiteral()
    {
        string source = Source();
        (string Member, string[] Arguments)[] calls = PublishCalls(source);

        Assert.Equal(ExpectedPublishCalls, calls.Length);
        Assert.All(calls, call =>
        {
            Assert.Equal(5, call.Arguments.Length);
            string readiness = call.Arguments[2];
            string reasons = call.Arguments[3];
            Assert.True(
                Regex.IsMatch(readiness, @"^WireToGateSessionReadiness\.\w+$")
                && Regex.IsMatch(reasons, @"^\[.*\]$", RegexOptions.Singleline),
                $"{call.Member} publishes readiness '{readiness}' with reasons '{reasons}'. Readiness comes from the "
                + "server's SessionReadiness (PublishServerReadiness) or is a lifecycle literal; to publish the current "
                + "state again with new versions, call PublishAcceptedVersions, which reads it under the lock "
                + "(control-server#380).");
        });

        (string Member, string[] Arguments) server = Assert.Single(Calls(source, "PublishServerReadiness"));
        Assert.Equal("ApplySessionReadiness", server.Member);
        Assert.Equal("mappedReadiness", server.Arguments[1]);
        Assert.Equal("readiness.ReasonCodes", server.Arguments[2]);
    }

    /// <summary>
    /// A line a receive loop read is published only while that loop is the live one, tested under the lock; the loop
    /// passes its own generation, and it is live before it runs (control-server#380, a replaced loop's readiness).
    /// </summary>
    [Fact]
    public void AReceiveLoopsReadinessIsPublishedOnlyWhileThatLoopIsLive()
    {
        string source = Source();

        string publish = Member(source, "PublishServerReadiness");
        int gate = publish.IndexOf("lock (_stateGate)", StringComparison.Ordinal);
        int live = publish.IndexOf("Interlocked.Read(ref _receiveLoopGeneration) != loop", StringComparison.Ordinal);
        int write = publish.IndexOf("PublishUnderGate(", StringComparison.Ordinal);
        int advance = publish.IndexOf("AdvanceSafetyStateVersion(", StringComparison.Ordinal);
        Assert.True(gate >= 0 && gate < live && live < advance && advance < write,
            "PublishServerReadiness must test the loop's generation under the lock before it moves the accepted safety "
            + "version or publishes.");

        (string Member, string[] Arguments)[] applies = Calls(source, "ApplySessionReadiness");
        Assert.Equal(
            ["ConnectAndRecoverAsync:receiveLoopGeneration: null", "ReceiveLoopAsync:generation"],
            applies.Select(call => $"{call.Member}:{call.Arguments[2]}").Order(StringComparer.Ordinal).ToArray());

        string start = Member(source, "StartReceiveLoop");
        Assert.True(
            start.IndexOf("Interlocked.Exchange(ref _receiveLoopGeneration, generation)", StringComparison.Ordinal)
                < start.IndexOf("Task.Run(", StringComparison.Ordinal),
            "StartReceiveLoop runs the loop before its generation is live; its first readiness line would be dropped.");
    }

    [Fact]
    public void TheSessionStateIsWrittenInOnePlaceAndOnlyUnderTheLock()
    {
        string source = Source();

        Assert.Equal(1, Regex.Count(source, @"Volatile\.Write\(ref _current,"));
        string writer = Member(source, "PublishUnderGate");
        Assert.Contains("Volatile.Write(ref _current,", writer, StringComparison.Ordinal);

        string[] callers = [.. Members(source)
            .Where(member => member.Name != "PublishUnderGate")
            .SelectMany(member => Enumerable.Repeat(member.Name, Regex.Count(member.Body, @"\bPublishUnderGate\(")))
            .Order(StringComparer.Ordinal)];
        Assert.Equal(["FinishFailedSession", "Publish", "PublishAcceptedVersions", "PublishServerReadiness"], callers);
        foreach (string caller in callers)
        {
            string body = Member(source, caller);
            int gate = body.IndexOf("lock (_stateGate)", StringComparison.Ordinal);
            int call = body.IndexOf("PublishUnderGate(", StringComparison.Ordinal);
            Assert.True(gate >= 0 && gate < call, $"{caller} reaches PublishUnderGate outside lock (_stateGate).");
        }

        string republish = Member(source, "PublishAcceptedVersions");
        int read = republish.IndexOf("_current", StringComparison.Ordinal);
        Assert.True(
            read > republish.IndexOf("lock (_stateGate)", StringComparison.Ordinal),
            "PublishAcceptedVersions reads the session state before it holds the lock.");

        // A failed loop's finish publishes through PublishUnderGate, not Publish, so the literal check above does not see
        // it (independent incremental review S-2): its readiness and reasons are held to the same rule here.
        (string Member, string[] Arguments) finishPublish = Assert.Single(
            Calls(source, "PublishUnderGate"), call => call.Member == "FinishFailedSession");
        Assert.Matches(@"^WireToGateSessionReadiness\.\w+$", finishPublish.Arguments[2]);
        Assert.Matches(@"^\[.*\]$", finishPublish.Arguments[3]);

        // A failed loop's finish checks its generation under the same lock it publishes under (independent review S3/S-6):
        // taken apart, a reconnect can complete between the two and the finish's Disconnected lands on the new session.
        string finish = Member(source, "FinishFailedSession");
        int finishGate = finish.IndexOf("lock (_stateGate)", StringComparison.Ordinal);
        int check = finish.IndexOf("Interlocked.CompareExchange(ref _receiveLoopGeneration", StringComparison.Ordinal);
        Assert.True(
            finishGate >= 0 && finishGate < check,
            "FinishFailedSession checks its generation outside lock (_stateGate).");
    }

    /// <summary>
    /// The call scanner sees a call written through <c>this.</c> as well as a bare one (independent review S-1): the old
    /// republish written as <c>this.Publish(current.Connected, ...)</c> passed every guard here and every G2 test.
    /// </summary>
    [Fact]
    public void TheCallScannerSeesCallsThroughThis()
    {
        const string sample = """
            internal sealed class Sample
            {
                private void Republish()
                {
                    WireToGateSessionSnapshot current = Current;
                    this.Publish(current.Connected, current.SessionGeneration, current.Readiness, current.ReasonCodes, "x");
                }

                private void Lifecycle()
                {
                    Publish(false, null, WireToGateSessionReadiness.Disconnected, [], "y");
                    other.Publish(1, 2, 3, 4, 5);
                }

                private void Publish(bool connected, long? generation, object readiness, object reasonCodes, string source)
                {
                }
            }
            """;

        (string Member, string[] Arguments)[] calls = PublishCalls(sample);

        Assert.Equal(
            ["Lifecycle:WireToGateSessionReadiness.Disconnected", "Republish:current.Readiness"],
            calls.Select(call => $"{call.Member}:{call.Arguments[2]}").Order(StringComparer.Ordinal).ToArray());
    }

    /// <summary>
    /// Nothing in the product marshals synchronously onto the UI thread (independent review S1). The session client raises
    /// StateChanged, and a failed loop's finish JourneyChanged, while holding <c>_stateGate</c>; that is safe only while no
    /// subscriber waits on another thread. <c>Dispatcher.Invoke</c> is exactly such a wait: a handler that used it would
    /// hold the lock until the UI thread ran, and a UI thread about to publish session state would wait for the lock.
    /// <c>BeginInvoke</c> and <c>InvokeAsync</c> queue and return.
    /// </summary>
    [Fact]
    public void NothingInTheProductMarshalsSynchronouslyOntoTheUiThread()
    {
        string root = Path.Combine(ProtocolIdentityArchitectureTests.RepositoryRoot(), "src");
        string[] files = [.. Directory.EnumerateFiles(root, "*.cs", SearchOption.AllDirectories)
            .Where(file => !file.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
                && !file.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal))];
        Assert.Contains(files, file => file.EndsWith("MainViewModel.cs", StringComparison.Ordinal));

        string[] synchronous = [.. files
            .Where(file => SynchronousDispatches(WithoutWholeLineComments(File.ReadAllText(file))) > 0)
            .Select(file => Path.GetRelativePath(root, file))];

        Assert.Empty(synchronous);
    }

    /// <summary>The scanner above tells a synchronous dispatch from the queued ones, whichever way it is written.</summary>
    [Fact]
    public void TheDispatchScannerTellsInvokeFromBeginInvokeAndInvokeAsync()
    {
        Assert.Equal(1, SynchronousDispatches("Application.Current.Dispatcher.Invoke(() => Refresh());"));
        Assert.Equal(1, SynchronousDispatches("dispatcher.Invoke(action);"));
        Assert.Equal(1, SynchronousDispatches("Dispatcher . Invoke (action, DispatcherPriority.Send);"));
        // Independent incremental review S-1: the scanner once began with a word boundary, which fails after the
        // underscore of a field name, and did not allow the null-conditional between the object and Invoke.
        Assert.Equal(1, SynchronousDispatches("Application.Current?.Dispatcher?.Invoke(() => Refresh());"));
        Assert.Equal(1, SynchronousDispatches("_uiDispatcher.Invoke(action);"));
        Assert.Equal(0, SynchronousDispatches("_uiDispatcher?.BeginInvoke(action);"));
        Assert.Equal(0, SynchronousDispatches("_ = dispatcher.BeginInvoke(action);"));
        Assert.Equal(0, SynchronousDispatches("_ = Dispatcher.InvokeAsync(() => Focus());"));
        Assert.Equal(0, SynchronousDispatches("StateChanged?.Invoke(this, args);"));
    }

    private static int SynchronousDispatches(string source) =>
        Regex.Count(source, @"dispatcher\s*\??\s*\.\s*Invoke\s*\(", RegexOptions.IgnoreCase);

    private static string Source() => WithoutWholeLineComments(
        File.ReadAllText(Path.Combine(ProtocolIdentityArchitectureTests.RepositoryRoot(), ClientPath)));

    /// <summary>Every call of <c>Publish(</c> itself -- not its declaration, not the methods whose names start with it.</summary>
    private static (string Member, string[] Arguments)[] PublishCalls(string source) => Calls(source, "Publish");

    /// <summary>
    /// Every call of the method named <paramref name="name"/> on this instance -- bare, or through <c>this.</c> -- with the
    /// member it is made in; not its declaration, and not a same-named method of another object.
    /// </summary>
    private static (string Member, string[] Arguments)[] Calls(string source, string name) =>
    [
        .. Members(source).SelectMany(member => Regex.Matches(
                member.Body, $@"(?:(?<=\bthis\.)|(?<![\w.])){Regex.Escape(name)}\(")
            .Where(match => !member.Body[..match.Index].EndsWith("void ", StringComparison.Ordinal))
            .Select(match => (member.Name, Arguments(member.Body, match.Index + match.Length))))
    ];

    /// <summary>The top-level arguments of the call whose opening parenthesis ends just before <paramref name="start"/>.</summary>
    private static string[] Arguments(string text, int start)
    {
        List<string> arguments = [];
        int depth = 0;
        int from = start;
        for (int i = start; i < text.Length; i++)
        {
            char c = text[i];
            if (c is '(' or '[' or '{')
            {
                depth++;
            }
            else if (c is ')' or ']' or '}')
            {
                if (depth == 0)
                {
                    arguments.Add(text[from..i].Trim());
                    return [.. arguments];
                }

                depth--;
            }
            else if (c == ',' && depth == 0)
            {
                arguments.Add(text[from..i].Trim());
                from = i + 1;
            }
        }

        throw new InvalidDataException("Unbalanced call.");
    }

    private sealed record SourceMember(string Name, string Body);

    /// <summary>The file cut at each member declared at class level (four spaces in).</summary>
    private static List<SourceMember> Members(string source)
    {
        MatchCollection declarations = Regex.Matches(
            source, @"^    (?:private|public|internal|protected)\b[^\n(=]*?\b(?<name>\w+)\s*\(", RegexOptions.Multiline);
        List<SourceMember> members = [];
        for (int i = 0; i < declarations.Count; i++)
        {
            int start = declarations[i].Index;
            int end = i + 1 < declarations.Count ? declarations[i + 1].Index : source.Length;
            members.Add(new SourceMember(declarations[i].Groups["name"].Value, source[start..end]));
        }

        return members;
    }

    private static string Member(string source, string name) =>
        Assert.Single(Members(source), member => member.Name == name).Body;

    private static string WithoutWholeLineComments(string source) =>
        Regex.Replace(source, @"^[ \t]*//.*$", string.Empty, RegexOptions.Multiline);
}
