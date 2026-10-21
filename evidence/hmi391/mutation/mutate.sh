#!/usr/bin/env bash
set -euo pipefail
cd /c/work/8005-wt/hmi391-8005-agv-onboard-hmi
out=/c/work/8005-scratch/hmi391/red
run() { # name file perl-expr filter
  local name=$1 file=$2 expr=$3 filter=$4
  test -z "$(git status --porcelain)" || { echo "dirty before $name"; exit 2; }
  perl -0pi -e "$expr" "$file"
  changed=$(git status --porcelain | wc -l)
  [ "$changed" = 1 ] || { echo "mutation $name changed $changed files"; git checkout -- "$file"; exit 2; }
  git diff --stat | tail -1
  set +e
  pwsh -File C:/work/8005-workspace-v2/Invoke-HeavyLocal.ps1 -Ticket hmi#391 dotnet test tests/SQCD.Agv.WireToGateG2Tests --filter "$filter" > "$out/mutation-$name.txt" 2>&1
  code=$?
  set -e
  git checkout -- "$file"
  echo "$name exit=$code"; grep -E "^\s+Failed |Passed!|Failed!" "$out/mutation-$name.txt" | head -5
}
run M2-press-on-refusal src/SQCD.Agv.Wpf/WireToGateBusinessService.cs 's/(            NoteRecoveryRefusal\(exception\.Message\);\n)/$1            MarkOperatorPress("mutation");\n/' "FullyQualifiedName~RecoveryVectorG2Tests.AResumeRefusedForWantOfAuthorization"
run M3-no-clear-on-open src/SQCD.Agv.Wpf/WireToGateBusinessService.cs 's/(\.ConfigureAwait\(false\);\n)                NoteRecoverySessionOpened\(\);\n/$1/' "FullyQualifiedName~RecoveryVectorG2Tests.OnceAuthorizedTheNextPress"
run M4-compensation-wording src/SQCD.Agv.Wpf/WireToGateBusinessService.RecoveryVectors.cs 's/reason == RecoveryNotOpenReasonCode\n/false\n/' "FullyQualifiedName~RecoveryVectorG2Tests.ACompensationRefusedForWantOfAuthorization"
git status --porcelain
