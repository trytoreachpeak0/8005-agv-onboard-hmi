"""Part-2 guard mutations for onboard-hmi#317. Each mutates exactly one file, runs the targeted tests, restores it."""
import subprocess, sys

VM = 'src/SQCD.Agv.Wpf/ViewModels/MainViewModel.cs'

MUTATIONS = {
    # P1: a write site bypasses AppendLogLine.
    'P1-bypass-helper': (VM, [(
        '''            AppendLogLine(new LogLineViewModel(
                Clock.Now,
                OperatorRecordKind.Warning,
                message,
                OperatorRecordSource.OperatorRejection));''',
        '''            Logs.Add(new LogLineViewModel(
                Clock.Now,
                OperatorRecordKind.Warning,
                message,
                OperatorRecordSource.OperatorRejection));''')]),
    # P2: a write site forgets its source.
    'P2-source-dropped': (VM, [(
        '''                $"{Describe(previous)} 的修正窗口已随{Describe(demandId)} 装货关闭。",
                OperatorRecordSource.CorrectionWindow));''',
        '''                $"{Describe(previous)} 的修正窗口已随{Describe(demandId)} 装货关闭。"));''')]),
    # P3: the rejection names the wrong source (the guard accepts any known source; G2 must catch it).
    'P3-source-wrong': (VM, [(
        '''                message,
                OperatorRecordSource.OperatorRejection));''',
        '''                message,
                OperatorRecordSource.CorrectionWindow));''')]),
    # P4: the helper no longer trims.
    'P4-no-trim': (VM, [(
        '''        Logs.Add(line);
        ClearLogsCommand.RaiseCanExecuteChanged();
        TrimLogs();''',
        '''        Logs.Add(line);
        ClearLogsCommand.RaiseCanExecuteChanged();''')]),
    # P5 (review S1): the operator event's attempt is dropped.
    'P5-event-attempt-null': (VM, [(
        '''                operatorEvent.Operation?.SlotOperationAttemptId,''',
        '''                null,''')]),
    # P6 (review S1): the controller record's operation id is dropped.
    'P6-controller-operation-null': (VM, [(
        '''            snapshot.ActiveOperation?.OperationId));''',
        '''            null));''')]),
    # P7 (review S1): the correction window and controller record sources swapped.
    'P7-sources-swapped': (VM, [
        ('''                OperatorRecordSource.CorrectionWindow));''', '''                OperatorRecordSource.ControllerRecord));'''),
        ('''            OperatorRecordSource.ControllerRecord,''', '''            OperatorRecordSource.CorrectionWindow,'''),
    ]),
}

UNIT_FILTER = 'FullyQualifiedName~OperatorRecordLogArchitectureTests'
G2_FILTER = ('FullyQualifiedName~SQCD.Agv.WireToGateG2Tests.OperatorEventVisibilityTests'
             '|FullyQualifiedName~SQCD.Agv.WireToGateG2Tests.OperatorRecordLogTests'
             '|FullyQualifiedName~TheClosedCorrectionWindowNoticeIsLoggedUnderItsOwnSource')


def run(cmd):
    result = subprocess.run(cmd, capture_output=True, text=True, encoding='utf-8', errors='replace')
    lines = result.stdout.splitlines()
    keep = [line.strip() for line in lines if 'Passed!' in line or 'Failed!' in line or ' error ' in line]
    failed = sorted({line.strip()[len('Failed '):].split(' [')[0].split('.')[-1]
                     for line in lines if line.startswith('  Failed ')})
    summary = keep[-1] if keep else ''
    return result.returncode, summary + (f' red={failed}' if failed else '')


def main(names):
    for name in names:
        path, pairs = MUTATIONS[name]
        original = open(path, encoding='utf-8', newline='').read()
        try:
            text = original
            for old, new in pairs:
                assert text.count(old) == 1, f'{name}: anchor not unique'
                text = text.replace(old, new)
            open(path, 'w', encoding='utf-8', newline='').write(text)
            changed = subprocess.run(['git', 'diff', '--name-only'], capture_output=True, text=True).stdout.split()
            assert changed == [path], f'{name}: expected exactly {path} changed, got {changed}'
            b_unit = run(['dotnet', 'build', 'tests/SQCD.Agv.UnitTests', '-v', 'q', '-nologo'])
            b_g2 = run(['dotnet', 'build', 'tests/SQCD.Agv.WireToGateG2Tests', '-v', 'q', '-nologo'])
            unit = run(['dotnet', 'test', 'tests/SQCD.Agv.UnitTests', '--no-build', '--filter', UNIT_FILTER])
            g2 = run(['dotnet', 'test', 'tests/SQCD.Agv.WireToGateG2Tests', '--no-build', '--filter', G2_FILTER])
            print(f'{name}: build unit={b_unit[0]} g2={b_g2[0]} | unit exit={unit[0]} {unit[1]} | g2 exit={g2[0]} {g2[1]}', flush=True)
        finally:
            open(path, 'w', encoding='utf-8', newline='').write(original)


if __name__ == '__main__':
    main(sys.argv[1:] or list(MUTATIONS))
