#Requires -Version 7
$root = 'C:/Users/szy/Desktop/8005-workspace-v2/evidence/hmi295-runs/baseline-c60013ae'
New-Item -ItemType Directory -Path $root -Force | Out-Null
foreach ($s in 'FP-IS-04','FP-IS-01','FP-IS-10') {
  $w = [Diagnostics.Stopwatch]::StartNew()
  & pwsh -NoProfile -File C:/Users/szy/Desktop/8005-workspace-v2/worktrees/hmi295-red-8005-agv-onboard-hmi/scripts/run-w2g-g2.ps1 -Slice $s -ProtocolRoot C:/Users/szy/Desktop/8005-workspace-v2/scratch/hmi295-protocol-v3.0.0 -EvidenceRoot "$root/evidence" *>&1 | Out-File "$root/one-call-$s.console.log"
  "$s exit=$LASTEXITCODE seconds=$([math]::Round($w.Elapsed.TotalSeconds,1))" | Tee-Object -FilePath "$root/timings.txt" -Append
}
