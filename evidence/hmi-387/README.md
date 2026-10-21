# hmi#387 evidence: current-stop worklist font sizes

Before = `origin/w2g/fp-v2-impl` at `5696716`; after = this branch. Both renders use the same harness and the same rows,
so the only difference is `MainWindow.xaml`.

## How the pictures were made

`render-harness.cs.txt` is a throwaway xunit test (not committed as a test) that loads the real `MainWindow.xaml` the way
`WorklistScrollingLayoutTests` does (App.xaml resources inlined, `x:Class`/`Click` stripped), binds a real `MainViewModel`,
replaces `WorklistItems` with seven rows, lays the window content out at 1004×660 (the onboard screen's client area) and
renders it with `RenderTargetBitmap`. `HMI387_XAML` picks the XAML file, so "before" reads the integration branch's copy.

Two scenarios, seven rows each:

- `machine-pickup`: two-line rows (sublot; direction, task type, basket count) with a side label.
- `staging-pickup`: one-line rows (sublot, basket count, 「取消」), the layout from hmi#353.

Sublots step through lengths: `Q26056680-14` (real MES format, 12), 13, 14, 15, 16 characters, `FIELD-1009-AGV02-01` (19,
field test barcode) and `L2-CS-20260916T110453970Z-0` (27, synthetic L2). Task types include the longest texts
(`待送→焊线机台`, `装片→焊线待送`, `焊线→质检关卡`).

## What the measurements say (`*-measure.txt`)

`w=<actual>/<natural>` per TextBlock. For the Auto columns (basket count, side) natural includes the 6 px left margin and
actual does not, so their `TRIMMED` flag is a measuring artifact (36 vs 30 = the margin); nothing there is cut.

| Row kind | Sublot shown in full, before | after |
| --- | --- | --- |
| machine pickup (two lines) | up to 27 chars | up to 19 chars (`FIELD-1009-AGV02-01` 189/203 px) |
| staging pickup (one line + 取消) | up to 19 chars | up to 14 chars (`Q260566829-123` 145/153 px) |

The detail line's longest text (`取货  装片→焊线待送  12 篮`) is 188/218 px after, so task type and count are not cut.
Longer sublots end in an ellipsis with the full value in the ToolTip, as before. UIA reads `TextBlock.Text`, which
trimming does not touch, so L2's "first TextBlock in the row is the sublot" reading is unaffected.

Row height: machine 47 → 55 px, staging 35 → 37 px; the left column now shows about one row fewer at a time.

## Checks

- `targeted-tests.txt`: `WorklistScrollingLayoutTests`, `DoorOpenPromptLayoutTests` (and the render harness) on the after
  XAML, 7 passed.
- `check-ui-layout.txt`: `scripts/check-ui-layout.ps1`, PASS.
- `format-verify.txt`: `dotnet format SQCD_8005AGV.sln --verify-no-changes`, exit 0.
