using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading.Tasks;
using Windows.Devices.Enumeration;
using Windows.Media.Capture;
using Windows.Media.Devices;
using Windows.Storage;

namespace HAAC_Recorder_SL
{
    /// <summary>
    /// Owns the MediaCapture for a real take: negotiating a working mode at
    /// Start, rolling over at the 4 GiB WAV ceiling, stopping cleanly, and
    /// reporting a mid-take failure.
    ///
    /// Deliberately knows nothing about the UI. Everything it wants to say
    /// comes back as a return value or through the Failed callback, and the
    /// page decides how to show it. That separation is what makes the
    /// deactivation path workable: Silverlight's Deactivated has no deferral
    /// and roughly ten seconds to act, so it needs to finalize the file
    /// without touching a single control.
    /// </summary>
    public sealed class RecordingEngine
    {
        #region Constants

        // The fixed raw PCM format used everywhere. Kept here so the
        // free-space estimate always matches what is actually recorded.
        public const int SampleRateHz = 48000;
        public const int BytesPerSample = 2; // 16-bit

        // RIFF/WAV stores chunk sizes in 32-bit fields, so a single file
        // cannot exceed 4 GiB - about 5h47m of 48 kHz stereo, or 2h53m at
        // 4-channel. Rather than ending a take there, the recording rolls
        // over into a new file once the *current segment* is estimated to
        // have reached this many bytes.
        //
        // The ~200 MB gap between this figure and the real ceiling is
        // deliberate headroom: the check only runs on the health timer, so
        // the estimate can be a full interval stale when it fires; the
        // estimate is elapsed time x byte rate, which drifts slightly from
        // what was actually written; and the header and any chunk padding
        // have to fit inside the same 4 GiB budget.
        public const ulong SegmentRolloverBytes = 3800000000UL;

        private const int SettleMs = 200;

        #endregion

        #region State

        private MediaCapture _capture;
        private StorageFolder _folder;

        private CaptureAttempt _activeAttempt;
        private int _activeOccurrenceNumber;

        // Base timestamp for the take, so every segment of one recording
        // carries the same stamp and sorts together in the folder.
        private string _takeStamp;

        private int _segmentNumber;

        // True only for the brief window inside TryRotateAsync between
        // stopping one segment and starting the next. The stop half of a
        // rollover can raise MediaCapture.Failed, which would otherwise be
        // mistaken for a real mid-take fault and tear the whole recording
        // down.
        private bool _rotationInProgress;

        private readonly Stopwatch _takeStopwatch = new Stopwatch();
        private readonly Stopwatch _segmentStopwatch = new Stopwatch();

        public bool IsRecording { get; private set; }
        public StorageFile CurrentFile { get; private set; }
        public int Channels { get; private set; }
        public string DeviceNameUsed { get; private set; }
        public int SegmentNumber { get { return _segmentNumber; } }
        public TimeSpan TakeElapsed { get { return _takeStopwatch.Elapsed; } }
        public TimeSpan SegmentElapsed { get { return _segmentStopwatch.Elapsed; } }

        public long BytesPerSecond
        {
            get { return SampleRateHz * Math.Max(Channels, 1) * BytesPerSample; }
        }

        /// <summary>
        /// Raised when the capture pipeline reports a fault mid-take. Arrives
        /// on a background thread - the page marshals it to the UI thread
        /// itself. Suppressed during a rollover, which can legitimately raise
        /// the underlying event.
        /// </summary>
        public event Action<string> Failed;

        public RecordingEngine()
        {
            this.Channels = 2;
            this.DeviceNameUsed = "Default";
        }

        #endregion

        #region Start

