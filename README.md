# HyperDrop

<img src="assets/icon/hyperdrop-256.png" alt="HyperDrop icon" width="96" />

A small Windows desktop app that lets you drag files and folders straight into a
running Hyper-V virtual machine, with real progress and a notification when it finishes.

No network share. No mounting VHDX files. No `Copy-VMFile` in an elevated prompt wondering whether
anything is actually happening.


## What it does

- Lists the Hyper-V VMs on your machine and lets you pick one.
- Accepts **files and folders** dropped anywhere on the window. Folders are walked recursively and
  their structure is recreated inside the guest.
- Transfers in the background on a serial queue, with per-file progress, transfer rate and ETA,
  plus overall progress on the Windows taskbar button.
- Notifies you when the batch finishes, even if the window is behind something else.
- Lets you cancel individual files mid-transfer, and retry the ones that failed.
- **Updates itself** from the Releases page, with a banner and an explicit "Update and restart".
- Has an **About** dialog, reachable from the link in the bottom left, that reports the version and
  the host facts a bug report needs, with a button to copy them to the clipboard.

  <img width="846" height="633" alt="image" src="https://github.com/user-attachments/assets/3ba48d9c-0040-49c1-9cf1-a8e740c9d640" />


## Download

Grab the latest build from the [Releases page](https://github.com/Structed/hyperdrop/releases).
Releases are cut by hand, versioned by date as `v{year}.{month}.{day}.{build}`.

The zip contains a single self-contained `HyperDrop.exe`, so **no .NET runtime is needed** — unzip it
anywhere and run it. Each release also ships a `.sha256` file if you want to verify the download.
After the first run HyperDrop keeps itself current, so this is normally the only manual download —
see [Updates](#updates).

Two things to expect on first run:

- If your account is not in the **Hyper-V Administrators** group, HyperDrop says so and offers to
  add it for you. That needs a one-time UAC prompt and a sign-out to take effect.
- The executable is code signed, and Windows will name **SignPath Foundation** as the publisher
  rather than the author — that is how the free certificate for open source projects works, see
  [Code signing policy](#code-signing-policy) below. SmartScreen still builds reputation per
  binary, so a fresh release may briefly warn anyway; choose **More info → Run anyway**, or build
  from source using the steps below.

## Requirements

- Windows with the **Hyper-V role** enabled.
- **.NET 10 SDK** to build from source. Not needed for the download above, which is self-contained
  (the app targets `net10.0-windows`).
- Membership of the local **Hyper-V Administrators** group. That, not elevation, is what governs
  access to the Hyper-V WMI provider, and UAC does not strip the group from the everyday token.
  HyperDrop deliberately runs unelevated, because elevating breaks drag & drop — see
  [Dropping files does nothing](#dropping-files-does-nothing).
- For the default transfer method, the target VM needs the **Guest Service Interface** integration
  service enabled. The app detects when it is off and offers a one-click **Enable**.

## Build and run

```powershell
dotnet build
dotnet run --project src/HyperDrop.App
```

Run the tests with:

```powershell
dotnet test
```

The test suite needs neither Hyper-V nor any special group membership.

A local build is versioned `{year}.{month}.{day}.0-dev` from today's UTC date, so that is what the
About dialog shows. Released builds get their version from `release.yml` instead, which is the only
place that can number two releases on the same day apart. See [Updates](#updates) for why the `-dev`
suffix matters.

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

## Updates

HyperDrop keeps itself up to date. On startup — at most once a day — it asks the GitHub Releases
API whether anything newer than the running build has been published, and shows a banner if so.
Nothing is downloaded until you press **Update and restart**.

This is the only thing HyperDrop uses the network for. Turn it off with **Check for updates
automatically** in the About dialog, and it will never call out again.

What happens when you accept an update:

1. The release zip is downloaded to `%LOCALAPPDATA%\HyperDrop\updates`.
2. Its SHA256 is compared with the `.sha256` published beside it. A mismatch discards the download
   and stops there.
3. The current files are renamed aside to `.old`, the new ones are moved into place, and HyperDrop
   restarts. If any part of that fails, the renamed files are put back.
4. The `.old` leftovers are deleted the next time HyperDrop starts, which is also what proves the
   new build runs.

No helper process and no installer are involved. Windows refuses to delete or overwrite a running
executable, but it does allow one to be *renamed* within its folder, and a renamed executable keeps
running quite happily — which is the whole trick.

A few things worth knowing:

- **The restart waits for your transfers.** While files are still moving the button is disabled,
  because restarting would abandon whatever is in flight.
- **Read-only folders are detected, not fought.** A copy unzipped into `Program Files` cannot be
  replaced by an unelevated process, and HyperDrop deliberately stays unelevated, so it says so and
  points at the Releases page instead of asking for UAC.
- **Local builds never update themselves.** A build that did not come from CI is versioned
  `{year}.{month}.{day}.0-dev`, and that `-dev` suffix is not a version HyperDrop can compare
  against a release tag. It treats anything it cannot read as a local build and stays quiet, rather
  than offering to overwrite the build you are testing with a release published the same day.
- **The check is unauthenticated**, so no token is needed. GitHub allows 60 requests an hour per
  address and once a day is nowhere near it.
- **SmartScreen does not re-prompt.** The download does not go through a browser, so the replacement
  executable is not tagged with a mark of the web.

## Troubleshooting

### Dropping files does nothing

This is the one to know about, and it is why HyperDrop does not run elevated.

WPF only speaks **OLE drag & drop**. Windows **User Interface Privilege Isolation** blocks that
protocol outright when the target window sits at a higher integrity level than the process doing
the dragging — an elevated window receiving a drop from ordinary Explorer is exactly that case. The
app looks perfectly healthy and silently ignores every drop.

`ChangeWindowMessageFilterEx` is widely cited as the fix. It is not: it only opens up the legacy
`WM_DROPFILES` protocol, which OLE drag & drop does not use. HyperDrop shipped that workaround for
a while and drag & drop still did not work.

So HyperDrop runs unelevated, where OLE drops work normally. If you do start it elevated — through
**Restart as administrator**, or from an elevated terminal — it falls back to the legacy
`WM_DROPFILES` protocol, which does survive the integrity boundary once the message filter is
open. Drops still work there, but Windows gives no drag-over highlight. The About dialog reports
which protocol is in use.

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

### "Hyper-V will not talk to this account"

Hyper-V does not fail an unauthorised read — it quietly returns nothing, which is indistinguishable
from a host with no virtual machines on it. HyperDrop tells the two apart by also looking for
`Msvm_VirtualSystemManagementService`, a host singleton that every authorised caller can see.

The fix is to join the local **Hyper-V Administrators** group, which the banner offers to do for
you. Restarting as an administrator also works, at the cost of the drag-over highlight.

## Project layout

```
src/HyperDrop.Core/     Hyper-V access, transfer queue, settings. No UI dependencies.
  HyperV/                WMI plumbing, permission probe and both copy engines
  Transfer/              drop expansion, queue, rate estimation, staging
  Update/                release lookup, checksum verification and the self-update swap
  Settings/              JSON-backed preferences
src/HyperDrop.App/      WPF front end (net10.0-windows)
  Assets/                the application icon
  Interop/               drop protocol selection, taskbar flash, de-elevated link opening
  ViewModels/            MVVM layer
  Views/                 credential prompt, About dialog, styles
assets/icon/            icon artwork and the script that renders it
tests/HyperDrop.Core.Tests/
```

`HyperDrop.Core` is deliberately free of UI and Hyper-V-instance dependencies at its seams: copy
engines sit behind `IGuestFileCopier`, machine enumeration behind `IVmProvider` and release lookup
behind `IUpdateSource`, so the queue, drop expansion, error mapping, settings and the whole update
flow are unit tested without a hypervisor and without a network.

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

## Code signing policy

Free code signing provided by [SignPath.io](https://about.signpath.io), certificate by
[SignPath Foundation](https://signpath.org).

Released builds of `HyperDrop.exe` are Authenticode signed. The certificate belongs to SignPath
Foundation, so that is the publisher Windows names. Nothing is signed on a developer machine: the
[release workflow](.github/workflows/release.yml) hands the executable to SignPath as a GitHub
Actions artifact, and SignPath verifies with GitHub that the binary really is the output of that
workflow run on this repository before signing it.

**Roles**

- Committers and reviewers: [Johannes Ebner](https://github.com/Structed)
- Approvers: [Johannes Ebner](https://github.com/Structed)

**Privacy policy**

This program will not transfer any information to other networked systems unless specifically
requested by the user or the person installing or operating it. Files leave the host only when you
drop them onto the window or add them explicitly, and they travel over VMBus to the virtual machine
you selected. Guest credentials entered for PowerShell Direct are passed to the worker process on
standard input, and are never written to disk.

The only change HyperDrop makes to the system outside its own settings file is adding your account
to the local **Hyper-V Administrators** group, which it does only when you ask it to and only
through a UAC prompt.

## License

[MIT](LICENSE). Copyright (c) 2026 Johannes Ebner.
