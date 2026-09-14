<div align="center">

# 🎙️ HAAC Recorder

**Multichannel 16-bit PCM recording for Windows Phone 8.1**

![Platform](https://img.shields.io/badge/platform-Windows%20Phone%208.1-0078D7?logo=windows&logoColor=white)
![Language](https://img.shields.io/badge/language-C%23-239120?logo=csharp&logoColor=white)
![IDE](https://img.shields.io/badge/built%20with-Visual%20Studio%202013-5C2D91?logo=visualstudio&logoColor=white)
![License](https://img.shields.io/badge/license-GPLv3-blue)

</div>

---

Select Nokia Lumia phones shipped with HAAC microphone arrays — hardware capable of genuine multichannel capture, and largely wasted by the software that shipped with it. HAAC Recorder writes verified 2- and 4-channel PCM straight to WAV, and is built around one assumption: **a take runs unattended for hours and must not be lost.**

This is a Windows Phone 8.1 **Silverlight** app, and that is deliberate. `ApplicationIdleDetectionMode` — the only mechanism that keeps a recording alive once the screen locks — does not exist in a WP8.1 Store/WinRT app. The whole project is built on Silverlight to get it.

## 🎯 How it works

On first launch the app records a fraction of a second on each microphone endpoint and reads the file back, checking that the channels are genuinely independent rather than one signal copied across several. Accepting a 4-channel request means nothing on this hardware; only a mode that survives that check gets listed.

| Priority | Channels | Result |
|:---:|:---:|---|
| 1 | 4-channel | 🥇 All four HAAC mics captured independently |
| 2 | 2-channel | Genuine stereo, verified from one mic pair |
| 3 | Mono | Fallback, listed only when nothing multichannel verifies |

The result is cached, so every later launch starts instantly and never touches the microphone until you press **Start**. Detection can be re-run any time from Settings, and writes a log to `Music\recordings` explaining every accepted, rejected and filtered mode.

> **Make some noise while detection runs.** In a silent room every channel records digital zero, which is indistinguishable from a duplicate, and good microphones get rejected.

## 🎧 Before a long take

- **Turn on Airplane Mode.** A call or notification plays a sound into your recording, and answering one ends the take. Windows Phone gives no app the ability to silence the ringer on your behalf.
- **Connect a charger.** Four hours of capture is a lot of battery.
- **Lock the screen.** Recording continues, the display sleeps, and no on-screen control can be hit by accident. This is the largest battery saving available and it costs nothing.

While recording, the app reports elapsed time, remaining recording time and battery, and warns before storage or battery runs out. Stopping takes two taps, on opposite ends of the screen, with the second one disabled for the first 1.2 seconds.

## ⚙️ Settings

Lists every verified mode, best first. Pick one to override the automatic choice — the subtitle then reads `(manual)` and stays that way until you tap **Use best available**. Also holds the lock-screen recording toggle, which applies at launch rather than live, and **Run detection again**.

## 📁 Output

| Property | Value |
|---|---|
| Format | 16-bit PCM WAV, 48 kHz |
| Channels | 4 or 2 where verified, mono otherwise |
| Location | `Music\recordings\` on device |

Filenames describe what recorded them: `4ch-Mic Array-D1-2026-09-06-124738.wav`. RIFF/WAV caps a single file at 4 GiB — about 5h47m in stereo, 2h53m at 4-channel — so a longer take rolls over automatically into `-pt2`, `-pt3` and so on rather than stopping. Expect a fraction of a second missing at the seam; WP8.1 has no seamless rotation API.

## ⚠️ On "raw"

The app requests `AudioProcessing.Default`, not `Raw`. Raw was tried and dropped: on this hardware it crashed mid-take within seconds whenever more than one channel was involved, and where it did run it never delivered better channel independence than standard processing did. `MediaCategory.Other` is used throughout, which avoids the voice-call-style processing that `Communications` forces on some Lumia firmware.

## 🛠️ Build & run

1. Open `HAAC Recorder SL.sln` in **Visual Studio 2013 (Update 5)** with the WP8.1 SDK installed.
2. Build for **ARM**, configuration **Release**.
3. Deploy to a physical Lumia. The emulator has no HAAC hardware, so detection will only ever find mono — useful for exercising the fallback path and the Settings list, not for judging audio.

The project builds with VS2013's default **C# 5** compiler: no expression-bodied members, no `await` inside `catch`/`finally`.

## 💬 Feedback

No GitHub account but hit a bug? Email **start-07axed@icloud.com**. Also worth hearing: your phone model, and which modes show up in your Settings list.

Known Lumia handsets with HAAC mics:

- 1520
- 928

## License

GNU General Public License v3.0.

---

<div align="center">
<sub>Built with the help of <a href="https://claude.ai">Claude</a> 🤖</sub>
</div>
