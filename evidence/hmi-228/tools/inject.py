"""Fault and delay injection for onboard-hmi#228, restored from backups (never git checkout).

usage:
  python inject.py apply <name> [<name> ...]   restore everything from backup, then apply the named injections
  python inject.py restore                     restore product and fixture files from backup and touch them
  python inject.py tests old|new               put the changed test files back to HEAD (old) or to this ticket's (new)
  python inject.py list

Every replacement must match exactly once; a miss aborts before anything is written, so an injection that did
not apply cannot pass for a green run. Every injected line carries the marker HMI228-INJECT.
"""
import io
import os
import shutil
import subprocess
import sys
import time

WT = 'C:/Users/szy/Desktop/8005-workspace-v2/worktrees/hmi228-8005-agv-onboard-hmi/'
BACKUP = 'C:/Users/szy/Desktop/8005-workspace-v2/scratch/hmi228/backup/'
TESTS = 'tests/SQCD.Agv.WireToGateG2Tests/'
VM = 'src/SQCD.Agv.Wpf/ViewModels/MainViewModel.cs'
BUS = 'src/SQCD.Agv.Wpf/WireToGateBusinessService.cs'
BUSRV = 'src/SQCD.Agv.Wpf/WireToGateBusinessService.RecoveryVectors.cs'
SES = 'src/SQCD.Agv.Infrastructure/WireToGateSessionClient.cs'
IDLE = 'src/SQCD.Agv.Application/WireToGateIdleReturnText.cs'
FAKE = TESTS + 'FakeControlServer.cs'
GUARDED = [VM, BUS, BUSRV, SES, IDLE, FAKE]

M = ' // HMI228-INJECT'

