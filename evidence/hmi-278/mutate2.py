import sys

V = 'src/SQCD.Agv.Wpf/WireToGateBusinessService.RecoveryVectors.cs'
E = 'src/SQCD.Agv.Application/WireToGateSlotOperationExecutor.cs'
B = 'src/SQCD.Agv.Wpf/WireToGateBusinessService.cs'
C = 'src/SQCD.Agv.Wpf/WireToGateBusinessService.ConflictedRecovery.cs'

# Each mutation: (description, file, old, new). Every old string must occur exactly once.
FORGET = ('ForgetSettledVector 不写标记：TakenOverSlotOperationAttemptId 照抄原值', V,
          '            TakenOverSlotOperationAttemptId = TakenOverMarkerAfterForgetting(state, context)\n',
          '            TakenOverSlotOperationAttemptId = state.TakenOverSlotOperationAttemptId\n')
RECORD = ('MarkResultRecordedAsync 不认标记：标记那一项改成 false', E,
          '                    || string.Equals(state.TakenOverSlotOperationAttemptId, slotOperationAttemptId, StringComparison.Ordinal))',
          '                    || false)')
SETTLE = ('TrySettleInterruptedOperationAsync 不认标记：标记比较改成 false', B,
          '''                && (string.Equals(
                        Volatile.Read(ref _lastRecoveryState).TakenOverSlotOperationAttemptId,
                        attemptId,
                        StringComparison.Ordinal)
                    || context.OperationType''',
          '''                && (false
                    || context.OperationType''')
VCLEAR = ('SettleRecoveryVectorStateAsync 主分支不清标记：删掉 TakenOverSlotOperationAttemptId = null 一行', V,
          '''                        PendingLoadCancellation = null,
                        TakenOverSlotOperationAttemptId = null,
''',
          '''                        PendingLoadCancellation = null,
''')
RCLEAR = ('Recorded 不清标记：删掉 TakenOverSlotOperationAttemptId = null 一行', E,
          '''            PendingLoadCancellation = null,
            TakenOverSlotOperationAttemptId = null,
''',
          '''            PendingLoadCancellation = null,
''')
ACTED = ('第 3 条：去掉「向量做过事」条件（vectorActed 恒为 true）', V,
         '        bool vectorActed = state.ProvenRecoveryCheckpoint != WireToGateRecoveryCheckpoint.Prepared\n',
         '        bool vectorActed = true || state.ProvenRecoveryCheckpoint != WireToGateRecoveryCheckpoint.Prepared\n')
X1 = ('审查 X1：检查点写入把标记带过去（= current.TakenOverSlotOperationAttemptId）', E,
      '''                    TakenOverSlotOperationAttemptId = null
                };''',
      '''                    TakenOverSlotOperationAttemptId = current.TakenOverSlotOperationAttemptId
                };''')
X2 = ('审查 X2：人工核对结束不写标记（结束后照抄原值）', C,
      '        WireToGateRecoveryState ended = ForgetSettledVector(state, vector);',
      '        WireToGateRecoveryState ended = ForgetSettledVector(state, vector) with { TakenOverSlotOperationAttemptId = state.TakenOverSlotOperationAttemptId };')
X3 = ('审查 X3 的新形状：忘掉向量时一律丢掉已有标记（保留分支改成 null）', V,
      '''                ? state.TakenOverSlotOperationAttemptId
                : null;''',
      '''                ? null
                : null;''')
X4 = ('审查 X4：标记分支不再重发未确认的 COMPLETED', B,
      '''                if (!sent.Acknowledged)
                {
                    _ = await TryResendUnacknowledgedResultAsync(context, resultKey, cancellationToken)
                        .ConfigureAwait(false);
                }

                return InterruptedOperationSettlement.NotSettled;
            }

            if (sent is not null)''',
      '''                if (!sent.Acknowledged && attemptId == "never-matches")
                {
                    _ = await TryResendUnacknowledgedResultAsync(context, resultKey, cancellationToken)
                        .ConfigureAwait(false);
                }

                return InterruptedOperationSettlement.NotSettled;
            }

            if (sent is not null)''')
R6A = ('第 6 条：服务端拒绝非补偿向量时不清标记', V,
       '            TakenOverSlotOperationAttemptId = compensation ? current.TakenOverSlotOperationAttemptId : null\n',
       '            TakenOverSlotOperationAttemptId = current.TakenOverSlotOperationAttemptId\n')
R6B = ('第 6 条：日志簿已换成别的尝试时仍保留指向旧尝试的标记', V,
       '''        return string.Equals(
            state.TakenOverSlotOperationAttemptId,
            state.UnsettledSlotOperationAttemptId,
            StringComparison.Ordinal)
                ? state.TakenOverSlotOperationAttemptId
                : null;''',
       '''        return state.TakenOverSlotOperationAttemptId;''')

M = {
    'PRE': [FORGET, RECORD, SETTLE],
    'M1': [FORGET], 'M2': [RECORD], 'M3': [SETTLE], 'M4': [VCLEAR], 'M5': [RCLEAR],
    'M6': [ACTED], 'X1': [X1], 'X2': [X2], 'X3': [X3], 'X4': [X4], 'R6A': [R6A], 'R6B': [R6B],
}

for description, f, old, new in M[sys.argv[1]]:
    s = open(f, encoding='utf-8', newline='').read()
    if '\r\n' in s:
        old = old.replace('\n', '\r\n')
        new = new.replace('\n', '\r\n')
    assert s.count(old) == 1, (sys.argv[1], f, s.count(old))
    open(f, 'w', encoding='utf-8', newline='').write(s.replace(old, new))
    print(f'{sys.argv[1]}: {description} ({f})')
