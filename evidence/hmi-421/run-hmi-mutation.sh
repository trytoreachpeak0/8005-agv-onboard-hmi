#!/usr/bin/env bash
# usage: run-hmi-mutation.sh <name> <python-mutator> <file> ; run from the onboard-hmi worktree
set -o pipefail
M=/c/work/8005-scratch/hmi421-mutations; name=$1; mut=$2; f=$3
F="FullyQualifiedName~AfterARefusalIsANewRequestUnderANewId|FullyQualifiedName~TheUnansweredPressIsJournaledWithItsOwnId|FullyQualifiedName~RepeatedRowCancellationPress|FullyQualifiedName~MultiDemandJourneyG2Tests.ASecondConfirmedPressOnTheSameRow|FullyQualifiedName~ALostAuthorizationIsAskedForAgainWithTheFirstPressContent|FullyQualifiedName~TheSubjectKeysOfAPressSurviveTheJournal"
python "$mut" "$f" || exit 9
[ "$(git diff --name-only | wc -l)" = 1 ] || { echo "not exactly one file changed"; git checkout -- "$f"; exit 8; }
git diff > $M/hmi-$name.diff
{ dotnet build SQCD_8005AGV.sln -c Release --no-incremental -v q && dotnet test SQCD_8005AGV.sln -c Release --no-build --filter "$F" --logger "console;verbosity=normal"; } > $M/hmi-$name.log 2>&1
echo "exit=$?" >> $M/hmi-$name.log
git checkout -- "$f"
grep -E "总计|exit=" $M/hmi-$name.log
grep -E "^\s+(失败|已通过) SQCD" $M/hmi-$name.log | sed 's/SQCD.Agv.WireToGateG2Tests.//; s/SQCD.Agv.UnitTests.//'
