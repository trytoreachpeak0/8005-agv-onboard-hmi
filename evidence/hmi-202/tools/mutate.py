# Mutation for hmi#202: EnsureResultObservedAtAsync stops persisting RecoveryResultObservedAt,
# but still returns a timestamp so the result goes out and the vector completes.
# usage: python mutate.py apply|restore <worktree>
import shutil, sys, pathlib
mode, root = sys.argv[1], pathlib.Path(sys.argv[2])
target = root / "src/SQCD.Agv.Application/WireToGateRecoveryVectorExecutor.cs"
backup = pathlib.Path(__file__).with_name("WireToGateRecoveryVectorExecutor.cs.bak")
if mode == "restore":
    shutil.copyfile(backup, target)
    print("restored from", backup)
    sys.exit(0)
shutil.copyfile(target, backup)
data = target.read_bytes()
pairs = [
    (b"return current with { RecoveryResultObservedAt = _clock.Now.ToUniversalTime() };",
     b"return current; // MUTATION hmi#202: stamp not persisted"),
    (b"?? written?.RecoveryResultObservedAt\n            ?? throw new UnreachableException();",
     b"?? written?.RecoveryResultObservedAt\n            ?? _clock.Now.ToUniversalTime(); // MUTATION hmi#202"),
]
for old, new in pairs:
    n = data.count(old)
    if n != 1:
        print(f"MATCH COUNT {n} != 1 for: {old!r}")
        sys.exit(1)
    data = data.replace(old, new)
target.write_bytes(data)
print("applied 2 replacements")