        /// <summary>
        /// The order Start walks through: the effective mode first, then
        /// everything else that was detected, as a safety net. When detection
        /// produced nothing, falls back to a best-effort, unverified order
        /// tried directly against the real output file - something plausible
        /// beats a dead Start button, even without the independent-channel
        /// verification a detection run does.
        ///
        /// Mono stays in that last-resort order even though the detected list
        /// no longer offers it: this branch only runs when nothing at all
        /// could be verified, and there a mono recording beats no recording.
        /// </summary>
        public static async Task<List<CaptureAttempt>> BuildStartOrderAsync(
            List<CaptureAttempt> availableModes, CaptureAttempt effectiveMode)
        {
            var order = new List<CaptureAttempt>();

            if (availableModes != null && availableModes.Count > 0)
            {
                if (effectiveMode != null)
                {
                    order.Add(effectiveMode);
                }

                foreach (var mode in availableModes)
                {
                    if (mode != effectiveMode)
                    {
                        order.Add(mode);
                    }
                }

                return order;
            }

            DeviceInformationCollection devices = null;
            try
            {
                devices = await DeviceInformation.FindAllAsync(DeviceClass.AudioCapture);
            }
            catch
            {
            }

            if (devices != null && devices.Count > 0)
            {
                foreach (var device in devices)
                {
                    order.Add(new CaptureAttempt(2, device.Id, device.Name, false));
                    order.Add(new CaptureAttempt(1, device.Id, device.Name, false));
                }

                return order;
            }

            // Enumeration itself failed - last resort, negotiate against
            // whatever AudioDeviceRole.Default resolves to, the way the app
            // did before it knew multiple devices existed.
            order.Add(new CaptureAttempt(2, null, "Default", false));
            order.Add(new CaptureAttempt(1, null, "Default", false));

            return order;
        }

        /// <summary>
        /// Walks the given order and starts recording on the first entry that
        /// works, into a file named for that entry - built before the attempt
        /// rather than after, so a recording is always named for what it is
        /// actually using at every step of a fallback chain, including one
        /// that fails two seconds in.
        ///
        /// <paramref name="canonicalModes"/> is the detected list, used only
        /// to work out the "D1"/"D2" filename suffix so it matches the
        /// numbering Settings shows.
        ///
        /// Returns null on success; an error message on failure. Nothing is
        /// left holding the microphone either way.
        /// </summary>
        public async Task<string> StartAsync(
            List<CaptureAttempt> order, List<CaptureAttempt> canonicalModes)
        {
            if (this.IsRecording)
            {
                return "Already recording.";
            }

            string lastError = null;

            try
            {
                _folder = await AppSettings.GetRecordingsFolderAsync();
            }
            catch (Exception ex)
            {
                return "Couldn't open Music\\recordings: " + ex.Message;
            }

            _takeStamp = DateTime.Now.ToString("yyyy-MM-dd-HHmmss");

            foreach (var attempt in order)
            {
                DisposeCapture();
                await Task.Delay(SettleMs);

                MediaCapture capture = null;
                string error = null;

                try
                {
                    capture = await InitializeCaptureAsync(attempt.DeviceId);
                }
                catch (Exception ex)
                {
                    error = ex.Message;
                }

                if (capture == null)
                {
                    lastError = error ?? "device would not initialize";
                    continue;
                }

                _capture = capture;
                _capture.Failed += Capture_Failed;

                int occurrence = ComputeOccurrence(attempt, canonicalModes);
                StorageFile file = null;

                try
                {
                    file = await _folder.CreateFileAsync(
                        BuildFileName(attempt, occurrence, 1, _takeStamp),
                        CreationCollisionOption.GenerateUniqueName);

                    await _capture.StartRecordToStorageFileAsync(
                        ModeDetector.CreateProfile(attempt.Channels), file);
                }
                catch (Exception ex)
                {
                    error = ex.Message;
                    file = null;
                }

                if (file == null)
                {
                    // C# 5 doesn't allow await inside a catch block, so the
                    // cleanup for a failed start happens out here instead.
                    lastError = error;
                    DisposeCapture();
                    await Task.Delay(SettleMs);
                    continue;
                }

                this.CurrentFile = file;
                this.Channels = attempt.Channels;
                this.DeviceNameUsed = attempt.ShortDeviceName;

                _activeAttempt = attempt;
                _activeOccurrenceNumber = occurrence;
                _segmentNumber = 1;

                _takeStopwatch.Reset();
                _takeStopwatch.Start();
                _segmentStopwatch.Reset();
                _segmentStopwatch.Start();

                this.IsRecording = true;
                return null;
            }

            DisposeCapture();

            return lastError == null
                ? "This device didn't accept PCM capture in any channel count."
                : "Couldn't start capture: " + lastError;
        }

        #endregion

        #region Rollover

        /// <summary>
        /// True when the current file is close enough to the WAV ceiling that
        /// it should be rolled over. Estimated from elapsed time and byte
        /// rate rather than by stat-ing the file, so nothing here touches the
        /// file being written.
        /// </summary>
        public bool ShouldRollOver()
        {
            if (!this.IsRecording)
            {
                return false;
            }

            var segmentBytes = (ulong)_segmentStopwatch.Elapsed.TotalSeconds * (ulong)this.BytesPerSecond;
            return segmentBytes >= SegmentRolloverBytes;
        }

