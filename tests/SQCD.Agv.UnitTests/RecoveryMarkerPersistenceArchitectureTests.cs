using System.Text.RegularExpressions;

namespace SQCD.Agv.UnitTests;

/// <summary>
/// The machine guard on "the recovery-announcement mark and the owed recovery entry never outlive the process"
/// (<c>trytoreachpeak0/8005-agv-onboard-hmi#162</c>).
/// </summary>
/// <remarks>
/// <para>
/// <b>What it protects.</b> <c>_recoveryAnnouncedAttemptId</c> and <c>_owedRecoveryEntry</c> in
/// <c>WireToGateBusinessService</c> are process state. After a restart the restore is the operator's only way
/// back to the recovery entry; a mark carried over the restart makes it publish nothing. That is
/// onboard-hmi#109's failure: CI and every unit test green, the entry gone, and only the real-onboard recovery
/// scenario seeing it. Before this class the line was held by warnings in two tickets and a doc comment.
/// </para>
/// <para>
/// <b>A mark survives a restart only in two steps, and this guards both.</b> Something writes it out, and
/// something reads it back into the field at startup. Writing out alone is harmless -- the attempt id is in the
/// journal anyway -- and reading back has to go through a write to the field. So:
/// </para>
/// <list type="bullet">
/// <item><b>The read-back side</b>: each field has one writer
/// (<see cref="EachMarkIsWrittenOnlyByItsOneWriter"/>), and every member that can reach a writer is a registered
/// caller with a stated reason (<see cref="EveryWayToSetAMarkHasARegisteredCaller"/>). A startup path that
/// restores a mark has to add a caller, and a new caller is red until somebody answers where its value comes
/// from.</item>
/// <item><b>The write-out side</b>: neither name, nor the name of any member that sets them, appears anywhere in
/// the product outside <c>WireToGateBusinessService.cs</c>
/// (<see cref="TheMarksAndTheirSettersNeverLeaveTheBusinessServiceSource"/>), inside that file only registered
/// members mention the fields (<see cref="OnlyRegisteredMembersTouchTheMarks"/>), and none of those members
/// names a persistence API (<see cref="NoMemberThatTouchesAMarkNamesAPersistenceApi"/>).</item>
/// </list>
/// <para>
/// <b>Why the file bound alone is not enough, although the ticket offered it as the hardest check.</b> Its
/// argument is that passing the value out needs the name somewhere else. That holds for other files; it does not
/// hold inside the file, which already hands values read from <c>_owedRecoveryEntry</c> to <c>_logger.Write</c>.
/// A journal call written in the same file keeps the name in the same file. The member registry and the
/// persistence-API check are what close that.
/// </para>
/// <para>
/// <b>The conditions the file bound rests on</b>, checked for this class rather than assumed from
/// onboard-hmi#176 (the coordinator asked for the same list): the fields are <c>private</c>, so only the class can
/// touch them -- <b>but the class is <c>partial</c></b>, with three more files
/// (<c>.HardwareRecovery</c>, <c>.ManualChargingReturn</c>, <c>.RecoveryVectors</c>). Those can read the fields
/// directly; the file bound catches that, because their names would then appear there. It is <c>sealed</c>, so no
/// subclass. Private fields are not bindable from XAML. No product code under <c>src/</c> or <c>tools/</c> uses
/// <c>BindingFlags.NonPublic</c>, <c>GetField</c> or <c>UnsafeAccessor</c> (2026-09-21), and a reflective read
/// would name the field in a string, which the file bound also sees. No source generator contributes to the class.
/// </para>
/// <para>
/// <b>What it cannot see.</b> It follows names, not values. A registered member that copies a mark into a local
/// and hands it to a method with an innocent name, which persists it somewhere else, passes every check here --
/// that case is pinned in <see cref="TheseGuardsTellALeakFromTheCodeAsItIs"/> as a known blind spot. The
/// persistence-API list is a list: an API whose name matches none of its words slips through.
/// <c>_logger.Write</c> is allowed on a premise, not a check: the technical log is written and never read back
/// into process state (nothing under <c>src/</c> reads it, 2026-09-21). Member boundaries come from
/// <c>dotnet format</c>'s four-space member indent.
/// </para>
/// <para>
/// <b>No assertion here is a count.</b> Every registry is compared as a set with what the source has, in both
/// directions: a new member is red, and a registered member that no longer does the thing is red too, so the
/// registry cannot go stale silently -- and a scanner gone blind finds nothing, which is not the registry.
/// </para>
/// </remarks>
public sealed class RecoveryMarkerPersistenceArchitectureTests
{
    private const string BusinessServicePath = "src/SQCD.Agv.Wpf/WireToGateBusinessService.cs";

