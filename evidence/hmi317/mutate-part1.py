import subprocess, sys

LOG = 'src/SQCD.Agv.Wpf/OperatorRecordOutput/OperatorRecordLog.cs'
APP = 'src/SQCD.Agv.Wpf/App.xaml.cs'

HANDLER_TRY = '''        try
        {
            if (IsDisabled)
            {
                return;
            }

            _entryProbe?.Invoke(nameof(OnLogsCollectionChanged));'''
HANDLER_CATCH = '''        catch (Exception failure)
        {
            Disable(nameof(OnLogsCollectionChanged), failure);
        }
    }

    private void Disable('''

DISABLE_LOG = '''        try
        {
            _logger.Write(
                LogSeverity.Warning,
                OnboardTechnicalLogCategory.LocalFault,
                nameof(OperatorRecordLog),
                $"操作记录落盘停用（{where}），本次运行不再写：{failure.GetType().Name}: {failure.Message}");
        }
        catch (Exception)
        {
            // The logger can throw (research 4.3, item 1); the record log is off either way.
        }'''

MUTATIONS = {
    # M1: the handler is no longer wrapped; a throwing logger escapes Logs.Add.
    'M1-handler-unwrapped': [(LOG, [
        (HANDLER_TRY, '''        {
            if (IsDisabled)
            {
                return;
            }

            _entryProbe?.Invoke(nameof(OnLogsCollectionChanged));'''),
        (HANDLER_CATCH, '''    }

    private void Disable('''),
    ])],
    # M2: the log write inside Disable loses its own catch.
    'M2-disable-log-unwrapped': [(LOG, [
        (DISABLE_LOG, '''        _logger.Write(
            LogSeverity.Warning,
            OnboardTechnicalLogCategory.LocalFault,
            nameof(OperatorRecordLog),
            $"操作记录落盘停用（{where}），本次运行不再写：{failure.GetType().Name}: {failure.Message}");'''),
    ])],
    # M3: the record log names an input box.
    'M3-reads-input-box': [(LOG, [
        ('    internal const string Prefix = "操作记录 ";',
         '    internal const string Prefix = "操作记录 ";\n\n    private static readonly string Probe = nameof(MainViewModel.ScanText);'),
    ])],
    # M4: trims are written too.
    'M4-trim-written': [(LOG, [
        ('''                    case NotifyCollectionChangedAction.Reset:''',
         '''                    case NotifyCollectionChangedAction.Remove:
                        lines.Add(Cleared(e.OldItems?.Count ?? 0));
                        break;
                    case NotifyCollectionChangedAction.Reset:'''),
    ])],
    # M5: the "turned off" line loses its explicit LocalFault category.
    'M5-category-dropped': [(LOG, [
        (DISABLE_LOG, DISABLE_LOG.replace('                OnboardTechnicalLogCategory.LocalFault,\n', '')),
    ])],
    # M6: App.StartOperatorRecordLog is no longer wrapped.
    'M6-app-start-unwrapped': [(APP, [
        ('''        try
        {
            FileAppLogger logger = _logger ?? throw new InvalidOperationException("日志器未初始化。");
            _operatorRecordLog = OperatorRecordLog.TryStart(settings.Logging, viewModel.Logs, logger);
        }
        catch (Exception exception)
        {
            try
            {
                _logger?.Write(LogSeverity.Warning, nameof(App), "操作记录落盘启动失败，本次运行不写。", exception);
            }
            catch (Exception)
            {
                // 日志器也可能抛（调研 4.3 第 1 条）；不落盘，车照常启动。
            }
        }''', '''        FileAppLogger logger = _logger ?? throw new InvalidOperationException("日志器未初始化。");
        _operatorRecordLog = OperatorRecordLog.TryStart(settings.Logging, viewModel.Logs, logger);'''),
    ])],
}

UNIT_FILTER = ('FullyQualifiedName~OperatorRecordLogArchitectureTests'
               '|FullyQualifiedName~ExplicitTechnicalLogCategoryPinTests')
G2_FILTER = ('FullyQualifiedName~SQCD.Agv.WireToGateG2Tests.OperatorRecordLogTests'
             '|FullyQualifiedName~SQCD.Agv.WireToGateG2Tests.OperatorRecordLogDispatcherTests')


def run(cmd):
    result = subprocess.run(cmd, capture_output=True, text=True, encoding='utf-8', errors='replace')
    tail = [line for line in result.stdout.splitlines() if 'Passed!' in line or 'Failed!' in line or ' error ' in line]
    return result.returncode, tail[-1].strip() if tail else result.stdout.strip().splitlines()[-1:]


def main(names):
    for name in names:
        edits = MUTATIONS[name]
        originals = {}
        try:
            for path, pairs in edits:
                text = originals.setdefault(path, open(path, encoding='utf-8', newline='').read())
                for old, new in pairs:
                    assert text.count(old) == 1, f'{name}: anchor not unique in {path}'
                    text = text.replace(old, new)
                open(path, 'w', encoding='utf-8', newline='').write(text)
            build_unit = run(['dotnet', 'build', 'tests/SQCD.Agv.UnitTests', '-v', 'q', '-nologo'])
            build_g2 = run(['dotnet', 'build', 'tests/SQCD.Agv.WireToGateG2Tests', '-v', 'q', '-nologo'])
            unit = run(['dotnet', 'test', 'tests/SQCD.Agv.UnitTests', '--no-build', '--filter', UNIT_FILTER])
            g2 = run(['dotnet', 'test', 'tests/SQCD.Agv.WireToGateG2Tests', '--no-build', '--filter', G2_FILTER])
            print(f'{name}: build unit={build_unit[0]} g2={build_g2[0]} | unit exit={unit[0]} {unit[1]} | g2 exit={g2[0]} {g2[1]}', flush=True)
        finally:
            for path, text in originals.items():
                open(path, 'w', encoding='utf-8', newline='').write(text)


if __name__ == '__main__':
    main(sys.argv[1:] or list(MUTATIONS))
