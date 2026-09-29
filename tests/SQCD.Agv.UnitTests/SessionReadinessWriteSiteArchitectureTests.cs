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
    /// dispose, receive failed, SessionReadiness. Kept literal: a scanner that stops finding calls reports nothing, which
    /// looks exactly like nothing wrong.
    /// </summary>
    private const int ExpectedPublishCalls = 7;

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
            bool literal = Regex.IsMatch(readiness, @"^WireToGateSessionReadiness\.\w+$")
                           && Regex.IsMatch(reasons, @"^\[.*\]$", RegexOptions.Singleline);
            bool fromServer = call.Member == "ApplySessionReadiness"
                              && readiness == "mappedReadiness"
                              && reasons == "readiness.ReasonCodes";
            Assert.True(
                literal || fromServer,
                $"{call.Member} publishes readiness '{readiness}' with reasons '{reasons}'. Readiness comes from the "
                + "server's SessionReadiness or is a lifecycle literal; to publish the current state again with new "
                + "versions, call PublishAcceptedVersions, which reads it under the lock (control-server#380).");
        });
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
        Assert.Equal(["Publish", "PublishAcceptedVersions"], callers);
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
    }

    private static string Source() => WithoutWholeLineComments(
        File.ReadAllText(Path.Combine(ProtocolIdentityArchitectureTests.RepositoryRoot(), ClientPath)));

    /// <summary>Every call of <c>Publish(</c> itself -- not its declaration, not the methods whose names start with it.</summary>
    private static (string Member, string[] Arguments)[] PublishCalls(string source) =>
    [
        .. Members(source).SelectMany(member => Regex.Matches(member.Body, @"(?<![\w.])Publish\(")
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
