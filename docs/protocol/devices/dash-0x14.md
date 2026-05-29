## Dash / meter (Device `0x14` / 20)

Device `0x14` is the dashboard/meter address on the internal serial bus. Two
physical peripherals use it: the legacy Moza MDD display, and the CM2 Racing
Dash when attached behind a wheelbase. Steering wheels with integrated display
screens are a separate device at `0x17`.

### CM2 Racing Dash

The CM2 (USB PID `0x0025`) connects in one of two topologies:

| Topology | Transport | Screen-telemetry target dev |
|----------|-----------|-----------------------------|
| Standalone USB | Own USB CDC device on its own COM port | `0x12` (CM2 bridge/main) |
| Behind the wheelbase | Bridged on the wheelbase serial bus as the meter at `0x14` | `0x14` (`0x12` there is the base main) |

- **Detection is by USB PID enumeration** (registry / `usbser`); no COM-port
  scanning. A standalone CM2 is claimed on a dedicated dashboard connection
  filtered to PID `0x0025`. A base-bridged CM2 has no own USB port and is the
  dash sub-device at `0x14` on the wheelbase connection.
- **Identity:** the CM2 answers the group-`0x43` identity probe with display
  model `S09 Display` (HW/SW `RS21-W08`, device type `01-02-08-06`).
- **The CM2 is driven by the group-`0x43` telemetry session pipeline** — tier
  definitions on session `0x01`, value frames on session `0x02`, routed by
  FlagByte. It acks host session opens with `fc:00` on sessions `0x01`/`0x02`.
- **The legacy group-`0x33` dash-LED surface below does not drive the CM2** — its
  per-LED RPM/flag colour and indicator-mode writes have no visible effect on a
  CM2, so they are not sent to a CM2 device. CM2 stored-LED config uses group
  `0x32` on the bridge/main at `0x12` (brightness `17 00 FF`, stored colors
  `1B 00 FF <idx>`, mode/threshold family `18`/`19`/`11`/`0D`/`0E`/`05`); see
  [`main-hub-0x12.md`](main-hub-0x12.md) § "CM2 bridge/main routing".

### Group `0x32` / `0x33` (50 / 51) — Settings (legacy MDD / wheel-dash)

| Command | ID | Bytes | Type | Notes |
|---------|----|-------|------|-------|
| rpm-timings | `05` | 10 | array | |
| rpm-display-mode | `07` | 1 | int | |
| flag-colors | `08 00` | 18 | array | Write-only |
| rpm-blink-color1 | `09 00` | 3 | array | RGB; write-only |
| rpm-blink-color2 | `09 01` | 3 | array | |
| rpm-blink-color3 | `09 02` | 3 | array | |
| rpm-blink-color4 | `09 03` | 3 | array | |
| rpm-blink-color5 | `09 04` | 3 | array | |
| rpm-blink-color6 | `09 05` | 3 | array | |
| rpm-blink-color7 | `09 06` | 3 | array | |
| rpm-blink-color8 | `09 07` | 3 | array | |
| rpm-blink-color9 | `09 08` | 3 | array | |
| rpm-blink-color10 | `09 09` | 3 | array | |
| rpm-brightness | `0A 00` | 1 | int | |
| flags-brightness | `0A 02` | 1 | int | |
| rpm-color1 | `0B 00 00` | 3 | array | RGB |
| rpm-color2 | `0B 00 01` | 3 | array | |
| rpm-color3 | `0B 00 02` | 3 | array | |
| rpm-color4 | `0B 00 03` | 3 | array | |
| rpm-color5 | `0B 00 04` | 3 | array | |
| rpm-color6 | `0B 00 05` | 3 | array | |
| rpm-color7 | `0B 00 06` | 3 | array | |
| rpm-color8 | `0B 00 07` | 3 | array | |
| rpm-color9 | `0B 00 08` | 3 | array | |
| rpm-color10 | `0B 00 09` | 3 | array | |
| flag-color1 | `0B 02 00` | 3 | array | |
| flag-color2 | `0B 02 01` | 3 | array | |
| flag-color3 | `0B 02 02` | 3 | array | |
| flag-color4 | `0B 02 03` | 3 | array | |
| flag-color5 | `0B 02 04` | 3 | array | |
| flag-color6 | `0B 02 05` | 3 | array | |
| rpm-mode | `0D` | 1 | int | |
| rpm-value1 | `0E 00` | 4 | int | RPM threshold for LED 1 |
| rpm-value2 | `0E 01` | 4 | int | |
| rpm-value3 | `0E 02` | 4 | int | |
| rpm-value4 | `0E 03` | 4 | int | |
| rpm-value5 | `0E 04` | 4 | int | |
| rpm-value6 | `0E 05` | 4 | int | |
| rpm-value7 | `0E 06` | 4 | int | |
| rpm-value8 | `0E 07` | 4 | int | |
| rpm-value9 | `0E 08` | 4 | int | |
| rpm-value10 | `0E 09` | 4 | int | |
| rpm-indicator-mode | `11 00` | 1 | int | 0=Off, 1=RPM, 2=On |
| rpm-interval | `0C` | 4 | int | |
| flags-indicator-mode | `11 02` | 1 | int | 0=Off, 1=Flags, 2=On |
