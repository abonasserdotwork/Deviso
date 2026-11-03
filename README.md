# Deviso

Deviso is an original Windows desktop storage-health utility. It does not contain
Hard Disk Sentinel code, assets, or reverse-engineered implementation details.

## Credits and license

Created by **Abonasser.Dev**. Deviso is proprietary software; all rights are
reserved. See [LICENSE](./LICENSE) for the applicable terms; it is included next
to the published executable.

## Current features

- Original dark storage dashboard with a persistent side-navigation rail,
  at-a-glance drive summaries, live operation status, and Overview,
  Temperature, Performance, Remote, and Alerts & Reports sections.
- Search the drive list by device name, interface, volume, or health status;
  select a drive to inspect its reported health, temperature, wear, remaining
  life, and power-on time.
- Uses the supplied blue storage-monitor image as the embedded Windows app icon,
  window icon, notification-area icon, and Start menu shortcut icon.
- The notification-area icon's right-click menu includes Open Deviso, Refresh
  drives, Reports & history, and Exit Deviso.
- Lists physical disks exposed by the Windows Storage module and shows Windows
  health, device type, capacity, mounted volume, and available reliability
  counters (temperature, power-on hours, and wear). Drives list and remote peer
  views expose power-on time and supported SMART life remaining as a percentage.
- Offers an explicit **Unlock SMART details (Admin)** action because Windows
  can deny reliability-counter requests to a standard process. It displays the
  reported source and reason when counters are missing. Approving UAC can improve
  access, but a controller may still withhold its sensors.
- Uses `smartctl` JSON when smartmontools is installed to read supported SMART
  temperature, power-on hours, overall assessment, and remaining-life data.
  The HS-SSD-E100 128G reports its remaining-life percentage in the raw value
  of SMART attribute 233, scaled by 1,000; Deviso applies that model-specific
  decoding instead of treating the normalized SMART value as remaining life.
  Windows' `Wear` counter is treated as percent used and converted to remaining
  percentage only when its value is valid. NVMe percentage-used is also converted
  to remaining percentage. Supported SSD lifetime attributes also show the
  complementary wear-used percentage; drives without a supported indicator show
  "Not reported." The display never invents 100% life from a generic Windows
  "Healthy" status.
- Shows a drive-reported remaining-life percentage only; it does not calculate
  or predict time until failure. SMART wear indicators can vary by manufacturer
  and should not be treated as an accurate lifespan forecast.
- Shows the same temperature, power-on, wear, remaining-life, health, and source
  fields in the remote peer drive list.
- Refreshes automatically every five minutes and appends JSON Lines history to
  `%LOCALAPPDATA%\Deviso\drive-history.jsonl`. The temperature chart plots
  recorded readings; it does not fabricate earlier readings.
- Runs a 64 MB temporary-file read/write benchmark on a mounted volume. The test
  file is removed after the run; results are volume-level, not raw-device speeds.
- Runs Windows `chkdsk` for the selected mounted volume without requesting
  repairs. This is not a physical surface scan.
- Logs detected Windows health/temperature warnings, scan results, and
  benchmark results in the Activity section.
- Sends Windows tray notifications for newly detected Windows health or
  temperature warnings while Deviso is running; the temperature threshold is
  configurable in Alerts & Reports.
- Exports the current Windows-reported drive inventory to a CSV report.
- Can start a read-only HTTPS health endpoint on active private IPv4 LAN
  interfaces. A peer must provide the one-time pairing token and verify/pin the
  displayed SHA-256 certificate fingerprint. No public-address listener is
  opened. The Windows firewall may require an administrator to allow the chosen
  port (`47821`).
- Can request a short or extended SMART self-test through `smartctl` when
  smartmontools is installed and the drive/controller allows it. Deviso asks for
  confirmation and reports the tool's actual response; it does not claim that
  unsupported tests ran.

## Requirements

- **To run a published build:** Windows 10 or 11 (64-bit). The release is
  self-contained, so a separate .NET runtime installation is not required.
- **To build from source:** the .NET 10 SDK installed on Windows. No separate
  Windows Desktop workload or third-party NuGet packages are required.
- **Windows PowerShell 5.1** and `chkdsk.exe` are included with Windows and are
  used for disk information and file-system checks.
- **[smartmontools](https://www.smartmontools.org/)** is optional. Install it
  for additional SMART data and SMART self-tests. Deviso looks for
  `smartctl.exe` on `PATH` and in the standard Program Files installation
  folders. Without it, Windows-provided health and reliability information
  remains available where supported.

## Build from source

On Windows with the .NET 10 SDK installed:

```powershell
dotnet build .\Deviso.csproj
dotnet run --project .\Deviso.csproj
dotnet publish .\Deviso.csproj -c Release -r win-x64 --self-contained true
```

The self-contained executable is published under
`bin\Release\net10.0-windows\win-x64\publish\`.

SMART/reliability counters vary by storage driver, controller, and device. Deviso
reports unavailable counters rather than treating missing data as a healthy result.
The Alerts & Reports section can add or remove a shortcut in the current user's
Windows Start menu.
The running app's Windows taskbar Jump List provides Open, Refresh drives, Open
reports & history, and Exit actions. Windows controls its own built-in taskbar
context-menu entries. If Deviso was already pinned before its taskbar identity
was updated, unpin the old icon and pin Deviso again from the Start menu.

Alerts are local tray notifications while the app is running; there is no
background Windows service, email, or SMS delivery. Remote monitoring is an
authenticated, read-only Deviso-to-Deviso LAN endpoint, not a cloud service or
remote-control feature. SMART self-test commands require smartmontools to be
installed separately and may require elevated rights. Physical surface scanning,
individual raw SMART attributes, and reliable lifetime prediction are not
implemented. Remaining-life percentages are drive wear indicators, not a
prediction of time until failure. Data remains dependent on the model, driver,
controller, permissions, and installed smartmontools support.
