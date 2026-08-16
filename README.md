# HyperDrop

<img src="assets/icon/hyperdrop-256.png" alt="HyperDrop icon" width="96" />

A small Windows desktop app that lets you drag files and folders straight into a
running Hyper-V virtual machine, with real progress and a notification when it finishes.

No network share. No mounting VHDX files. No `Copy-VMFile` in an elevated prompt wondering whether
anything is actually happening.

```
┌────────────────────────────────────────────────────────────┐
│ VM: [ WIN11-DEV        ▾ ] ⟳    Guest Service: Enabled      │
│ Destination in guest: [ C:\Users\Public\Downloads       ]  │
│ ☐ Overwrite existing      ☑ Create destination folders     │
├────────────────────────────────────────────────────────────┤
│ installer.msi     ▓▓▓▓▓▓▓▓░░░░  64%  18.2 MB/s  0:07 left ✕│
│ docs\readme.md    ✔ Done                                   │
├────────────────────────────────────────────────────────────┤
│ Overall ▓▓▓▓▓░░░░░  2 of 5 files       [ Clear completed ] │
└────────────────────────────────────────────────────────────┘
```

## What it does

- Lists the Hyper-V VMs on your machine and lets you pick one.
- Accepts **files and folders** dropped anywhere on the window. Folders are walked recursively and
  their structure is recreated inside the guest.
- Transfers in the background on a serial queue, with per-file progress, transfer rate and ETA,
  plus overall progress on the Windows taskbar button.
- Notifies you when the batch finishes, even if the window is behind something else.
- Lets you cancel individual files mid-transfer, and retry the ones that failed.
- Has an **About** dialog, reachable from the link in the bottom left, that reports the version and
  the host facts a bug report needs, with a button to copy them to the clipboard.

## Download