INJECTIONS = {
    # ---- fixed delays: the old wait must go red, the new one stay green -------------------------------------
    'delay-idle-status': [(VM,
        '        IdleReturnStatus = WireToGateIdleReturnText.Status(snapshot);\n',
        '        IdleReturnStatus = WireToGateIdleReturnText.Status(snapshot);\n'
        '        if (IdleReturnStatus == WireToGateIdleReturnText.AtWaitingPointStatus) { Thread.Sleep(300); }' + M + '\n')],
    'delay-loading-phase': [(VM,
        '        _loadingPhase = snapshot.VehicleBusinessState?.LoadingPhase;\n        RefreshLoadingPhaseCore();\n',
        '        if (snapshot.VehicleBusinessState?.LoadingPhase?.State == "CLOSED") { Thread.Sleep(500); }' + M + '\n'
        '        _loadingPhase = snapshot.VehicleBusinessState?.LoadingPhase;\n        RefreshLoadingPhaseCore();\n')],
    'delay-rejection-banner': [(VM,
        '            TrimLogs();\n            _operatorNotice = message;\n',
        '            TrimLogs();\n            Thread.Sleep(300);' + M + '\n            _operatorNotice = message;\n')],
    'delay-cancel-entry': [
        (VM,
         '        CanSubmit = _wireToGateCanSubmit?.Invoke() ?? CanSubmit;\n',
         '        CanSubmit = _wireToGateCanSubmit?.Invoke() ?? CanSubmit;\n'
         '        if (!CanSubmit && CanRequestLoadCancellation) { Thread.Sleep(300); }' + M + '\n'),
        (VM,
         '        CanSubmit = _wireToGateCanSubmit?.Invoke() ?? snapshot.State == OnboardState.ReadyToScan;\n',
         '        CanSubmit = _wireToGateCanSubmit?.Invoke() ?? snapshot.State == OnboardState.ReadyToScan;\n'
         '        if (!CanSubmit && CanRequestLoadCancellation) { Thread.Sleep(300); }' + M + '\n')],
    'delay-fake-server-handling': [
        (FAKE,
         '                RecordMessage(connectionIndex, context.ReceivedOrder, messageType, messageId, line);\n',
         '                RecordMessage(connectionIndex, context.ReceivedOrder, messageType, messageId, line);\n'
         '                if (messageType == "SafetyStateSnapshot"\n'
         '                    || (messageType == "SnapshotAppliedAck" && line.Contains("EXCEPTION_RECOVERY_SESSION", StringComparison.Ordinal))\n'
         '                    || (messageType == "SafetyStateChanged" && Interlocked.Increment(ref _hmi228SafetyChanges) == 4))\n'
         '                {\n'
         '                    await Task.Delay(300, stoppingToken).ConfigureAwait(false);' + M + '\n'
         '                }\n'),
        (FAKE,
         '    private async Task HandleConnectionAsync(\n',
         '    private int _hmi228SafetyChanges;' + M + '\n\n    private async Task HandleConnectionAsync(\n')],
    'delay-after-rejection-log': [(VM,
        '            TrimLogs();\n            // 又有事发生了',
        '            TrimLogs();\n            if (operatorEvent.Kind == "SUBLOT_REJECTED") { Thread.Sleep(300); }' + M + '\n            // 又有事发生了')],
    # ---- probes: a fixed gap at each layer boundary, to let the whole suite say which tests read across it ----
    'probe-journey-gap': [(SES,
        '            Volatile.Write(ref _journey, updated);\n        }\n\n        JourneyChanged?.Invoke(this, new ValueChangedEventArgs<WireToGateJourneySnapshot>(updated));\n',
        '            Volatile.Write(ref _journey, updated);\n        }\n\n        Thread.Sleep(200);' + M + '\n'
        '        JourneyChanged?.Invoke(this, new ValueChangedEventArgs<WireToGateJourneySnapshot>(updated));\n')],
    'probe-operator-event-gap': [(BUS,
        '        if (!_operatorEventDeduplicator.ShouldPublish(deduplicationKey))\n',
        '        Thread.Sleep(100);' + M + '\n        if (!_operatorEventDeduplicator.ShouldPublish(deduplicationKey))\n')],
    'probe-session-state-gap': [(SES,
        '        StateChanged?.Invoke(this, new ValueChangedEventArgs<WireToGateSessionSnapshot>(snapshot));\n',
        '        Thread.Sleep(100);' + M + '\n'
        '        StateChanged?.Invoke(this, new ValueChangedEventArgs<WireToGateSessionSnapshot>(snapshot));\n')],
    'delay-completed-event': [(BUS,
        '                PublishOperatorEvent(\n                    $"operation-result:{command.SlotOperationAttemptId}:{execution.OverallOutcome}",\n',
        '                Thread.Sleep(400);' + M + '\n'
        '                PublishOperatorEvent(\n                    $"operation-result:{command.SlotOperationAttemptId}:{execution.OverallOutcome}",\n')],

    # ---- product regressions: the rewritten test must still go red ------------------------------------------
    # The vehicle never acknowledges the plan snapshot (ActivationResultAck.cs: three acks expected).
    'regress-no-plan-ack': [(SES,
        '        WireToGateEnvelope ack = WireToGateProtocolSerializer.Create(\n            "SnapshotAppliedAck",\n',
        '        if (snapshotKind == "UPCOMING_STOP_PLAN") { return; }' + M + '\n'
        '        WireToGateEnvelope ack = WireToGateProtocolSerializer.Create(\n            "SnapshotAppliedAck",\n')],
    # The vehicle never acknowledges a recovery session snapshot (ClosedSnapshotRetry.cs).
    'regress-closed-snapshot-not-acked': [(SES,
        '        WireToGateEnvelope ack = WireToGateProtocolSerializer.Create(\n            "SnapshotAppliedAck",\n',
        '        if (snapshotKind == "EXCEPTION_RECOVERY_SESSION") { return; }' + M + '\n'
        '        WireToGateEnvelope ack = WireToGateProtocolSerializer.Create(\n            "SnapshotAppliedAck",\n')],
    # The refusal is stored and never published to the operator (EntryRequestExpiry.cs, three tests).
    'regress-rejection-unpublished': [(BUS,
        '        PublishOperatorEvent(\n            $"sublot-rejected:{command.MessageId}",\n',
        '        if (Environment.TickCount64 >= 0) { return; }' + M + '\n'
        '        PublishOperatorEvent(\n            $"sublot-rejected:{command.MessageId}",\n')],
    # Once open, the scan entry on screen never shuts again (EntryRequestExpiry.cs: the waits for it to shut).
    'regress-sticky-scan-entry': [
        (VM,
         '        CanSubmit = _wireToGateCanSubmit?.Invoke() ?? CanSubmit;\n',
         '        CanSubmit = CanSubmit || (_wireToGateCanSubmit?.Invoke() ?? false);' + M + '\n'),
        (VM,
         '        CanSubmit = _wireToGateCanSubmit?.Invoke() ?? snapshot.State == OnboardState.ReadyToScan;\n',
         '        CanSubmit = CanSubmit || (_wireToGateCanSubmit?.Invoke() ?? snapshot.State == OnboardState.ReadyToScan);' + M + '\n')],
    # The cancel-before-scan entry no longer depends on an entry request being in hand (EntryRequestExpiry.cs).
    'regress-cancel-entry-stays': [(BUSRV,
        '        return session.Readiness == WireToGateSessionReadiness.Ready\n'
        '            && state.PendingLoadCancellation?.SlotOperationAttemptId is null\n'
        '            && FindLoadCancellationBeforeSublot(state, null).Availability\n'
        '                is not LoadCancellationBeforeSublotAvailability.None;\n',
        '        return session.Readiness == WireToGateSessionReadiness.Ready' + M + '\n'
        '            && state.PendingLoadCancellation?.SlotOperationAttemptId is null;\n')],
    # The closed-reason line is never shown (Paths.cs, restart).
    'regress-no-closed-line': [(VM,
        '        bool closed = view.Line == LoadingPhaseLine.Closed;\n',
        '        bool closed = view.Line == LoadingPhaseLine.Closed && Environment.TickCount64 < 0;' + M + '\n')],
    # Any newer worklist withdraws the entry request, the same stop's included (Paths.cs, local refusal).
    'regress-same-stop-advance-withdraws': [(BUS,
        '            || !EndsStopOf(worklist, request)\n',
        '            || worklist.Revision <= request.WorklistRevision' + M + '\n')],
    # A refused scan is logged and never put on the banner (Paths.cs, local refusal).
    'regress-no-rejection-banner': [(VM,
        '            _operatorNotice = message;\n            _operatorNoticeUntil = Clock.Now + OperatorNoticeHold;\n            Guidance = message;\n',
        '            _operatorNoticeUntil = Clock.Now + OperatorNoticeHold;' + M + '\n')],
    # A completed load with no recovery session open offers compensation (OccupancyConflictRecovery.cs).
    'regress-compensation-on-completed-load': [(BUSRV,
        '            return _session.Current.Readiness == WireToGateSessionReadiness.RecoveryRequired;\n',
        '            return Environment.TickCount64 >= 0;' + M + '\n')],
    # The visit text stays "on the way" after the waiting-point leg has arrived (WaitingPointIdleReturn).
    'regress-visit-text-stale': [(IDLE,
        '        return leg.State == "ARRIVED"\n            ? $"在等待点 {leg.StationId} 待命"\n            : $"空闲返回：前往等待点 {leg.StationId}";\n',
        '        return $"空闲返回：前往等待点 {leg.StationId}";' + M + '\n')],
    # The vehicle does not answer a mid-session SafetyStateSnapshotRequested (ExpectedActionOverdue.cs).
    'regress-no-mid-session-snapshot': [(SES,
        '        long? generation = await PublishMidSessionSnapshotAsync(\n            "SafetyStateSnapshot",\n',
        '        if (Environment.TickCount64 >= 0) { return false; }' + M + '\n'
        '        long? generation = await PublishMidSessionSnapshotAsync(\n            "SafetyStateSnapshot",\n')],
}


