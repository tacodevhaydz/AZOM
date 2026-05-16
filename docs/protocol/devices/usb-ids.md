# USB Vendor / Product IDs

All MOZA Racing devices on the host USB bus share Vendor ID **`0x346E`**
(Gudsen Technology). The plugin uses the Product ID to decide which
connection class (if any) is allowed to claim the COM port — so a CRP
pedal set never sees wheelbase probe traffic and the AB9 shifter never
sees base/hub frames.

Canonical C# source: [`../../../Protocol/MozaUsbIds.cs`](../../../Protocol/MozaUsbIds.cs).
Update both this table and the inventory dictionary together; they are
expected to stay in sync.

## Inventory

| PID      | Category    | Devices                       | Confidence  |
|----------|-------------|-------------------------------|-------------|
| `0x0000` | Wheelbase   | R16, R21                      | confirmed   |
| `0x0001` | Pedals      | CRP, CRP2                     | confirmed   |
| `0x0002` | Wheelbase   | R9                            | confirmed   |
| `0x0003` | Pedals      | SRP                           | confirmed   |
| `0x0004` | Wheelbase   | R5                            | confirmed   |
| `0x0005` | Wheelbase   | R3                            | unconfirmed |
| `0x0006` | Wheelbase   | R12, R12v2                    | confirmed   |
| `0x001E` | Shifter     | HGP                           | unconfirmed |
| `0x001F` | Handbrake   | HBP                           | unconfirmed |
| `0x0020` | Hub         | Universal HUB                 | unconfirmed |
| `0x1000` | Ab9         | AB9 active shifter            | confirmed   |

PID `0x0006` is reported by Windows as USB string `"MOZA R12 Base"`
(see [`../../../usb-capture/USB-device-tree-view-infos.txt`](../../../usb-capture/USB-device-tree-view-infos.txt)).
PID `0x0002` is verified as R9 from the user's hardware inventory.

## Categories

| Category    | Connection class                                                          | Probe target                                                          |
|-------------|---------------------------------------------------------------------------|-----------------------------------------------------------------------|
| `Wheelbase` | [`MozaSerialConnection`](../../../Protocol/MozaSerialConnection.cs) wired in [`MozaPlugin.cs`](../../../MozaPlugin.cs) | `MozaProbeTarget.BaseAndHub` — base probe (group `0x2B`) + hub probe (group `0x64`) |
| `Ab9`       | [`MozaAb9DeviceManager`](../../../Devices/MozaAb9DeviceManager.cs)        | `MozaProbeTarget.Ab9` — identity probe (group `0x09` dev `0x12`, accepts `0x89` response) |
| `Pedals`    | *(none — plugin currently does not open pedals over the CDC pipe)*        | *(none — wheelbase filter skips this category)*                       |
| `Shifter`   | *(none yet — placeholder for HGP/sequential CDC traffic)*                 | *(none)*                                                              |
| `Handbrake` | *(none yet — placeholder for HBP CDC traffic)*                            | *(none)*                                                              |
| `Hub`       | *(none yet — placeholder for Universal HUB CDC traffic)*                  | *(none)*                                                              |
| `Unknown`   | Both `Wheelbase` and `Ab9` connections accept unknown PIDs as fallback    | Each runs its own probe; the first matching response wins             |

## Discovery path

`MozaPortDiscovery` walks the registry under
```
HKLM\SYSTEM\CurrentControlSet\Enum\USB\VID_346E&PID_xxxx&MI_00\<instance>\Device Parameters\PortName
```
filtering to instances whose `Service` value is `usbser` (the standard
Windows CDC ACM driver). Live COM ports are cross-checked against
`SerialPort.GetPortNames()` so ghost registry entries are dropped.
Implementation: [`../../../Protocol/MozaPortDiscovery.cs`](../../../Protocol/MozaPortDiscovery.cs) — VID at line 32, driver filter at line 194.

The same filter applies to the serial-probe fallback in
[`MozaSerialConnection`](../../../Protocol/MozaSerialConnection.cs) that
runs when the registry returns zero MOZA devices (Wine/Proton, missing
driver, fresh Windows install). Both paths use the per-connection PID
filter lambdas in `MozaPlugin.cs` and `MozaAb9DeviceManager.cs`.

## Unknown-PID fallback

When a Moza-VID device with a PID not in the inventory shows up, both
the wheelbase and AB9 connections accept it as a probe candidate. Each
runs its protocol-specific identity probe and only the matching one
holds the port. The AB9 probe pre-flights with a base-disambiguation
check so it cannot mis-claim a wheelbase port.

`MozaPortDiscovery` logs a single Info-level line the first time it
sees each unknown PID per process lifetime:
```
[Moza] Unknown Moza PID 0xXXXX on COMn — not in usb-ids inventory.
Will be probed with every known protocol; please report so
docs/protocol/devices/usb-ids.md can be updated.
```

## Legacy constant name caveat

[`MozaUsbIds.cs`](../../../Protocol/MozaUsbIds.cs) defines
`PidWheelbaseR9 = "0x0006"` and `PidWheelbaseR12 = "0x0002"`. The
R9/R12 suffixes are **mis-labelled** relative to the hardware mapping
in the table above (R9 is `0x0002`, R12 is `0x0006`). Renaming would
churn diffs without changing behaviour because `IsWheelbasePid` accepts
both PIDs unconditionally. The names are preserved as-is for diff
hygiene; new code should prefer `MozaUsbIds.Categorize(pid)` or the
neutral constants (`PidWheelbaseR16R21`, `PidWheelbaseR5`,
`PidWheelbaseR3`).
