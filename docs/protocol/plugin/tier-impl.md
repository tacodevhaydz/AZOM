### Tier definition implementation

> Tier-concept reference: [`../telemetry/tiers.md`](../telemetry/tiers.md)
> (`package_level` semantics, flag-byte mapping, end-to-end channel
> example). This page covers how the plugin builds and sends tier defs.

Encoding selection is driven by the wheel-era policy, not a user "Protocol version" setting. `EraPolicy` (`Telemetry/Era/EraPolicy.cs`) resolves a `TierDefEncoding` from the per-wheel-page `MozaWheelEra` (Auto resolves via `EraPolicy.GuessFromWheelModel`):

- **V2Type02**: compact numeric tier definitions via `TierDefinitionBuilder.BuildTierDefinitionMessage()`.
- **V0Url**: URL subscription via `TierDefinitionBuilder.BuildV0UrlSubscription()`. Double-sent (once at startup, once after preamble) to match PitHouse.

The tier-def **session is resolved dynamically**, independent of era, by `TelemetrySender.ResolveTierDefSession()`:

- If the wheel's real catalog + END marker is on the **management** session `0x01` (`ChannelCatalogParser.HasRealCatalogOnSession`), the tier-def goes there.
- Otherwise the catalog is **followed onto** the telemetry session `0x02` **only for a Form-B (`FlagByte`-policy) wheel**. A Form-A (`MgmtPort`-policy) wheel — e.g. CS-Pro — keeps the tier-def on `0x01` regardless of where the catalog landed. This matters on cold start: after a hardware power cycle the wheel's first catalog can transiently arrive on `0x02`, and the old "follow the catalog" rule put the tier-def there too, leaving the wheel **unbound** — host kind=4 ignored, test mode dead — until a `DisplayWatchdog` recovery (~24 s + Stop/Start) nudged the catalog back to `0x01`. The catalog is still *parsed* from whichever session carried it; only the tier-def echo is pinned to where a Form-A wheel actually binds the subscription (mgmt). Verified CS-Pro 2026-06-07: catalog-on-0x02 + tier-def-on-0x02 → no bind; tier-def-on-0x01 → binds.
- Default: `0x01` until a catalog appears.

FF-init records, dashboard-switch (kind=4), and property pushes ride the **mirror** session (`ResolveFfSession()`, the *opposite* of the tier-def session — Form A: tier-def `0x01` / FF `0x02`; Form B: the mirror; PitHouse-derived). The flag byte for value frames is 0x00-based, not session-port-based.

(The earlier `TelemetryProtocolVersion` and `FlagByteMode` settings were removed in favour of this era policy.)

Dashboard upload controlled by `TelemetryUploadDashboard` (UI: Telemetry > Advanced > Upload dashboard, default: on). Uploads `.mzdash` file to wheel on **session 0x04** (2025-11 firmware file-transfer path, Path B below) using TLV-path + MD5 sub-msg 1/2 framing. Path A (session 0x01 FF-prefix) was the original pre-2025-11 implementation; replaced because 2025-11 firmware only actions mzdash writes via session 0x04. Mzdash content loaded from user-selected file or from embedded resource matching active profile name.

Plugin parses wheel's incoming channel catalog (session 0x02 tag 0x04 URLs) during preamble and displays detected channels in UI. Catalog also used to **filter tier definition** (`FilterProfileToCatalog`) before sending — channels in profile whose URL doesn't appear in wheel's advertised set are dropped, along with any tier ending up empty. Match case-insensitive on full URL, with last-path-segment fallback. Falls back to unfiltered profile if filtering removes everything.

**Catalog-only synthesis dedups by URL.** When the host has no `.mzdash` and synthesises the profile straight from the wheel's catalog (`DashboardProfileStore.BuildProfileFromCatalog`), the same dash's channels can appear at *multiple* catalog idxs — the wheel re-advertises a dashboard at a fresh idx range on some switches (rather than back-referencing the old idxs), so e.g. `Rpm` shows up at both idx 39 and idx 104. Emitting one tier-def channel per catalog entry then doubled the subscription (CS-Pro Marco: 127 channels for ~75 real ones), splitting the fast tier into three 30 ms sub-tiers and lagging the wheel's render. The synth now skips a URL it has already emitted (first occurrence wins), so each URL yields exactly one channel regardless of how many idxs carry it.

Before transmitting tier definition, plugin calls `WaitForChannelCatalogQuiet(quietMs=200, timeoutMs=2000)` so wheel's pre-tier-def channel-registration burst (session 0x02 tag 0x04 entries) finishes arriving first. Without this wait, fast connections can race tier def against wheel's own catalog push and wheel rejects tier def.
