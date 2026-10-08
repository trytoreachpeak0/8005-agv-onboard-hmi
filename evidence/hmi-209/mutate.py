# Mutation run for hmi#209. Each mutation is one exact text replacement; the build is --no-incremental, and the file
# is restored from git (HEAD) afterwards. Run from the worktree root.
import subprocess, sys, re

G2 = ("FullyQualifiedName~EveryLoadedDemand|FullyQualifiedName~AForgottenHandoffMarker|FullyQualifiedName~AnOpenSessionKeepsTheEntry"
      "|FullyQualifiedName~AForcedMechanicalRecoveryIsAskedForTheChosenLoad")
UNIT = ("FullyQualifiedName~WireToGateLoadedDemandsTests|FullyQualifiedName~ARecordedLoadPutsItsDemandOnBoard"
        "|FullyQualifiedName~RecoveryDemandChoiceXamlTests")

RV = "src/SQCD.Agv.Wpf/WireToGateBusinessService.RecoveryVectors.cs"
EX = "src/SQCD.Agv.Application/WireToGateSlotOperationExecutor.cs"
CORE = "src/SQCD.Agv.Core/WireToGateRecovery.cs"
JR = "src/SQCD.Agv.Infrastructure/SqliteWireToGateJournal.cs"
VMC = "src/SQCD.Agv.Wpf/ViewModels/MainViewModel.RecoveryDemandChoices.cs"
XAML = "src/SQCD.Agv.Wpf/MainWindow.xaml"

MUTATIONS = [
    ("R0", "reverse check: only the last load is a subject (back to one stored context)", CORE,
     "        LoadedDemandOperationContexts\n        ?? (LastCompletedLoadOperationContext is { } last ? [last] : []);",
     "        (LastCompletedLoadOperationContext is { } last ? [last] : []);"),
    ("M1", "a settled vector does not take its demand off the list", RV,
     "        vector.VectorType == WireToGateRecoveryVectorTypes.LoadCorrection\n            ? state\n            : state.WithoutLoadOnBoard(vector.DemandId);",
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
     "        LoadedDemandOperationContexts = state.LoadedDemandOperationContexts?\n            .Select(load => load with { Slots = load.Slots.Order().ToArray() })\n            .ToArray()",
     "        LoadedDemandOperationContexts = null"),
    ("M7", "a late acknowledgement under a vector does not put the load on board", EX,
     "        WithLoadOnBoardRecorded(state, slotOperationAttemptId) with\n        {\n            PendingResults",
     "        state with\n        {\n            PendingResults"),
    ("M8", "the row container carries no ItemStatus", XAML,
     '                                    <Setter Property="AutomationProperties.ItemStatus" Value="{Binding DemandId}" />\n',
     ""),
    ("M9", "an open session's demand is not used to pick the subject", RV,
     "        if (FindRecoveryOperation(state, action, snapshot.DemandId) is not { } context)",
     "        if (FindRecoveryOperation(state, action, null) is not { } context)"),
]

def run(cmd):
    return subprocess.run(cmd, stdout=subprocess.PIPE, stderr=subprocess.STDOUT, text=True, encoding="utf-8", errors="replace", shell=True)

def summary(out):
    fails = sorted(set(re.findall(r"\.(\w+)(?:\([^)]*\))? \[FAIL\]", out)))
    totals = re.findall(r"(Failed!|Passed!)  - Failed: +(\d+), Passed: +(\d+)", out)
    return fails, totals

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