        public ulong EstimatedSegmentBytes
        {
            get { return (ulong)_segmentStopwatch.Elapsed.TotalSeconds * (ulong)this.BytesPerSecond; }
        }

        /// <summary>
        /// Ends the current segment and immediately begins a new one, so a
        /// take can run past the 4 GiB single-file ceiling instead of being
        /// stopped by it.
        ///
        /// Deliberately reuses the existing MediaCapture rather than
        /// disposing and re-initializing it. Re-initialization is the riskiest
        /// operation in this app - it's where the driver misbehaviour that
        /// motivated the fallback logic shows up - and doing it mid-take,
        /// hours in, would be the worst possible moment. StopRecordAsync
        /// followed by StartRecordToStorageFileAsync on a still-live capture
        /// keeps the device open throughout and is by far the more stable of
        /// the two options.
        ///
        /// There is an unavoidable gap between the two calls, so a rollover
        /// is not sample-accurate: expect a fraction of a second missing at
        /// the seam. WP8.1's MediaCapture has no seamless file-rotation API,
        /// so the alternatives are this or ending the take at 4 GiB, and for
        /// multi-hour recordings a brief seam beats a hard stop.
        ///
        /// Returns null when the next segment is running, or an error message
        /// when the take is over. On failure everything recorded up to that
        /// point is already closed and safe on disk, because the stop half
        /// completes before anything else is attempted.
        /// </summary>
        public async Task<string> TryRotateAsync()
        {
            if (!this.IsRecording || _capture == null || _activeAttempt == null)
            {
                return "Not recording.";
            }

            _rotationInProgress = true;

            string error = null;
            StorageFile nextFile = null;

            try
            {
                // Finalize the segment that's ending. This is what writes its
                // RIFF sizes, so it has to succeed for that file to be
                // playable - if it doesn't, don't start a new segment on top
                // of an unknown state.
                await _capture.StopRecordAsync();

                nextFile = await _folder.CreateFileAsync(
                    BuildFileName(_activeAttempt, _activeOccurrenceNumber, _segmentNumber + 1, _takeStamp),
                    CreationCollisionOption.GenerateUniqueName);

                await _capture.StartRecordToStorageFileAsync(
                    ModeDetector.CreateProfile(_activeAttempt.Channels), nextFile);
            }
            catch (Exception ex)
            {
                error = ex.Message;
            }

            if (error != null)
            {
                _rotationInProgress = false;
                this.IsRecording = false;
                _takeStopwatch.Stop();
                _segmentStopwatch.Stop();
                DisposeCapture();

                return "Couldn't continue into a new file at the 4 GB limit: " + error
                       + ". Everything recorded so far has been saved.";
            }

            this.CurrentFile = nextFile;
            _segmentNumber++;
            _segmentStopwatch.Reset();
            _segmentStopwatch.Start();

            _rotationInProgress = false;
            return null;
        }

        #endregion

        #region Stop

        /// <summary>
        /// Normal stop from the UI. Returns null on success, or a message
        /// describing what went wrong - in which case the file may still be
        /// on disk but with unfinished RIFF sizes.
        /// </summary>
        public async Task<string> StopAsync()
        {
            string error = null;

            try
            {
                if (this.IsRecording && _capture != null)
                {
                    await _capture.StopRecordAsync();
                }
            }
            catch (Exception ex)
            {
                error = ex.Message;
            }

            this.IsRecording = false;
            _takeStopwatch.Stop();
            _segmentStopwatch.Stop();
            DisposeCapture();

            return error;
        }

        /// <summary>
        /// The deactivation path, and the single biggest structural
        /// difference from the WinRT build.
        ///
        /// A WinRT app gets a suspension deferral and can await a clean
        /// StopRecordAsync. Silverlight's Deactivated has no deferral at all:
        /// the handler gets roughly ten seconds of wall clock and the process
        /// may be gone the moment it returns, so an un-awaited async stop is
        /// a coin toss on whether the WAV header ever gets its sizes written.
        ///
        /// So this blocks the calling thread on the stop, with a timeout well
        /// inside that budget. Blocking the UI thread is normally
        /// indefensible; here the alternative is a corrupt file, and the app
        /// is being torn down regardless.
        ///
        /// Returns true if the stop completed within the timeout.
        /// </summary>
        public bool StopBlocking(int timeoutMs)
        {
            try
            {
                if (!this.IsRecording || _capture == null)
                {
                    return true;
                }

                var capture = _capture;
                this.IsRecording = false;
                _takeStopwatch.Stop();
                _segmentStopwatch.Stop();

                bool completed = false;

                try
                {
                    completed = capture.StopRecordAsync().AsTask().Wait(timeoutMs);
                }
                catch
                {
                }

                DisposeCapture();
                return completed;
            }
            catch
            {
                return false;
            }
        }