    /// <summary>A mark and its one writer.</summary>
    private sealed record Marker(string Field, string Writer);

    private static readonly Marker[] Markers =
    [
        new("_recoveryAnnouncedAttemptId", "MarkRecoveryAnnounced"),
        new("_owedRecoveryEntry", "ExchangeOwedRecoveryEntry")
    ];

    /// <summary>
    /// Every member that sets a mark, directly or by calling one that does. A caller of any of these is a way to
    /// put a value into a mark.
    /// </summary>
    private static readonly string[] Setters =
    [
        "MarkRecoveryAnnounced",
        "TryClaimRecoveryAnnouncement",
        "ExchangeOwedRecoveryEntry",
        "OweRecoveryEntry",
        "ForgetOwedRecoveryEntry",
        "PublishOwedRecoveryEntry"
    ];

    /// <summary>One registered call into a setter, and why the value it passes cannot be a restored mark.</summary>
    private sealed record Caller(string Setter, string Member, string Why);

    private static readonly Caller[] Callers =
    [
        new(
            "MarkRecoveryAnnounced",
            "TryClaimRecoveryAnnouncement",
            "The claim itself: the caller is about to publish the announcement now, in this process."),
        new(
            "MarkRecoveryAnnounced",
            "TrySettleInterruptedOperationAsync",
            "Marks the attempt just before this process publishes its OPERATION_RECOVERY_REQUIRED."),
        new(
            "MarkRecoveryAnnounced",
            "HandleSlotOperationAsync",
            "Marks the attempt this process just executed, just before publishing its recovery."),
        new(
            "TryClaimRecoveryAnnouncement",
            "RestorePendingRecoveryOperationProjectionAsync",
            "The attempt id comes from the journal -- but the restore claims in order to publish, so the mark it "
            + "sets means 'announced in this process', which is then true. Setting it without publishing would "
            + "be the restored mark this class exists to stop."),
        new(
            "ExchangeOwedRecoveryEntry",
            "OweRecoveryEntry",
            "Owes the entry the restore of this process could not put on screen."),
        new(
            "ExchangeOwedRecoveryEntry",
            "ForgetOwedRecoveryEntry",
            "Clears the debt; clearing cannot restore anything."),
        new(
            "ExchangeOwedRecoveryEntry",
            "PublishOwedRecoveryEntry",
            "Clears the debt as it is paid."),
        new(
            "OweRecoveryEntry",
            "RestorePendingRecoveryOperationProjectionAsync",
            "The context is the journal's, but the debt is created by this process's restore finding the doors "
            + "taken, and lives only until it is paid."),
        new(
            "ForgetOwedRecoveryEntry",
            "RestorePendingRecoveryOperationProjectionAsync",
            "Clears the debt when the attempt no longer needs an entry."),
        new(
            "PublishOwedRecoveryEntry",
            "RestorePendingRecoveryOperationProjectionAsync",
            "Pays the debt; paying reads the mark and clears it."),
        new(
            "PublishOwedRecoveryEntry",
            "ReleaseInFlightAttempt",
            "Pays the debt at the release of the last claim (onboard-hmi#156).")
    ];

    /// <summary>The members of <c>WireToGateBusinessService.cs</c> whose code may mention each field.</summary>
    private static readonly (string Field, string Member)[] Readers =
    [
        ("_recoveryAnnouncedAttemptId", "MarkRecoveryAnnounced"),
        ("_recoveryAnnouncedAttemptId", "TryClaimRecoveryAnnouncement"),
        ("_recoveryAnnouncedAttemptId", "PublishOwedRecoveryEntry"),
        ("_owedRecoveryEntry", "ExchangeOwedRecoveryEntry"),
        ("_owedRecoveryEntry", "ForgetOwedRecoveryEntry"),
        ("_owedRecoveryEntry", "PublishOwedRecoveryEntry")
    ];

