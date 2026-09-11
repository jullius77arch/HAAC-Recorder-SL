# Lock screen survival test — Windows Phone 8.1 Silverlight

This is a deliberately small app that answers one question on real hardware:

> With `ApplicationIdleDetectionMode` disabled, does a Silverlight 8.1 app keep
> running **and keep `MediaCapture` writing a WAV file** while the phone's
> screen is locked?

Everything in the port plan depends on the answer, so it's worth ten minutes
before any code gets moved.

## Install

Drop these into your `HAAC Recorder SL` project, replacing the template files:

| File | Destination |
|---|---|
| `App.xaml.cs` | project root (replace) |
| `MainPage.xaml` | project root (replace) |
| `MainPage.xaml.cs` | project root (replace) |
| `ProbeLog.cs` | project root (**new** — right-click project → Add → Existing Item) |
| `Properties/WMAppManifest.xml` | `Properties\` (replace) |
| `Package.appxmanifest` | project root (replace) |

`App.xaml`, `LocalizedStrings.cs`, `Resources\` and `Assets\` are unchanged.

`ProbeLog.cs` is the only genuinely new file, so it's the only one you have to
add to the project explicitly — the rest are already referenced by the
`.csproj` you sent.

Build target: **ARM**, configuration **Release**, then deploy to the phone.

## Two things that will invalidate the test

**Don't run it under the debugger.** The stock template disables
`UserIdleDetectionMode` when a debugger is attached, which stops the phone from
locking at all. I've removed that line, but the debugger changes OS behaviour in
other ways too, and a USB cable also keeps the phone charging. Deploy, unplug,
then run.

**Make sure the screen actually locks.** If it never locks, nothing is being
tested. The app reports this — a run where the screen stayed on comes back
`INCONCLUSIVE` rather than passing.

## Run

1. Unplug from USB.
2. Turn on **Airplane Mode**. An incoming call deactivates the app no matter
   what, and would look like a failure.
3. Open the app. Check the line under the title says
   `ApplicationIdleDetectionMode = Disabled (accepted)` in green. If it's red,
   stop — the mechanism isn't available and the rest is moot.
4. Tap **Start test recording**.
5. Press the **Power** button to lock the phone.
6. Leave it locked at least **10 minutes**, ideally with music or conversation
   in the room so the WAV has real content.
7. Wake and unlock.
8. Tap **Stop and save**.

## Reading the result

The verdict is computed from the WAV's size on disk, not from anything the UI
counted. A timer can keep ticking while the audio pipeline is dead, so bytes
written is the only measure worth trusting.

- **PASS** — audio length matches the wall clock, and the screen was genuinely
  locked for part of the run.
- **FAIL, short by N seconds** — the app or the pipeline died mid-run. The log
  shows where: look for the last `heartbeat` line before the gap, and whether
  an `APP DEACTIVATED` line follows it.
- **FAIL, nothing captured** — the pipeline never survived the transition.
- **INCONCLUSIVE** — the screen never locked. Shorten the lock timeout in
  Settings and don't touch the phone.

Stopping automatically exports the log to
`Music\recordings\locktest-<timestamp>-log.txt`, alongside the WAV.

## If the app is gone when you unlock

That is itself a clean result, and it's why the log lives in isolated storage
rather than memory. Relaunch the app and tap **Export log to Music\recordings**
— the heartbeat lines up to the moment it died will be there, along with the
`APP DEACTIVATED` marker and its reason.

## Run it on both handsets

The 928 and the 1520 took different update paths — the 928 is a Verizon device
and is the likelier of the two to behave differently. A pass on one isn't a pass
on both.

## What happens next

- **Both pass** → 8.1 Silverlight is confirmed, and your existing `MediaCapture`
  capture core ports across essentially unchanged. Send me your real
  `MainPage.xaml.cs` and `MainPage.xaml` and I'll write the port: the
  `LockScreenService`, the `Obscured`/`Unobscured` power handling, the converted
  UI, and the change list for the capture core.
- **Either fails** → we drop to a WP8.0 Silverlight target, which is the
  configuration Audio Recorder Pro actually shipped. That means rewriting the
  capture core against `AudioVideoCaptureDevice`, so it's a real cost — but the
  test having failed is exactly the evidence needed to justify paying it.

Send me the exported log either way. The heartbeat timestamps tell me more than
the pass/fail line does.
