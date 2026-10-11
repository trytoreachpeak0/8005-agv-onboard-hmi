# Structural-guard mutations after re-review (R4b-R4e). The guard reads source text at run time: no rebuild needed.
import subprocess
VM='src/SQCD.Agv.Wpf/ViewModels/MainViewModel.cs'
BS='src/SQCD.Agv.Wpf/WireToGateBusinessService.ManualChargingReturn.cs'
ENV='Environment.GetEnvironmentVariable'
prop_bs = "    private bool ProofPresent =>\n        !string.IsNullOrWhiteSpace(" + ENV + "(_recoveryOptions.AuthenticationProofEnvironmentVariable));\n\n    public Task<bool> RequestManualChargingReturnToServiceAsync("
prop_vm = "    private static bool ProofSet => !string.IsNullOrWhiteSpace(" + ENV + "(\"CONTROL_SERVER_RECOVERY_PROOF\"));\n\n    private void RefreshManualChargingReturnGuidanceCore()"
helper_bs = "    private bool ProofConfiguredForLine() =>\n        !string.IsNullOrWhiteSpace(" + ENV + "(_recoveryOptions.AuthenticationProofEnvironmentVariable));\n\n    public Task<bool> RequestManualChargingReturnToServiceAsync("
muts=[
 ("R4b 原因经新 helper 方法读 proof", [(BS, "        CanRequestManualChargingReturnToService\n            ? WireToGateManualChargingReturnBlocker.None", "        CanRequestManualChargingReturnToService && ProofConfiguredForLine()\n            ? WireToGateManualChargingReturnBlocker.None"), (BS, "    public Task<bool> RequestManualChargingReturnToServiceAsync(", helper_bs)]),
 ("R4c 视图模型重算说明时直接读 proof 环境变量", [(VM, "    private void RefreshManualChargingReturnGuidanceCore()\n    {\n", "    private void RefreshManualChargingReturnGuidanceCore()\n    {\n        _ = " + ENV + "(\"CONTROL_SERVER_RECOVERY_PROOF\");\n")]),
 ("R4d 原因属性里加 && ProofPresent（读 proof 的属性）", [(BS, "        CanRequestManualChargingReturnToService\n            ? WireToGateManualChargingReturnBlocker.None", "        CanRequestManualChargingReturnToService && ProofPresent\n            ? WireToGateManualChargingReturnBlocker.None"), (BS, "    public Task<bool> RequestManualChargingReturnToServiceAsync(", prop_bs)]),
 ("R4e 视图模型新加读 proof 的静态属性并在刷新方法里引用", [(VM, "    private void RefreshManualChargingReturnGuidanceCore()", prop_vm), (VM, "            _manualChargingHold,\n", "            _manualChargingHold && ProofSet,\n")]),
]
for name, edits in muts:
    files = sorted({f for f, _, _ in edits})
    for f, old, new in edits:
        s = open(f, encoding='utf-8', newline='').read()
        assert s.count(old) == 1, (name, f, old[:40], s.count(old))
        open(f, 'w', encoding='utf-8', newline='').write(s.replace(old, new, 1))
    u = subprocess.run(['dotnet', 'test', 'tests/SQCD.Agv.UnitTests', '--no-build', '--filter', 'FullyQualifiedName~WireToGateManualChargingReturnTextTests'], capture_output=True, text=True, encoding='utf-8', errors='replace')
    print(name, '=>', 'RED（被抓住）' if u.returncode else 'GREEN（未抓住）')
    for l in [l.strip() for l in u.stdout.splitlines() if 'outside the set' in l or 'Failed!' in l or 'Passed!' in l][:3]: print('   ', l)
    subprocess.run(['git', 'checkout', '--'] + files)