        private void Capture_Failed(MediaCapture sender, MediaCaptureFailedEventArgs args)
        {
            if (_rotationInProgress)
            {
                // Expected: the stop half of a rollover can raise this.
                return;
            }

            var message = args == null || string.IsNullOrEmpty(args.Message)
                ? "unknown error"
                : args.Message;

            this.IsRecording = false;
            _takeStopwatch.Stop();
            _segmentStopwatch.Stop();

            var handler = this.Failed;
            if (handler != null)
            {
                handler(message);
            }
        }

        public void DisposeCapture()
        {
            var capture = _capture;
            _capture = null;

            if (capture == null)
            {
                return;
            }

            try
            {
                capture.Failed -= Capture_Failed;
            }
            catch
            {
            }

            ModeDetector.DisposeQuietly(capture);
        }

        #endregion

        #region Naming and init

        /// <summary>
        /// Builds the descriptive filename for an attempt, e.g.
        /// "4ch-Mic Array-D2-2026-09-06-124738.wav". segmentNumber above 1
        /// appends "-pt2", "-pt3" and so on for the files produced when a
        /// long take rolls over the 4 GiB ceiling.
        /// </summary>
        private static string BuildFileName(
            CaptureAttempt attempt, int occurrenceNumber, int segmentNumber, string stamp)
        {
            var name = string.Format("{0}ch-{1}", attempt.Channels, attempt.ShortDeviceName);

            if (occurrenceNumber > 0)
            {
                name += "-D" + occurrenceNumber;
            }

            name += "-" + stamp;

            if (segmentNumber > 1)
            {
                name += "-pt" + segmentNumber;
            }

            return Sanitize(name) + ".wav";
        }

        private static string Sanitize(string name)
        {
            var invalid = new[] { '<', '>', ':', '"', '/', '\\', '|', '?', '*' };
            foreach (var c in invalid)
            {
                name = name.Replace(c, '-');
            }

            return name;
        }

        /// <summary>
        /// This attempt's 1-based occurrence number against the canonical
        /// detected list, so the filename suffix matches the "#1"/"#2" shown
        /// in Settings. Returns 0 for an attempt that isn't in that list -
        /// i.e. one of the last-resort, live-negotiated attempts used when
        /// detection found nothing at all. That path has no verified list to
        /// number against, and it's rare enough that a filename produced from
        /// it just goes without the suffix.
        /// </summary>
        private static int ComputeOccurrence(CaptureAttempt attempt, List<CaptureAttempt> canonicalModes)
        {
            if (canonicalModes == null || canonicalModes.Count == 0)
            {
                return 0;
            }

            int index = canonicalModes.IndexOf(attempt);
            if (index < 0)
            {
                return 0;
            }

            return ModeRanking.ComputeDeviceOccurrenceNumbers(canonicalModes)[index];
        }

        /// <summary>
        /// A null or empty deviceId resolves AudioDeviceRole.Default
        /// internally - used only by the last-resort fallback, when device
        /// enumeration itself failed. Every other caller passes a specific
        /// Id discovered during detection, which is what actually fixed the
        /// original "2 channels but byte-for-byte identical L/R" bug:
        /// initializing against whatever AudioDeviceRole.Default happened to
        /// resolve to was never guaranteed to be the device that was checked.
        /// </summary>
        private static async Task<MediaCapture> InitializeCaptureAsync(string deviceId)
        {
            if (string.IsNullOrEmpty(deviceId))
            {
                try
                {
                    deviceId = MediaDevice.GetDefaultAudioCaptureId(AudioDeviceRole.Default);
                }
                catch
                {
                }
            }

            var capture = new MediaCapture();

            var settings = new MediaCaptureInitializationSettings
            {
                StreamingCaptureMode = StreamingCaptureMode.Audio,
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
                ModeDetector.DisposeQuietly(capture);
                return null;
            }
        }

        #endregion
    }
}
