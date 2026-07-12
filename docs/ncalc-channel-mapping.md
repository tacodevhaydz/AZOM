# NCalc / formula channel mapping

The channel mapper (Wheel/Dash page → "Channel mappings" section) binds each
wheel dashboard channel to a SimHub data point. Originally a binding was a
single SimHub **property path** string (e.g. `DataCorePlugin.GameData.SpeedKmh`).
This feature lets a binding instead be a full **SimHub formula** — the same
`[property]` NCalc (or `js:` JavaScript) expression language used by SimHub
dashboards — so users can scale, combine, and condition multiple properties:

```
[SpeedKmh] * 0.621                       # km/h → mph
[DataCorePlugin.GameData.Rpms] / [CarSettings_MaxRPM]
[Gear] == 'R' ? -1 : [Gear]
js: return $prop('Rpms') > 6000 ? 1 : 0  # JavaScript escape
```

## Design: reuse SimHub's own engine — no new dependency

SimHub ships `NCalc.dll` and exposes its dashboard formula evaluator as a
**public, instantiable** type in `SimHub.Plugins.dll` (verified against the
CI-tracked reference DLL, currently 9.11.15). We reuse it rather than bundling
our own NCalc so the dialect, function set, and `[property]` resolution match
SimHub dashboards exactly.

Key SimHub types (namespace
`SimHub.Plugins.OutputPlugins.Dash.TemplatingCommon` /
`…GLCDTemplating` / `…WPFUI`):

| Type | Role |
|---|---|
| `NCalcEngineBase` | `new NCalcEngineBase()` works standalone; binds to `PluginManager.Instance` internally. `public double ParseValueOrDefault(ExpressionValue, double)` and the `string` overload evaluate and **never throw** (catch → default). Caches per-frame keyed on the PluginManager hash. |
| `ExpressionValue` | A compiled formula. Has an **implicit `string → ExpressionValue`** conversion that defaults to the NCalc interpreter and auto-selects JavaScript on a `js:` prefix. `[name]` references resolve from the live property tree at compile time via a `PropertyEntryWrapper.GetValue()`. |
| `BindingEditor` (`…EditorControls`) | SimHub's formula-editor **dialog** (`SHDialogContentBase`): property browser, NCalc/JS, function help, live preview. Constructed with the `NCalcEngineBase`; shown via `ShowDialogWindowAsync(owner)` → `Task<DialogResult>` (`1` = OK). Its `DataContext` is a `DashboardBindingData` carrying the `Formula` (`ExpressionValue`), `Mode` (`BindingMode.None/Formula`), `TargetPropertyName`, and `TargetType`. Needs only the engine — no dashboard/screen context — so it opens standalone. We drive it from our own compact **ƒₓ** button rather than SimHub's heavyweight templated `FormulaPickerButton` (which renders as a large control). |

## Why this is a small change

Every binding already funnels through one string field,
`ChannelDefinition.SimHubProperty`, resolved by
`SimHubPropertyResolver.ResolveAsDouble/ResolveAsString` and forwarded via
`TelemetrySender.PropertyResolver`. An expression is just a **different string**
in that same field, so:

- **No persistence/schema change.** `MozaProfile.TelemetryChannelMappings`
  (page → dashKey → url → string) stores expressions as-is. `SetChannelMapping`,
  the FSR1/CM1 stores, and migration are untouched. Old profiles load unchanged.
- **No frame-builder change.** The builder reads `ch.SimHubProperty` live each
  tick.
- Works uniformly for tier-def, CM1, FSR1, CM2, and string channels because all
  of them resolve through the same two resolver methods.

## Components

### `Telemetry/NCalcExpressionEvaluator.cs` (new)

Isolates the typed SimHub dependency; lazy-inits one shared `NCalcEngineBase`
inside a try/catch so a future SimHub rename degrades gracefully (expressions
return their default `0`/`null`) instead of breaking the resolver.

- `bool LooksLikeExpression(string)` — true when the string contains `[`, a
  `js:` prefix, or NCalc operator/paren characters. **Plain dotted property
  paths keep the existing `GetPropertyValue` fast path** — zero overhead, full
  back-compat (no shipped default in `Data/Telemetry.json` contains those
  characters).
