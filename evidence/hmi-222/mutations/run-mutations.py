"""hmi#222 fault injection: one mutation at a time, build, run, record which tests go red, restore.

Run from the worktree root. Each mutation must match exactly once (a replacement that matched nothing would run
the unmutated code and look green). Restore rewrites the original text, which also refreshes the file's mtime so
the next build recompiles. R0 is the red evidence: the entry and the send path taken away as they were before
this ticket; its full test output is kept under evidence/hmi-222/red/.
"""
import io
import os
import re
import subprocess
import sys

ROOT = os.getcwd()
OUT = os.path.join(ROOT, "evidence", "hmi-222", "mutations")
RED = os.path.join(ROOT, "evidence", "hmi-222", "red")

BUSINESS = "src/SQCD.Agv.Wpf/WireToGateBusinessService.UnableToCharge.cs"
BUSINESS_MAIN = "src/SQCD.Agv.Wpf/WireToGateBusinessService.cs"
CLIENT = "src/SQCD.Agv.Infrastructure/WireToGateSessionClient.cs"
VIEWMODEL = "src/SQCD.Agv.Wpf/ViewModels/MainViewModel.cs"
CORE = "src/SQCD.Agv.Core/WireToGateUnableToCharge.cs"
TEXT = "src/SQCD.Agv.Application/WireToGateUnableToChargeText.cs"
WIRING = "src/SQCD.Agv.Wpf/UnableToChargeWiring.cs"
WINDOW = "src/SQCD.Agv.Wpf/MainWindow.xaml.cs"
LEGACY_G2 = "tests/SQCD.Agv.WireToGateG2Tests/WireToGateG2Tests.cs"
CLEARANCE = "src/SQCD.Agv.Wpf/WireToGateBusinessService.StationClearance.cs"
APPSETTINGS = "src/SQCD.Agv.Wpf/appsettings.json"
BINDING = "tests/SQCD.Agv.UnitTests/ProtocolVectorTestBindingArchitectureTests.cs"

G2 = ("tests/SQCD.Agv.WireToGateG2Tests", "FullyQualifiedName~UnableToChargeFieldConfirmationG2Tests")
G2_SHAPE = ("tests/SQCD.Agv.WireToGateG2Tests",
            "FullyQualifiedName~UnableToChargeFieldConfirmationG2Tests|FullyQualifiedName~ProtocolPayloadShapeArchitectureTests")
UNIT = ("tests/SQCD.Agv.UnitTests", "FullyQualifiedName~WireToGateUnableToChargeTests")
G2_CLEARANCE = ("tests/SQCD.Agv.WireToGateG2Tests",
                "FullyQualifiedName~ManualStationClearanceG2Tests.AResultFollowedAtOnceByTheSnapshotThatEndsTheClearanceIsStillShownToTheOperator")
G2_VECTOR = ("tests/SQCD.Agv.WireToGateG2Tests",
             "FullyQualifiedName~UnableToChargeFieldConfirmationG2Tests.TheReportCarriesTheOperatorTheChargerAndTheConditionAndTheVectorRunsInOrder")
CONFIG = ("tests/SQCD.Agv.UnitTests", "FullyQualifiedName~ConfigurationTests")
SLICES = ("tests/SQCD.Agv.UnitTests",
          "FullyQualifiedName~IntegrationSliceTraitArchitectureTests|FullyQualifiedName~ProtocolVectorTestBindingArchitectureTests")

