#Requires -Version 7
# Runs the two hmi#204 session-failure tests 10 times against each code state. Run from the worktree root.
param(
    [string[]] $States = @('PRE', 'M2', 'M3', 'M4', 'FIX'),
    [int] $Runs = 10,
    [string] $Filter = 'FullyQualifiedName~ASafetyReportWhoseConnectionClosedUnderItDoesNotDropTheNextSession|FullyQualifiedName~ASafetyReportUnansweredOnALiveConnectionStillDisconnectsTheSession',
    [string] $Tag = 'r10'
)

$ErrorActionPreference = 'Stop'
# pwsh -File hands a comma list over as one string.
$States = @($States | ForEach-Object { $_ -split ',' } | Where-Object { $_ })
$sp = 'C:/Users/szy/AppData/Local/Temp/claude/C--Users-szy-Desktop-8005-workspace-v2/bf6b5571-299c-4809-abb2-bb7c48bda32f/scratchpad'
$client = 'src/SQCD.Agv.Infrastructure/WireToGateSessionClient.cs'
$business = 'src/SQCD.Agv.Wpf/WireToGateBusinessService.cs'
$project = 'tests/SQCD.Agv.WireToGateG2Tests/SQCD.Agv.WireToGateG2Tests.csproj'
$filter = $Filter

function Get-ProductHash {
    ((Get-FileHash $client, $business -Algorithm SHA256).Hash -join ' ')
}

function Restore-Product {
    Copy-Item "$sp/backup/r10-client.cs" $client -Force
    Copy-Item "$sp/backup/r10-business.cs" $business -Force
    # Copy-Item carries the backup's old timestamp; left as is, MSBuild would keep the previous state's binaries.
    foreach ($file in $client, $business) { (Get-Item $file).LastWriteTime = Get-Date }
}

Copy-Item $client "$sp/backup/r10-client.cs" -Force
Copy-Item $business "$sp/backup/r10-business.cs" -Force
$baseline = Get-ProductHash
"dotnet $(dotnet --version) in $(Get-Location)"

foreach ($state in $States) {
    "================ $state"
    try {
        switch ($state) {
            'PRE' {
                # The product as bc30a54 left it: its reading of the generation, before 1d651db, and without the
                # replay change reverted since (68e8318 would carry that). Byte-exact from the object store, worktree
                # only; the index keeps HEAD, and the backup restores the files below.
                git restore --source bc30a54 --worktree -- $client $business
                if ($LASTEXITCODE -ne 0) { throw 'git restore for PRE failed' }
            }
            'HEAD' {
                # The committed client: its epoch and its writer still two fields, before the handle.
                git restore --source HEAD --worktree -- $client
                if ($LASTEXITCODE -ne 0) { throw 'git restore for HEAD failed' }
            }
            'FIX' { }
            default {
                python "$sp/mutate.py" (Get-Location).Path $state
                if ($LASTEXITCODE -ne 0) { throw "mutation $state did not apply exactly once" }
            }
        }

        "  diff: $(git diff --stat -- src | Select-Object -Last 1)"
        # --no-incremental: every state is compiled from its own sources, never judged up to date by timestamps.
        $build = dotnet build $project --no-incremental -v q --nologo 2>&1 | Select-String -Pattern 'Error\(s\)|error CS' | ForEach-Object { $_.Line.Trim() } | Sort-Object -Unique
        "  build: $($build -join '; ')"
        if (-not ($build -match '^0 Error\(s\)$')) { throw "build of $state did not report 0 Error(s)" }

        for ($i = 1; $i -le $Runs; $i++) {
            $out = "$sp/$Tag-$state-$i.txt"
            dotnet test $project --no-build --filter $filter *> $out
            $text = Get-Content $out -Raw
            $summary = if ($text -match 'Failed:\s+(\d+), Passed:\s+(\d+), Skipped:\s+\d+, Total:\s+(\d+)') {
                "fail=$($Matches[1]) pass=$($Matches[2]) total=$($Matches[3])"
            } else { 'NO SUMMARY' }
            $red = [regex]::Matches($text, 'MultiDemandJourneyG2Tests\.(\S+?)(\((?:next|trigger): \w+\))? \[FAIL\]') |
                ForEach-Object { ($_.Groups[1].Value -replace 'ASafetyReportWhoseConnectionClosedUnderItDoesNotDropTheNextSession', 'closed' -replace 'ASafetyReportUnansweredOnALiveConnectionStillDisconnectsTheSession', 'live' -replace 'ASafetyReportStillInFlightStaysOutOfTheNextHandshake', 'inflight' -replace 'ASafetyReportJudgedWhileTheConnectionIsClosingIsNotWrittenIntoIt', 'closing') + $_.Groups[2].Value } |
                Sort-Object -Unique
            $lines = [regex]::Matches($text, 'HandshakeWindowIntrusion\.cs:line (\d+)') | ForEach-Object { $_.Groups[1].Value } |
                Group-Object | Sort-Object Count -Descending | Select-Object -First 3 | ForEach-Object { "$($_.Name)x$($_.Count)" }
            "  run ${i}: $summary | red: $(if ($red) { $red -join ' ' } else { 'none' }) | at: $($lines -join ' ')"
        }
    }
    finally {
        Restore-Product
        if ((Get-ProductHash) -ne $baseline) { throw 'RESTORE MISMATCH' }
        '  restored OK'
    }
}