    /// <summary>
    /// Words a persistence API is named with here: the journal, JSON, files and streams, the outbox, SQLite,
    /// settings. Deliberately wide -- a false red in a member that touches a mark costs one look.
    /// </summary>
    /// <remarks>
    /// <b>Case-insensitive, and that is not cosmetic.</b> The first version matched <c>Journal</c> only with a
    /// capital: the synthetic leak <c>_journal.WriteRecoveryMarkAsync(...)</c> -- the way a journal field is actually
    /// named -- passed it. The reverse check caught that before anything else did.
    /// </remarks>
    private static readonly Regex PersistenceApiRegex = new(
        @"\b\w*(?:Journal|Serializ|Persist|Sqlite|Outbox|AtomicJsonFile|Database)\w*\b"
        + @"|\bFile\.|\b\w*Stream\w*\b|\bSave\w*\s*\(|\bStore\w*\s*\(|\bSettings\b",
        RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);

    private static readonly Regex BlockCommentRegex = new(
        @"/\*.*?\*/",
        RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.Singleline);

    private static readonly Regex MemberBlockEndRegex = new(
        @"^    \}\s*$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Regex MemberExpressionEndRegex = new(
        @"^    \S.*;\s*$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Regex MemberSignatureRegex = new(
        @"^    [A-Za-z].*?\b(?<name>\w+)\s*(?:<[^>]*>)?\s*\(",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    /// <summary>Where product code lives. <c>tests/</c> is left out: the guards themselves name every token.</summary>
    private static readonly string[] ProductDirectories = ["src", "tools"];

    private static readonly string[] ConfinedExtensions = [".cs", ".xaml", ".json", ".config", ".xml", ".resx"];

    /// <summary>
    /// Each mark is written in exactly one member, its writer. "Written" is every shape: plain and compound
    /// assignment, deconstruction, and handing the field out by <c>ref</c>/<c>out</c> (onboard-hmi#176 learned
    /// the last one the hard way: every setter in the view model is <c>SetProperty(ref _field, value)</c>, so it
    /// is the shape a copied line takes). An initializer on the declaration is a write outside the writer too.
    /// </summary>
    [Fact]
    public void EachMarkIsWrittenOnlyByItsOneWriter()
    {
        Write[] writes = Writes(ReadProductFile(BusinessServicePath));
        Write[] stray = [.. writes.Where(write => write.Member != WriterOf(write.Field))];

        Assert.True(
            stray.Length == 0,
            "A mark is written outside its one writer:"
            + string.Concat(stray.Select(write =>
                $"{Environment.NewLine}  {BusinessServicePath}:{write.Line}  {write.Member}  {write.Field}"))
            + $"{Environment.NewLine}Route it through the writer. The writer is not a style rule: it is what makes every "
            + "way of setting the mark a caller the next test can see, and a mark restored at startup would show up "
            + "exactly as a new way of setting it (onboard-hmi#162, onboard-hmi#109).");

        foreach (Marker marker in Markers)
        {
            Assert.True(
                writes.Any(write => write.Field == marker.Field && write.Member == marker.Writer),
                $"{marker.Writer} does not write {marker.Field}. Either the writer moved -- update {nameof(Markers)} "
                + "to the new one -- or the scanner no longer sees writes, and every other check here is blind.");
        }
    }

    /// <summary>
    /// Every call into a setter comes from a registered member, and every registered call is still there.
    /// This is the read-back side: bringing a mark back after a restart needs a new call.
    /// </summary>
    [Fact]
    public void EveryWayToSetAMarkHasARegisteredCaller()
    {
        (string Setter, string Member)[] actual = Calls(ReadProductFile(BusinessServicePath));
        (string Setter, string Member)[] registered = [.. Callers.Select(caller => (caller.Setter, caller.Member))];

        (string Setter, string Member)[] added = [.. actual.Except(registered)];
        (string Setter, string Member)[] gone = [.. registered.Except(actual)];

        Assert.True(
            added.Length == 0 && gone.Length == 0,
            "The ways of setting a recovery mark changed."
            + string.Concat(added.Select(pair =>
                $"{Environment.NewLine}  new:  {pair.Member} -> {pair.Setter}"))
            + string.Concat(gone.Select(pair =>
                $"{Environment.NewLine}  gone: {pair.Member} -> {pair.Setter}"))
            + $"{Environment.NewLine}**Do not just register a new caller.** First answer: where does the value it "
            + "passes come from, and is it this process that is about to publish that recovery? If it can come from "
            + "anything that outlived the process -- the journal, a file, settings, a server replay -- without this "
            + "process publishing, it is a restored mark, and the operator loses the recovery entry after a restart "
            + "with CI green (onboard-hmi#109). Only then add it to Callers, with that answer as its reason. "
            + "A caller that is gone: remove its line, so the registry says what the code does.");
    }

    /// <summary>
    /// Neither field, nor any member that sets one, is named anywhere in the product outside
    /// <c>WireToGateBusinessService.cs</c>: not in the other three partial files, not in any other class, not as a
    /// string (a reflective read, a JSON key), not in XAML or configuration. Comments in other <c>.cs</c> files do
    /// not count -- a remark that mentions a field leaks nothing.
    /// </summary>
    [Fact]
    public void TheMarksAndTheirSettersNeverLeaveTheBusinessServiceSource()
    {
        string root = ProtocolIdentityArchitectureTests.RepositoryRoot();
        (string Path, string Text)[] files =
        [
            .. ProductDirectories
                .SelectMany(directory => Directory.EnumerateFiles(
                    Path.Combine(root, directory), "*", SearchOption.AllDirectories))
                .Where(path => ConfinedExtensions.Contains(Path.GetExtension(path), StringComparer.OrdinalIgnoreCase))
                .Select(path => Path.GetRelativePath(root, path).Replace('\\', '/'))
                .Where(relative => !relative.Contains("/bin/", StringComparison.Ordinal)
                    && !relative.Contains("/obj/", StringComparison.Ordinal))
                .Select(relative => (relative, File.ReadAllText(Path.Combine(root, relative))))
        ];

        Assert.Contains(files, file => file.Path == BusinessServicePath);
        Assert.Contains(files, file => file.Path.EndsWith("WireToGateBusinessService.HardwareRecovery.cs", StringComparison.Ordinal));

        (string Path, string Token)[] leaks = OutsideMentions(files, BusinessServicePath);

        Assert.True(
            leaks.Length == 0,
            "A recovery mark, or a member that sets one, is named outside WireToGateBusinessService.cs:"
            + string.Concat(leaks.Select(leak => $"{Environment.NewLine}  {leak.Path}  {leak.Token}"))
            + $"{Environment.NewLine}These are process state and must stay in that file. Taking one elsewhere is how it "
            + "gets logged into a projection, a snapshot, the journal or a JSON file -- and a mark that survives a "
            + "restart takes the recovery entry off the operator's screen with CI green (onboard-hmi#109).");
    }

    /// <summary>
    /// Inside <c>WireToGateBusinessService.cs</c>, the code that mentions a field is exactly the registered set
    /// of members. The file bound cannot see a journal call written in the same file; this narrows the question
    /// to a handful of members, and the next test asks it of them.
    /// </summary>
    [Fact]
    public void OnlyRegisteredMembersTouchTheMarks()
    {
        (string Field, string Member)[] actual = Mentions(
            ReadProductFile(BusinessServicePath),
            [.. Markers.Select(marker => marker.Field)]);

        (string Field, string Member)[] added = [.. actual.Except(Readers)];
        (string Field, string Member)[] gone = [.. Readers.Except(actual)];

        Assert.True(
            added.Length == 0 && gone.Length == 0,
            "The members that touch a recovery mark changed."
            + string.Concat(added.Select(pair => $"{Environment.NewLine}  new:  {pair.Member} uses {pair.Field}"))
            + string.Concat(gone.Select(pair => $"{Environment.NewLine}  gone: {pair.Member} no longer uses {pair.Field}"))
            + $"{Environment.NewLine}Before registering a new one, answer: does anything it passes the value to "
            + "outlive the process? Registering puts the member under the persistence check below; it does not "
            + "make the member safe.");
    }

    /// <summary>
    /// No member that touches a mark names a persistence API. <c>_logger.Write</c> is allowed: the technical log is
    /// written and never read back into process state.
    /// </summary>
    [Fact]
    public void NoMemberThatTouchesAMarkNamesAPersistenceApi()
    {
        string source = ReadProductFile(BusinessServicePath);
        string[] members = [.. Readers.Select(reader => reader.Member).Concat(Markers.Select(marker => marker.Writer)).Distinct()];

        (string Member, string Api)[] hits = PersistenceApis(source, members);

        Assert.True(
            hits.Length == 0,
            "A member that touches a recovery mark names a persistence API:"
            + string.Concat(hits.Select(hit => $"{Environment.NewLine}  {hit.Member}  {hit.Api}"))
            + $"{Environment.NewLine}If the mark's value reaches it, the mark can outlive the process. If it does not, "
            + "move the call out of this member rather than widening the check.");

        Assert.All(members, member => Assert.True(
            MemberSpans(StripComments(source)).Any(span => span.Name == member),
            $"{member} is registered but not found in {BusinessServicePath}; this check would pass over nothing."));
    }

    /// <summary>
    /// The checks above say red on each way to break the line, and not on the code as it is -- two answers, or they
    /// have no discrimination. Every check is a pure function of source text, so this feeds them synthetic source.
    /// </summary>
    [Fact]
    public void TheseGuardsTellALeakFromTheCodeAsItIs()
    {
        // ---- Writes: the one writer is allowed, every other shape and place is not. ----
        Assert.Empty(StrayWrites(
            """
                private void MarkRecoveryAnnounced(string attemptId)
                {
                    lock (_gate)
                    {
                        _recoveryAnnouncedAttemptId = attemptId;
                    }
                }
            """));
        Assert.Single(StrayWrites(
            """
                private void RestoreMarksAtStartup(string restored)
                {
                    _recoveryAnnouncedAttemptId = restored;
                }
            """));
        Assert.Single(StrayWrites(
            """
                private void PublishOwedRecoveryEntry()
                {
                    Interlocked.Exchange(ref _owedRecoveryEntry, null);
                }
            """));
        Assert.Single(StrayWrites(
            """
                private void RestoreMarksAtStartup(string restored)
                {
                    _recoveryAnnouncedAttemptId ??= restored;
                }
            """));
        Assert.Single(StrayWrites(
            """
                private void RestoreMarksAtStartup(string restored)
                {
                    (_recoveryAnnouncedAttemptId, _other) = (restored, 1);
                }
            """));
        Assert.Single(StrayWrites(
            """
                private string? _recoveryAnnouncedAttemptId = LoadFromDisk();
            """));
        // A writer that exists only in a comment does not make the real write legal.
        Assert.Single(StrayWrites(
            """
                private void RestoreMarksAtStartup(string restored)
                {
                    /* MarkRecoveryAnnounced(restored); */
                    _recoveryAnnouncedAttemptId = restored;
                }
            """));

        // ---- Callers: a new way into a setter is seen, as a call or as a method group; a comment is not. ----
        Assert.Contains(
            ("MarkRecoveryAnnounced", "RestoreMarksAtStartup"),
            Calls(ClassBody(
                """
                    private void RestoreMarksAtStartup(string restored)
                    {
                        MarkRecoveryAnnounced(restored);
                    }
                """)));
        Assert.Contains(
            ("ExchangeOwedRecoveryEntry", "Wire"),
            Calls(ClassBody(
                """
                    private void Wire()
                    {
                        _onRestore = ExchangeOwedRecoveryEntry;
                    }
                """)));
        Assert.Empty(Calls(ClassBody(
            """
                private void Wire()
                {
                    // MarkRecoveryAnnounced(restored) used to be called here.
                }
            """)));

        // ---- Mentions inside the file. ----
        Assert.Contains(
            ("_owedRecoveryEntry", "SnapshotForDiagnostics"),
            Mentions(
                ClassBody(
                    """
                        private object SnapshotForDiagnostics()
                        {
                            return new { Owed = _owedRecoveryEntry };
                        }
                    """),
                ["_owedRecoveryEntry"]));
        Assert.Empty(Mentions(
            ClassBody(
                """
                    private string? _owedRecoveryEntry;
                """),
            ["_owedRecoveryEntry"]));

        // ---- Persistence APIs in a member that touches a mark; the logger is not one. ----
        Assert.NotEmpty(PersistenceApis(
            ClassBody(
                """
                    private void MarkRecoveryAnnounced(string attemptId)
                    {
                        _recoveryAnnouncedAttemptId = attemptId;
                        _journal.WriteRecoveryMarkAsync(attemptId);
                    }
                """),
            ["MarkRecoveryAnnounced"]));
        Assert.NotEmpty(PersistenceApis(
            ClassBody(
                """
                    private void PublishOwedRecoveryEntry()
                    {
                        string saved = JsonSerializer.Serialize(_owedRecoveryEntry);
                    }
                """),
            ["PublishOwedRecoveryEntry"]));
        Assert.Empty(PersistenceApis(
            ClassBody(
                """
                    private void ForgetOwedRecoveryEntry(string attemptId)
                    {
                        _ = ExchangeOwedRecoveryEntry(null);
                        _logger.Write(LogSeverity.Information, nameof(WireToGateBusinessService), attemptId);
                    }
                """),
            ["ForgetOwedRecoveryEntry"]));

        // ---- Outside the file: other files, strings and config are leaks; a comment is not. ----
        const string Home = "src/SQCD.Agv.Wpf/WireToGateBusinessService.cs";
        Assert.Single(OutsideMentions(
            [(Home, "_recoveryAnnouncedAttemptId"),
             ("src/SQCD.Agv.Wpf/WireToGateBusinessService.HardwareRecovery.cs", "var mark = _recoveryAnnouncedAttemptId;")],
            Home));
        Assert.Single(OutsideMentions(
            [("src/SQCD.Agv.Wpf/Diagnostics.cs", "type.GetField(\"_owedRecoveryEntry\", flags)")],
            Home));
        Assert.Single(OutsideMentions(
            [("src/SQCD.Agv.Wpf/appsettings.json", "{ \"_recoveryAnnouncedAttemptId\": null }")],
            Home));
        Assert.Single(OutsideMentions(
            [("src/SQCD.Agv.Wpf/WireToGateBusinessService.RecoveryVectors.cs", "PublishOwedRecoveryEntry();")],
            Home));
        Assert.Empty(OutsideMentions(
            [("src/SQCD.Agv.Wpf/Other.cs", "// unlike _owedRecoveryEntry, this survives a restart")],
            Home));

        // ---- Known blind spot, pinned at its current answer. ----
        // A registered member copies the mark into a local and hands it to a method whose name says nothing of
        // persistence. If that method writes to disk, the mark leaks, and every check here stays green: they follow
        // names, not values. Telling this apart needs data flow. When the scanner learns it, this assertion
        // fails -- move the case up among the leaks then.
        Assert.Empty(PersistenceApis(
            ClassBody(
                """
                    private void PublishOwedRecoveryEntry()
                    {
                        OwedRecoveryEntry? owed = _owedRecoveryEntry;
                        Remember(owed);
                    }
                """),
            ["PublishOwedRecoveryEntry"]));
    }

    // ------------------------------------------------------------------------------------------------------------
    // The scanner. Every function here is a pure function of source text, so the test above can feed it.
    // ------------------------------------------------------------------------------------------------------------

    private sealed record Write(string Field, string Member, int Line);

    private sealed record MemberSpan(string Name, int Start, int End);

    private static string WriterOf(string field) => Markers.Single(marker => marker.Field == field).Writer;

    private static Write[] StrayWrites(string members) =>
        [.. Writes(ClassBody(members)).Where(write => write.Member != WriterOf(write.Field))];

    /// <summary>Every write to a mark, in every shape, with the member it sits in.</summary>
    private static Write[] Writes(string source)
    {
        string[] lines = StripComments(source);
        List<Write> writes = [];
        foreach (MemberSpan member in MemberSpans(lines))
        {
            for (int index = member.Start; index <= member.End; index++)
            {
                foreach (Marker marker in Markers)
                {
                    if (WriteShapes(marker.Field).Any(shape => shape.IsMatch(lines[index])))
                    {
                        writes.Add(new Write(marker.Field, member.Name, index + 1));
                    }
                }
            }
        }

        return [.. writes];
    }

    private static Regex[] WriteShapes(string field)
    {
        string name = $@"(?:this\.)?{Regex.Escape(field)}";
        return
        [
            new Regex($@"(?<![\w.]){name}\s*(?:\?\?|<<|>>>|>>|[|&^+\-*/%])?=(?![=>])", RegexOptions.CultureInvariant),
            new Regex($@"\((?=[^()]*(?<![\w.]){name}\b)[^()]*,[^()]*\)\s*=(?![=>])", RegexOptions.CultureInvariant),
            new Regex($@"\b(?:ref|out)\s+{name}\b", RegexOptions.CultureInvariant)
        ];
    }

    /// <summary>Every (setter, member) pair where a member other than the setter itself names it in code.</summary>
    private static (string Setter, string Member)[] Calls(string source) =>
        [.. Mentions(source, Setters).Select(pair => (Setter: pair.Token, pair.Member))];

    /// <summary>
    /// Every (token, member) pair where the member's code names the token. Comments do not count, the token's own
    /// declaration does not count (a field's declaration line, a method's own member), and
    /// <c>this.</c>-qualified mentions do.
    /// </summary>
    private static (string Token, string Member)[] Mentions(string source, string[] tokens)
    {
        string[] lines = StripComments(source);
        HashSet<(string, string)> found = [];
        foreach (MemberSpan member in MemberSpans(lines))
        {
            foreach (string token in tokens.Where(token => token != member.Name))
            {
                Regex mention = new($@"(?<![\w.])(?:this\.)?{Regex.Escape(token)}\b", RegexOptions.CultureInvariant);
                Regex declaration = new(
                    $@"^    (?:private|internal|public|protected)\b[^=;(]*\s{Regex.Escape(token)}\s*;\s*$",
                    RegexOptions.CultureInvariant);
                for (int index = member.Start; index <= member.End; index++)
                {
                    if (mention.IsMatch(lines[index]) && !declaration.IsMatch(lines[index]))
                    {
                        found.Add((token, member.Name));
                        break;
                    }
                }
            }
        }

        return [.. found];
    }

    /// <summary>Every persistence-API word in the code of the named members.</summary>
    private static (string Member, string Api)[] PersistenceApis(string source, string[] members)
    {
        string[] lines = StripComments(source);
        return
        [
            .. MemberSpans(lines)
                .Where(span => members.Contains(span.Name, StringComparer.Ordinal))
                .SelectMany(span => lines[span.Start..(span.End + 1)]
                    .SelectMany(line => PersistenceApiRegex.Matches(line).Select(match => (span.Name, match.Value))))
                .Distinct()
        ];
    }

    /// <summary>
    /// Every (file, token) pair where a file other than <paramref name="home"/> names a mark or a setter. Comments
    /// are stripped from <c>.cs</c> files only; in every other kind of file any mention counts.
    /// </summary>
    private static (string Path, string Token)[] OutsideMentions((string Path, string Text)[] files, string home)
    {
        string[] tokens = [.. Markers.Select(marker => marker.Field).Concat(Setters)];
        return
        [
            .. files
                .Where(file => file.Path != home)
                .SelectMany(file =>
                {
                    string text = file.Path.EndsWith(".cs", StringComparison.OrdinalIgnoreCase)
                        ? string.Join('\n', StripComments(file.Text))
                        : file.Text;
                    return tokens
                        .Where(token => Regex.IsMatch(text, $@"(?<![\w]){Regex.Escape(token)}\b", RegexOptions.CultureInvariant))
                        .Select(token => (file.Path, token));
                })
        ];
    }

    /// <summary>Comments removed, line numbers kept. Block comments first, so a guard hidden in one is gone.</summary>
    private static string[] StripComments(string source)
    {
        string normalised = source.Replace("\r\n", "\n", StringComparison.Ordinal);
        string withoutBlocks = BlockCommentRegex.Replace(
            normalised,
            match => new string('\n', match.Value.Count(character => character == '\n')));
        return [.. withoutBlocks.Split('\n').Select(line => line.Split("//", 2)[0])];
    }

    /// <summary>
    /// The class body cut into members at four-space indent: a block member ends at a lone <c>}</c>, an
    /// expression-bodied member or field at a line ending in <c>;</c>.
    /// </summary>
    private static MemberSpan[] MemberSpans(string[] lines)
    {
        List<MemberSpan> spans = [];
        int start = 0;
        for (int index = 0; index < lines.Length; index++)
        {
            if (!MemberBlockEndRegex.IsMatch(lines[index]) && !MemberExpressionEndRegex.IsMatch(lines[index]))
            {
                continue;
            }

            spans.Add(new MemberSpan(NameOf(lines, start, index), start, index));
            start = index + 1;
        }

        if (start < lines.Length)
        {
            spans.Add(new MemberSpan(NameOf(lines, start, lines.Length - 1), start, lines.Length - 1));
        }

        return [.. spans];
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

        return "(field or unnamed)";
    }

    private static string ClassBody(string members) =>
        $"public sealed class Synthetic{Environment.NewLine}{{{Environment.NewLine}{members}{Environment.NewLine}}}{Environment.NewLine}";

    private static string ReadProductFile(string relativePath) => File.ReadAllText(Path.Combine(
        ProtocolIdentityArchitectureTests.RepositoryRoot(),
        relativePath.Replace('/', Path.DirectorySeparatorChar)));
}
