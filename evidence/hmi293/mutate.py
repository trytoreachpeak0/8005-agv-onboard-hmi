import subprocess, sys
VM='src/SQCD.Agv.Wpf/ViewModels/MainViewModel.cs'
BS='src/SQCD.Agv.Wpf/WireToGateBusinessService.ManualChargingReturn.cs'
TX='src/SQCD.Agv.Application/WireToGateManualChargingReturnText.cs'
FILTER="FullyQualifiedName~ManualChargingReturnEntryG2Tests|FullyQualifiedName~ManualChargingReturnGuidanceViewModelTests"
muts=[
 ("M1 锁存分支不刷新指引", VM, "            CanRequestHardwareRepairRelease = false;\n            RefreshManualChargingReturnGuidanceCore();\n            return;", "            CanRequestHardwareRepairRelease = false;\n            return;"),
 ("M2 输入刷新路径不刷新指引", VM, "            AllowRecoveryEntry(_wireToGateCanRequestManualChargingReturn?.Invoke() == true);\n        RefreshManualChargingReturnGuidanceCore();", "            AllowRecoveryEntry(_wireToGateCanRequestManualChargingReturn?.Invoke() == true);"),
 ("M3 业务服务先说权限再说会话", BS, "            : !SessionCanCarryOperatorRequest()\n                ? WireToGateManualChargingReturnBlocker.SessionNotReady\n                : WireToGateManualChargingReturnBlocker.OperatorNotAuthorized;", "            : WireToGateManualChargingReturnBlocker.OperatorNotAuthorized;"),
 ("M4 保持不进视图模型", VM, "        _manualChargingHold = snapshot.VehicleBusinessState?.ManualChargingHold == true;", "        _manualChargingHold = true;"),
 ("M5 按钮可按时仍显示指引", TX, "if (!manualChargingHold || entryOffered)", "if (!manualChargingHold)"),
]
for name,f,old,new in muts:
    s=open(f,encoding='utf-8',newline='').read()
    assert s.count(old)==1,(name,s.count(old))
    open(f,'w',encoding='utf-8',newline='').write(s.replace(old,new))
    r=subprocess.run(['dotnet','test','tests/SQCD.Agv.WireToGateG2Tests','--filter',FILTER],capture_output=True,text=True,encoding='utf-8',errors='replace')
    u=subprocess.run(['dotnet','test','tests/SQCD.Agv.UnitTests','--filter','FullyQualifiedName~WireToGateManualChargingReturnTextTests'],capture_output=True,text=True,encoding='utf-8',errors='replace')
    lines=[l.strip() for l in (r.stdout+u.stdout).splitlines() if l.strip().startswith('Failed ') or 'Failed!' in l or 'Passed!' in l]
    r.returncode = r.returncode or u.returncode
    print(name, '=>', 'RED' if r.returncode!=0 else 'GREEN(未抓住)')
    for l in lines[:6]: print('   ',l)
    subprocess.run(['git','checkout','--',f])
