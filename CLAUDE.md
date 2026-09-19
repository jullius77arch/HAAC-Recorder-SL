# CLAUDE.md

Conventions and hard-won facts for this repository. Read before making changes.

## Commits and pushing

- **Never put a `Claude-Session:` trailer, or any `claude.ai` session link, in a
  commit message.** This repository is public and commit messages are permanent
  once pushed. `Co-Authored-By: Claude Opus 5 <noreply@anthropic.com>` is fine.
- **Ask before pushing.** Commit locally and say so; pushing is the maintainer's
  call, per branch.

## Build constraints

- Visual Studio 2013 Update 5, **C# 5 only**. No string interpolation (`$"..."`),
  no expression-bodied members, no `nameof`, no null-conditional (`?.`), and no
  `await` inside `catch` or `finally`. These compile fine in a modern editor and
  fail here.
- Target is Windows Phone 8.1 **Silverlight**, not WinRT, and that is deliberate:
  `ApplicationIdleDetectionMode` is the only thing that keeps a recording alive
  once the screen locks, and it does not exist in a WP8.1 Store app. Do not
  "modernize" this to a Store/WinRT app - it would delete the app's reason to
  exist.
- A new `.cs` file must be added to `HAAC Recorder SL.csproj` as
  `<Compile Include="Name.cs" />`. Dropping it in the folder is not enough; it
  will not compile and the error will look like a missing namespace.
- Framework assemblies are auto-referenced on this target. The Reference Manager
  showing "All of the Framework assemblies are already referenced" is normal, not
  a problem to solve.

## Platform facts established the hard way

- **WP8.1 mutes app playback while a capture session is live**, in both possible
  orderings, while still reporting the sound as `Playing`. No self-generated test
  tone can reach a recording from this app. Any design that needs a known sound
  source is dead on arrival - this was proven across eight runs before the
  approach was abandoned.
- `MediaCapture` is per-instance. Code that holds its own capture must be torn
  down on `Deactivated` explicitly; `MainPage.Service_Deactivated` only knows
  about `RecordingEngine` and returns early when no take is running. A capture
  left alive through deactivation strands the audio endpoint.
- Some Lumia drivers do not release a capture endpoint immediately. Leave a pause
  (~400 ms) between init/start/stop/dispose cycles.

## Hardware findings (measured, not assumed)

Established with `AmbientProbe` across two handsets. See the `spike/speaker-probe`
branch history for the full derivation.

| Device | Endpoint | Finding |
|---|---|---|
| Lumia 1520 | Microphone Array | Two genuinely separate mics. ch2/ch3 silent on a 4ch request - it is physically a 2-channel endpoint. |
| Lumia 1520 | Surround Microphone | Delivers 4 distinct channels, but they are **derived** from a shared set, not four microphones. Runs ~18 dB quieter, consistent with heavy noise suppression. |
| Lumia 928 | Enhanced Audio Recording Device | Two genuinely separate mics. The only usable endpoint on this handset. |
| both | Microphone | Mono duplicated across channels. Rejected by detection anyway. |

Two consequences worth keeping in mind:

- `WavProbe`'s byte-exact distinctness test **cannot** tell four real microphones
  from four differently-weighted mixes of the same signals - the mixes genuinely
  are not copies. That is why the 1520's top-ranked mode (4ch Surround) is the
  more processed one. Channel independence needs a coherence measurement, not a
  byte comparison.
- Do not add `"microphone"` to `ModeRanking.IsExcludedFromDetection`. Any
  `Contains` test would also match "Microphone Array" and "Surround Microphone"
  and exclude everything, and on a handset without an array the bare "Microphone"
  endpoint may be the only one there is.