def read(path):
    return io.open(path, encoding='utf-8', newline='').read()


def write(path, text):
    io.open(path, 'w', encoding='utf-8', newline='').write(text)


def backup_path(rel):
    return BACKUP + rel.replace('/', '__') + '.orig'


def ensure_backups():
    os.makedirs(BACKUP, exist_ok=True)
    for rel in GUARDED:
        target = backup_path(rel)
        if not os.path.exists(target):
            text = read(WT + rel)
            assert 'HMI228-INJECT' not in text, f'{rel} is already injected; refusing to back it up'
            shutil.copy2(WT + rel, target)


def restore():
    ensure_backups()
    for rel in GUARDED:
        shutil.copyfile(backup_path(rel), WT + rel)
        os.utime(WT + rel, None)
    left = [rel for rel in GUARDED if 'HMI228-INJECT' in read(WT + rel)]
    assert not left, left


def apply(names):
    restore()
    texts = {}
    for name in names:
        for rel, old, new in INJECTIONS[name]:
            text = texts.get(rel) or read(WT + rel)
            assert text.count(old) == 1, (name, rel, text.count(old))
            texts[rel] = text.replace(old, new)
    for rel, text in texts.items():
        write(WT + rel, text)
        os.utime(WT + rel, None)
    print('applied', names, 'to', sorted(texts))


def changed_tests():
    out = subprocess.run(['git', '-C', WT, 'diff', '--name-only', 'HEAD', '--', TESTS],
                         capture_output=True, text=True, check=True).stdout.split()
    return [rel for rel in out if rel != FAKE]


def tests(which):
    new_dir = BACKUP + 'tests-new/'
    os.makedirs(new_dir, exist_ok=True)
    marker = BACKUP + 'tests-state.txt'
    state = read(marker).strip() if os.path.exists(marker) else 'new'
    if which == 'old':
        assert state == 'new', 'test files are already the HEAD versions'
        files = changed_tests()
        write(BACKUP + 'tests-changed.txt', '\n'.join(files))
        for rel in files:
            shutil.copyfile(WT + rel, new_dir + os.path.basename(rel))
            head = subprocess.run(['git', '-C', WT, 'show', 'HEAD:' + rel], capture_output=True, check=True).stdout
            open(WT + rel, 'wb').write(head)
            os.utime(WT + rel, None)
        write(marker, 'old')
        print('HEAD versions in place for', len(files), 'test files')
    else:
        assert state == 'old', 'test files are already this ticket\'s versions'
        files = read(BACKUP + 'tests-changed.txt').split()
        for rel in files:
            shutil.copyfile(new_dir + os.path.basename(rel), WT + rel)
            os.utime(WT + rel, None)
        write(marker, 'new')
        print('this ticket\'s versions back in place for', len(files), 'test files')


if __name__ == '__main__':
    command = sys.argv[1]
    if command == 'apply':
        apply(sys.argv[2:])
    elif command == 'restore':
        restore()
        print('restored')
    elif command == 'tests':
        tests(sys.argv[2])
    elif command == 'list':
        print('\n'.join(INJECTIONS))
    time.sleep(0)
