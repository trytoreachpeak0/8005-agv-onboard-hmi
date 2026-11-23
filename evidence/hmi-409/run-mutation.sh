#!/usr/bin/env bash
# usage: run-mutation.sh <name> <file> <python-old> <python-new> ; run from the hmi409 worktree
set -o pipefail
M=/c/work/8005-scratch/hmi409/mut3; mkdir -p $M; name=$1; f=$2
python - "$f" "$3" "$4" <<'PY' || exit 9
import sys
p,old,new=sys.argv[1:4]
s=open(p,encoding='utf-8').read()
assert s.count(old)==1, "anchor count %d" % s.count(old)
open(p,'w',encoding='utf-8',newline='').write(s.replace(old,new))
PY
[ "$(git diff --name-only | wc -l)" = 1 ] || { echo "not exactly one file changed"; git checkout -- "$f"; exit 8; }
git diff > $M/$name.diff
F="FullyQualifiedName~ARefusedCloseOutRunsOnceTheReleasedStop|FullyQualifiedName~ARefusedCloseOutIsRetriedOnlyOnPublications"
pwsh -File C:/work/8005-workspace-v2/Invoke-HeavyLocal.ps1 -Ticket hmi#409 dotnet test tests/SQCD.Agv.WireToGateG2Tests -c Release --filter "$F" --logger "console;verbosity=normal" > $M/$name.log 2>&1
echo "exit=$?" >> $M/$name.log
git checkout -- "$f"
echo "== $name"; grep -E "^exit=" $M/$name.log
grep -E "^\s+(失败|已通过) SQCD" $M/$name.log | sed 's/SQCD.Agv.WireToGateG2Tests.//'
