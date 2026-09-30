"""hmi#221 fault injection: one mutation at a time, build, run, record which tests go red, restore.

Run from the worktree root. Each mutation must match exactly once (a replacement that matched nothing
would run the unmutated code and look green). Restore rewrites the original text, which also refreshes
the file's mtime so the next build recompiles.
"""
import io
import os
import re
import subprocess
import sys

ROOT = os.getcwd()
OUT = os.path.join(ROOT, "evidence", "hmi-221", "mutations")
os.makedirs(OUT, exist_ok=True)

BUSINESS = "src/SQCD.Agv.Wpf/WireToGateBusinessService.StationClearance.cs"
BUSINESS_MAIN = "src/SQCD.Agv.Wpf/WireToGateBusinessService.cs"
CLIENT = "src/SQCD.Agv.Infrastructure/WireToGateSessionClient.cs"
VIEWMODEL = "src/SQCD.Agv.Wpf/ViewModels/MainViewModel.cs"
CORE = "src/SQCD.Agv.Core/WireToGateStationClearance.cs"
WIRING = "src/SQCD.Agv.Wpf/StationClearanceWiring.cs"
WINDOW = "src/SQCD.Agv.Wpf/MainWindow.xaml.cs"
XAML = "src/SQCD.Agv.Wpf/MainWindow.xaml"

G2 = ("tests/SQCD.Agv.WireToGateG2Tests", "FullyQualifiedName~ManualStationClearanceG2Tests")
UNIT = ("tests/SQCD.Agv.UnitTests", "FullyQualifiedName~WireToGateStationClearanceTests")

