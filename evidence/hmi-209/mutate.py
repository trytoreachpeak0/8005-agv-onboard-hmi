# Mutation run for hmi#209. Each mutation is one exact text replacement; the build is --no-incremental, and the file
# is restored from git (HEAD) afterwards. Run from the worktree root. Pass mutation keys to run only those.
import subprocess, sys, re

G2 = ("FullyQualifiedName~EveryLoadedDemand|FullyQualifiedName~AForgottenHandoffMarker"
      "|FullyQualifiedName~AForcedMechanicalRecoveryIsAskedForTheChosenLoad|FullyQualifiedName~AnOpenSessionLocks"
      "|FullyQualifiedName~APreparedVectorLocks|FullyQualifiedName~AnUnknownHandoffOverASettledLoad"
      "|FullyQualifiedName~CompensationNamesItsOwnTarget|FullyQualifiedName~AfterTheLaterLoadIsUnloaded"
      "|FullyQualifiedName~ASelectionWhoseRowIsGone|FullyQualifiedName~WireToGateLoadEndedByVectorTests"
      "|FullyQualifiedName~ASingleDemandHandoffTheServerDidNotReconcile|FullyQualifiedName~AfterAForcedRecoveryAHandoffPress"
      "|FullyQualifiedName~APlanOfTheNextJourneyDropsWhatTheLastOneLeft"
      "|FullyQualifiedName~TheFallbackTargetIsTheSettledLoadOnlyWhileNothingIsArmed")
UNIT = ("FullyQualifiedName~WireToGateLoadedDemandsTests|FullyQualifiedName~ARecordedLoadPutsItsDemandOnBoard"
        "|FullyQualifiedName~RecoveryDemandChoiceXamlTests")

RV = "src/SQCD.Agv.Wpf/WireToGateBusinessService.RecoveryVectors.cs"
EX = "src/SQCD.Agv.Application/WireToGateSlotOperationExecutor.cs"
CORE = "src/SQCD.Agv.Core/WireToGateRecovery.cs"
JR = "src/SQCD.Agv.Infrastructure/SqliteWireToGateJournal.cs"
VMC = "src/SQCD.Agv.Wpf/ViewModels/MainViewModel.RecoveryDemandChoices.cs"
XAML = "src/SQCD.Agv.Wpf/MainWindow.xaml"
BS = "src/SQCD.Agv.Wpf/WireToGateBusinessService.cs"