- `double EvalDouble(string)` / `string EvalString(string)` /
  `object EvalForDisplay(string)` via `ParseValueOrDefault`.
- A `ConcurrentDictionary<string, ExpressionValue>` caches compiled
  `ExpressionValue` objects across frames (don't realloc per tick).
- A single lock around evaluation — **load-bearing**: `NCalcEngineBase` is not
  safe for concurrent evaluation on one instance (per-eval mutation of plain
  collections; see [`docs/simhub.md`](simhub.md) § Formula Engine). The
  resolver's shared evaluator serves the ~30 ms telemetry tick and the 500 ms
  UI display tick; the 50 Hz haptics workers (LFE, mBooster) get their OWN
  evaluator instances via `MozaPlugin.CreateHapticsFormulaResolver()` +
  `SimHubPropertyResolver.ResolveAsDouble(path, formula)` so their ticks never
  queue behind telemetry/UI evaluations. Stateful functions (`blink()`,
  `changed()`, `inertia()`) keep per-engine state, so isolation also keeps each
  consumer's timing state independent.
- Exposes the `NCalcEngineBase Engine` for the UI's advanced formula dialog.

### `SimHubPropertyResolver`

`ResolveAsDouble`, `ResolveAsString`, and `GetValueForDisplay` branch to the
evaluator when `LooksLikeExpression(path)` (after the existing `@internal/`
check); otherwise unchanged. Exposes `FormulaEngine` (the shared
`NCalcEngineBase`). `MozaPlugin.ChannelFormulaEngine` forwards it to the UI.

### UI — dual mode (pencil + ƒₓ)

Each row's "SimHub property" cell shows the current mapping text plus two
buttons:

- **Pencil → simple edit.** The original inline searchable property list (filter
  + virtualized `ListBox`), and for FSR1/CM1 the per-field boundary/scale/bias
  steppers. Picks a single property; commits a bare path.
- **ƒₓ → advanced edit.** A compact custom button that opens SimHub's
  `BindingEditor` dialog (above) against the shared engine and a *working copy*
  of the row's `ExpressionValue`. On OK, the result is written back via
  `ChannelMappingRow.ApplyEditedFormula`, which serializes it into
  `SimHubProperty` and fires the existing persistence listener. The dialog never
  mutates the row's live `Expression` mid-edit (it works on the copy).

`ChannelMappingRow` keeps the stored string (`SimHubProperty`) as the source of
truth and a bound `ExpressionValue` in sync:

- string → expression: a **bare property path is wrapped as `[path]`** so the
  NCalc editor sees a valid single-property formula (a mapping persisted before
  this feature is a bare path, which is invalid NCalc — without wrapping the
  editor opens broken). A string that already looks like a formula
  (brackets/operators/`js:`) is used verbatim. No data migration is needed —
  wrapping happens only at display time.
- expression → string: `ApplyEditedFormula` (called on dialog OK) serializes the
  result and assigns `SimHubProperty`, which fires the existing
  `OnMappingRowPropertyChanged` persistence listener. A **sole `[property]` is
  unwrapped to its bare path** so existing mappings keep their plain stored form
  and the resolver's fast `GetPropertyValue` path; a real formula (`[a]+[b]`,
  functions, `js:` …) is stored verbatim. A `_syncing` guard prevents the
  string→expression sync from re-firing during the write-back.

The simple editor also keeps `SimHubProperty` in sync the other way: committing a
property from the list updates the string, whose setter re-wraps the bound
`Expression` so the next ƒₓ open shows the right formula. The FSR1/CM1
boundary/scale/bias steppers are unchanged and live in the pencil's inline panel.

### Known limitations (v1)

- Multi-line JavaScript **PreExpression** (the editor's JS "setup" block) is not
  round-tripped — only the single `Expression` string (with a `js:` prefix for
  JS) is persisted, since the mapping store is one string per channel. NCalc
  formulas and single-expression `js:` formulas round-trip fully.
- If the shared engine fails to construct on some SimHub build, expressions read
  as their default and the failure is logged once; plain property paths are
  unaffected.

## Wire-format gate

Expressions change only the *resolved value* of a channel, never frame
structure, channel indices, or compression — so `tools/sim_golden.py --check`
stays byte-exact for the shipped wheels. Run it after any change here.
