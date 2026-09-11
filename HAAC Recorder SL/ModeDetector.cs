using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Windows.Devices.Enumeration;
using Windows.Media.Capture;
using Windows.Media.MediaProperties;
using Windows.Storage;

namespace HAAC_Recorder_SL
{
    /// <summary>
    /// What a detection run produces: the mode list Settings will offer, and
    /// a line-by-line account of every attempt made along the way - including
    /// everything FilterRedundantModes dropped and why - for the
    /// troubleshooting log.
    /// </summary>
    public sealed class DetectionResult
    {
        public readonly List<CaptureAttempt> Modes;
        public readonly List<string> LogLines;

        public DetectionResult(List<CaptureAttempt> modes, List<string> logLines)
        {
            this.Modes = modes;
            this.LogLines = logLines;
        }
    }

    /// <summary>
    /// Finds out what this particular handset can actually do, by recording a
    /// fraction of a second on every plausible endpoint/channel-count pair
    /// and reading the result back.
    ///
    /// Lifted out of the WinRT MainPage into its own class. It owns its
    /// MediaCapture for the duration of a run and always disposes it, so a
    /// detection run can never leave the microphone held - which on this
    /// hardware would make every later attempt, including the user's actual
    /// recording, fail for no visible reason.
    ///
    /// Nothing here is Silverlight-specific: the whole class talks to WinRT
    /// APIs available through interop. The progress callback is invoked on
    /// whatever thread the awaits resume on, so the caller marshals it.
    /// </summary>
    public static class ModeDetector
    {
        private const int SampleRateHz = 48000;
        private const int BytesPerSample = 2; // 16-bit

        // Some Lumia audio drivers don't release the capture endpoint
        // synchronously on Dispose(). Detection runs several init/start/stop/
        // dispose cycles back to back, and without a pause between them the
        // second or third can fail spuriously - which would make the app
        // report fewer supported modes than the phone really has.
        private const int ProbeSettleMs = 200;

        // How long a probe actually records before its channels are
        // compared. This can't be zero: an immediate start/stop writes a WAV
        // with a header and no sample data, and a file with no samples can't
        // be shown to have distinct channels, so every device would silently
        // fail verification and get demoted to mono.
        //
        // It also has to be long enough to catch real signal. Two genuinely
        // independent channels recorded in a dead-silent room can both be
        // digital zero, which is indistinguishable from duplication - hence
        // the setup screen asking for some sound to be present while this
        // runs.
        private const int MultiChannelProbeRecordMs = 750;

        // Channel counts worth asking for, best first. 4 covers the HAAC
        // arrays that genuinely deliver four distinct signals; 3 is here for
        // handsets where three microphones is the whole hardware story; 2 is
        // the common case. 1 is not in this list - it's the fallback used
        // when none of these verify, and needs no verification itself.
        private static readonly int[] MultiChannelCandidates = { 4, 3, 2 };

        // Scratch file used only for detection. Lives in the temp folder and
        // ReplaceExisting means a leftover from a crashed run can't
        // accumulate.
        private const string ProbeFileName = "modeprobe.wav";

