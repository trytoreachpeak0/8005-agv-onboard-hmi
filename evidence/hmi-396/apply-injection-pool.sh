#!/usr/bin/env bash
# onboard-hmi#396, the thread-pool stall in AWaitThatRunsOutNamesTheMessageTheVehicleIsStuckOn: raises the pool's minimum
# worker threads to 64 at the start of that test (a pool as large as or larger than CI's 25 threads), as an uncommitted
# change; undo with git checkout -- tests/SQCD.Agv.WireToGateG2Tests. Writes the minimum before and after and the
# pool's size to W2G_HMI396_MARKS. Works before the fix and after it.
# Usage: apply-injection-pool.sh [checkout] [minimum]   (defaults: the checkout this script sits in, 64)
set -euo pipefail
here=$(cd "$(dirname "$0")" && pwd)
W=${1:-$(git -C "$here" rev-parse --show-toplevel)}
M=${2:-64}
t=$W/tests/SQCD.Agv.WireToGateG2Tests/MultiDemandJourneyG2Tests.WaitFailureScene.cs
M=$M perl -0pi -e '
s/(public async Task AWaitThatRunsOutNamesTheMessageTheVehicleIsStuckOn\(bool stallTheThreadPool\)\n    \{\n        CancellationToken token = TestContext\.Current\.CancellationToken;\n)/$1        ThreadPool.GetMinThreads(out int hmi396Workers, out int hmi396Io);\n        ThreadPool.SetMinThreads(Math.Max(hmi396Workers, $ENV{M}), hmi396Io);\n        if (Environment.GetEnvironmentVariable("W2G_HMI396_MARKS") is { Length: > 0 } hmi396Marks)\n        {\n            File.AppendAllText(hmi396Marks, \$"POOL stall={stallTheThreadPool} min {hmi396Workers}->{Math.Max(hmi396Workers, $ENV{M})} threads={ThreadPool.ThreadCount}{Environment.NewLine}");\n        }\n\n/ or die "no test";
' "$t"
test "$(grep -c 'hmi396' "$t")" -eq 4 || { echo "injection did not apply cleanly to $t"; exit 1; }
git -C "$W" status --short