# name, what it breaks, file, old, new, runs, tests expected to go red (substring of the test name)
MUTATIONS = [
    ("M01-resubmission-gets-a-new-id",
     "重提时不沿用未应答的请求，每按一次生成新的 confirmationRequestId",
     BUSINESS,
     "firstSend = shown.ResubmittedConfirmationRequestId is null;",
     "firstSend = true;",
     [G2],
     ["AnUnansweredClearanceIsShownAsUnknownAndAResubmissionCarriesTheSameConfirmationRequestId",
      "ADroppedSessionLeavesAnUnknownThatIsResubmittedUnderTheSameIdAfterReconnecting"]),
    ("M02-press-not-bound-to-the-prompt",
     "不核对操作员当时看到的那一份，只要入口在就发",
     BUSINESS,
     "if (view.Prompt != shown)",
     "if (view.Prompt is null)",
     [G2],
     ["APressMadeForOneChargerDoesNotConfirmAnother",
      "APressMadeAsAResubmissionIsRefusedOnceTheResultHasArrived"]),
    ("M03-no-gate-on-a-second-press",
     "第一次应答之前不挡第二次按下",
     BUSINESS,
     "if (Interlocked.CompareExchange(ref _stationClearanceAwaitingAnswer, 1, 0) != 0)",
     "if (Interlocked.Exchange(ref _stationClearanceAwaitingAnswer, 1) < 0)",
     [G2],
     ["ASecondPressWhileTheFirstIsUnansweredSendsNothing"]),
    ("M04-released-locally-on-confirmed",
     "CONFIRMED 且 stationReleased=true 之后本地就把入口收起（当作清桩已结束）",
     BUSINESS,
     "        if (!journey.IsClearingStop)\n        {\n            return new(null, WireToGateStationClearanceUnavailability.NotClearing, outcome);",
     "        if (!journey.IsClearingStop\n            || outcome is { Kind: WireToGateStationClearanceOutcomeKind.Confirmed, StationReleased: true })\n        {\n            return new(null, WireToGateStationClearanceUnavailability.NotClearing, outcome);",
     [G2],
     ["AConfirmedClearanceChangesNothingOnTheVehicleUntilTheServersNextSnapshot"]),
    ("M05-public-station-function-not-null",
     "publicStationFunction 发 GATE 而不是 null",
     BUSINESS,
     "                        null,\n                        \"STATION_EMPTY\",",
     "                        \"GATE\",\n                        \"STATION_EMPTY\",",
     [G2],
     ["TheConfirmationCarriesTheOperatorTheChargerAndTheConditionAndIsShownConfirmed"]),
    ("M06-repeated-result-raised-again",
     "重复到达的同一应答不认作重复，再投递一次",
     CLIENT,
     "        if (repeated)\n        {\n            return;\n        }\n\n        ServerCommandReceived?.Invoke(",
     "        if (repeated && envelope.MessageId.Length == 0)\n        {\n            return;\n        }\n\n        ServerCommandReceived?.Invoke(",
     [G2],
     ["ARepeatedResultIsHandledOnce"]),
    ("M07-latch-shuts-the-entry",
     "严重安全故障锁存期间把清桩入口也关掉",
     VIEWMODEL,
     "        if (_stationClearanceView?.Invoke() is not { } view)",
     "        if (RecoveryEntriesBlockedByFatalFault || _stationClearanceView?.Invoke() is not { } view)",
     [G2],
     ["ALatchLeavesTheClearanceEntryOpenAndOpensNoDoor"]),
    ("M08-purpose-not-checked",
     "不看 activePurpose，只要有业务状态就给入口",
     BUSINESS,
     "        if (!journey.IsClearingStop)\n        {\n            return new(null, WireToGateStationClearanceUnavailability.NotClearing, outcome);",
     "        if (journey.VehicleBusinessState is null)\n        {\n            return new(null, WireToGateStationClearanceUnavailability.NotClearing, outcome);",
     [G2],
     ["TheEntryIsNotOfferedUnlessTheServerSaysTheVehicleIsClearing"]),
    ("M09a-proof-not-required",
     "不要求管理员认证凭据",
     BUSINESS,
     "if (!CanUseRecoveryOperator(requireProof: true)",
     "if (!CanUseRecoveryOperator(requireProof: false)",
     [G2],
     ["TheEntryIsNotOfferedWithoutAVerifiedMaintainer(maintenanceSwitch: True"]),
    ("M09b-maintenance-switch-not-required",
     "不看维护开关与凭据，只要有操作员号和在线会话",
     BUSINESS,
     "if (!CanUseRecoveryOperator(requireProof: true)",
     "if (!CanUseStationOperator()",
     [G2],
     ["TheEntryIsNotOfferedWithoutAVerifiedMaintainer(maintenanceSwitch: False",
      "TheEntryIsNotOfferedWithoutAVerifiedMaintainer(maintenanceSwitch: True"]),
    ("M10-station-guessed-from-the-current-leg",
     "计划里没有恰好一个充电桩时拿当前腿的站点号顶上",
     CORE,
     "return chargers.Length == 1 ? chargers[0] : null;",
     "return chargers.Length == 1 ? chargers[0] : journey.CurrentLeg?.StationId;",
     [UNIT, G2],
     ["WithoutExactlyOneChargerInThePlanNoStationIsNamed",
      "TheEntryIsNotOfferedWithoutOneChargerInThePlanAndTheScreenSaysWhy"]),
    ("M11a-unanswered-kept-across-clearances",
     "服务端说清桩结束后不清掉未应答的请求",
     BUSINESS,
     "            _stationClearanceUnanswered = null;\n            _stationClearanceOutcome = null;\n",
     "            _stationClearanceOutcome = null;\n",
     [G2],
     ["ALaterClearanceOfTheSameChargerStartsFromNothing"]),
    ("M11b-result-line-not-hidden-when-over",
     "读视图时不按当前旅程隐藏结果，只靠事件处理清字段",
     BUSINESS,
     "        if (ServerSaysTheClearanceIsOver(journey))\n        {\n            outcome = null;",
     "        if (ServerSaysTheClearanceIsOver(journey) && outcome is null)\n        {\n            outcome = null;",
     [G2],
     ["AConfirmedClearanceChangesNothingOnTheVehicleUntilTheServersNextSnapshot"]),
    ("M12-late-result-dropped",
     "没有等待者的应答一律丢掉，不交给业务服务",
     CLIENT,
     "        if (repeated)\n        {\n            return;\n        }\n\n        ServerCommandReceived?.Invoke(",
     "        if (repeated || envelope.MessageId.Length > 0)\n        {\n            return;\n        }\n\n        ServerCommandReceived?.Invoke(",
     [G2],
     ["AResultThatArrivesAfterTheTimeoutSettlesTheUnknownOnce",
      "APressMadeAsAResubmissionIsRefusedOnceTheResultHasArrived"]),
    ("M13a-first-message-id-not-the-request-id",
     "第一次发送的 messageId 不用 confirmationRequestId",
     BUSINESS,
     "firstSend ? request.ConfirmationRequestId : Guid.NewGuid().ToString(\"D\"),",
     "Guid.NewGuid().ToString(\"D\"),",
     [G2],
     ["TheConfirmationCarriesTheOperatorTheChargerAndTheConditionAndIsShownConfirmed",
      "ALaterClearanceOfTheSameChargerStartsFromNothing"]),
    ("M13b-resubmission-reuses-the-message-id",
     "重提沿用第一次的 messageId（服务端入站去重会判内容冲突）",
     BUSINESS,
     "firstSend ? request.ConfirmationRequestId : Guid.NewGuid().ToString(\"D\"),",
     "request.ConfirmationRequestId,",
     [G2],
     ["AnUnansweredClearanceIsShownAsUnknownAndAResubmissionCarriesTheSameConfirmationRequestId"]),
    ("M14-unknown-reported-as-success",
     "超时或断线后按下返回 true（unknown-as-success）",
     BUSINESS,
     "                : new StationClearancePress(\n                    false,\n                    \"STATION_CLEARANCE_UNKNOWN\",",
     "                : new StationClearancePress(\n                    true,\n                    \"STATION_CLEARANCE_UNKNOWN\",",
     [G2],
     ["AnUnansweredClearanceIsShownAsUnknownAndAResubmissionCarriesTheSameConfirmationRequestId",
      "ADroppedSessionLeavesAnUnknownThatIsResubmittedUnderTheSameIdAfterReconnecting"]),
    ("M15-cleared-condition-not-validated",
     "发送前不校验 clearedCondition",
     CLIENT,
     "            || payload.ClearedCondition is not (\"STATION_EMPTY\" or \"OBSTRUCTION_REMOVED\" or \"CARGO_RELOCATED\")\n",
     "",
     [G2],
     ["APayloadTheSchemaDoesNotAllowIsRefusedBeforeItIsSent(broken: \"clearedCondition\")"]),
    ("M16-conflicting-answer-accepted",
     "同一确认号下不同的应答不判冲突",
     CLIENT,
     "            throw new InvalidDataException(\"BUSINESS_ID_CONTENT_CONFLICT\");\n        }\n\n        return true;\n    }\n\n    // ---- end of CV-MANUAL-STATION-CLEARANCE ----",
     "            return true;\n        }\n\n        return true;\n    }\n\n    // ---- end of CV-MANUAL-STATION-CLEARANCE ----",
     [G2],
     ["ADifferentAnswerUnderAnAnsweredIdFailsTheSessionAndDoesNotRewriteTheResult"]),
    ("M17-view-not-wired",
     "接线把视图委托接到一个恒为「不在清桩」的值上",
     WIRING,
     "() => business.StationClearance,",
     "() => SQCD.Agv.Core.WireToGateStationClearanceView.NotClearing,",
     [G2],
     ["TheConfirmationCarriesTheOperatorTheChargerAndTheConditionAndIsShownConfirmed"]),
    ("M18-click-handler-reads-the-entry-again",
     "点击处理在对话框之后重新读一次入口再交回去",
     WINDOW,
     "_viewModel.ConfirmStationClearanceAsync(prompt)",
     "_viewModel.ConfirmStationClearanceAsync(_viewModel.StationClearance.Prompt!)",
     [UNIT],
     ["TheClickHandlerConfirmsThePromptItBuiltTheDialogFrom"]),
    ("M19-result-status-unknown-hidden",
     "结果未知时不显示结果那一行",
     BUSINESS,
     "            _stationClearanceOutcome = new WireToGateStationClearanceOutcome(\n                WireToGateStationClearanceOutcomeKind.Unknown,",
     "            _ = new WireToGateStationClearanceOutcome(\n                WireToGateStationClearanceOutcomeKind.Unknown,",
     [G2],
     ["AnUnansweredClearanceIsShownAsUnknownAndAResubmissionCarriesTheSameConfirmationRequestId"]),
    # ---- after the independent review (S1, S3, S4) ----
    ("R1-protocol-problem-treated-as-unknown",
     "审查 S1：与请求关联的 ProtocolProblem 不单独认，照旧当成普通失败",
     CLIENT,
     "                throw new WireToGateRequestNotAcceptedException(\n",
     "                throw new InvalidDataException(\n",
     [G2],
     ["AProtocolProblemToTheRequestEndsItsIdAndEachLaterPressIsANewConfirmation"]),
    ("R2-only-listed-exceptions-caught",
     "审查 S3：按下只接列表里的四种异常",
     BUSINESS,
     "        catch (Exception exception)\n        {\n            // Every failure, not a list of them.",
     "        catch (Exception exception) when (exception is IOException or TimeoutException or InvalidOperationException or InvalidDataException)\n        {\n            // Every failure, not a list of them.",
     [G2],
     ["ASessionThatFailsWithAnUnexpectedExceptionStillEndsThePressAsUnknown"]),
    ("R3-contradictory-result-accepted",
     "审查 S4：不拒收自相矛盾的应答",
     CLIENT,
     "            || payload.Outcome == \"REJECTED\" && payload.StationReleased\n            || payload.Outcome == \"CONFIRMED\" && payload.Problem is not null)\n",
     ")\n",
     [G2],
     ["AResultThatContradictsItselfIsRefusedAndThePressEndsAsUnknown"]),
    ("R4-malformed-result-surfaces-as-json-exception",
     "审查 S3：畸形应答不在类型化读取之前拒收，JsonException 原样冒出",
     CLIENT,
     ["            || outcome.ValueKind is not JsonValueKind.String\n",
      "        catch (JsonException exception)\n        {\n            throw new InvalidDataException(\"PROTOCOL_SCHEMA_INVALID\", exception);\n        }\n\n        RequireUuid(payload.ConfirmationRequestId, nameof(payload.ConfirmationRequestId));\n        // The two combinations"],
     ["",
      "        catch (JsonException)\n        {\n            throw;\n        }\n\n        RequireUuid(payload.ConfirmationRequestId, nameof(payload.ConfirmationRequestId));\n        // The two combinations"],
     [G2],
     ["AMalformedResultLeavesTheVehicleUnlatchedAndTheConfirmationResubmittable"]),
]

