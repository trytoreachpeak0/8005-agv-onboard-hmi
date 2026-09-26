#!/usr/bin/env bash
# Probe 03 for onboard-hmi#208: the main test's other assertions already held before any fix. The three product files are
# taken from 6a249f5 (their content is w2g/fp-v2-impl@b789c3d4, before this PR); the test is HEAD's, with its two main
# assertions reversed -- the run passes only if the server DID have departure-safe on file and DID announce READY, so a green
# here says the code under test is the unfixed one. The on-file sequence of each cell is printed from the test's own output.
# Afterwards the files are taken back from HEAD and the tree must be clean.
set -u
cd /c/Users/szy/Desktop/8005-workspace-v2/worktrees/hmi208-8005-agv-onboard-hmi || exit 1
OUT=${1:?output file}
T=tests/SQCD.Agv.WireToGateG2Tests/MultiDemandJourneyG2Tests.StaleSafetyResend.cs
SRC="src/SQCD.Agv.Infrastructure/WireToGateSessionClient.cs src/SQCD.Agv.Infrastructure/WireToGateSessionService.cs src/SQCD.Agv.Wpf/WireToGateBusinessService.cs"
FILTER="FullyQualifiedName~ASafetyChangeTheHandshakeDidNotCarry"
[ -z "$(git status --porcelain -- src tests)" ] || { echo "tree not clean, abort"; exit 2; }

{
  echo "# head $(git rev-parse HEAD); product files from 6a249f5 (= w2g/fp-v2-impl@b789c3d4, unfixed); main assertions reversed"
  echo "# build: dotnet build tests/SQCD.Agv.WireToGateG2Tests --no-incremental -v q"
  echo "# test:  dotnet test tests/SQCD.Agv.WireToGateG2Tests --no-build --filter \"$FILTER\" --logger \"console;verbosity=detailed\""
} > "$OUT"

git checkout 6a249f5 -- $SRC
git diff --stat HEAD -- src >> "$OUT"
python - "$T" >> "$OUT" <<'PY'
import sys, io
p = sys.argv[1]
s = io.open(p, encoding='utf-8', newline='').read()
for old, new in [
    ('                safeOnFile.Length == 0,', '                safeOnFile.Length > 0,'),
    ('            Assert.Empty(readyAnnounced);', '            Assert.NotEmpty(readyAnnounced);'),
]:
    n = s.count(old)
    if n != 1:
        print('replacement matched', n, 'times, abort:', old)
        sys.exit(3)
    s = s.replace(old, new)
io.open(p, 'w', encoding='utf-8', newline='').write(s)
print('probe applied: 2 replacements')
PY
[ $? -eq 0 ] || { git checkout HEAD -- $SRC $T; exit 3; }
git diff -U0 -- "$T" | grep -E '^[+-][^+-]' >> "$OUT"

echo "build: $(dotnet build tests/SQCD.Agv.WireToGateG2Tests --no-incremental -v q -nologo 2>&1 | grep -E 'Error\(s\)')" >> "$OUT"
res=$(dotnet test tests/SQCD.Agv.WireToGateG2Tests --no-build --filter "$FILTER" --logger "console;verbosity=detailed" 2>&1)
code=$?
echo "$res" | grep -E '^\s+(Passed|Failed) SQCD|On file in the second session' | sed -E 's/SQCD\.Agv\.WireToGateG2Tests\.MultiDemandJourneyG2Tests\.//' >> "$OUT"
echo "$res" | grep -E 'Passed!|Failed!' | sed -E 's/, Duration.*//' >> "$OUT"
echo "exit=$code" >> "$OUT"

git checkout HEAD -- $SRC $T
touch $SRC $T
echo "restored; tree clean: $( [ -z "$(git status --porcelain -- src tests)" ] && echo yes || echo NO )" >> "$OUT"