Grab the latest build from the [Releases page](https://github.com/Structed/hyperdrop/releases). Every
merge to `main` publishes one automatically, versioned by date as `v{year}.{month}.{day}.{build}`.

The zip contains a single self-contained `HyperDrop.exe`, so **no .NET runtime is needed** — unzip it
anywhere and run it. Each release also ships a `.sha256` file if you want to verify the download.

Two things to expect on first run:

- Windows prompts for elevation. That is by design; the Hyper-V WMI provider requires an elevated
  token, so the app asks for one up front rather than failing later with an opaque access error.
- SmartScreen warns that the publisher is unknown, because the executable is not code signed. Choose
  **More info → Run anyway**, or build from source using the steps below.

## Requirements

- Windows with the **Hyper-V role** enabled.
- **.NET 10 SDK** to build from source. Not needed for the download above, which is self-contained
  (the app targets `net10.0-windows`).
- **Administrator rights.** The Hyper-V WMI provider requires an elevated token, so the app
  requests elevation at launch and UAC will prompt every time. This is by design.
- For the default transfer method, the target VM needs the **Guest Service Interface** integration
  service enabled. The app detects when it is off and offers a one-click **Enable**.

## Build and run

```powershell
dotnet build
```

`dotnet run` **cannot** start HyperDrop. It launches the executable with `CreateProcess`, which
cannot raise a UAC prompt, so it fails with *"The requested operation requires elevation"*. Start it
through the shell instead, so Windows can elevate it:

```powershell
Start-Process .\src\HyperDrop.App\bin\Debug\net10.0-windows\HyperDrop.exe -Verb RunAs
```

The **Run** script in [`.github/github-app.yml`](.github/github-app.yml) does exactly this.

Run the tests with:

```powershell
dotnet test
```

The test suite needs neither Hyper-V nor elevation.

## How files actually get into the VM

Two engines are available, selectable in the UI.

### Guest Service Interface (default)

Calls `Msvm_GuestFileService.CopyFilesToGuest` on the Hyper-V WMI provider and polls the
`Msvm_ConcreteJob` it returns for `PercentComplete`.

This is the interesting part: the familiar `Copy-VMFile` cmdlet wraps the same API but blocks and
throws away the job, which is why it can never show progress. Talking to the job directly is what
makes a real progress bar possible. Files are submitted one per call so each gets its own job.

- No guest credentials needed.
- No networking needed in the VM — everything travels over VMBus.
- Works with Windows guests, and Linux guests running `hv_fcopy_daemon`.
- Progress granularity is whole percent, because that is all Hyper-V reports.

### PowerShell Direct (fallback)

For VMs where the Guest Service Interface is unavailable. A single `powershell.exe` worker process
is kept alive for the batch, opens one `PSSession` to the VM, and streams each file in chunks.

- Requires guest credentials, and a Windows guest.
- Progress is byte-exact, so it is actually finer-grained than the default engine.
- Credentials are passed to the worker on standard input. They never appear on a command line and
  are never written to disk.

The engine runs in a child process rather than through the PowerShell SDK NuGet package, which
would have added roughly 150 MB to the build output.

## Troubleshooting

### Dropping files does nothing

This is the one to know about. HyperDrop must run elevated, which puts its window at high
integrity. Windows **User Interface Privilege Isolation** then silently discards drag & drop
messages sent from medium-integrity Explorer — the app looks perfectly healthy and just ignores
every drop.

HyperDrop works around this by calling `ChangeWindowMessageFilterEx` for `WM_DROPFILES`,
`WM_COPYDATA` and `WM_COPYGLOBALDATA` when the window is created. If your environment blocks that
anyway, the app says so and you can still use **Add files… / Add folder…** or **Ctrl+V**.

### "Access denied" on a file that you can clearly read

`CopyFilesToGuest` is executed by the Hyper-V Virtual Machine Management service, not by you. That
service cannot see your per-user drive mappings and has no credentials for remote shares, so files
on `Z:\` or `\\server\share` fail with a confusing access error.

HyperDrop detects network sources and stages them into `%ProgramData%\HyperDrop\staging` first,
then copies from there and cleans up. You can switch this off in settings.

### "The guest file service is not available"

The VM is not running, or the Guest Service Interface integration service is off. Use the
**Enable** link next to the status text, and confirm integration services are installed and running
inside the guest.

### Nothing found, or "not running as an administrator"

The Hyper-V WMI provider does not deny access to an unelevated caller — it just returns an empty
list. HyperDrop checks for elevation so this shows up as a clear message rather than "no virtual
machines found".

## Project layout

```
src/HyperDrop.Core/     Hyper-V access, transfer queue, settings. No UI dependencies.
  HyperV/                WMI plumbing and both copy engines
  Transfer/              drop expansion, queue, rate estimation, staging
  Settings/              JSON-backed preferences
src/HyperDrop.App/      WPF front end (net10.0-windows)
  Assets/                the application icon
  Interop/               UIPI drag & drop fix, taskbar flash, de-elevated link opening
  ViewModels/            MVVM layer
  Views/                 credential prompt, About dialog, styles
assets/icon/            icon artwork and the script that renders it
tests/HyperDrop.Core.Tests/
```

`HyperDrop.Core` is deliberately free of UI and Hyper-V-instance dependencies at its seams: copy
engines sit behind `IGuestFileCopier` and machine enumeration behind `IVmProvider`, so the queue,
drop expansion, error mapping and settings are all unit tested without a hypervisor.

## The icon

The icon is generated rather than hand-drawn. `assets/icon/New-HyperDropIcon.ps1` declares the
artwork once and renders it with WPF, so there is no binary original to keep in sync:

```powershell
pwsh -File assets/icon/New-HyperDropIcon.ps1
```

That writes `src/HyperDrop.App/Assets/HyperDrop.ico` with nine frames from 16 to 256 pixels, plus
the SVG original and the PNG above. Frames at 16, 20 and 24 pixels drop the arrow inside the
droplet, because at that scale it turns into a smudge and the silhouette is what the eye reads.

## Limitations

- Host to guest only. `CopyFilesToGuest` is one-directional.
- The destination is typed in, not browsed. There is no API to enumerate the guest filesystem over
  this transport. HyperDrop remembers the last destination per VM.
- The VM must be running.