# name, what it breaks, file(s), old, new, runs, tests expected to go red (substring of the test name)
MUTATIONS = [
    ("R0-no-entry-no-send-path",
     "红证据：拿掉入口与发送路径，回到本票之前（入口永远不出现，会话层没有发送路径）",
     [BUSINESS, CLIENT],
     ["        if (!WireToGateUnableToCharge.IsCharging(journey))\n",
      "        ThrowIfDisposed();\n        RequireUuid(messageId, nameof(messageId));\n        ValidateUnableToChargeRequest(payload);\n"],
     ["        if (DateTimeOffset.UtcNow.Year > 0)\n",
      "        ThrowIfDisposed();\n        if (DateTimeOffset.UtcNow.Year > 0)\n        {\n            throw new NotSupportedException(\"UnableToChargeFieldConfirmationRequested has no send path yet.\");\n        }\n\n        RequireUuid(messageId, nameof(messageId));\n        ValidateUnableToChargeRequest(payload);\n"],
     [G2_SHAPE],
     ["TheReportCarriesTheOperatorTheChargerAndTheConditionAndTheVectorRunsInOrder",
      "TheServersDecisionChangesNothingOnTheVehicleUntilItsNextBusinessState",
      "EveryObservedConditionIsOfferedAndGoesOutAsChosen",
      "TheEntryIsOfferedWhateverTheLegAndTheCycleSay",
      "TheSessionDrivesEveryImplementedOnboardToServerMessageType"]),
    ("M01-resubmission-gets-a-new-id",
     "重提时不沿用未应答的请求，每按一次生成新的 confirmationRequestId",
     BUSINESS,
     "firstSend = shown.ResubmittedConfirmationRequestId is null;",
     "firstSend = true;",
     [G2],
     ["AnUnansweredConfirmationIsShownAsUnknownAndAResubmissionCarriesTheSameIdAndCondition",
      "ADroppedSessionLeavesAnUnknownThatIsResubmittedUnderTheSameIdAfterReconnecting"]),
    ("M02-press-not-bound-to-the-prompt",
     "不核对操作员当时看到的那一份，只要入口在就发",
     BUSINESS,
     "if (!view.Prompts.Contains(shown))",
     "if (view.Prompts.Count == 0)",
     [G2],
     ["APressMadeForOneChargerDoesNotConfirmAnother",
      "AnotherConditionIsRefusedWhileAConfirmationIsUnknown"]),
    ("M03-no-gate-on-a-second-press",
     "第一次应答之前不挡第二次按下",
     BUSINESS,
     "if (Interlocked.CompareExchange(ref _unableToChargeAwaitingAnswer, 1, 0) != 0)",
     "if (Interlocked.Exchange(ref _unableToChargeAwaitingAnswer, 1) < 0)",
     [G2],
     ["ASecondPressWhileTheFirstIsUnansweredSendsNothing"]),
    ("M04-decided-locally-on-confirmed",
     "CONFIRMED 之后本地就把入口收起（当作服务端的决定已经生效）",
     BUSINESS,
     "        if (!WireToGateUnableToCharge.IsCharging(journey))\n",
     "        if (!WireToGateUnableToCharge.IsCharging(journey)\n            || outcome is { Kind: WireToGateUnableToChargeOutcomeKind.Confirmed })\n",
     [G2],
     ["TheServersDecisionChangesNothingOnTheVehicleUntilItsNextBusinessState"]),
    ("M05-conditions-filtered-locally",
     "本地只给出 CONNECTION_FAILED 与 CHARGER_FAULT 两种（替服务端判哪些算充不上）",
     BUSINESS,
     "                .. WireToGateUnableToCharge.ObservedConditions\n",
     "                .. WireToGateUnableToCharge.ObservedConditions.Where(condition => condition is \"CONNECTION_FAILED\" or \"CHARGER_FAULT\")\n",
     [G2],
     ["EveryObservedConditionIsOfferedAndGoesOutAsChosen",
      "TheEntryIsOfferedWhateverTheLegAndTheCycleSay"]),
    ("M06-repeated-result-raised-again",
     "重复到达的同一应答不认作重复，再投递一次",
     CLIENT,
     "bool repeated = IsRepeatedUnableToChargeResult(envelope, result);",
     "bool repeated = IsRepeatedUnableToChargeResult(envelope, result) && envelope.MessageId.Length == 0;",
     [G2],
     ["ARepeatedResultIsHandledOnce"]),
    ("M07-latch-shuts-the-entry",
     "严重安全故障锁存期间把这个入口也关掉",
     VIEWMODEL,
     "        if (_unableToChargeView?.Invoke() is not { } view)",
     "        if (RecoveryEntriesBlockedByFatalFault || _unableToChargeView?.Invoke() is not { } view)",
     [G2],
     ["ALatchLeavesTheEntryOpenAndOpensNoDoor"]),
    ("M08-purpose-not-checked",
     "不看 activePurpose，有业务状态就算在充电",
     BUSINESS,
     "        if (!WireToGateUnableToCharge.IsCharging(journey))\n",
     "        if (journey.VehicleBusinessState is null)\n",
     [G2],
     ["TheEntryIsNotOfferedUnlessTheServerSaysTheVehicleIsCharging"]),
    ("M09a-proof-not-required",
     "不要求管理员凭据已配置",
     BUSINESS,
     "CanUseRecoveryOperator(requireProof: true)\n",
     "CanUseRecoveryOperator(requireProof: false)\n",
     [G2],
     ["TheEntryIsNotOfferedWithoutAVerifiedMaintainer"]),
    ("M09b-maintenance-switch-not-required",
     "不要求维护开关，只要有操作员号",
     BUSINESS,
     "!CanUseRecoveryOperator(requireProof: true)\n",
     "!CanUseStationOperator()\n",
     [G2],
     ["TheEntryIsNotOfferedWithoutAVerifiedMaintainer"]),
    ("M10-charger-guessed-from-any-leg",
     "当前腿不是充电桩时，从计划里任一条充电桩腿猜站点",
     CORE,
     "        return journey.CurrentLeg is { StopPurposeCategory: \"CHARGER\" } leg\n",
     "        return journey.UpcomingStopPlan?.Legs.LastOrDefault(item => item.StopPurposeCategory == \"CHARGER\") is { } leg\n",
     [G2, UNIT],
     ["TheEntryIsNotOfferedUnlessTheCurrentLegIsAChargerAndTheScreenSaysWhy",
      "WithoutACurrentChargerLegNoStationIsNamed"]),
    ("M11-unanswered-kept-across-attempts",
     "服务端说不在充电后不忘掉未应答的请求",
     BUSINESS,
     "        lock (_unableToChargeGate)\n        {\n            _unableToChargeUnanswered = null;\n            _unableToChargeOutcome = null;\n        }\n",
     "        lock (_unableToChargeGate)\n        {\n            _unableToChargeOutcome = null;\n        }\n",
     [G2],
     ["ALaterChargingAttemptStartsFromNothing"]),
    ("M12-late-result-dropped",
     "迟到的应答不处理",
     BUSINESS_MAIN,
     "                    HandleLateUnableToChargeResult(unableToCharge);\n",
     "                    _ = unableToCharge;\n",
     [G2],
     ["AResultThatArrivesAfterTheTimeoutSettlesTheUnknownOnce"]),
    ("M13a-first-message-id-not-the-request-id",
     "第一次发送的 messageId 不等于 confirmationRequestId",
     BUSINESS,
     "firstSend ? request.ConfirmationRequestId : Guid.NewGuid().ToString(\"D\"),",
     "Guid.NewGuid().ToString(\"D\"),",
     [G2],
     ["TheReportCarriesTheOperatorTheChargerAndTheConditionAndTheVectorRunsInOrder",
      "ALaterChargingAttemptStartsFromNothing"]),
    ("M13b-resubmission-reuses-the-message-id",
     "重新提交沿用第一次的 messageId",
     BUSINESS,
     "firstSend ? request.ConfirmationRequestId : Guid.NewGuid().ToString(\"D\"),",
     "request.ConfirmationRequestId,",
     [G2],
     ["AnUnansweredConfirmationIsShownAsUnknownAndAResubmissionCarriesTheSameIdAndCondition"]),
    ("M14-unknown-reported-as-confirmed",
     "超时与断线报成已确认",
     BUSINESS,
     "                WireToGateUnableToChargeOutcomeKind.Unknown,\n",
     "                WireToGateUnableToChargeOutcomeKind.Confirmed,\n",
     [G2],
     ["AnUnansweredConfirmationIsShownAsUnknownAndAResubmissionCarriesTheSameIdAndCondition",
      "AResultThatArrivesAfterTheTimeoutSettlesTheUnknownOnce"]),
    ("M15-observed-condition-not-validated",
     "发送前不校验 observedCondition 的取值",
     CLIENT,
     "            || payload.ObservedCondition is not (\"CHARGER_UNREACHABLE\" or \"CHARGER_OCCUPIED\" or \"CONNECTION_FAILED\"\n                or \"CHARGER_FAULT\")\n",
     "",
     [G2],
     ["APayloadTheSchemaDoesNotAllowIsRefusedBeforeItIsSent"]),
    ("M16-conflicting-answer-accepted",
     "同一确认号下内容不同的第二份应答不报内容冲突",
     CLIENT,
     "            throw new InvalidDataException(\"BUSINESS_ID_CONTENT_CONFLICT\");\n        }\n\n        return true;\n    }\n\n    // ---- end of CV-UNABLE-TO-CHARGE-FIELD-CONFIRMATION ----",
     "            return true;\n        }\n\n        return true;\n    }\n\n    // ---- end of CV-UNABLE-TO-CHARGE-FIELD-CONFIRMATION ----",
     [G2],
     ["ADifferentAnswerUnderAnAnsweredIdFailsTheSessionAndDoesNotRewriteTheResult"]),
    ("M17-view-not-wired",
     "接线把入口视图接到一个永远不在充电的视图上",
     WIRING,
     "            () => business.UnableToCharge,",
     "            () => SQCD.Agv.Core.WireToGateUnableToChargeView.NotCharging,",
     [G2],
     ["TheReportCarriesTheOperatorTheChargerAndTheConditionAndTheVectorRunsInOrder",
      "ALatchLeavesTheEntryOpenAndOpensNoDoor"]),
    ("M18-click-handler-reads-the-entry-again",
     "点击处理在对话框之后重新读视图模型的入口",
     WINDOW,
     "_viewModel.ConfirmUnableToChargeAsync(prompt)",
     "_viewModel.ConfirmUnableToChargeAsync(_viewModel.UnableToCharge.Options[0].Prompt)",
     [UNIT],
     ["TheClickHandlerConfirmsThePromptOfThePressedButton"]),
    ("M19-decision-not-shown",
     "结果一行不写服务端的决定",
     TEXT,
     "string decisionSentence = decision.Length == 0 ? string.Empty : $\"服务端决定：{decision}。\";",
     "string decisionSentence = string.Empty;",
     [G2, UNIT],
     ["TheServersDecisionChangesNothingOnTheVehicleUntilItsNextBusinessState",
      "ARejectedConfirmationShowsTheServersReasonAndDecisionAndTheNextPressIsANewConfirmation",
      "TheResultLineSaysWhatTheServerSaidAndDecided"]),
    ("M20-rejection-with-a-decision-refused",
     "把带决定的 REJECTED 当成自相矛盾拒收",
     CLIENT,
     "            || payload.Outcome == \"CONFIRMED\" && payload.Problem is not null\n            || payload.ChargingPolicyDecision",
     "            || payload.Outcome == \"CONFIRMED\" && payload.Problem is not null\n            || payload.Outcome == \"REJECTED\" && payload.ChargingPolicyDecision is not null\n            || payload.ChargingPolicyDecision",
     [G2],
     ["ARejectedConfirmationShowsTheServersReasonAndDecisionAndTheNextPressIsANewConfirmation"]),
    ("M21-confirmation-with-a-problem-accepted",
     "不拒收带 problem 的 CONFIRMED",
     CLIENT,
     "            || payload.Outcome == \"CONFIRMED\" && payload.Problem is not null\n            || payload.ChargingPolicyDecision",
     "            || payload.ChargingPolicyDecision",
     [G2],
     ["AResultThatContradictsItselfOrTheSchemaIsRefusedAndThePressEndsAsUnknown"]),
    ("M22-decision-value-not-validated",
     "不校验 chargingPolicyDecision 的取值",
     CLIENT,
     "\n            || payload.ChargingPolicyDecision is not (null or \"RETRY_LATER\" or \"MANUAL_CHARGING_HOLD\"\n                or \"REASSIGN_CHARGER\"))\n",
     ")\n",
     [G2],
     ["AResultThatContradictsItselfOrTheSchemaIsRefusedAndThePressEndsAsUnknown"]),
    ("M23-protocol-problem-treated-as-unknown",
     "与请求关联的 ProtocolProblem 不单独认，当成普通失败（结果未知、沿用号）",
     CLIENT,
     "                // out, because the caller ends the id on this one.\n                throw new WireToGateRequestNotAcceptedException(\n                    WireToGateProtocolSerializer\n                        .DeserializePayload<ProtocolProblemPayload>(responseEnvelope).Problem.ReasonCode);\n            }\n\n            WireToGateProtocolSerializer.RequireMessage(\n                responseEnvelope,\n                \"UnableToChargeFieldConfirmationResult\",",
     "                // out, because the caller ends the id on this one.\n                throw new InvalidDataException(\n                    WireToGateProtocolSerializer\n                        .DeserializePayload<ProtocolProblemPayload>(responseEnvelope).Problem.ReasonCode);\n            }\n\n            WireToGateProtocolSerializer.RequireMessage(\n                responseEnvelope,\n                \"UnableToChargeFieldConfirmationResult\",",
     [G2],
     ["AProtocolProblemToTheRequestIsShownAsNotAcceptedAndEndsItsId"]),
    ("M24-slice-trait-dropped-from-an-existing-test",
     "反向验证：去掉 6 条既有「充电后返回服务」测试里第一条的 FP-IS-13 标记",
     LEGACY_G2,
     "    [Trait(\"IntegrationSlice\", \"FP-IS-13\")]\n    [Trait(\"ProtocolVector\", \"CV-MANUAL-CHARGING-RETURN\")]\n    public async Task ManualChargingReturnToServiceAcceptedResultIsCorrelatedToRequestedMessage()",
     "    [Trait(\"ProtocolVector\", \"CV-MANUAL-CHARGING-RETURN\")]\n    public async Task ManualChargingReturnToServiceAcceptedResultIsCorrelatedToRequestedMessage()",
     [SLICES],
     ["EverySliceTraitIsExactlyTheProjectionOfTheTestsOwnVectors"]),
    ("M25-slice-flipped-with-the-vector-still-pinned",
     "翻面时把本票的向量留在待建钉位里",
     BINDING,
     "            [\"CV-WORKLIST-SELECTION-ACCEPTED\"] = \"FP-IS-09, batch 11\",",
     "            [\"CV-UNABLE-TO-CHARGE-FIELD-CONFIRMATION\"] = \"FP-IS-13, batch 9\",\n            [\"CV-WORKLIST-SELECTION-ACCEPTED\"] = \"FP-IS-09, batch 11\",",
     [SLICES],
     ["ProtocolVectorTestBindingArchitectureTests"]),
    # ---- review round (8005-agv-onboard-hmi#222 independent review) ----
    ("N01-X1-old-shape-unable-to-charge",
     "审查第 2 条，修复前的形状：结果交给等待者后紧跟的快照先把请求忘掉（等待路径前加 300 ms），等待路径不按本次请求结算",
     [BUSINESS, BUSINESS],
     ["            // Settled against this press's own request, not against the shared field a snapshot may have cleared.\n            WireToGateUnableToChargeOutcome? settled",
      "            if (ReferenceEquals(_unableToChargeForgotten, request))\n            {\n                _unableToChargeForgotten = null;\n                return outcome;"],
     ["            await Task.Delay(300, CancellationToken.None).ConfigureAwait(false);\n            WireToGateUnableToChargeOutcome? settled",
      "            if (ReferenceEquals(_unableToChargeForgotten, request))\n            {\n                _unableToChargeForgotten = null;\n                return null;"],
     [G2_VECTOR],
     ["TheReportCarriesTheOperatorTheChargerAndTheConditionAndTheVectorRunsInOrder"]),
    ("N02-X1-with-the-fix-unable-to-charge",
     "审查员的 X1 原样（等待路径前加 300 ms），修复保留：应保持绿",
     BUSINESS,
     "            // Settled against this press's own request, not against the shared field a snapshot may have cleared.\n            WireToGateUnableToChargeOutcome? settled",
     "            await Task.Delay(300, CancellationToken.None).ConfigureAwait(false);\n            WireToGateUnableToChargeOutcome? settled",
     [G2_VECTOR],
     []),
    ("N03-X1-old-shape-station-clearance",
     "审查第 4 条，清桩入口修复前的形状：加 300 ms，等待路径不按本次请求结算",
     [CLEARANCE, CLEARANCE],
     ["            // Settled against this press's own request, not against the shared field a snapshot may have cleared.\n            WireToGateStationClearanceOutcome? settled",
      "            if (ReferenceEquals(_stationClearanceForgotten, request))\n            {\n                _stationClearanceForgotten = null;\n                return outcome;"],
     ["            await Task.Delay(300, CancellationToken.None).ConfigureAwait(false);\n            WireToGateStationClearanceOutcome? settled",
      "            if (ReferenceEquals(_stationClearanceForgotten, request))\n            {\n                _stationClearanceForgotten = null;\n                return null;"],
     [G2_CLEARANCE],
     ["AResultFollowedAtOnceByTheSnapshotThatEndsTheClearanceIsStillShownToTheOperator"]),
    ("N04-X1-with-the-fix-station-clearance",
     "X1 用在清桩入口（等待路径前加 300 ms），修复保留：应保持绿",
     CLEARANCE,
     "            // Settled against this press's own request, not against the shared field a snapshot may have cleared.\n            WireToGateStationClearanceOutcome? settled",
     "            await Task.Delay(300, CancellationToken.None).ConfigureAwait(false);\n            WireToGateStationClearanceOutcome? settled",
     [G2_CLEARANCE],
     []),
    ("N05-switch-ignored",
     "审查第 1 条：不看 unableToChargeEntryEnabled",
     BUSINESS,
     "        if (!_recoveryOptions.UnableToChargeEntryEnabled)\n",
     "        if (_recoveryOptions.UnableToChargeEntryEnabled && false)\n",
     [G2],
     ["TheEntryIsNotOfferedWhileTheSwitchIsOff"]),
    ("N06-switch-ships-on",
     "审查第 1 条：出厂配置把开关写成 true",
     APPSETTINGS,
     '    "unableToChargeEntryEnabled": false,\n',
     '    "unableToChargeEntryEnabled": true,\n',
     [CONFIG],
     ["TheUnableToChargeEntryShipsSwitchedOff"]),
    ("N07-X2-resubmission-not-bound-to-the-operator",
     "审查第 3 条（X2）：重提不要求确认人是同一个",
     BUSINESS,
     "            && string.Equals(unanswered.ChargerStationId, chargerStationId, StringComparison.Ordinal)\n            && string.Equals(unanswered.Operator.OperatorId, operatorId, StringComparison.Ordinal);",
     "            && string.Equals(unanswered.ChargerStationId, chargerStationId, StringComparison.Ordinal);",
     [G2],
     ["AnotherMaintainerIsAskedAfreshInsteadOfResubmittingSomeoneElsesConfirmation"]),
    ("N08-dropped-session-reported-as-not-sent",
     "审查第 1 条：今天的服务端断连时，把这次按下说成「未发送」而不是结果未知",
     BUSINESS,
     "            if (request is null)\n            {\n                return UnableToChargePress.Blocked($\"现场确认充不上未发送：{exception.Message}。\");",
     "            if (request is not null)\n            {\n                return UnableToChargePress.Blocked($\"现场确认充不上未发送：{exception.Message}。\");",
     [G2],
     ["TodaysServerEndsTheSessionAndThePressIsShownAsUnknownWithoutAReplay"]),
    # ---- incremental review (8005-agv-onboard-hmi#222) ----
    ("P01-ended-charging-not-marked",
     "增量审查第 1 条：快照已结束充电用途的那次按下不标记，文案仍叫操作员重新提交",
     BUSINESS,
     "                return outcome with { ChargingEnded = true };",
     "                return outcome;",
     [G2],
     ["AnUnknownPressWhoseChargingTheServerEndedDoesNotAskForAResubmission"]),
    ("P02-ended-clearance-not-marked",
     "增量审查第 1 条：清桩入口同上",
     CLEARANCE,
     "                return outcome with { ClearanceEnded = true };",
     "                return outcome;",
     [("tests/SQCD.Agv.WireToGateG2Tests", "FullyQualifiedName~ManualStationClearanceG2Tests")],
     ["AnUnknownPressWhoseClearanceTheServerEndedDoesNotAskForAResubmission"]),
    ("P03-unable-to-charge-answer-id-not-checked",
     "增量审查 Y6：不核对应答里的 confirmationRequestId",
     CLIENT,
     "            UnableToChargeFieldConfirmationResultPayload result = DeserializeUnableToChargeResult(responseEnvelope);\n            if (!string.Equals(",
     "            UnableToChargeFieldConfirmationResultPayload result = DeserializeUnableToChargeResult(responseEnvelope);\n            if (result.Outcome.Length < 0 && !string.Equals(",
     [G2],
     ["AnAnswerNamingAnotherConfirmationIsNotTakenAsThisPresssResult"]),
    ("P04-station-clearance-answer-id-not-checked",
     "增量审查 Y6：清桩入口不核对应答里的 confirmationRequestId",
     CLIENT,
     "                DeserializeManualStationClearanceResult(responseEnvelope);\n            if (!string.Equals(",
     "                DeserializeManualStationClearanceResult(responseEnvelope);\n            if (result.Outcome.Length < 0 && !string.Equals(",
     [("tests/SQCD.Agv.WireToGateG2Tests", "FullyQualifiedName~ManualStationClearanceG2Tests")],
     ["AnAnswerNamingAnotherConfirmationIsNotTakenAsThisPresssResult"]),
    ("P05-ended-sentence-dropped-from-the-text",
     "增量审查第 1 条：文案不认标记，结束后的「结果未知」仍叫操作员重新提交",
     TEXT,
     "            _ when outcome.ChargingEnded =>",
     "            _ when outcome.ChargingEnded && outcome.ReasonCode == \"never\" =>",
     [UNIT],
     ["AnEndedChargingClaimIsSaidAndNoResubmissionIsAskedFor"]),
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


def targets(mutation):
    _, _, paths, old, _, _, _ = mutation
    olds = old if isinstance(old, list) else [old]
    files = paths if isinstance(paths, list) else [paths] * len(olds)
    return files, olds


def check():
    """Prints how often each pattern matches, without changing anything."""
    for mutation in MUTATIONS:
        files, olds = targets(mutation)
        print(mutation[0], [read(path).count(a) for path, a in zip(files, olds)])


def main():
    args = sys.argv[1:]
    if args == ["--check"]:
        check()
        return
    os.makedirs(OUT, exist_ok=True)
    os.makedirs(RED, exist_ok=True)
    only = set(args)
    summary = []
    for mutation in MUTATIONS:
        name, what, _, old, new, runs, expected = mutation
        if only and name not in only:
            continue
        files, olds = targets(mutation)
        news = new if isinstance(new, list) else [new]
        originals = {path: read(path) for path in set(files)}
        hits = [originals[path].count(a) for path, a in zip(files, olds)]
        if any(hit != 1 for hit in hits):
            print(f"{name}: NOT APPLIED, patterns matched {hits} times")
            summary.append((name, what, "NOT APPLIED", [], expected))
            continue
        mutated = dict(originals)
        for path, a, b in zip(files, olds, news):
            mutated[path] = mutated[path].replace(a, b)
        for path, text in mutated.items():
            write(path, text)
        log = [f"# {name}", what, "files: " + ", ".join(sorted(set(files))), "", "--- replaced ---",
               " ||| ".join(olds), "--- with ---", " ||| ".join(news), ""]
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
                if name.startswith("R0"):
                    write(os.path.join(RED, os.path.basename(project) + ".txt"), output)
                lines = output.splitlines()
                for line in lines:
                    match = FAILED.match(line)
                    if match:
                        failed.append(match.group(1))
                log += [f"--- dotnet test {project} --filter {test_filter} (exit {code}) ---"]
                log += [line for line in lines if line.lstrip().startswith(("Failed", "Passed!", "Failed!", "Total"))]
        finally:
            for path, text in originals.items():
                write(path, text)
        missing = [item for item in expected if not any(item in test for test in failed)]
        if status != "ran":
            verdict = status
        elif not expected:
            verdict = "GREEN AS EXPECTED" if not failed else "NOT GREEN AS EXPECTED"
        else:
            verdict = "RED AS EXPECTED" if failed and not missing else "NOT RED AS EXPECTED"
        log += ["", f"verdict: {verdict}", "expected red: " + "; ".join(expected), "missing: " + ("; ".join(missing) or "none")]
        write(os.path.join(OUT, name + ".txt"), "\n".join(log) + "\n")
        summary.append((name, what, verdict, failed, expected))
        print(f"{name}: {verdict} ({len(failed)} failed)")
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