        /// <summary>
        /// Returns survivors ranked best-first, after the redundant options
        /// have been removed. An empty list is a valid outcome (detection
        /// unavailable) rather than an error - Start negotiates live in that
        /// case.
        /// </summary>
        public static async Task<DetectionResult> DetectAsync(Action<string> progress)
        {
            var found = new List<CaptureAttempt>();
            var log = new List<string>();

            log.Add("HAAC Recorder - Mode Detection Log");
            log.Add("Run started: " + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"));
            log.Add("Build: Windows Phone 8.1 Silverlight");
            log.Add(string.Empty);

            StorageFile probeFile = null;
            try
            {
                probeFile = await ApplicationData.Current.TemporaryFolder.CreateFileAsync(
                    ProbeFileName, CreationCollisionOption.ReplaceExisting);
            }
            catch (Exception ex)
            {
                log.Add("Could not create a temporary probe file: " + ex.Message);
            }

            if (probeFile == null)
            {
                log.Add("Detection aborted - Start will negotiate a mode live instead.");
                return new DetectionResult(found, log);
            }

            MediaCapture capture = null;

            // Note the shape of the cleanup below: C# 5 (VS2013's default
            // compiler) does not allow await inside a finally block, so the
            // probe file deletion happens after the try/catch rather than
            // inside it.
            try
            {
                DeviceInformationCollection devices = null;
                try
                {
                    devices = await DeviceInformation.FindAllAsync(DeviceClass.AudioCapture);
                }
                catch (Exception ex)
                {
                    // Enumeration failing entirely just means an empty
                    // result - Start still falls back to live negotiation.
                    log.Add("Device enumeration failed: " + ex.Message);
                }

                if (devices == null || devices.Count == 0)
                {
                    log.Add("No audio capture devices were enumerated.");
                }
                else
                {
                    log.Add(string.Format("Found {0} audio capture device(s).", devices.Count));
                    log.Add(string.Empty);

                    foreach (var device in devices)
                    {
                        log.Add(string.Format("Device: {0}  (Id: {1})", device.Name, device.Id));

                        if (ModeRanking.IsExcludedFromDetection(device.Name))
                        {
                            log.Add("  Skipped - handset and generic default endpoints have never "
                                    + "delivered better than mono on this hardware.");
                            log.Add(string.Empty);
                            continue;
                        }

                        var shortName = new CaptureAttempt(1, device.Id, device.Name, false).ShortDeviceName;

                        bool anyMultiChannelVerified = false;
                        int bestVerifiedChannels = 0;

                        foreach (var candidateChannels in MultiChannelCandidates)
                        {
                            // 3 is probed on every device, because "this
                            // phone has three mics" is only knowable after
                            // the fact - but there's no point asking for 3
                            // where 4 already verified on the same device.
                            if (candidateChannels == 3 && bestVerifiedChannels >= 4)
                            {
                                log.Add("    3ch: skipped - 4ch already verified on this device.");
                                continue;
                            }

                            if (progress != null)
                            {
                                progress(string.Format("Testing {0}ch - {1}...", candidateChannels, shortName));
                            }

                            capture = null;
                            bool started = false;
                            string error = null;

                            try
                            {
                                capture = await TryInitializeAsync(device.Id, candidateChannels);
                            }
                            catch (Exception ex)
                            {
                                error = ex.Message;
                                capture = null;
                            }

                            if (capture == null)
                            {
                                log.Add(string.Format(
                                    "    {0}ch: rejected - the device would not initialize{1}",
                                    candidateChannels,
                                    error == null ? "." : ": " + error));

                                await Task.Delay(ProbeSettleMs);
                                continue;
                            }

                            try
                            {
                                await capture.StartRecordToStorageFileAsync(
                                    CreateProfile(candidateChannels), probeFile);
                                started = true;
                            }
                            catch (Exception ex)
                            {
                                error = ex.Message;
                            }

                            if (!started)
                            {
                                log.Add(string.Format(
                                    "    {0}ch: rejected - capture would not start: {1}",
                                    candidateChannels, error));
                            }
                            else
                            {
                                // Actually record for a moment. An immediate
                                // stop would leave a WAV with no sample data,
                                // which can never be shown to have distinct
                                // channels.
                                await Task.Delay(MultiChannelProbeRecordMs);
                                await StopQuietlyAsync(capture);
                            }

                            // Tear the capture down *before* reading the probe
                            // file back: while it's still alive it can hold
                            // the file handle, and a failed read is
                            // indistinguishable from "channels were
                            // duplicated" - which would quietly demote a
                            // genuinely multi-channel device.
                            DisposeQuietly(capture);
                            capture = null;
                            await Task.Delay(ProbeSettleMs);

                            if (!started)
                            {
                                continue;
                            }

                            var analysis = await WavProbe.AnalyzeChannelsAsync(probeFile, candidateChannels);

                            log.Add(string.Format(
                                "    {0}ch: {1}", candidateChannels, analysis.DescribeOutcome()));

                            if (analysis.Verified)
                            {
                                found.Add(new CaptureAttempt(
                                    candidateChannels, device.Id, device.Name, true));

                                anyMultiChannelVerified = true;

                                if (candidateChannels > bestVerifiedChannels)
                                {
                                    bestVerifiedChannels = candidateChannels;
                                }
                            }
                        }

                        if (!anyMultiChannelVerified)
                        {
                            // Nothing multi-channel could be proven on this
                            // device, so fall back to an honest single
                            // channel. Mono needs no verification: there is
                            // no second channel it could be a duplicate of.
                            if (progress != null)
                            {
                                progress(string.Format("Testing 1ch - {0}...", shortName));
                            }

                            bool monoOk = false;
                            string monoError = null;

                            try
                            {
                                capture = await TryInitializeAsync(device.Id, 1);
                            }
                            catch (Exception ex)
                            {
                                monoError = ex.Message;
                                capture = null;
                            }

                            if (capture != null)
                            {
                                try
                                {
                                    await capture.StartRecordToStorageFileAsync(CreateProfile(1), probeFile);
                                    monoOk = true;
                                }
                                catch (Exception ex)
                                {
                                    monoError = ex.Message;
                                }

                                if (monoOk)
                                {
                                    await Task.Delay(MultiChannelProbeRecordMs);
                                    await StopQuietlyAsync(capture);
                                }

                                DisposeQuietly(capture);
                                capture = null;
                                await Task.Delay(ProbeSettleMs);
                            }

                            if (monoOk)
                            {
                                found.Add(new CaptureAttempt(1, device.Id, device.Name, false));
                                log.Add("    1ch: accepted as a single-channel fallback.");
                            }
                            else
                            {
                                log.Add("    1ch: rejected" +
                                        (monoError == null ? "." : " - " + monoError));
                            }
                        }

                        log.Add(string.Empty);
                    }
                }
            }
            catch (Exception ex)
            {
                log.Add("Detection stopped early: " + ex.Message);
            }

            // Belt and braces. Whatever happened above, don't leave a probe's
            // MediaCapture holding the microphone - on this hardware that
            // would make the user's next real recording fail for no visible
            // reason.
            DisposeQuietly(capture);

            try
            {
                await probeFile.DeleteAsync();
            }
            catch
            {
            }

            log.Add("Filtering:");
            int beforeFilter = found.Count;

            var filtered = ModeRanking.FilterRedundantModes(found, log);
            filtered.Sort(ModeRanking.CompareModes);

            if (filtered.Count == beforeFilter)
            {
                log.Add("  Nothing dropped.");
            }

            log.Add(string.Empty);
            log.Add("Final mode list (best first):");

            if (filtered.Count == 0)
            {
                log.Add("  (empty - Start will negotiate a mode live instead)");
            }
            else
            {
                foreach (var mode in filtered)
                {
                    log.Add(string.Format(
                        "  {0}{1}",
                        mode.DisplayName,
                        mode.VerifiedIndependentChannels
                            ? string.Empty
                            : " (unverified single-channel fallback)"));
                }
            }

            return new DetectionResult(filtered, log);
        }

        /// <summary>
        /// Writes a run's log lines to a timestamped text file in
        /// Music\recordings, alongside the WAV output - so it's found the
        /// same way and pulled off the phone the same way. Best-effort: a
        /// failure here is a missing diagnostic file, never a reason to
        /// disturb results that already succeeded.
        /// </summary>
        public static async Task WriteDetectionLogAsync(List<string> lines)
        {
            try
            {
                var folder = await AppSettings.GetRecordingsFolderAsync();
                var name = "mode-detection-" + DateTime.Now.ToString("yyyy-MM-dd-HHmmss") + ".txt";

                var file = await folder.CreateFileAsync(name, CreationCollisionOption.GenerateUniqueName);
                await FileIO.WriteLinesAsync(file, lines);
            }
            catch
            {
            }
        }

        #region Capture plumbing

        /// <summary>
        /// Creates and initializes a MediaCapture against a specific audio
        /// device. Returns null (instead of throwing) when the device simply
        /// rejects it, so the caller can move on to the next candidate.
        ///
        /// Always requests AudioProcessing.Default. This app used to also
        /// negotiate AudioProcessing.Raw, but raw processing was found to
        /// crash mid-take on this hardware within a couple of seconds
        /// whenever more than one channel was involved - and even where it
        /// ran, it never delivered genuinely independent channels any better
        /// than standard processing did.
        /// </summary>
        private static async Task<MediaCapture> TryInitializeAsync(string deviceId, int channels)
        {
            var capture = new MediaCapture();

            var settings = new MediaCaptureInitializationSettings
            {
                StreamingCaptureMode = StreamingCaptureMode.Audio,

                // WP8.1's MediaCategory enum only has Other and
                // Communications. Other is the right choice - Communications
                // tends to force voice-call-style processing on some Lumia
                // firmware, which is precisely what a raw PCM recorder is
                // trying to avoid.
                MediaCategory = MediaCategory.Other,
                AudioProcessing = Windows.Media.AudioProcessing.Default
            };

            if (!string.IsNullOrEmpty(deviceId))
            {
                settings.AudioDeviceId = deviceId;
            }

            try
            {
                await capture.InitializeAsync(settings);
                return capture;
            }
            catch
            {
                // Dispose before giving up. Leaking a half-built MediaCapture
                // keeps the microphone held and makes every later attempt
                // fail too.
                DisposeQuietly(capture);
                return null;
            }
        }

        internal static MediaEncodingProfile CreateProfile(int channels)
        {
            var profile = MediaEncodingProfile.CreateWav(AudioEncodingQuality.High);

            // Overwritten rather than relying on the quality preset: the
            // whole point of the app is a known, fixed, uncompressed format.
            profile.Audio = AudioEncodingProperties.CreatePcm(
                (uint)SampleRateHz, (uint)channels, (uint)(BytesPerSample * 8));

            return profile;
        }

        /// <summary>
        /// Stops a probe recording, swallowing failures - the mode clearly
        /// worked if it started at all, and a stop that misbehaves on a
        /// throwaway file isn't a reason to strike it off the list.
        /// </summary>
        private static async Task StopQuietlyAsync(MediaCapture capture)
        {
            try
            {
                await capture.StopRecordAsync();
            }
            catch
            {
            }
        }

        internal static void DisposeQuietly(MediaCapture capture)
        {
            if (capture == null)
            {
                return;
            }

            try
            {
                capture.Dispose();
            }
            catch
            {
            }
        }

        #endregion
    }
}
