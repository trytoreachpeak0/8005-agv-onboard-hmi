"""Lists every "wait, then assert" pair in tests/SQCD.Agv.WireToGateG2Tests and says which layers each side reads.

usage: python scan.py <tests-dir> [--all] [--dump]

A *wait* is an awaited call whose method name matches Wait\\w*Async (WaitUntilAsync, WaitForStageAsync, ...).
The *assertions that follow it* are the Assert.* statements between the end of that call and the next
`await` (or the end of the method). A following `await AssertWhileAsync(...)` is included and marked HELD.

Layers are read off the text by member access (see LAYERS). A named wait helper is expanded one level: the
text of its definition (looked up by name, same file first) stands in for its predicate.

Output: one entry per pair whose assertions read a layer the wait did not (CROSS), or - with --all - every
pair; --dump adds the source lines of each entry. The script only reads and sorts; it classifies nothing as a
defect. Every CROSS entry still has to be read against the product's write order.
"""
import io
import os
import re
import sys

LAYERS = [
    ("VM", r"\.ViewModel\.(\w+)|\bviewModel\.(\w+)|\b(OperatorLog)\(|\b(WorklistRows)\(|\b(PlanLegStatuses)\("
           r"|\b(TargetSlots)\(|\b(AssertHighlighted)\(|\bdisplay\.(\w+)"),
    ("BUS", r"\.Business\.(\w+)|\bbusiness\.(\w+)|\bBusiness\.(\w+)"),
    ("EVT", r"\.(Events)\b|\b(OperatorEvents)\b|\bevents\.(\w+)|\b(HasEvent)\(|\b(ProgressMessages)\("
            r"|\b(_events)\b|\b(_recoveryBlockedEvents)\b|\b(RecoveryBlockedCount)\b|\b(EventsOfKind)\("),
    ("SES", r"\.Session\.(\w+(?:\.\w+)?)|\bsession\.(\w+)|\bclient\.(\w+)|\.Client\.(\w+)"),
    ("SRV", r"[Ss]erver\.(\w+)|\b(Received)\(|\b(Submissions)\b|\b(ResultsOfType)\(|\b(Requests)\("),
    ("IO", r"\.Io\.(\w+)|\bio\.(\w+)"),
    ("CTL", r"\.Controller\.(\w+)|\bcontroller\.(\w+)"),
    ("JRN", r"\b[Jj]ournal\w*\.(\w+)|\b(ReadJournal\w*)\(|\b(ReadRecoveryState\w*)\(|\b(ReadOutgoing\w*)\("),
    ("LOG", r"\.Logger\.(\w+)|\blogger\.(\w+)"),
]
WAIT = re.compile(r"\bawait\s+(?:[\w\.]+\.)?(Wait\w*Async)\s*\(")
ANY_AWAIT = re.compile(r"\bawait\b")
METHOD = re.compile(r"^    (?:public|private|internal|protected)[^\n=;]*?\b(\w+)\s*\(", re.M)


def strip_comments(text):
    def keep_newlines(match):
        return re.sub(r"[^\n]", " ", match.group(0))
    text = re.sub(r"/\*.*?\*/", keep_newlines, text, flags=re.S)
    # Line comments, but not the // inside a string literal such as "http://".
    return re.sub(r"(?m)(?<!:)//[^\n]*", keep_newlines, text)


def call_end(text, open_paren):
    depth, i, in_str = 0, open_paren, None
    while i < len(text):
        ch = text[i]
        if in_str:
            if ch == "\\":
                i += 1
            elif ch == in_str:
                in_str = None
        elif ch in "\"'":
            in_str = ch
        elif ch == "(":
            depth += 1
        elif ch == ")":
            depth -= 1
            if depth == 0:
                return i + 1
        i += 1
    return len(text)


def layers_of(text):
    found = {}
    for name, pattern in LAYERS:
        for match in re.finditer(pattern, text):
            member = next((g for g in match.groups() if g), "")
            found.setdefault(name, set()).add(member)
    return found


def helper_bodies(sources):
    bodies = {}
    for path, text in sources.items():
        for match in re.finditer(r"\bTask(?:<[^>]+>)?\s+(Wait\w*Async)\s*\(", text):
            end = call_end(text, match.end() - 1)
            # To the first blank line after the body has started, which is where these helpers end (one
            # statement or one WaitUntilAsync call each), capped at 45 lines.
            rest = "\n".join(text[end:end + 6000].split("\n")[:45])
            stop = re.search(r";\n\n|\n        \}\n\n|\n    \}\n", rest)
            bodies.setdefault(match.group(1), []).append((path, rest[: stop.end() if stop else len(rest)]))
    return bodies


def main():
    root = sys.argv[1]
    show_all = "--all" in sys.argv
    dump = "--dump" in sys.argv
    raw, sources = {}, {}
    for name in sorted(os.listdir(root)):
        if name.endswith(".cs"):
            raw[name] = io.open(os.path.join(root, name), encoding="utf-8").read()
            sources[name] = strip_comments(raw[name])
    helpers = helper_bodies(sources)

    total = with_asserts = cross = 0
    buckets = {}
    for name, text in sources.items():
        methods = [(m.start(), m.group(1)) for m in METHOD.finditer(text)]
        for match in WAIT.finditer(text):
            helper = match.group(1)
            total += 1
            end = call_end(text, match.end() - 1)
            wait_text = text[match.start():end]
            if "=>" not in wait_text:
                same_file = [b for p, b in helpers.get(helper, []) if p == name]
                expansion = same_file or [b for _, b in helpers.get(helper, [])]
                wait_text += "\n" + "\n".join(expansion[:1])
            # The assertions that follow, up to the next await or the end of the method.
            rest = text[end:]
            method_end = re.search(r"\n    \}", rest)
            limit = method_end.start() if method_end else len(rest)
            nxt = ANY_AWAIT.search(rest, 0, limit)
            held = ""
            after = rest[: nxt.start() if nxt else limit]
            if nxt and re.match(r"await\s+AssertWhileAsync\s*\(", rest[nxt.start():]):
                after = rest[: call_end(rest, rest.index("(", nxt.start()))]
                held = " HELD"
            asserts = "\n".join(
                statement for statement in re.split(r";\s*\n", after) if "Assert" in statement)
            if not asserts.strip():
                continue
            with_asserts += 1
            waited, asserted = layers_of(wait_text), layers_of(asserts)
            missing = {layer: members for layer, members in asserted.items() if layer not in waited}
            line = text.count("\n", 0, match.start()) + 1
            span_end = text.count("\n", 0, end + len(after.rstrip())) + 1
            owner = next((n for start, n in reversed(methods) if start < match.start()), "?")
            if missing:
                cross += 1
                key = "+".join(sorted(missing))
                buckets[key] = buckets.get(key, 0) + 1
            if missing or show_all:
                fmt = lambda d: " ".join(f"{k}[{','.join(sorted(v))[:70]}]" for k, v in sorted(d.items())) or "-"
                print(f"{'CROSS' if missing else 'same '}{held} {name}:{line}-{span_end} {owner}\n"
                      f"      waits:   {helper} {fmt(waited)}\n"
                      f"      asserts: {fmt(asserted)}")
                if dump:
                    lines = raw[name].split("\n")[line - 1:span_end]
                    print("\n".join(f"      | {text_line}" for text_line in lines))
    print(f"\nTOTAL waits={total} followed-by-assertions={with_asserts} cross-layer={cross}")
    for key, count in sorted(buckets.items(), key=lambda item: -item[1]):
        print(f"  missing {key}: {count}")


if __name__ == "__main__":
    main()
