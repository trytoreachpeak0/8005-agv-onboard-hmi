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
/// (<see cref="EachMarkIsWrittenOnlyByItsOneWriter"/>), and every member that calls into the setters is a
/// registered caller with a stated reason (<see cref="EveryWayToSetAMarkHasARegisteredCaller"/>). A startup path
/// that restores a mark <b>from a new method</b> is red until somebody answers where its value comes from.
/// <b>An extra call inside a method that is already registered is not</b> -- the registry is kept per (method,
/// setter) pair, not per call site, and the closest thing to onboard-hmi#109 is exactly that:
/// <c>RestorePendingRecoveryOperationProjectionAsync</c> is registered, and its attempt id already comes from the
/// journal. A second <c>TryClaimRecoveryAnnouncement</c> there that claims without publishing passes every check
/// (pinned in <see cref="TheseGuardsTellALeakFromTheCodeAsItIs"/>). Per-call-site registration would close it
/// only by numbering the calls, which is a count; the review of that method stays with people.</item>
/// <item><b>The write-out side</b>: neither name, nor the name of any member that sets them, appears anywhere in
/// the product outside <c>WireToGateBusinessService.cs</c>
/// (<see cref="TheMarksAndTheirSettersNeverLeaveTheBusinessServiceSource"/>), inside that file only registered
/// members mention the fields (<see cref="OnlyRegisteredMembersTouchTheMarks"/>), the members a mark's value reaches
/// by name inside the class use no field outside a list (<see cref="MembersThatTouchAMarkReachOnlyTheirListedFields"/>),
/// and none of them names a persistence API (<see cref="NoMemberThatTouchesAMarkNamesAPersistenceApi"/>).</item>
/// </list>
/// <para>
/// <b>"Reading back has to write the field" is true of the field, and only of the field.</b> The same failure
/// can skip the field entirely: an "announced" flag kept in the journal, and a restore that returns early on it.
/// Nothing here sees that. It is a different state carrying the same meaning, and it would have to be guarded
/// where it is read.
/// </para>
/// <para>
/// <b>Why the field list is closed rather than a list of persistence receivers</b> (onboard-hmi#162 review M-2).
/// The first version only had a word list -- journal, serialize, file, stream -- and its synthetic leak was
/// <c>_journal.Write...</c>, a shape this class does not have: there is no <c>_journal</c> field here. What this
/// class really writes to disk through is <c>_executor.MarkResultRecordedAsync</c>/<c>RecordPendingResultAsync</c>
/// (the journal behind the executor) and <c>_session.Send*Async</c> (the outbox, and the server that can replay it),
/// none of whose names contains a listed word. The review passed a mark to <c>_executor.MarkResultRecordedAsync</c>
/// in a registered member and all six checks stayed green. The same mistake as onboard-hmi#176's first version,
/// one criterion over: that one did not recognise the file's own way of writing a field, this one did not
/// recognise the file's own way of writing to disk. Adding <c>_executor</c> and <c>_session</c> to a list would
/// repeat it for the next receiver. A list of the fields the value may reach is not passed by a receiver nobody
/// thought of -- <b>but only along the paths the closure follows</b>: a member of this class named in code, bare,
/// after <c>this.</c> or after the class's own name. A handler attached
/// to an event elsewhere, a delegate stored and invoked by another member, another type's method: those are paths
/// it does not follow, and a receiver at the end of one is not on the list's radar at all.
/// </para>
/// <para>
/// <b>The second review showed the first closed list was not closed</b>: it looked at each member's own lines, so
/// <c>this._executor</c>, one call to an existing method of the class that writes the journal
/// (<c>RecordAcknowledgedCompletedResultAsync</c>), and a mark held through <c>ExchangeOwedRecoveryEntry</c>'s return
/// value all passed. It now follows calls: from every member holding a mark, through every member of the class they
/// call, across all four files, until nothing new is reached. <b>It stops at the edge of the class</b>: a call on
/// another object or type, and the <c>OperatorEventPublished</c> event, are not followed. Nor is a call on another
/// <b>instance</b> of this class (<c>service.RecordAcknowledgedCompletedResultAsync(...)</c> from a static member):
/// <c>x.Name</c> may be another type's member of the same name, so it is not taken as this class's. That
/// instance's <b>fields</b> are seen (<c>service._executor</c>, fourth review medium-2).
/// </para>
/// <para>
/// <b>Why the file bound alone is not enough, although the ticket offered it as the hardest check.</b> Its
/// argument is that passing the value out needs the name somewhere else. That holds for other files; it does not
/// hold inside the file, which already hands values read from <c>_owedRecoveryEntry</c> to <c>_logger.Write</c>.
/// A journal call written in the same file keeps the name in the same file. The member registry, the closed field
/// list and the persistence-API check are what close that.
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
/// <b>What it cannot see.</b> It follows names, not values, and only inside the class. A member holding a mark that
/// hands it to a method of <b>another</b> type with an innocent name, which persists it, passes every check here --
/// pinned in <see cref="TheseGuardsTellALeakFromTheCodeAsItIs"/> as a known blind spot. So does a subscriber of
/// <c>OperatorEventPublished</c> that stores what it is handed (today: the view model, and a journal refresh that
/// only reads). <b>So does an event or delegate of the class itself</b> (third review M-1): a member holding a mark
/// raises <c>RecoveryEntryPaid?.Invoke(mark)</c>, and a handler attached in the constructor writes it to the
/// journal -- the closure follows the name into the event's declaration, not to where a handler was attached.
/// Pinned, not chased. A member of the class that only names a type (<c>OwedRecoveryEntry? x</c>) pulls that type into
/// the closure, which errs on the side of checking more. The persistence-API word list is a list:
/// a static API whose name matches none of its words slips through. <b>Registering a new reader is a hole a
/// person has to refuse</b>: a registered member that only returns a mark's value hands it to anyone, and the
/// failure messages can ask, not stop. A write split over lines is seen when the field ends one line and the
/// assignment operator starts the next, or <c>ref</c>/<c>out</c> ends one line and the field starts the next
/// (review M-3); a deconstruction split over lines is not.
/// <c>_logger.Write</c> is allowed on a premise, not a check: the technical log is written and never read back
/// into process state (nothing under <c>src/</c> reads it, 2026-09-21). Everything is read through a small lexer
/// (<see cref="Lex"/>) that knows regular, verbatim and interpolated strings, character literals and both kinds of
/// comment. Members are cut over its code view, character by character (<see cref="MemberSpans"/>): a <c>;</c> at
/// class depth ends one, and a <c>}</c> back at class depth ends one only if the member has had no <c>=</c> or
/// <c>=&gt;</c> of its own. <b>The depth and naming checks in <see cref="TheLexerNamesEveryMemberOfTheClass"/> cannot
/// catch a member cut in the wrong place</b> -- the fourth review found three members of this class cut short, each
/// half named and the depth at zero -- so that test also requires that no member start with a continuation token
/// (<c>||</c>, <c>?</c>, <c>:</c>, <c>.</c>, ...), and pins the three as whole members. <b>It refuses raw string
/// literals</b> rather than guessing at them -- none in the class today; a <c>"""</c> added there turns that test red
/// with the lexer's own message.
/// </para>
/// <para>
/// <b>Limits of the reading itself.</b> Spans are whole lines, so two members on one line share it and each is
/// checked with the other's code. An identifier written with an escape (<c>@_executor</c>, <c>\u005Fexecutor</c>) is
/// not recognised as a field or a name. Some legal literals are refused rather than read: <c>@""""</c> (a verbatim
/// string holding one quote) looks like a raw string, and a <c>'</c> in an interpolation's format
/// (<c>$"{d:dd'}"</c>) reads as a character literal; both fail loudly, neither is in the class. A setter handing a
/// mark back is recognised by its return type, <c>out</c>/<c>ref</c>, or a parameter whose type is named like a
/// delegate (<c>Action</c>, <c>Func</c>, <c>...Callback</c>, <c>...Handler</c>): a custom delegate named otherwise, or
/// a container passed in to be filled, is not.
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
    /// The writers and the helpers whose whole job is to set or clear a mark: each either writes a field or calls a
    /// writer, and none does anything else with the value. Their callers are registered in <see cref="Callers"/>.
    /// </summary>
    /// <remarks>
    /// The registry stops one level up, on purpose. The registered callers
    /// (<c>RestorePendingRecoveryOperationProjectionAsync</c>, <c>TrySettleInterruptedOperationAsync</c>,
    /// <c>HandleSlotOperationAsync</c>, <c>ReleaseInFlightAttempt</c>) also set a mark indirectly, but each is where
    /// the decision "this process is announcing this attempt now" is made -- after executing it, after settling it,
    /// or at the restore -- which is the question the registry asks. Their callers do pass the attempt id in (the
    /// server's command, for <c>HandleSlotOperationAsync</c>), but not the decision. Taking the closure would
    /// register most of the class and make every new caller of every method a red that says nothing about marks. (The first version's summary said "directly or by calling one that does",
    /// which by its letter included these four; the review pointed out the list did not follow it.)
    /// </remarks>
    private static readonly string[] Setters =
    [
        "MarkRecoveryAnnounced",
        "TryClaimRecoveryAnnouncement",
        "ExchangeOwedRecoveryEntry",
        "OweRecoveryEntry",
        "ForgetOwedRecoveryEntry",
        "PublishOwedRecoveryEntry"
    ];

    /// <summary>
    /// The setters whose return value is a mark's value. A caller of one holds the mark without naming the field,
    /// so it is checked like a member that does (second review S-3). Every other setter returns <c>void</c> or
    /// <c>bool</c> and has no <c>out</c>/<c>ref</c> parameter; <see cref="EverySetterThatHandsBackAValueIsRegisteredAsOne"/>
    /// checks that from the declarations, so a setter handing a mark back <b>through its return value or an out/ref
    /// parameter</b> is red until registered. One that hands it back another way -- storing it in a field another
    /// member reads, raising an event -- is not seen by that check (the field would be on the field list's radar;
    /// the event is the M-1 blind spot). <c>bool</c> is let through on a premise, not a fact: see
    /// <see cref="HandsOutAValue"/>.
    /// </summary>
    private static readonly string[] ValueReturningSetters = ["ExchangeOwedRecoveryEntry"];

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
    /// <b>Case-insensitive.</b> The first version matched <c>Journal</c> only with a capital, and the synthetic leak
    /// <c>_journal.WriteRecoveryMarkAsync(...)</c> passed it. That synthetic case was itself not this class's idiom --
    /// there is no <c>_journal</c> field here -- and the real gap was the one review M-2 found: the class writes to
    /// disk through <c>_executor</c> and <c>_session</c>, which is why <see cref="FieldsMarkMembersMayUse"/> exists.
    /// </remarks>
    private static readonly Regex PersistenceApiRegex = new(
        @"\b\w*(?:Journal|Serializ|Persist|Sqlite|Outbox|AtomicJsonFile|Database)\w*\b"
        + @"|\bFile\.|\b\w*Stream\w*\b|\bSave\w*\s*\(|\bStore\w*\s*\(|\bSettings\b",
        RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);


    private const string ClassName = "WireToGateBusinessService";

    /// <summary><c>nameof(...)</c> names a member without calling it; the closure removes it before following names.</summary>
    private static readonly Regex NameofRegex = new(
        @"\bnameof\s*\([^)]*\)",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    /// <summary>Where product code lives. <c>tests/</c> is left out: the guards themselves name every token.</summary>
    private static readonly string[] ProductDirectories = ["src", "tools"];

    /// <summary>
    /// A field by the repository's naming: underscore, lower-case letter -- bare, after <c>this.</c>, or after any other
    /// expression and a dot. The last one is another instance's field: a static member handed the service reaches its
    /// journal as <c>service._executor</c> (fourth review medium-2). Any <c>._camelCase</c> counts, not only the fields
    /// this class declares; that can only add a field to the list, never hide one.
    /// </summary>
    private static readonly Regex FieldTokenRegex = new(
        @"(?<![\w@])(?<field>_[a-z]\w*)",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    /// <summary>
    /// The second line of an assignment split over two lines: it starts with the operator. The first line ends with
    /// the field (review M-3: <c>_recoveryAnnouncedAttemptId</c> on one line, <c>= value;</c> on the next).
    /// </summary>
    private static readonly Regex AssignmentContinuationRegex = new(
        @"^\s*(?:\?\?|<<|>>>|>>|[|&^+\-*/%])?=(?![=>])",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Regex TrailingRefRegex = new(
        @"\b(?:ref|out)\s*$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

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
            + $"{Environment.NewLine}These are process state. Taking one elsewhere is how it gets into a projection, a "
            + "snapshot, the journal or a JSON file -- and a mark that survives a restart takes the recovery entry off "
            + "the operator's screen with CI green (onboard-hmi#109)."
            + $"{Environment.NewLine}The fix is to move the access back into WireToGateBusinessService.cs, into a member "
            + "the other checks here already cover, and pass out only what the other file really needs."
            + $"{Environment.NewLine}**Do not widen the home to a second file.** Every other check here reads "
            + $"{BusinessServicePath} only: a second home would lose all of them at once and stay green. If a second "
            + "file truly has to hold this state, make every check here read both files first.");
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
    /// The fields of the class that a mark's value may reach: used by a member holding a mark, or by any member of the
    /// class it calls, however deep. A field that can carry a value out of the process (<c>_executor</c> and
    /// <c>_vectorExecutor</c> write the journal, <c>_session</c> writes the outbox and the server) cannot be used by any
    /// member the closure reaches without this list changing. How far the closure reaches is the limit: members
    /// named in code, not event handlers attached elsewhere, not other types -- see the class remarks.
    /// </summary>
    private static readonly (string Field, string Why)[] FieldsMarkMembersMayUse =
    [
        ("_operationAttemptGate", "The lock both marks live under."),
        ("_recoveryAnnouncedAttemptId", "A mark."),
        ("_owedRecoveryEntry", "A mark."),
        ("_operationAttempts", "The in-flight set: an owed entry is paid only when it is empty. In memory."),
        ("_logger", "The technical log: written, never read back into process state (nothing under src/ reads it)."),
        ("_clock", "Time for the snapshot's timestamp."),
        ("_currentOperationSnapshot", "PublishOperatorEvent keeps the latest operation snapshot here. In memory."),
        ("_expectedActionWait", "PublishOperatorEvent stops or restarts the expected-action clock. In memory."),
        ("_operatorEventDeduplicator", "PublishOperatorEvent drops repeats. In memory.")
    ];

    /// <summary>
    /// Everything a mark's value can reach inside the class uses exactly the fields in
    /// <see cref="FieldsMarkMembersMayUse"/> -- no more, and every listed field still used, so the list says what the
    /// code does. "Everything it can reach" is the members holding a mark (<see cref="MarkValueHolders"/>) and every
    /// member of the class they call, level after level, across all four files (<see cref="Closure"/>). A field is
    /// any <c>_camelCase</c> identifier, with or without <c>this.</c>; <c>_</c> alone is a discard.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The second review found three ways past the first version of this check</b>, each compiling, formatted,
    /// and green: <c>this._executor</c> (the field pattern refused anything after a dot, <c>this.</c> included);
    /// one call to <c>RecordAcknowledgedCompletedResultAsync</c>, a method of this class that writes the journal
    /// (the check looked at each member's own lines and followed no calls -- and its own failure message told
    /// people to move the work into another member, which is that exact shape); and a journal call in
    /// <c>OweRecoveryEntry</c> passing <c>displaced</c>, a mark it got from <c>ExchangeOwedRecoveryEntry</c>'s return
    /// value without naming the field. Now the field pattern takes <c>this.</c>, members named in code are followed,
    /// and callers of a setter that returns a mark hold one.
    /// </para>
    /// <para>
    /// <b>Where the closure stops.</b> At the edge of the class: a call on another object or type
    /// (<c>_expectedActionWait.Observe(...)</c>, <c>WireToGateSublotRejectionText.Describe(...)</c>) is not followed,
    /// and the event <c>OperatorEventPublished</c> hands the snapshot to its subscribers in <c>App</c> (the view model,
    /// and a journal refresh that reads). A field on the list is safe only if what it reaches is -- that is what the
    /// Why column claims, and it is read by people.
    /// </para>
    /// </remarks>
    [Fact]
    public void MembersThatTouchAMarkReachOnlyTheirListedFields()
    {
        string source = ReadProductFile(BusinessServicePath);
        (string Path, string Text)[] classFiles = ClassFiles();

        // The closure reads the class from the files named after it. Every file that declares a part of the class
        // must be among them, or calls into that part go unfollowed -- a set comparison, not a count.
        string root = ProtocolIdentityArchitectureTests.RepositoryRoot();
        string[] declaring =
        [
            .. Directory.EnumerateFiles(Path.Combine(root, "src"), "*.cs", SearchOption.AllDirectories)
                .Select(path => Path.GetRelativePath(root, path).Replace('\\', '/'))
                .Where(relative => !relative.Contains("/obj/", StringComparison.Ordinal)
                    && !relative.Contains("/bin/", StringComparison.Ordinal))
                .Where(relative => Regex.IsMatch(
                    File.ReadAllText(Path.Combine(root, relative)),
                    $@"\bpartial\s+class\s+{ClassName}\b",
                    RegexOptions.CultureInvariant))
                .Order(StringComparer.Ordinal)
        ];
        Assert.Equal(declaring, classFiles.Select(file => file.Path).Order(StringComparer.Ordinal));

        Dictionary<string, string[]> reached = Closure(classFiles, MarkValueHolders(source));
        string[] used = FieldsUsedBy(reached);
        string[] listed = [.. FieldsMarkMembersMayUse.Select(entry => entry.Field)];

        string[] added = [.. used.Except(listed, StringComparer.Ordinal)];
        string[] gone = [.. listed.Except(used, StringComparer.Ordinal)];

        Assert.True(
            added.Length == 0 && gone.Length == 0,
            "The fields a recovery mark's value can reach changed."
            + string.Concat(added.Select(field => $"{Environment.NewLine}  new:  {field}"))
            + string.Concat(gone.Select(field => $"{Environment.NewLine}  gone: {field}"))
            + $"{Environment.NewLine}  reached through: {string.Join(", ", reached.Keys.Order(StringComparer.Ordinal))}"
            + $"{Environment.NewLine}**Do not just add a new one to the list.** First answer: can a value handed to "
            + "it outlive the process? _executor and _vectorExecutor write the journal, _session writes the outbox "
            + "and the server can replay it -- any of those carrying a mark is onboard-hmi#109 after the next restart. "
            + "**Do not hand the mark, or the work that touches it, to another member of this class to get past this "
            + "check**: a member of this class named in code -- bare, after this. or after the class name -- is "
            + "followed into, and that is the shape this check exists to catch. Handing it on through an event, "
            + "another type, or a call on another instance of this class is not followed -- that would be a leak "
            + "this check misses, not a fix. Keep journal and outbox work in members that never hold a mark. "
            + "A field that is gone: remove its line.");
    }

    /// <summary>
    /// A setter that can hand a value back to its caller -- a return type other than <c>void</c>/<c>bool</c>, or an
    /// <c>out</c>/<c>ref</c> parameter -- is registered in <see cref="ValueReturningSetters"/>, and a registered one
    /// still can. Its callers then hold a mark without naming the field, and are checked as holders (third review
    /// severe-3: an <c>out</c> parameter passed the first version, which only read the return type).
    /// </summary>
    [Fact]
    public void EverySetterThatHandsBackAValueIsRegisteredAsOne()
    {
        string source = ReadProductFile(BusinessServicePath);
        foreach (string setter in Setters)
        {
            (bool handsOut, string returns, string parameters) = HandsOutAValue(source, setter);
            bool registered = ValueReturningSetters.Contains(setter, StringComparer.Ordinal);
            Assert.True(
                handsOut == registered,
                handsOut
                    ? $"{setter} returns {returns} ({parameters}), so it can hand a mark back to its caller. Add it to "
                      + $"{nameof(ValueReturningSetters)}: its callers then hold a mark without naming the field, and "
                      + "must be checked like the members that do."
                    : $"{setter} is registered as handing a mark back but returns {returns} ({parameters}) and has no "
                      + "out/ref parameter. Remove it from the list, so the list says what the code does.");
        }
    }

    /// <summary>
    /// The lexer reads every file of the class, the brace depth ends at zero in each, every member has a name, and no
    /// member starts with a continuation token. An unnamed member is one the closure cannot follow a call into -- the
    /// third review found eighteen expression-bodied properties of this class unnamed, <c>CanSubmitSublot</c> among
    /// them. A raw string literal in the class is red here too, with the lexer's own message.
    /// </summary>
    /// <remarks>
    /// <b>The depth and naming checks cannot see a member cut in the wrong place at class depth</b> (fourth review
    /// severe-A): both halves get a name, often a plausible one, and the depth still ends at zero. What gives such a
    /// cut away is where the second half starts -- <c>||</c>, <c>?</c>, <c>:</c>, <c>.</c> -- which no declaration
    /// does. That is the check for it, and the three members of this class the line-based cut split are pinned
    /// below as whole members.
    /// </remarks>
    [Fact]
    public void TheLexerNamesEveryMemberOfTheClass()
    {
        HashSet<string> names = new(StringComparer.Ordinal);
        Dictionary<string, MemberSpan[]> byName = new(StringComparer.Ordinal);
        foreach ((string path, string text) in ClassFiles())
        {
            MemberSpan[] spans = MemberSpans(Lex(text));
            MemberSpan[] unnamed = [.. spans.Where(span => span.Name == Unnamed)];
            Assert.True(
                unnamed.Length == 0,
                $"{path}: members the lexer could not name, at lines "
                + $"{string.Join(", ", unnamed.Select(span => span.Start + 1))}. The closure cannot follow a call into "
                + "a member without a name; teach NameOf the shape before relying on this guard.");

            MemberSpan[] tails = [.. spans.Where(span => ContinuationStartRegex.IsMatch(span.Text))];
            Assert.True(
                tails.Length == 0,
                $"{path}: members that start with a continuation token, at lines "
                + $"{string.Join(", ", tails.Select(span => $"{span.Start + 1} ({span.Name})"))}. Each is the tail of the "
                + "member before it: the cut ended that member too early, and neither half is checked as what it is. "
                + "Fix MemberSpans for the shape before relying on this guard.");

            names.UnionWith(spans.Select(span => span.Name));
            foreach (IGrouping<string, MemberSpan> group in spans.GroupBy(span => span.Name, StringComparer.Ordinal))
            {
                byName[group.Key] = [.. byName.GetValueOrDefault(group.Key, []), .. group];
            }
        }

        Assert.Contains("CanSubmitSublot", names);
        Assert.Contains("PublishOperatorEvent", names);
        Assert.Contains("PublishOperatorResponse", names);

        // The three members the line-based cut split (fourth review severe-A), each one whole member now: its span
        // runs to the `;` and holds the part that used to be cut away as a member named `Read` or `null`.
        foreach ((string member, string tail) in new[]
        {
            ("RecoveryReasonAlreadyGiven", "RecoveryVector is not null"),
            ("ForgetRefusedVector", "RecoveryResultObservedAt"),
            ("PersistedOperatorOrNull", "new(operatorId, method, verifiedAt)"),
        })
        {
            MemberSpan span = Assert.Single(byName.GetValueOrDefault(member, []));
            Assert.Contains(tail, span.Text, StringComparison.Ordinal);
            Assert.EndsWith(";", span.Text.TrimEnd(), StringComparison.Ordinal);
        }

        Assert.DoesNotContain("Read", names);
        Assert.DoesNotContain("null", names);
    }

    /// <summary>
    /// No member that touches a mark names a persistence API. <c>_logger.Write</c> is allowed: the technical log is
    /// written and never read back into process state.
    /// </summary>
    /// <remarks>
    /// Since the closed field list above, this check is only for what that list cannot see: static APIs that
    /// need no field, such as <c>File.</c> or <c>JsonSerializer</c>.
    /// </remarks>
    [Fact]
    public void NoMemberThatTouchesAMarkNamesAPersistenceApi()
    {
        string source = ReadProductFile(BusinessServicePath);
        string[] members = MarkMembers();

        (string Member, string Api)[] hits = PersistenceApis(Closure(ClassFiles(), MarkValueHolders(source)));

        Assert.True(
            hits.Length == 0,
            "A member a recovery mark's value can reach names a persistence API:"
            + string.Concat(hits.Select(hit => $"{Environment.NewLine}  {hit.Member}  {hit.Api}"))
            + $"{Environment.NewLine}If the mark's value reaches it, the mark can outlive the process. Moving the call "
            + "into another member of this class that is named from here is still caught; moving it behind an event, "
            + "into another type, or onto another instance of this class is not caught, and is not a fix. Keep "
            + "persistence in members that never hold a mark.");

        Assert.All(members, member => Assert.True(
            MemberSpans(Lex(source)).Any(span => span.Name == member),
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

        // ---- Review round: each case below is written the way WireToGateBusinessService.cs itself writes it. ----
        // The first version's synthetic persistence leak was `_journal.Write...`, a shape this class does not have,
        // and the real shapes passed. Every criterion gets a case in the file's own idiom, not only the one that
        // went wrong last time (review M-2).

        // Fields: the class's real ways to disk, each reached from a member that touches a mark.
        string[] listedFields = [.. FieldsMarkMembersMayUse.Select(entry => entry.Field)];
        Assert.Equal(
            "_executor",
            Assert.Single(FieldsUsedBy(
                ClassBody(
                    """
                        private void PublishOwedRecoveryEntry()
                        {
                            _ = _executor.MarkResultRecordedAsync(_recoveryAnnouncedAttemptId ?? string.Empty, CancellationToken.None);
                        }
                    """),
                ["PublishOwedRecoveryEntry"]).Except(listedFields, StringComparer.Ordinal)));
        Assert.Equal(
            "_session",
            Assert.Single(FieldsUsedBy(
                ClassBody(
                    """
                        private void OweRecoveryEntry(WireToGateRecoveryOperationContext context, string guidance)
                        {
                            _ = _session.SendRecoveryOperationProgressAsync(_owedRecoveryEntry!.Context, CancellationToken.None);
                        }
                    """),
                ["OweRecoveryEntry"]).Except(listedFields, StringComparer.Ordinal)));
        Assert.Equal(
            "_session",
            Assert.Single(FieldsUsedBy(
                ClassBody(
                    """
                        private void ForgetOwedRecoveryEntry(string attemptId)
                        {
                            await _session.Journal.UpdateRecoveryStateAsync(state => state with { Announced = _recoveryAnnouncedAttemptId }, token);
                        }
                    """),
                ["ForgetOwedRecoveryEntry"]).Except(listedFields, StringComparer.Ordinal)));
        // The member as it is uses only listed fields; `_` alone is a discard, not a field.
        Assert.Empty(FieldsUsedBy(
            ClassBody(
                """
                    private void ForgetOwedRecoveryEntry(string attemptId)
                    {
                        lock (_operationAttemptGate)
                        {
                            _ = ExchangeOwedRecoveryEntry(null);
                        }

                        _logger.Write(LogSeverity.Information, nameof(WireToGateBusinessService), attemptId);
                    }
                """),
            ["ForgetOwedRecoveryEntry"]).Except(listedFields, StringComparer.Ordinal));

        // Writes split over two lines (review M-3 compiled this with no error and all six checks green).
        Assert.Single(StrayWrites(
            """
                private void PublishOwedRecoveryEntry()
                {
                    _recoveryAnnouncedAttemptId
                        = owed.Context.SlotOperationAttemptId;
                }
            """));
        Assert.Single(StrayWrites(
            """
                private void PublishOwedRecoveryEntry()
                {
                    Interlocked.Exchange(ref
                        _owedRecoveryEntry, null);
                }
            """));
        // ...while a comparison that happens to break after the field is not a write.
        Assert.Empty(StrayWrites(
            """
                private void PublishOwedRecoveryEntry()
                {
                    bool same = _recoveryAnnouncedAttemptId
                        == owed.Context.SlotOperationAttemptId;
                }
            """));

        // Callers, in the shapes the file uses: inside a condition, and a discarded exchange.
        Assert.Contains(
            ("TryClaimRecoveryAnnouncement", "ReplayAnnouncementsFromJournalAsync"),
            Calls(ClassBody(
                """
                    private async Task ReplayAnnouncementsFromJournalAsync(CancellationToken cancellationToken)
                    {
                        if (!TryClaimRecoveryAnnouncement(pending.SlotOperationAttemptId))
                        {
                            return;
                        }
                    }
                """)));
        Assert.Contains(
            ("ExchangeOwedRecoveryEntry", "ClearOnReconnect"),
            Calls(ClassBody(
                """
                    private void ClearOnReconnect()
                    {
                        _ = ExchangeOwedRecoveryEntry(null);
                    }
                """)));

        // Mentions, in the shapes the file reads a mark: null-conditional and pattern.
        Assert.Contains(
            ("_owedRecoveryEntry", "ShowDebtInStatusBar"),
            Mentions(
                ClassBody(
                    """
                        private string ShowDebtInStatusBar() =>
                            _owedRecoveryEntry?.Context.SlotOperationAttemptId ?? string.Empty;
                    """),
                ["_owedRecoveryEntry"]));

        // ---- Known blind spot (review M-1), pinned at its current answer. ----
        // The caller registry is per (method, setter) pair. A second call inside a method that is already registered
        // is the same pair, so nothing changes -- and the method this matters for is already registered: the restore
        // reads its attempt id from the journal. Claiming there without publishing is onboard-hmi#109, and every
        // check here stays green. Closing it per call site would mean numbering the calls, which is a count.
        (string Setter, string Member)[] registeredShape = Calls(ClassBody(
            """
                private async Task RestorePendingRecoveryOperationProjectionAsync(CancellationToken cancellationToken)
                {
                    if (!TryClaimRecoveryAnnouncement(context.SlotOperationAttemptId))
                    {
                        return;
                    }
                }
            """));
        (string Setter, string Member)[] withSilentClaim = Calls(ClassBody(
            """
                private async Task RestorePendingRecoveryOperationProjectionAsync(CancellationToken cancellationToken)
                {
                    _ = TryClaimRecoveryAnnouncement(journalAttemptId);
                    if (!TryClaimRecoveryAnnouncement(context.SlotOperationAttemptId))
                    {
                        return;
                    }
                }
            """));
        Assert.Equal(registeredShape, withSilentClaim);

        // ---- Second review: each written the way this class writes it. ----

        // S-1: `this.` in front of a field that writes to disk.
        Assert.Equal(
            "_executor",
            Assert.Single(FieldsUsedBy(
                ClassBody(
                    """
                        private void PublishOwedRecoveryEntry()
                        {
                            _ = this._executor.MarkResultRecordedAsync(_recoveryAnnouncedAttemptId ?? string.Empty, CancellationToken.None);
                        }
                    """),
                ["PublishOwedRecoveryEntry"]).Except(listedFields, StringComparer.Ordinal)));

        // S-2: one call to an existing method of the class that writes the journal. The closure follows it.
        Dictionary<string, string[]> throughHelper = SyntheticClosure(
            """
                private void PublishOwedRecoveryEntry()
                {
                    _ = RecordAcknowledgedCompletedResultAsync(owed.Context, CancellationToken.None);
                }

                private async Task RecordAcknowledgedCompletedResultAsync(
                    WireToGateRecoveryOperationContext context,
                    CancellationToken cancellationToken)
                {
                    await _executor.MarkResultRecordedAsync(context.SlotOperationAttemptId, cancellationToken)
                        .ConfigureAwait(false);
                }
            """,
            "PublishOwedRecoveryEntry");
        Assert.Contains("RecordAcknowledgedCompletedResultAsync", throughHelper.Keys);
        Assert.Equal("_executor", Assert.Single(FieldsUsedBy(throughHelper).Except(listedFields, StringComparer.Ordinal)));

        // ...and a mention that is not a call does not pull a member in: nameof, and constructing a type.
        Dictionary<string, string[]> notCalls = SyntheticClosure(
            """
                private void ForgetOwedRecoveryEntry(string attemptId)
                {
                    _logger.Write(LogSeverity.Information, nameof(Persist), attemptId);
                    _ = new OwedRecoveryEntry(context, guidance);
                }

                private void Persist()
                {
                    _session.Journal.Clear();
                }

                private sealed class OwedRecoveryEntry(WireToGateRecoveryOperationContext Context, string Guidance)
                {
                    private readonly object _journalHandle = new();
                }
            """,
            "ForgetOwedRecoveryEntry");
        Assert.Equal("ForgetOwedRecoveryEntry", Assert.Single(notCalls.Keys));

        // S-3: a member holding a mark from ExchangeOwedRecoveryEntry's return value, never naming the field.
        string exchangeCaller =
            """
                private void OweRecoveryEntry(WireToGateRecoveryOperationContext context, string guidance)
                {
                    OwedRecoveryEntry? displaced = ExchangeOwedRecoveryEntry(new OwedRecoveryEntry(context, guidance));
                    _ = _executor.MarkResultRecordedAsync(displaced?.Context.SlotOperationAttemptId ?? string.Empty, CancellationToken.None);
                }
            """;
        Assert.Contains("OweRecoveryEntry", MarkValueHolders(ClassBody(exchangeCaller)));
        Assert.Equal(
            "_executor",
            Assert.Single(FieldsUsedBy(ClassBody(exchangeCaller), MarkValueHolders(ClassBody(exchangeCaller)))
                .Except(listedFields, StringComparer.Ordinal)));

        // C-3: a parenthesised target, and a comparison in parentheses that is not a write.
        Assert.Single(StrayWrites(
            """
                private void PublishOwedRecoveryEntry()
                {
                    (_recoveryAnnouncedAttemptId) = owed.Context.SlotOperationAttemptId;
                }
            """));
        Assert.Empty(StrayWrites(
            """
                private void PublishOwedRecoveryEntry()
                {
                    bool same = (_recoveryAnnouncedAttemptId) == owed.Context.SlotOperationAttemptId;
                }
            """));

        // An expression-bodied member whose body runs onto a second line is its own member, not part of the next
        // one (the member cut used to merge PublishOperatorResponse into PublishOperatorEvent).
        Dictionary<string, string[]> split = SyntheticClosure(
            """
                private void PublishOperatorResponse(string kind, string message) =>
                    RaiseOperatorEvent(kind, message, operation: null);

                private void PublishOperatorEvent(string key)
                {
                    _ = _session.SendSafetyStateChangedAsync(key);
                }
            """,
            "PublishOperatorEvent");
        Assert.Equal("PublishOperatorEvent", Assert.Single(split.Keys));

        // ---- Third review: the lexer. Each case passed the regex version of this scanner. ----

        // Severe-4: `//` inside a string is not a comment; the write after it on the same line is seen.
        Assert.Single(StrayWrites(
            """
                private void ReleaseInFlightAttempt(string link)
                {
                    _ = Parse("onboard://recovery/" + link, out _recoveryAnnouncedAttemptId);
                }
            """));

        // Q-2: `/*` in one string and `*/` in another do not swallow the code between them.
        Assert.Single(StrayWrites(
            """
                private void ReleaseInFlightAttempt(string link)
                {
                    string open = "/*"; _recoveryAnnouncedAttemptId = link; string close = "*/";
                }
            """));

        // A verbatim string's doubled quote does not end it, and `//` inside it is text.
        Assert.Single(StrayWrites(
            """
                private void ReleaseInFlightAttempt(string link)
                {
                    string note = @"say ""//hi"" "; _recoveryAnnouncedAttemptId = link;
                }
            """));

        // An interpolation hole is code: a mark named inside one is seen even with string contents blanked.
        Assert.Contains(
            ("_owedRecoveryEntry", "Describe"),
            Mentions(
                ClassBody(
                    """
                        private string Describe() => $"onboard://{_owedRecoveryEntry?.Context.SlotOperationAttemptId}";
                    """),
                ["_owedRecoveryEntry"],
                keepStrings: false));

        // Braces inside strings, escaped braces in an interpolated string and brace characters do not shift the cut.
        Assert.Equal(
            "First,Second",
            string.Join(',', MemberSpans(Lex(ClassBody(
                """
                    private string First() => "{" + $"{{ {Second()} }}" + '{' + '"';

                    private string Second()
                    {
                        return "}";
                    }
                """))).Select(span => span.Name)));

        // A raw string is refused with the lexer's own message, not guessed at; an unbalanced cut is refused too.
        NotSupportedException raw = Assert.Throws<NotSupportedException>(
            () => Lex("public sealed class S\n{\n    private string A() => \"\"\"x\"\"\";\n}\n"));
        Assert.Contains("raw string", raw.Message, StringComparison.Ordinal);
        Assert.Throws<InvalidOperationException>(
            () => MemberSpans(Lex("public sealed class S\n{\n    private void A()\n    {\n")));

        // ---- Third review: member names the regex version could not read. ----

        // Severe-1: a tuple return type -- `(` after a keyword or `<` opens a type, not the parameter list.
        Assert.Equal("RecordPaid", NameOf("    private (bool Recorded, string AttemptId) RecordPaid(string attemptId)"));
        Assert.Equal("RecordPaidAsync", NameOf("    private async Task<(bool Recorded, string Id)> RecordPaidAsync(string id)"));
        Dictionary<string, string[]> throughTuple = SyntheticClosure(
            """
                private void PublishOwedRecoveryEntry()
                {
                    _ = RecordPaid("a");
                }

                private (bool Recorded, string AttemptId) RecordPaid(string attemptId)
                {
                    _ = _executor.MarkResultRecordedAsync(attemptId, CancellationToken.None);
                    return (true, attemptId);
                }
            """,
            "PublishOwedRecoveryEntry");
        Assert.Equal("_executor", Assert.Single(FieldsUsedBy(throughTuple).Except(listedFields, StringComparer.Ordinal)));

        // Severe-2: an expression-bodied property whose body is on the next line -- the shape dotnet format keeps --
        // a one-line property and an indexer are members the closure follows into.
        Dictionary<string, string[]> throughProperty = SyntheticClosure(
            """
                private WireToGateSlotOperationExecutor Executor =>
                    _executor;

                private void PublishOwedRecoveryEntry()
                {
                    _ = Executor.MarkResultRecordedAsync(_recoveryAnnouncedAttemptId ?? string.Empty, CancellationToken.None);
                }
            """,
            "PublishOwedRecoveryEntry");
        Assert.Equal("_executor", Assert.Single(FieldsUsedBy(throughProperty).Except(listedFields, StringComparer.Ordinal)));
        Assert.Equal("Executor", NameOf("    private WireToGateSlotOperationExecutor Executor { get { return _executor; } }"));
        Assert.Equal("this", NameOf("    private string this[int index] => _session.ToString();"));
        Dictionary<string, string[]> throughIndexer = SyntheticClosure(
            """
                private string this[int index] => _session.ToString();

                private void PublishOwedRecoveryEntry()
                {
                    string text = this[0];
                }
            """,
            "PublishOwedRecoveryEntry");
        Assert.Equal("_session", Assert.Single(FieldsUsedBy(throughIndexer).Except(listedFields, StringComparer.Ordinal)));
        Assert.Equal("_gate", NameOf("    private readonly object _gate = new();"));
        Assert.Equal("Wire", NameOf("    [Obsolete(\"   \")]\n    private int[] Wire(Func<bool> check)"));

        // Severe-3: an out parameter hands a value back as surely as a return type does.
        Assert.True(HandsOutAValue(
            ClassBody(
                """
                    private void ForgetOwedRecoveryEntry(string attemptId, out OwedRecoveryEntry? dropped)
                    {
                        dropped = null;
                    }
                """),
            "ForgetOwedRecoveryEntry").HandsOut);
        Assert.True(HandsOutAValue(
            ClassBody(
                """
                    private OwedRecoveryEntry? ExchangeOwedRecoveryEntry(OwedRecoveryEntry? next) => next;
                """),
            "ExchangeOwedRecoveryEntry").HandsOut);
        Assert.False(HandsOutAValue(
            ClassBody(
                """
                    private bool TryClaimRecoveryAnnouncement(string attemptId)
                    {
                        return true;
                    }
                """),
            "TryClaimRecoveryAnnouncement").HandsOut);

        // ---- Fourth review severe-A: a member cut short where a line ends in `}` at class depth. ----
        // Each passed the line-based cut: the part after the `}` line became a member of its own, named after its
        // first identifier, and no one followed a call into it.

        // A property pattern, then the rest of the conditional on the next lines.
        Dictionary<string, string[]> throughPattern = SyntheticClosure(
            """
                private void PublishOwedRecoveryEntry()
                {
                    _ = RecordPaidEntryAsync(_owedRecoveryEntry!);
                }

                private Task RecordPaidEntryAsync(OwedRecoveryEntry owed) =>
                    owed is { Context.SlotOperationAttemptId.Length: > 0 }
                        ? _executor.MarkResultRecordedAsync(owed.Context.SlotOperationAttemptId, CancellationToken.None)
                        : Task.CompletedTask;
            """,
            "PublishOwedRecoveryEntry");
        Assert.Equal("_executor", Assert.Single(FieldsUsedBy(throughPattern).Except(listedFields, StringComparer.Ordinal)));

        // `||` after the pattern.
        Dictionary<string, string[]> throughOr = SyntheticClosure(
            """
                private void PublishOwedRecoveryEntry()
                {
                    _ = Paid(_owedRecoveryEntry!);
                }

                private bool Paid(OwedRecoveryEntry owed) =>
                    owed is { Guidance.Length: 0 }
                    || _executor.MarkResultRecordedAsync(owed.Context.SlotOperationAttemptId, CancellationToken.None).IsCompleted;
            """,
            "PublishOwedRecoveryEntry");
        Assert.Equal("_executor", Assert.Single(FieldsUsedBy(throughOr).Except(listedFields, StringComparer.Ordinal)));

        // `&&` after the pattern, into a static API.
        Assert.Contains(
            ("Paid", "File."),
            PersistenceApis(
                ClassBody(
                    """
                        private void PublishOwedRecoveryEntry()
                        {
                            _ = Paid(_owedRecoveryEntry!);
                        }

                        private bool Paid(OwedRecoveryEntry owed) =>
                            owed is { Guidance.Length: > 0 }
                            && Remember(() => System.IO.File.AppendAllText("paid.txt", owed.Guidance));
                    """),
                ["PublishOwedRecoveryEntry"]));

        // `with { }` and `??`.
        Dictionary<string, string[]> throughWith = SyntheticClosure(
            """
                private void PublishOwedRecoveryEntry()
                {
                    _ = Next(_owedRecoveryEntry!);
                }

                private object Next(OwedRecoveryEntry owed) =>
                    owed with { Guidance = "" }
                    ?? _executor.Remember(owed);
            """,
            "PublishOwedRecoveryEntry");
        Assert.Equal("_executor", Assert.Single(FieldsUsedBy(throughWith).Except(listedFields, StringComparer.Ordinal)));

        // The read-back side: the tail used to be a member named after the setter it calls, so the call belonged to
        // no one and the declaration looked doubled. Now the caller is the member it is in.
        string claimAfterPattern = ClassBody(
            """
                private bool Claim(string attemptId) =>
                    _lastRecoveryState is { RecoveryVector: null }
                    && TryClaimRecoveryAnnouncement(attemptId);

                private bool TryClaimRecoveryAnnouncement(string attemptId)
                {
                    return true;
                }
            """);
        Assert.Contains(("TryClaimRecoveryAnnouncement", "Claim"), Calls(claimAfterPattern));
        Assert.False(HandsOutAValue(claimAfterPattern, "TryClaimRecoveryAnnouncement").HandsOut);

        // What still ends a member at `}`: a method, a property with accessors; an auto-property initializer and a
        // default parameter value do not make the `}` a non-end.
        Assert.Equal(
            "Count,Label,Run,Next",
            string.Join(',', MemberSpans(Lex(ClassBody(
                """
                    private int Count { get; set; } = 3;

                    private string Label { get => "x"; }

                    private void Run(int times = 1)
                    {
                    }

                    private void Next()
                    {
                    }
                """))).Select(span => span.Name)));

        // The continuation-token check that backs the cut: a tail starts with one, a declaration never does.
        Assert.Matches(ContinuationStartRegex, "    || _executor.MarkResultRecordedAsync(id, token).IsCompleted;");
        Assert.Matches(ContinuationStartRegex, "        ? _executor.MarkResultRecordedAsync(id, token)");
        Assert.Matches(ContinuationStartRegex, "    .Read(ref _lastRecoveryState).RecoveryVector is not null;");
        Assert.DoesNotMatch(ContinuationStartRegex, "    [Obsolete] private int Count { get; set; } = 3;");
        Assert.DoesNotMatch(ContinuationStartRegex, "    (bool A, string B) Pair() => (true, \"\");");

        // A setter declared twice is an explicit error with lines, not `Sequence contains more than one element`.
        InvalidOperationException twice = Assert.Throws<InvalidOperationException>(() => HandsOutAValue(
            ClassBody(
                """
                    private void MarkRecoveryAnnounced(string attemptId)
                    {
                    }

                    private void MarkRecoveryAnnounced(int attemptNumber)
                    {
                    }
                """),
            "MarkRecoveryAnnounced"));
        Assert.Contains("declared 2 times", twice.Message, StringComparison.Ordinal);

        // ---- Fourth review mediums. ----

        // Medium-1: a delegate parameter hands a mark back when the setter invokes it.
        Assert.True(HandsOutAValue(
            ClassBody(
                """
                    private void ForgetOwedRecoveryEntry(string attemptId, Action<OwedRecoveryEntry?> handBack)
                    {
                        handBack(ExchangeOwedRecoveryEntry(null));
                    }
                """),
            "ForgetOwedRecoveryEntry").HandsOut);

        // Medium-2: another instance's field, from a static member handed the service.
        Dictionary<string, string[]> throughInstance = SyntheticClosure(
            """
                private void PublishOwedRecoveryEntry()
                {
                    Archive(this, _recoveryAnnouncedAttemptId ?? string.Empty);
                }

                private static void Archive(Synthetic service, string attemptId)
                {
                    _ = service._executor.MarkResultRecordedAsync(attemptId, CancellationToken.None);
                }
            """,
            "PublishOwedRecoveryEntry");
        Assert.Equal("_executor", Assert.Single(FieldsUsedBy(throughInstance).Except(listedFields, StringComparer.Ordinal)));
        Assert.Single(StrayWrites(
            """
                private static void Reset(Synthetic service)
                {
                    service._owedRecoveryEntry = null;
                }
            """));
        Assert.Contains(
            ("MarkRecoveryAnnounced", "Restore"),
            Calls(ClassBody(
                """
                    private static void Restore(Synthetic service, string attemptId)
                    {
                        service.MarkRecoveryAnnounced(attemptId);
                    }
                """)));

        // Medium-3: a static member called through the class's name. The closure knows the real class's name, so
        // the synthetic call is written with it.
        Dictionary<string, string[]> throughClassName = SyntheticClosure(
            """
                private void PublishOwedRecoveryEntry()
                {
                    WireToGateBusinessService.Archive(this, _recoveryAnnouncedAttemptId ?? string.Empty);
                }

                private static void Archive(Synthetic service, string attemptId)
                {
                    _ = service._executor.MarkResultRecordedAsync(attemptId, CancellationToken.None);
                }
            """,
            "PublishOwedRecoveryEntry");
        Assert.Contains("Archive", throughClassName.Keys);

        // ---- Known blind spot (third review M-1), pinned at its current answer. ----
        // A member holding a mark raises an event of the class, and a handler subscribed elsewhere (here in the
        // constructor) writes it to the journal. The closure follows names into members; it does not follow a
        // subscription from where the event is raised to where the handler was attached. Not chased -- decided with
        // the coordinator. When the scanner learns it, this assertion fails; move the case among the leaks then.
        Assert.Empty(FieldsUsedBy(SyntheticClosure(
            """
                private event Action<string>? RecoveryEntryPaid;

                public Synthetic()
                {
                    RecoveryEntryPaid += id => _ = _executor.MarkResultRecordedAsync(id, CancellationToken.None);
                }

                private void PublishOwedRecoveryEntry()
                {
                    RecoveryEntryPaid?.Invoke(_recoveryAnnouncedAttemptId ?? string.Empty);
                }
            """,
            "PublishOwedRecoveryEntry")).Except(listedFields, StringComparer.Ordinal));

        // ---- Known blind spot, pinned at its current answer. ----
        // A member holding a mark hands it to a method of ANOTHER type whose name says nothing of persistence.
        // The closure stops at the edge of the class, so if that method writes to disk the mark leaks and every check
        // here stays green. (A method of this class named directly in the same position is caught -- the S-2 case
        // above; one reached through an event handler is not -- the M-1 case.)
        // Telling this apart needs the other type's source. When the scanner learns it, this assertion fails --
        // move the case up among the leaks then.
        Assert.Empty(PersistenceApis(
            ClassBody(
                """
                    private void PublishOwedRecoveryEntry()
                    {
                        OwedRecoveryEntry? owed = _owedRecoveryEntry;
                        DiagnosticsArchive.Remember(owed);
                    }
                """),
            ["PublishOwedRecoveryEntry"]));
        Assert.Empty(FieldsUsedBy(
            ClassBody(
                """
                    private void PublishOwedRecoveryEntry()
                    {
                        OwedRecoveryEntry? owed = _owedRecoveryEntry;
                        DiagnosticsArchive.Remember(owed);
                    }
                """),
            ["PublishOwedRecoveryEntry"]).Except(listedFields, StringComparer.Ordinal));
    }

    // ------------------------------------------------------------------------------------------------------------
    // The scanner. Every function here is a pure function of source text, so the test above can feed it.
    // ------------------------------------------------------------------------------------------------------------

    private sealed record Write(string Field, string Member, int Line);

    /// <summary>A member: its name, its first and last line (0-based), and its code from its first character.</summary>
    private sealed record MemberSpan(string Name, int Start, int End, string Text);

    /// <summary>
    /// A source file lexed into two views with the same length and the same lines. <see cref="Code"/> has every
    /// comment and every literal's contents blanked -- what is left is code, and only code. <see cref="Kept"/> has the
    /// comments blanked and the string contents kept, for the checks that must see a name written inside a string (a
    /// reflective read, a JSON key).
    /// </summary>
    private sealed record Lexed(string[] Code, string[] Kept);

    private static string WriterOf(string field) => Markers.Single(marker => marker.Field == field).Writer;

    private static Write[] StrayWrites(string members) =>
        [.. Writes(ClassBody(members)).Where(write => write.Member != WriterOf(write.Field))];

    /// <summary>Every write to a mark, in every shape, with the member it sits in. Reads code only.</summary>
    private static Write[] Writes(string source)
    {
        Lexed lexed = Lex(source);
        string[] lines = lexed.Code;
        List<Write> writes = [];
        foreach (MemberSpan member in MemberSpans(lexed))
        {
            for (int index = member.Start; index <= member.End; index++)
            {
                foreach (Marker marker in Markers)
                {
                    if (WriteShapes(marker.Field).Any(shape => shape.IsMatch(lines[index]))
                        || IsSplitWrite(lines, index, member.End, marker.Field))
                    {
                        writes.Add(new Write(marker.Field, member.Name, index + 1));
                    }
                }
            }
        }

        return [.. writes];
    }

    /// <summary>
    /// A write split over two lines, reported on the line where the field is: the field ends this line and the next
    /// non-blank line starts with an assignment operator, or <c>ref</c>/<c>out</c> ends the previous non-blank line and
    /// this one starts with the field (review M-3: the first shape compiled with no error and passed all six checks).
    /// </summary>
    private static bool IsSplitWrite(string[] lines, int index, int limit, string field)
    {
        string name = $@"(?:this\.)?{Regex.Escape(field)}";
        if (Regex.IsMatch(lines[index], $@"(?<!\w){name}\s*$", RegexOptions.CultureInvariant))
        {
            int next = index + 1;
            while (next <= limit && string.IsNullOrWhiteSpace(lines[next]))
            {
                next++;
            }

            if (next <= limit && AssignmentContinuationRegex.IsMatch(lines[next]))
            {
                return true;
            }
        }

        if (Regex.IsMatch(lines[index], $@"^\s*{name}\b", RegexOptions.CultureInvariant))
        {
            int previous = index - 1;
            while (previous >= 0 && string.IsNullOrWhiteSpace(lines[previous]))
            {
                previous--;
            }

            if (previous >= 0 && TrailingRefRegex.IsMatch(lines[previous]))
            {
                return true;
            }
        }

        return false;
    }

    private static Regex[] WriteShapes(string field)
    {
        string name = $@"(?:this\.)?{Regex.Escape(field)}";
        return
        [
            // Parentheses around the target are allowed (second review C-3: `(_mark) = x;` passed all three shapes).
            new Regex(
                $@"(?<!\w)(?:\(\s*)*{name}\s*(?:\)\s*)*(?:\?\?|<<|>>>|>>|[|&^+\-*/%])?=(?![=>])",
                RegexOptions.CultureInvariant),
            new Regex($@"\((?=[^()]*(?<!\w){name}\b)[^()]*,[^()]*\)\s*=(?![=>])", RegexOptions.CultureInvariant),
            new Regex($@"\b(?:ref|out)\s+{name}\b", RegexOptions.CultureInvariant)
        ];
    }

    /// <summary>Every (setter, member) pair where a member other than the setter itself names it in code.</summary>
    private static (string Setter, string Member)[] Calls(string source) =>
        [.. Mentions(source, Setters, keepStrings: false).Select(pair => (Setter: pair.Token, pair.Member))];

    /// <summary>
    /// Every (token, member) pair where the member names the token. Comments never count; a member does not mention
    /// itself (a field's own declaration, a method's own name); <c>this.</c>-qualified mentions count. With
    /// <paramref name="keepStrings"/> a name inside a string literal counts too -- a reflective read of a mark.
    /// </summary>
    private static (string Token, string Member)[] Mentions(string source, string[] tokens, bool keepStrings = true)
    {
        Lexed lexed = Lex(source);
        string[] lines = keepStrings ? lexed.Kept : lexed.Code;
        HashSet<(string, string)> found = [];
        foreach (MemberSpan member in MemberSpans(lexed))
        {
            foreach (string token in tokens.Where(token => token != member.Name))
            {
                Regex mention = new($@"(?<!\w)(?:this\.)?{Regex.Escape(token)}\b", RegexOptions.CultureInvariant);
                for (int index = member.Start; index <= member.End; index++)
                {
                    if (mention.IsMatch(lines[index]))
                    {
                        found.Add((token, member.Name));
                        break;
                    }
                }
            }
        }

        return [.. found];
    }

    /// <summary>The members whose code mentions a mark: the registered readers and the writers.</summary>
    private static string[] MarkMembers() =>
        [.. Readers.Select(reader => reader.Member).Concat(Markers.Select(marker => marker.Writer)).Distinct()];

    /// <summary>
    /// The members that hold a mark's value: those that mention a mark, and those that get one from a setter that
    /// hands a mark back -- by return value or through an <c>out</c>/<c>ref</c> parameter -- without naming the field
    /// (second review S-3: <c>OweRecoveryEntry</c> gets <c>displaced</c> from <c>ExchangeOwedRecoveryEntry</c>).
    /// </summary>
    private static string[] MarkValueHolders(string source) =>
    [
        .. MarkMembers()
            .Concat(Calls(source)
                .Where(call => ValueReturningSetters.Contains(call.Setter, StringComparer.Ordinal))
                .Select(call => call.Member))
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
    ];

    /// <summary>
    /// Whether <paramref name="setter"/>, as declared in <paramref name="source"/>, can hand a value back to its
    /// caller: a return type other than <c>void</c> or <c>bool</c>, or an <c>out</c>/<c>ref</c> parameter (third review
    /// severe-3: <c>ForgetOwedRecoveryEntry(string, out OwedRecoveryEntry? dropped)</c> passed the return-type check).
    /// </summary>
    /// <remarks>
    /// <b><c>bool</c> is let through on a premise, not a fact.</b> <c>TryClaimRecoveryAnnouncement</c>'s <c>bool</c> is
    /// a function of the mark -- whether it already named this attempt. What a restart would have to bring back to
    /// hide a recovery entry is the attempt id itself, and one bit that the caller already knew the id of does not
    /// carry it anywhere new. That is the argument; nothing here checks it.
    /// </remarks>
    private static (bool HandsOut, string Returns, string Parameters) HandsOutAValue(string source, string setter)
    {
        Lexed lexed = Lex(source);
        MemberSpan[] declared = [.. MemberSpans(lexed).Where(candidate => candidate.Name == setter)];
        if (declared.Length != 1)
        {
            throw new InvalidOperationException(
                $"{setter} is declared {declared.Length} times (lines "
                + $"{string.Join(", ", declared.Select(span => span.Start + 1))}). This check reads one declaration per "
                + "setter: an overload of a setter is a second way to set the mark and needs its own name and its own "
                + "registration; a member of that name that is not a declaration means MemberSpans cut something in the "
                + "wrong place. Read the lines above before changing the registry.");
        }

        string header = declared[0].Text;
        Match match = Regex.Match(header, $@"\b{Regex.Escape(setter)}\s*\(", RegexOptions.CultureInvariant);
        if (!match.Success)
        {
            throw new InvalidOperationException(
                $"{setter} at line {declared[0].Start + 1} has no parameter list: it is not a method, so this check "
                + "cannot tell what it hands back. A setter is a method.");
        }
        string returns = Regex.Replace(
                header[..match.Index],
                @"\b(?:public|private|protected|internal|static|async|override|sealed|virtual|abstract|unsafe|extern|new|partial)\b",
                string.Empty,
                RegexOptions.CultureInvariant)
            .Trim();
        int open = match.Index + match.Length - 1;
        int close = MatchingClose(header, open);
        string parameters = header[(open + 1)..close];
        bool handsOut = returns is not ("void" or "bool")
            || Regex.IsMatch(parameters, @"\b(?:out|ref)\b", RegexOptions.CultureInvariant)
            || DelegateParameterRegex.IsMatch(parameters);
        return (handsOut, returns, parameters);
    }

    /// <summary>
    /// A parameter of a delegate type: the setter can hand a mark to its caller by invoking it
    /// (<c>Action&lt;OwedRecoveryEntry?&gt; handBack</c> ... <c>handBack(ExchangeOwedRecoveryEntry(null))</c>; fourth review
    /// medium-1). Recognised by name: the framework's delegate types, and a type ending in <c>Callback</c>,
    /// <c>Handler</c> or <c>Delegate</c>. A custom delegate named otherwise, or a mutable container passed in to be
    /// filled, is not recognised -- a limit, stated in the class remarks.
    /// </summary>
    private static readonly Regex DelegateParameterRegex = new(
        @"\b(?:Action|Func|Predicate|Converter|Comparison|EventHandler|Delegate|\w+(?:Callback|Handler|Delegate))\b",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static int MatchingClose(string text, int open)
    {
        int depth = 0;
        for (int index = open; index < text.Length; index++)
        {
            if (text[index] == '(')
            {
                depth++;
            }
            else if (text[index] == ')' && --depth == 0)
            {
                return index;
            }
        }

        throw new InvalidOperationException($"No closing parenthesis for the one at {open}.");
    }

    /// <summary>
    /// <paramref name="seeds"/> and every member of the class they reach by naming it, level after level until
    /// nothing new is reached (second review S-2). Bodies come from all the class's files, read as code only.
    /// <c>nameof(...)</c> is not a call, <c>new X(</c> constructs a type rather than calling a member, and the class's
    /// own name is its constructor, which no member calls by name. A property, an indexer (named <c>this</c>) or a
    /// field is a member like a method: naming it pulls its body in (third review severe-2).
    /// </summary>
    private static Dictionary<string, string[]> Closure((string Path, string Text)[] classFiles, string[] seeds)
    {
        Dictionary<string, List<string>> bodies = new(StringComparer.Ordinal);
        foreach ((string _, string text) in classFiles)
        {
            Lexed lexed = Lex(text);
            foreach (MemberSpan span in MemberSpans(lexed).Where(span => span.Name != Unnamed))
            {
                if (!bodies.TryGetValue(span.Name, out List<string>? body))
                {
                    body = [];
                    bodies[span.Name] = body;
                }

                body.AddRange(lexed.Code[span.Start..(span.End + 1)]);
            }
        }

        string[] callable = [.. bodies.Keys.Where(name => name != ClassName)];
        Dictionary<string, string[]> reached = new(StringComparer.Ordinal);
        Queue<string> pending = new(seeds);
        while (pending.TryDequeue(out string? member))
        {
            if (reached.ContainsKey(member) || !bodies.TryGetValue(member, out List<string>? body))
            {
                continue;
            }

            reached[member] = [.. body];
            string code = NameofRegex.Replace(string.Join('\n', body), string.Empty);
            foreach (string other in callable.Where(other => other != member))
            {
                // `this.X` and `WireToGateBusinessService.X` are this class's members (fourth review medium-3: a static
                // member called through the class name was not followed). `other.X` on some other expression is not
                // followed -- it may be another type's member of the same name.
                string pattern = other == "this"
                    ? @"(?<![\w.])this\s*\["
                    : $@"(?<![\w.])(?<!new\s)(?:(?:this|{ClassName})\s*\.\s*)?{Regex.Escape(other)}\b";
                if (Regex.IsMatch(code, pattern, RegexOptions.CultureInvariant))
                {
                    pending.Enqueue(other);
                }
            }
        }

        return reached;
    }

    /// <summary>Every field (<c>_camelCase</c>, with or without <c>this.</c>) named in the given bodies, sorted.</summary>
    private static string[] FieldsUsedBy(Dictionary<string, string[]> bodies) =>
    [
        .. bodies.Values
            .SelectMany(body => body)
            .SelectMany(line => FieldTokenRegex.Matches(line).Select(match => match.Groups["field"].Value))
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
    ];

    /// <summary>Every persistence-API word in the given bodies, with the member it is in.</summary>
    private static (string Member, string Api)[] PersistenceApis(Dictionary<string, string[]> bodies) =>
    [
        .. bodies
            .SelectMany(entry => entry.Value
                .SelectMany(line => PersistenceApiRegex.Matches(line).Select(match => (entry.Key, match.Value))))
            .Distinct()
    ];

    /// <summary>The whole class, all four files, as the closure reads it.</summary>
    private static (string Path, string Text)[] ClassFiles()
    {
        string root = ProtocolIdentityArchitectureTests.RepositoryRoot();
        return
        [
            .. Directory.EnumerateFiles(Path.Combine(root, "src", "SQCD.Agv.Wpf"), $"{ClassName}*.cs")
                .Select(path => (Path.GetRelativePath(root, path).Replace('\\', '/'), File.ReadAllText(path)))
                .Order()
        ];
    }

    /// <summary>A synthetic class, as the closure reads it.</summary>
    private static Dictionary<string, string[]> SyntheticClosure(string members, params string[] seeds) =>
        Closure([("synthetic.cs", ClassBody(members))], seeds);

    /// <summary>The fields reached from <paramref name="seeds"/> in one source, closure included.</summary>
    private static string[] FieldsUsedBy(string source, string[] seeds) =>
        FieldsUsedBy(Closure([("source.cs", source)], seeds));

    /// <summary>The persistence-API words reached from <paramref name="seeds"/> in one source, closure included.</summary>
    private static (string Member, string Api)[] PersistenceApis(string source, string[] seeds) =>
        PersistenceApis(Closure([("source.cs", source)], seeds));

    /// <summary>
    /// Every (file, token) pair where a file other than <paramref name="home"/> names a mark or a setter. In a
    /// <c>.cs</c> file comments are removed and strings are kept (a reflective read names the field in a string); in
    /// every other kind of file any mention counts.
    /// </summary>
    /// <remarks>
    /// A <c>.cs</c> file this lexer cannot read -- today one, <c>SqliteWireToGateJournal.cs</c>, whose SQL is in raw
    /// strings -- is scanned as raw text instead. That only adds matches (a mention in a comment would count), so it
    /// errs towards red, never towards a missed leak. The class's own files get no such fallback: the lexer refuses
    /// them outright, because every other check cuts members from them.
    /// </remarks>
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
                        ? KeptOrRaw(file.Text)
                        : file.Text;
                    return tokens
                        .Where(token => Regex.IsMatch(text, $@"(?<![\w]){Regex.Escape(token)}\b", RegexOptions.CultureInvariant))
                        .Select(token => (file.Path, token));
                })
        ];
    }

    private static string KeptOrRaw(string text)
    {
        try
        {
            return string.Join('\n', Lex(text).Kept);
        }
        catch (NotSupportedException)
        {
            return text;
        }
    }

    /// <summary>The comment-free view of a source, strings kept.</summary>
    private static string[] StripComments(string source) => Lex(source).Kept;

    // ------------------------------------------------------------------------------------------------------------
    // The lexer (third review). Four rounds of regular expressions approximating C# each missed a shape the next
    // review found: a `//` inside a string cut the rest of the line away as a comment; `/*` in one string and `*/`
    // in another swallowed the code between them; a brace in an unusual literal could shift every member after it.
    // This walks the text character by character instead, the way the compiler reads literals and comments.
    // ------------------------------------------------------------------------------------------------------------

    /// <summary>
    /// Lexes C# source into <see cref="Lexed"/>. Handles regular, verbatim (<c>@"</c>) and interpolated (<c>$"</c>,
    /// <c>$@"</c>, <c>@$"</c>) strings -- an interpolation hole is code, lexed as code, nested strings and all --
    /// character literals, <c>//</c> and <c>/* */</c> comments, and preprocessor lines. A raw string literal
    /// (<c>"""</c>) is refused with <see cref="NotSupportedException"/>: the guard cannot read it, and saying so is
    /// the point -- a lexer that guesses is what the four rounds before this one were.
    /// </summary>
    private static Lexed Lex(string source)
    {
        string text = source.Replace("\r\n", "\n", StringComparison.Ordinal);
        char[] code = text.ToCharArray();
        char[] kept = text.ToCharArray();
        int position = 0;
        LexCode(text, ref position, code, kept, inHole: false);
        return new Lexed(new string(code).Split('\n'), new string(kept).Split('\n'));
    }

    private static void LexCode(string text, ref int position, char[] code, char[] kept, bool inHole)
    {
        int nesting = 0;
        bool lineStart = true;
        while (position < text.Length)
        {
            char current = text[position];
            if (inHole && nesting == 0 && current == '}')
            {
                return;
            }

            if (current == '\n')
            {
                lineStart = true;
                position++;
                continue;
            }

            if (lineStart && current == '#' && !inHole)
            {
                int end = text.IndexOf('\n', position);
                end = end < 0 ? text.Length : end;
                Blank(code, position, end);
                Blank(kept, position, end);
                position = end;
                continue;
            }

            if (!char.IsWhiteSpace(current))
            {
                lineStart = false;
            }

            if (current == '/' && At(text, position + 1) == '/')
            {
                int end = text.IndexOf('\n', position);
                end = end < 0 ? text.Length : end;
                Blank(code, position, end);
                Blank(kept, position, end);
                position = end;
            }
            else if (current == '/' && At(text, position + 1) == '*')
            {
                int end = text.IndexOf("*/", position + 2, StringComparison.Ordinal);
                if (end < 0)
                {
                    throw new NotSupportedException($"Unterminated block comment at {position}.");
                }

                Blank(code, position, end + 2);
                Blank(kept, position, end + 2);
                position = end + 2;
            }
            else if (current == '"' && At(text, position + 1) == '"' && At(text, position + 2) == '"')
            {
                throw RawStringRefused(text, position);
            }
            else if (current is '$' or '@')
            {
                int quote = position;
                bool interpolated = false;
                bool verbatim = false;
                while (quote < text.Length && text[quote] is '$' or '@')
                {
                    interpolated |= text[quote] == '$';
                    verbatim |= text[quote] == '@';
                    quote++;
                }

                if (At(text, quote) != '"')
                {
                    position++;
                    continue;
                }

                if (quote - position > 2 || (interpolated && text[position..quote].Count(character => character == '$') > 1)
                    || (At(text, quote + 1) == '"' && At(text, quote + 2) == '"'))
                {
                    throw RawStringRefused(text, position);
                }

                position = quote;
                LexString(text, ref position, code, kept, verbatim, interpolated);
            }
            else if (current == '"')
            {
                LexString(text, ref position, code, kept, verbatim: false, interpolated: false);
            }
            else if (current == '\'')
            {
                int end = position + 1;
                if (At(text, end) == '\\')
                {
                    end++;
                }

                end = text.IndexOf('\'', end + 1);
                if (end < 0)
                {
                    throw new NotSupportedException($"Unterminated character literal at {position}.");
                }

                Blank(code, position + 1, end);
                position = end + 1;
            }
            else
            {
                if (inHole)
                {
                    nesting += current is '{' or '(' or '[' ? 1 : current is '}' or ')' or ']' ? -1 : 0;
                }

                position++;
            }
        }

        if (inHole)
        {
            throw new NotSupportedException("Unterminated interpolation hole.");
        }
    }

    /// <summary>
    /// A string starting at the opening quote under <paramref name="position"/>. Its text is blanked in the code
    /// view; an interpolation hole's braces are blanked and its inside is lexed as code.
    /// </summary>
    private static void LexString(string text, ref int position, char[] code, char[] kept, bool verbatim, bool interpolated)
    {
        int start = position;
        position++;
        while (position < text.Length)
        {
            char current = text[position];
            if (!verbatim && current == '\\')
            {
                position += 2;
                continue;
            }

            if (!verbatim && current == '\n')
            {
                throw new NotSupportedException($"Newline inside a regular string starting at {start}.");
            }

            if (current == '"')
            {
                if (verbatim && At(text, position + 1) == '"')
                {
                    position += 2;
                    continue;
                }

                Blank(code, start + 1, position);
                position++;
                return;
            }

            if (interpolated && current is '{' or '}')
            {
                if (At(text, position + 1) == current)
                {
                    position += 2;
                    continue;
                }

                if (current == '}')
                {
                    throw new NotSupportedException($"Unbalanced '}}' in an interpolated string at {position}.");
                }

                // Blank the text before the hole and the hole's opening brace, lex the hole as code, then carry on
                // with the string after the hole's closing brace.
                Blank(code, start + 1, position + 1);
                position++;
                LexCode(text, ref position, code, kept, inHole: true);
                code[position] = ' ';
                start = position;
                position++;
                continue;
            }

            position++;
        }

        throw new NotSupportedException($"Unterminated string starting at {start}.");
    }

    private static NotSupportedException RawStringRefused(string text, int position) => new(
        $"This guard does not read raw string literals (\"\"\"), found at line {text[..position].Count(character => character == '\n') + 1}. "
        + "Extend the lexer in RecoveryMarkerPersistenceArchitectureTests before using one in this class; guessing "
        + "at it is how a member boundary shifts without anything turning red.");

    private static char At(string text, int index) => index < text.Length ? text[index] : '\0';

    private static void Blank(char[] characters, int from, int to)
    {
        for (int index = from; index < to && index < characters.Length; index++)
        {
            if (characters[index] != '\n')
            {
                characters[index] = ' ';
            }
        }
    }

    private const string Unnamed = "(unnamed)";

    /// <summary>
    /// The class body cut into members over the code view, character by character. A member starts at the first
    /// code character at class depth and ends at one of two places:
    /// <list type="bullet">
    /// <item>a <c>;</c> at class depth, outside parentheses and brackets; or</item>
    /// <item>a <c>}</c> that brings the depth back to the class's, <b>only if</b> the member has had no <c>=</c> or
    /// <c>=&gt;</c> at class depth before it and the next code character is not <c>=</c>. A method, a nested type or a
    /// property with accessors ends there; an expression-bodied member, a field initializer and an auto-property
    /// initializer (<c>{ get; } = x;</c>) go on to their <c>;</c>.</item>
    /// </list>
    /// The fourth review found the line-based cut before this one ending a member on any line at class depth whose code
    /// ended in <c>}</c>: <c>=&gt; x is { State: "A" }</c> followed by <c>|| ...</c> on the next line was two members,
    /// the second named after whatever came next (<c>Read</c>, <c>null</c>). Three members of this class were cut
    /// that way, and the tail of each was followed into by no one. Naming and depth checks cannot see that -- both
    /// halves had names and the depth ended at zero -- so <see cref="TheLexerNamesEveryMemberOfTheClass"/> also
    /// checks that no member starts with a continuation token.
    /// Spans are whole lines: two members on one line share it (a limit, stated in the class remarks). The depth must
    /// be back at zero at the end of the file -- otherwise the cut is wrong somewhere and this says so instead of
    /// cutting on (third review M-2).
    /// </summary>
    private static MemberSpan[] MemberSpans(Lexed lexed)
    {
        string[] code = lexed.Code;
        List<MemberSpan> spans = [];
        int depth = 0;
        int grouping = 0;
        bool assigns = false;
        (int Line, int Column)? start = null;
        for (int index = 0; index < code.Length; index++)
        {
            string line = code[index];
            for (int column = 0; column < line.Length; column++)
            {
                char current = line[column];
                if (char.IsWhiteSpace(current))
                {
                    continue;
                }

                if (depth == 1 && start is null)
                {
                    start = (index, column);
                    grouping = 0;
                    assigns = false;
                }

                bool ends = false;
                switch (current)
                {
                    case '{':
                        depth++;
                        break;
                    case '}':
                        depth--;
                        ends = depth == 1 && grouping == 0 && !assigns && NextCode(code, index, column + 1) != '=';
                        break;
                    case '(' or '[' when depth == 1:
                        grouping++;
                        break;
                    case ')' or ']' when depth == 1:
                        grouping--;
                        break;
                    case ';':
                        ends = depth == 1 && grouping == 0;
                        break;
                    case '=' when depth == 1 && grouping == 0:
                        // `=` and `=>`; not `==`, `!=`, `<=`, `>=` (an operator declaration's name).
                        assigns |= At(line, column + 1) != '=' && (column == 0 || line[column - 1] is not ('=' or '!' or '<' or '>'));
                        break;
                }

                if (ends && start is (int firstLine, int firstColumn))
                {
                    string text = firstLine == index
                        ? code[index][firstColumn..(column + 1)]
                        : code[firstLine][firstColumn..] + "\n"
                          + string.Join('\n', code[(firstLine + 1)..index]) + (index > firstLine + 1 ? "\n" : string.Empty)
                          + code[index][..(column + 1)];

                    spans.Add(new MemberSpan(NameOf(text), firstLine, index, text));
                    start = null;
                }
            }
        }

        if (depth != 0)
        {
            throw new InvalidOperationException(
                $"Brace depth ends at {depth}, not 0: the member cut cannot be trusted for this source.");
        }

        return [.. spans];
    }

    /// <summary>The next code character after (<paramref name="line"/>, <paramref name="column"/>), or <c>'\0'</c>.</summary>
    private static char NextCode(string[] code, int line, int column)
    {
        for (int index = line; index < code.Length; index++)
        {
            string text = code[index];
            for (int position = index == line ? column : 0; position < text.Length; position++)
            {
                if (!char.IsWhiteSpace(text[position]))
                {
                    return text[position];
                }
            }
        }

        return '\0';
    }

    /// <summary>
    /// A member whose code starts with one of these is the tail of the member before it, cut in the wrong place: no
    /// declaration starts with an operator, a member access or a pattern keyword (fourth review severe-A).
    /// </summary>
    private static readonly Regex ContinuationStartRegex = new(
        @"^\s*(?:[&|?:.,;)\]}=+\-*/%^<>!]|(?:is|as|with|switch|when|and|or|not)\b)",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly HashSet<string> DeclarationKeywords = new(StringComparer.Ordinal)
    {
        "public", "private", "protected", "internal", "static", "readonly", "async", "override", "sealed", "virtual",
        "abstract", "unsafe", "extern", "partial", "new", "const", "volatile", "event", "implicit", "explicit",
        "required", "file", "ref", "fixed"
    };

    /// <summary>
    /// A member's name from its header, read token by token over code: the identifier directly before the first
    /// parameter list's <c>(</c> (a <c>(</c> after a keyword or <c>&lt;</c> opens a tuple type and is skipped, so
    /// <c>private (bool A, string B) Name(</c> and <c>Task&lt;(bool, string)&gt; Name(</c> give <c>Name</c>); otherwise the
    /// last identifier before <c>=&gt;</c>, <c>=</c>, <c>{</c> or <c>;</c> (a property, expression-bodied or not, an
    /// event, a field). An indexer is <c>this</c>. Attributes before the header and array brackets in a type are
    /// skipped; generic arguments do not hide the name before them (third review severe-1, severe-2).
    /// </summary>
    private static string NameOf(string header)
    {
        string? last = null;
        string? previous = null;
        int position = 0;
        while (position < header.Length)
        {
            char current = header[position];
            if (char.IsWhiteSpace(current))
            {
                position++;
                continue;
            }

            if (char.IsLetter(current) || current == '_' || current == '@')
            {
                int end = position + 1;
                while (end < header.Length && (char.IsLetterOrDigit(header[end]) || header[end] == '_'))
                {
                    end++;
                }

                last = header[position..end].TrimStart('@');
                previous = last;
                position = end;
                continue;
            }

            if (current == '=' && At(header, position + 1) == '>')
            {
                return last ?? Unnamed;
            }

            if (current is '=' or '{' or ';')
            {
                return last ?? Unnamed;
            }

            if (current == '[')
            {
                if (last == "this")
                {
                    return "this";
                }

                position = SkipBalanced(header, position, '[', ']');
                continue;
            }

            if (current == '<')
            {
                position = SkipBalanced(header, position, '<', '>');
                continue;
            }

            if (current == '(')
            {
                if (previous is not null && !DeclarationKeywords.Contains(previous) && previous == last)
                {
                    return previous;
                }

                position = SkipBalanced(header, position, '(', ')');
                previous = ")";
                continue;
            }

            previous = current.ToString();
            position++;
        }

        return last ?? Unnamed;
    }

    private static int SkipBalanced(string text, int position, char open, char close)
    {
        int depth = 0;
        for (int index = position; index < text.Length; index++)
        {
            if (text[index] == open)
            {
                depth++;
            }
            else if (text[index] == close && --depth == 0)
            {
                return index + 1;
            }
        }

        return text.Length;
    }

    private static string ClassBody(string members) =>
        $"public sealed class Synthetic{Environment.NewLine}{{{Environment.NewLine}{members}{Environment.NewLine}}}{Environment.NewLine}";

    private static string ReadProductFile(string relativePath) => File.ReadAllText(Path.Combine(
        ProtocolIdentityArchitectureTests.RepositoryRoot(),
        relativePath.Replace('/', Path.DirectorySeparatorChar)));
}
