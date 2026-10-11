# Mutation self-check for hmi#293 after review: each mutant must turn at least one targeted test red.
import subprocess
VM='src/SQCD.Agv.Wpf/ViewModels/MainViewModel.cs'
BS='src/SQCD.Agv.Wpf/WireToGateBusinessService.ManualChargingReturn.cs'
RV='src/SQCD.Agv.Wpf/WireToGateBusinessService.RecoveryVectors.cs'
TX='src/SQCD.Agv.Application/WireToGateManualChargingReturnText.cs'
G2F="FullyQualifiedName~ManualChargingReturn|FullyQualifiedName~FatalFaultLatchViewModelTests|FullyQualifiedName~RecoveryEntryNotificationViewModelTests"
UF="FullyQualifiedName~WireToGateManualChargingReturnTextTests"
helper = """    private bool ProofConfiguredForLine() =>
        !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(_recoveryOptions.AuthenticationProofEnvironmentVariable));

    public Task<bool> RequestManualChargingReturnToServiceAsync("""
muts=[
 ("M1 展示路径锁存分支 return 前不重算说明", [(VM, "            CanRequestHardwareRepairRelease = false;\n            RefreshManualChargingReturnGuidanceCore();\n            return;", "            CanRequestHardwareRepairRelease = false;\n            return;")]),
 ("M2 输入刷新路径不重算说明", [(VM, "            AllowRecoveryEntry(_wireToGateCanRequestManualChargingReturn?.Invoke() == true);\n        RefreshManualChargingReturnGuidanceCore();", "            AllowRecoveryEntry(_wireToGateCanRequestManualChargingReturn?.Invoke() == true);")]),
 ("M3 原因删掉会话分支（离线也说未开权限）", [(BS, "            : !SessionCanCarryOperatorRequest()\n                ? WireToGateManualChargingReturnBlocker.SessionNotReady\n                : WireToGateManualChargingReturnBlocker.OperatorNotAuthorized;", "            : WireToGateManualChargingReturnBlocker.OperatorNotAuthorized;")]),
 ("M4 视图模型里的保持写死为 true", [(VM, "        _manualChargingHold = snapshot.VehicleBusinessState?.ManualChargingHold == true;", "        _manualChargingHold = true;")]),
 ("M5 按钮可按时仍显示说明", [(TX, "if (!manualChargingHold || entryOffered)", "if (!manualChargingHold)")]),
 ("R3a 展示路径正常分支不重算说明（锁存解除只走这条）", [(VM, "        CanRequestManualChargingReturn = _wireToGateCanRequestManualChargingReturn?.Invoke() == true;\n        RefreshManualChargingReturnGuidanceCore();", "        CanRequestManualChargingReturn = _wireToGateCanRequestManualChargingReturn?.Invoke() == true;")]),
 ("R4b 原因经新 helper 读 proof", [(BS, "        CanRequestManualChargingReturnToService\n            ? WireToGateManualChargingReturnBlocker.None", "        CanRequestManualChargingReturnToService && ProofConfiguredForLine()\n            ? WireToGateManualChargingReturnBlocker.None"), (BS, "    public Task<bool> RequestManualChargingReturnToServiceAsync(", helper)]),
 ("R4c 视图模型重算说明时读 proof 环境变量", [(VM, "    private void RefreshManualChargingReturnGuidanceCore()\n    {\n", "    private void RefreshManualChargingReturnGuidanceCore()\n    {\n        _ = Environment.GetEnvironmentVariable(\"CONTROL_SERVER_RECOVERY_PROOF\");\n")]),
 ("R5x 架空请求路径开头的本地检查", [(BS, "        if (!CanRequestManualChargingReturnToService)\n        {\n            PublishOperatorResponse(", "        if (_session is null)\n        {\n            PublishOperatorResponse(")]),
 ("R8 按钮条件去掉 session.Connected（集成分支原有代码）", [(RV, "        if (!session.Connected\n            || session.Readiness", "        if (session.Readiness")]),
]
for name, edits in muts:
    files = sorted({f for f, _, _ in edits})
    for f, old, new in edits:
        s = open(f, encoding='utf-8', newline='').read()
        assert s.count(old) == 1, (name, f, s.count(old))
        open(f, 'w', encoding='utf-8', newline='').write(s.replace(old, new))
    b = subprocess.run(['dotnet', 'build', 'tests/SQCD.Agv.WireToGateG2Tests'], capture_output=True, text=True, encoding='utf-8', errors='replace')
    if b.returncode != 0:
        print(name, '=> BUILD FAILED'); print([l for l in b.stdout.splitlines() if ' error ' in l][:2])
    else:
        r = subprocess.run(['dotnet', 'test', 'tests/SQCD.Agv.WireToGateG2Tests', '--no-build', '--filter', G2F], capture_output=True, text=True, encoding='utf-8', errors='replace')
        u = subprocess.run(['dotnet', 'test', 'tests/SQCD.Agv.UnitTests', '--filter', UF], capture_output=True, text=True, encoding='utf-8', errors='replace')
        lines = [l.strip() for l in (r.stdout + u.stdout).splitlines() if l.strip().startswith('Failed ') or 'Failed!' in l or 'Passed!' in l]
        print(name, '=>', 'RED（被抓住）' if (r.returncode or u.returncode) else 'GREEN（未抓住）')
        for l in lines[:8]: print('   ', l)
    subprocess.run(['git', 'checkout', '--'] + files)
