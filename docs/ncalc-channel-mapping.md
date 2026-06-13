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
| `FormulaPickerButton` (`…WPFUI`) | SimHub's own formula-editor control — a templated `Control` showing the current formula and opening the full `BindingEditor` dialog (property browser, NCalc/JS, function help, live preview) on click. Bindable DPs: `Expression` (`ExpressionValue`, two-way — the dialog writes the chosen formula back into it) and `NCalcEngine` (`NCalcEngineBase`). The dialog needs only the supplied engine — no dashboard/screen context — so it hosts standalone. Its default template resolves automatically via SimHub.Plugins' `ThemeInfo`. |

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
- A single lock around evaluation: the ~30 ms telemetry tick thread and the
  500 ms UI display tick both evaluate against the one engine instance.
- Exposes the `NCalcEngineBase Engine` for the UI's `FormulaPickerButton`.

### `SimHubPropertyResolver`

`ResolveAsDouble`, `ResolveAsString`, and `GetValueForDisplay` branch to the
evaluator when `LooksLikeExpression(path)` (after the existing `@internal/`
check); otherwise unchanged. Exposes `FormulaEngine` (the shared
`NCalcEngineBase`). `MozaPlugin.ChannelFormulaEngine` forwards it to the UI.

### UI — `FormulaPickerButton`

Each row's "SimHub property" cell hosts a `FormulaPickerButton` bound to a
per-row `ExpressionValue` and the shared engine. Clicking opens SimHub's formula
editor; on commit the dialog mutates the bound `ExpressionValue` in place.

`ChannelMappingRow` keeps the stored string (`SimHubProperty`) as the source of
truth and a bound `ExpressionValue` in sync both ways:

- string → expression: a **bare property path is wrapped as `[path]`** so the
  NCalc editor sees a valid single-property formula (a mapping persisted before
  this feature is a bare path, which is invalid NCalc — without wrapping the
  editor opens broken). A string that already looks like a formula
  (brackets/operators/`js:`) is used verbatim. No data migration is needed —
  wrapping happens only at display time.
- expression → string: on the `ExpressionValue`'s `PropertyChanged`, serialize
  back, assigning `SimHubProperty`, which fires the existing
  `OnMappingRowPropertyChanged` persistence listener. A **sole `[property]` is
  unwrapped to its bare path** so existing mappings keep their plain stored form
  and the resolver's fast `GetPropertyValue` path; a real formula (`[a]+[b]`,
  functions, `js:` …) is stored verbatim. A `_syncing` guard prevents a feedback
  loop.

The FSR1/CM1 boundary/scale/bias steppers are unchanged and still reachable via
the per-row pencil (now gated to FSR1/CM1 rows — `ShowFieldOptions`); the
formula button replaces the old property filter+list for the property itself.

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