MUTATIONS = [
    ("R0", "reverse check: only the last load is a subject (back to one stored context)", CORE,
     "        LoadsOnBoard\n        ?? (LastCompletedLoadOperationContext is { } last ? [new WireToGateLoadOnBoard(last)] : []);",
     "        (LastCompletedLoadOperationContext is { } last ? [new WireToGateLoadOnBoard(last)] : []);"),
    ("M1", "a settled vector does not mark its demand handed off", RV,
     "        vector.VectorType == WireToGateRecoveryVectorTypes.LoadCorrection\n            ? state\n            : state.WithLoadHandedOff(vector.DemandId);",
     "        state;"),
    ("M2", "recording a result does not touch the list", EX,
     "                ? context.OperationType == OperationType.Load\n                    ? state.WithLoadOnBoard(context)\n                    : state.WithoutLoadOnBoard(context.DemandId)\n                : state;",
     "                ? state\n                : state;"),
    ("M3", "with several subjects and none named, the last one is picked", RV,
     "            ? subjects.Count == 1 ? subjects[0] : null",
     "            ? subjects.Count >= 1 ? subjects[^1] : null"),
    ("M4", "the request path ignores the operator's choice", RV,
     "            ?? selectedDemandId\n            ?? (RecoverySubjects(state, action).Count > 1",
     "            ?? (RecoverySubjects(state, action).Count > 1"),
    ("M5", "the view model lets the button be pressed without a choice", VMC,
     "    public bool CanPressFaultCargoHandoff => CanRequestFaultCargoHandoff && !HasRecoveryDemandSelectionHint;",
     "    public bool CanPressFaultCargoHandoff => CanRequestFaultCargoHandoff;"),
    ("M6", "the journal drops the list on write", JR,
     "        LoadsOnBoard = state.LoadsOnBoard?\n            .Select(item => item with { Load = item.Load with { Slots = item.Load.Slots.Order().ToArray() } })\n            .ToArray()",
     "        LoadsOnBoard = null"),
    ("M7", "a late acknowledgement under a vector does not put the load on board", EX,
     "        WithLoadOnBoardRecorded(state, slotOperationAttemptId) with\n        {\n            PendingResults",
     "        state with\n        {\n            PendingResults"),
    ("M8", "the row container carries no ItemStatus", XAML,
     '                                    <Setter Property="AutomationProperties.ItemStatus" Value="{Binding DemandId}" />\n',
     ""),
    ("M9", "an open session's demand is not used to pick the subject", RV,
     "        if (FindRecoveryOperation(state, action, snapshot.DemandId) is not { } context)",
     "        if (FindRecoveryOperation(state, action, null) is not { } context)"),
    # The review's own mutations (r209-keep/rmut.py), same replacements.
    ("RA", "review RA: a correction also marks its demand handed off", RV,
     "            ? state\n            : state.WithLoadHandedOff(vector.DemandId);",
     "            ? state.WithLoadHandedOff(vector.DemandId)\n            : state.WithLoadHandedOff(vector.DemandId);"),
    ("RB", "review RB: forgetting a vector (UNKNOWN, FAILED) also marks its demand handed off", RV,
     "        WireToGateRecoveryState cleared = state with\n",
     "        WireToGateRecoveryState cleared = state.WithLoadHandedOff(context.DemandId) with\n"),
    ("RC", "review RC: an acknowledged isolation does not mark its demand", RV,
     "                    return WithLoadEndedBy(journalled, onFile) with\n",
     "                    return (isolation is null ? WithLoadEndedBy(journalled, onFile) : journalled) with\n"),
    ("RE", "review RE: a selection whose row is gone moves to the first row", VMC,
     "SelectedRecoveryDemandChoice = RecoveryDemandChoices.FirstOrDefault(row => row.DemandId == selected);",
     "SelectedRecoveryDemandChoice = RecoveryDemandChoices.FirstOrDefault(row => row.DemandId == selected) ?? (selected is null ? null : RecoveryDemandChoices.FirstOrDefault());"),
    # The fixes of the review round.
    ("S1a", "the list stays offered while a vector or an open session is locked on a demand", RV,
     "            if (LockedRecoveryDemandId(state) is not null || IsArmed(state, out _))",
     "            if (IsArmed(state, out _))"),
    ("S1b", "a press that names another demand than the locked one is overridden in silence", RV,
     "        if (lockedDemandId is not null\n            && selectedDemandId is not null",
     "        if (lockedDemandId is not null\n            && selectedDemandId is null"),
    ("S1c", "the target line ignores the locked demand", RV,
     "FindRecoveryOperation(state, FaultCargoHandoffAction, LockedRecoveryDemandId(state)) is { } subject",
     "FindRecoveryOperation(state, FaultCargoHandoffAction, null) is { } subject"),
    ("S3", "compensation never gets its own target line", VMC,
     "        bool separate = compensationDemandId is not null",
     "        bool separate = compensationDemandId is null"),
    # S4 as decided: marked, not taken off; the journey's closure and a plan of another journey take loads off.
    ("S4a", "the journey's closure does not empty the loads on board", CORE,
     "            return this with { LoadsOnBoard = [] };",
     "            return this;"),
    ("S4b", "a plan of another journey does not drop the loads of an earlier one", CORE,
     "                    || planned.Contains(item.JourneyAnchorDemandId, StringComparer.Ordinal))",
     "                    || true)"),
    ("S4c", "back to taking a handed-off demand off (the 8668d4a behaviour)", RV,
     "            : state.WithLoadHandedOff(vector.DemandId);",
     "            : state.WithoutLoadOnBoard(vector.DemandId);"),
    ("S4d", "the row of a handed-off demand does not say so", VMC,
     "                + (item.HandedOffAwaitingServer ? HandedOffAwaitingServerText : string.Empty)))",
     "))"),
    ("S4e", "the business service does not align the loads with the journey", BS,
     "        _ = UpdateLoadsOnBoardForJourneyAsync(args.Value);\n",
     ""),
    # The re-review's (r280-keep/mut280.py), same replacements, and the operator text of a pruning.
    ("MX7", "re-review MX7: the lone target line does not say it awaits the server", VMC,
     "RecoveryFallbackTargetText += HandedOffAwaitingServerText;",
     "RecoveryFallbackTargetText += string.Empty;"),
    ("MX8", "re-review MX8: only a closure with no purpose empties the list, not another kind of journey", CORE,
     "        journey?.VehicleBusinessState is { } business\n        && !string.Equals(business.ActivePurpose, TransportPurpose, StringComparison.Ordinal);",
     "        journey?.VehicleBusinessState is { } business\n        && business.ActivePurpose is null;"),
    ("PX1", "a plan's pruning to nothing is told as the journey's end", RV,
     "        bool closed = WireToGateRecoveryState.DescribesNoTransport(journey);",
     "        bool closed = after == 0;"),
]

def run(cmd):
    return subprocess.run(cmd, stdout=subprocess.PIPE, stderr=subprocess.STDOUT, text=True, encoding="utf-8", errors="replace", shell=True)

def summary(out):
    fails = sorted(set(re.findall(r"\.(\w+)(?:\([^)]*\))? \[FAIL\]", out)))
    totals = re.findall(r"(Failed!|Passed!)  - Failed: +(\d+), Passed: +(\d+)", out)
    return fails, totals

if __name__ == "__main__":
    only = set(sys.argv[1:])
    for key, why, path, old, new in MUTATIONS:
        if only and key not in only:
            continue
        text = open(path, encoding="utf-8", newline="").read()
        if text.count(old) != 1:
            print(f"{key} SKIPPED: pattern found {text.count(old)} times in {path}", flush=True)
            continue
        open(path, "w", encoding="utf-8", newline="").write(text.replace(old, new))
        try:
            b1 = run("dotnet build tests/SQCD.Agv.WireToGateG2Tests --no-incremental")
            b2 = run("dotnet build tests/SQCD.Agv.UnitTests --no-incremental")
            errs = [m for b in (b1, b2) for m in re.findall(r"(\d+) Error\(s\)", b.stdout)]
            if any(e != "0" for e in errs) or b1.returncode or b2.returncode:
                print(f"{key} BUILD FAILED ({why}) errors={errs}", flush=True)
                continue
            g2 = run(f'dotnet test tests/SQCD.Agv.WireToGateG2Tests --no-build --filter "{G2}"')
            un = run(f'dotnet test tests/SQCD.Agv.UnitTests --no-build --filter "{UNIT}"')
            f1, t1 = summary(g2.stdout)
            f2, t2 = summary(un.stdout)
            print(f"{key} ({why}) build 0 Error(s); G2 exit={g2.returncode} {t1} red={f1}; unit exit={un.returncode} {t2} red={f2}", flush=True)
        finally:
            run(f"git checkout HEAD -- {path}")