FAILED = re.compile(r"^\s+Failed (\S.*?) \[[^\]]*\]\s*$")


def read(path):
    with io.open(path, encoding="utf-8", newline="") as handle:
        return handle.read()


def write(path, text):
    with io.open(path, "w", encoding="utf-8", newline="") as handle:
        handle.write(text)


def run(args):
    done = subprocess.run(args, cwd=ROOT, capture_output=True, text=True, encoding="utf-8", errors="replace")
    return done.returncode, done.stdout + done.stderr


def main():
    only = set(sys.argv[1:])
    summary = []
    for name, what, path, old, new, runs, expected in MUTATIONS:
        if only and name not in only:
            continue
        original = read(path)
        pairs = list(zip(old, new)) if isinstance(old, list) else [(old, new)]
        hits = [original.count(a) for a, _ in pairs]
        if any(hit != 1 for hit in hits):
            print(f"{name}: NOT APPLIED, patterns matched {hits} times")
            summary.append((name, what, "NOT APPLIED", [], expected))
            continue
        mutated = original
        for a, b in pairs:
            mutated = mutated.replace(a, b)
        write(path, mutated)
        old, new = " ||| ".join(a for a, _ in pairs), " ||| ".join(b for _, b in pairs)
        log = [f"# {name}", what, f"file: {path}", "", "--- replaced ---", old, "--- with ---", new, ""]
        failed = []
        status = "ran"
        try:
            for project, test_filter in runs:
                code, output = run(["dotnet", "build", project, "-nologo", "-v", "q"])
                if code != 0:
                    status = "BUILD FAILED"
                    log += ["--- build output ---", output[-4000:]]
                    break
                code, output = run(["dotnet", "test", project, "--no-build", "-nologo", "--filter", test_filter])
                lines = output.splitlines()
                for line in lines:
                    match = FAILED.match(line)
                    if match:
                        failed.append(match.group(1))
                log += [f"--- dotnet test {project} --filter {test_filter} (exit {code}) ---"]
                log += [line for line in lines if line.lstrip().startswith(("Failed", "Passed!", "Failed!", "Total"))]
        finally:
            write(path, original)
        missing = [item for item in expected if not any(item in test for test in failed)]
        verdict = status if status != "ran" else ("RED AS EXPECTED" if failed and not missing else "NOT RED AS EXPECTED")
        log += ["", f"verdict: {verdict}", "expected red: " + "; ".join(expected), "missing: " + ("; ".join(missing) or "none")]
        write(os.path.join(OUT, name + ".txt"), "\n".join(log) + "\n")
        summary.append((name, what, verdict, failed, expected))
        print(f"{name}: {verdict} ({len(failed)} failed)")
        for test in failed:
            print("    " + test)
        sys.stdout.flush()
    lines = []
    for name, what, verdict, failed, expected in summary:
        lines.append(f"{name}\t{verdict}\t{what}")
        for test in failed:
            lines.append(f"\t{test}")
    mode = "a" if only else "w"
    with io.open(os.path.join(OUT, "summary.tsv"), mode, encoding="utf-8", newline="") as handle:
        handle.write("\n".join(lines) + "\n")


if __name__ == "__main__":
    main()
