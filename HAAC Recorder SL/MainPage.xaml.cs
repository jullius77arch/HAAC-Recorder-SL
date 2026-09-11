using System;
using System.ComponentModel;
using System.Diagnostics;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media;
using System.Windows.Threading;
using Microsoft.Phone.Controls;
using Microsoft.Phone.Shell;
using Windows.Media.Capture;
using Windows.Media.MediaProperties;
using Windows.Storage;

namespace HAAC_Recorder_SL
{
    /// <summary>
    /// Minimal, self-contained test of one question:
    ///
    ///     On this handset, does a Windows Phone 8.1 *Silverlight* app with
    ///     ApplicationIdleDetectionMode disabled keep running — and keep
    ///     MediaCapture writing to a WAV file — while the screen is locked?
    ///
    /// A timer that keeps ticking is not enough evidence. The process staying
    /// alive and the audio pipeline continuing to write are separate things,
    /// and the second one is what the recorder actually depends on. So this
    /// records a real 48kHz PCM WAV into Music\recordings for the duration of
    /// the test, and the verdict is read off the resulting file's length, not
    /// off the UI.
    ///
    /// Deliberately not a port of the real app: no mode detection, no device
    /// enumeration, no rollover, no overlays. Fewer moving parts means a
    /// failure here points at the platform rather than at this code.
    /// </summary>
    public partial class MainPage : PhoneApplicationPage
    {
        #region Constants

        // Matches the real app so the byte-rate arithmetic below lines up
        // with what it will actually record.
        private const int SampleRateHz = 48000;
        private const int BytesPerSample = 2; // 16-bit

        // Five seconds rather than one. The heartbeat exists to prove the app
        // is alive across a long locked stretch, and one line every five
        // seconds gives a readable log over a ten-minute run without waking
        // the CPU more than necessary. The on-screen clock is separate and
        // only runs while the screen is on.
        private const int HeartbeatSeconds = 5;

        #endregion

        #region Fields

        private MediaCapture _capture;
        private StorageFile _recordingFile;
        private StorageFolder _recordingFolder;

        private bool _isRecording;
        private int _channels;

        // Monotonic. DateTime.Now would jump if the phone picked up an NTP
        // correction part-way through a long run, which is exactly the kind
        // of thing that would make an otherwise good result look broken.
        private readonly Stopwatch _elapsed = new Stopwatch();

        private DispatcherTimer _uiTimer;
        private DispatcherTimer _heartbeatTimer;

        // Tracks the screen state so the heartbeat can label each line, and
        // so the summary can say whether the run ever actually went under the
        // lock screen. A "pass" with no obscured period tested nothing.
        private bool _isObscured;
        private bool _wasEverObscured;
        private DateTime _obscuredAt;
        private TimeSpan _longestObscuredSpan;

        #endregion

        public MainPage()
        {
            InitializeComponent();

            Loaded += MainPage_Loaded;
        }

        private void MainPage_Loaded(object sender, RoutedEventArgs e)
        {
            try
            {
                ReportIdleDetectionState();
                HookObscuredEvents();
                RefreshLogTail();

                // A log left over from a previous run is the interesting case:
                // it means the app was killed rather than stopped, which is
                // itself the answer. Say so instead of quietly appending.
                if (ProbeLog.HasEntries())
                {
                    StatusText.Text =
                        "A log from an earlier run is on disk. Export it before starting a new test, " +
                        "or clear it to start fresh.";
                }
            }
            catch (Exception ex)
            {
                StatusText.Text = "Startup problem: " + ex.Message;
            }
        }

        /// <summary>
        /// If disabling idle detection threw, every result below is
        /// meaningless — so this is stated at the top of the screen in red
        /// rather than buried.
        /// </summary>
        private void ReportIdleDetectionState()
        {
            if (App.RunningUnderLockScreenEnabled)
            {
                IdleModeText.Foreground = new SolidColorBrush(Colors.Green);
                IdleModeText.Text = "ApplicationIdleDetectionMode = Disabled  (accepted)";
            }
            else
            {
                IdleModeText.Foreground = new SolidColorBrush(Colors.Red);
                IdleModeText.Text =
                    "Could not disable idle detection — this test cannot pass. " +
                    (App.LockScreenSetupError ?? string.Empty);
            }
        }

        #region Obscured / unobscured

        /// <summary>
        /// Obscured fires when something covers the app — the lock screen, but
        /// also an incoming call screen or a toast. ObscuredEventArgs.IsLocked
        /// distinguishes them, and only the locked case is what's being tested
        /// here.
        ///
        /// In the real app this pair is also where CPU use gets cut while the
        /// screen is off: stop the per-second clock, keep the once-a-minute
        /// health check. Over a four-hour take that difference is worth real
        /// battery.
        /// </summary>
        private void HookObscuredEvents()
        {
            var frame = Application.Current.RootVisual as PhoneApplicationFrame;

            if (frame == null)
            {
                // Shouldn't happen once Loaded has fired, but a null here
                // would otherwise be an unexplained silent gap in the log.
                ProbeLog.Append("WARNING: root frame unavailable, obscured events not hooked");
                return;
            }

            frame.Obscured += Frame_Obscured;
            frame.Unobscured += Frame_Unobscured;
        }

        private void Frame_Obscured(object sender, ObscuredEventArgs e)
        {
            _isObscured = true;
            _obscuredAt = DateTime.Now;

            if (e.IsLocked)
            {
                _wasEverObscured = true;
            }

            ProbeLog.Append("OBSCURED  (IsLocked=" + e.IsLocked + ")");

            // The clock is only meaningful when someone can see it, and a
            // 1Hz dispatcher tick behind a locked screen is pure waste.
            StopUiTimer();

            ObscuredText.Text = "Screen was obscured at " + _obscuredAt.ToString("HH:mm:ss");
        }

        private void Frame_Unobscured(object sender, EventArgs e)
        {
            var span = DateTime.Now - _obscuredAt;

            if (_isObscured && span > _longestObscuredSpan)
            {
                _longestObscuredSpan = span;
            }

            _isObscured = false;

            ProbeLog.Append(string.Format(
                "UNOBSCURED  (obscured for {0:0}s)", span.TotalSeconds));

            ObscuredText.Text = string.Format(
                "Last locked stretch: {0:0}s   ·   longest this run: {1:0}s",
                span.TotalSeconds, _longestObscuredSpan.TotalSeconds);

            if (_isRecording)
            {
                StartUiTimer();
                UpdateElapsedText();
            }

            RefreshLogTail();
        }

        #endregion

        #region Start

        private async void StartButton_Click(object sender, RoutedEventArgs e)
        {
            StartButton.IsEnabled = false;
            ResultText.Text = string.Empty;

            try
            {
                await StartTestRecordingAsync();
            }
            catch (Exception ex)
            {
                StatusText.Text = "Failed to start: " + ex.Message;
                ProbeLog.Append("START FAILED: " + ex.Message);
                DisposeCapture();
                StartButton.IsEnabled = true;
            }
        }

        private async Task StartTestRecordingAsync()
        {
            StatusText.Text = "Opening the microphone...";

            _recordingFolder = await KnownFolders.MusicLibrary.CreateFolderAsync(
                "recordings", CreationCollisionOption.OpenIfExists);

            _capture = new MediaCapture();

            var settings = new MediaCaptureInitializationSettings
            {
                StreamingCaptureMode = StreamingCaptureMode.Audio,

                // Other rather than Communications: Communications tends to
                // pull in voice-call-style processing on Lumia firmware, which
                // is the opposite of what this app is for. Same choice the
                // WinRT build already makes.
                MediaCategory = MediaCategory.Other,
                AudioProcessing = Windows.Media.AudioProcessing.Default
            };

            await _capture.InitializeAsync(settings);

            _capture.Failed += Capture_Failed;

            var fileName = "locktest-" + DateTime.Now.ToString("yyyyMMdd-HHmmss") + ".wav";
            _recordingFile = await _recordingFolder.CreateFileAsync(
                fileName, CreationCollisionOption.GenerateUniqueName);

            // Stereo first, mono as a fallback. No device enumeration here —
            // whichever endpoint the default role resolves to is fine, because
            // this test is about survival, not about channel quality.
            if (await TryStartAsync(2))
            {
                _channels = 2;
            }
            else if (await TryStartAsync(1))
            {
                _channels = 1;
            }
            else
            {
                throw new InvalidOperationException("The microphone accepted neither stereo nor mono.");
            }

            _isRecording = true;
            _elapsed.Restart();

            _wasEverObscured = false;
            _longestObscuredSpan = TimeSpan.Zero;

            ProbeLog.Append(string.Format(
                "RECORDING STARTED  file={0}  channels={1}  rate={2}",
                _recordingFile.Name, _channels, SampleRateHz));

            StartUiTimer();
            StartHeartbeatTimer();

            StopButton.IsEnabled = true;
            StatusText.Text = "Recording. Press the Power button now to lock the phone.";

            RefreshLogTail();
        }

        private async Task<bool> TryStartAsync(int channels)
        {
            var profile = MediaEncodingProfile.CreateWav(AudioEncodingQuality.High);
            profile.Audio = AudioEncodingProperties.CreatePcm(
                SampleRateHz, (uint)channels, BytesPerSample * 8);

            try
            {
                await _capture.StartRecordToStorageFileAsync(profile, _recordingFile);
                return true;
            }
            catch (ArgumentException)
            {
                // The device rejected that channel count specifically. Any
                // other exception is a real problem and is allowed to escape.
                return false;
            }
        }

        #endregion

        #region Stop

        private async void StopButton_Click(object sender, RoutedEventArgs e)
        {
            StopButton.IsEnabled = false;

            try
            {
                await StopTestRecordingAsync();
            }
            catch (Exception ex)
            {
                StatusText.Text = "Failed to stop cleanly: " + ex.Message;
                ProbeLog.Append("STOP FAILED: " + ex.Message);
            }

            DisposeCapture();
            StartButton.IsEnabled = true;
        }

        private async Task StopTestRecordingAsync()
        {
            StopUiTimer();
            StopHeartbeatTimer();

            var wallClock = _elapsed.Elapsed;
            _elapsed.Stop();

            if (_isRecording && _capture != null)
            {
                await _capture.StopRecordAsync();
            }

            _isRecording = false;

            ProbeLog.Append(string.Format("RECORDING STOPPED  wallclock={0:0}s", wallClock.TotalSeconds));

            await ReportVerdictAsync(wallClock);
            await ExportLogAsync();
        }

        /// <summary>
        /// The actual verdict. Audio length is derived from the file size
        /// rather than trusted from any counter in this app: bytes on disk is
        /// the only measure that can't be faked by a UI that kept running
        /// while the pipeline was dead.
        ///
        /// A pass needs both halves — audio length matching the wall clock,
        /// AND the screen having genuinely been locked for a meaningful
        /// stretch during the run.
        /// </summary>
        private async Task ReportVerdictAsync(TimeSpan wallClock)
        {
            if (_recordingFile == null)
            {
                return;
            }

            double audioSeconds;
            double sizeMb;

            try
            {
                var props = await _recordingFile.GetBasicPropertiesAsync();

                // 44 bytes of canonical WAV header. Negligible over a ten
                // minute run, subtracted anyway so short runs don't look
                // suspiciously long.
                var dataBytes = props.Size > 44 ? props.Size - 44 : 0;
                var bytesPerSecond = (double)SampleRateHz * BytesPerSample * _channels;

                audioSeconds = dataBytes / bytesPerSecond;
                sizeMb = props.Size / (1024.0 * 1024.0);
            }
            catch (Exception ex)
            {
                ResultText.Text = "Couldn't measure the file: " + ex.Message;
                return;
            }

            var shortfall = wallClock.TotalSeconds - audioSeconds;

            var sb = new StringBuilder();
            sb.AppendLine(string.Format("File: {0}  ({1:N1} MB, {2}ch)", _recordingFile.Name, sizeMb, _channels));
            sb.AppendLine(string.Format("Wall clock: {0:0}s     Audio captured: {1:0}s", wallClock.TotalSeconds, audioSeconds));
            sb.AppendLine(string.Format("Longest locked stretch: {0:0}s", _longestObscuredSpan.TotalSeconds));
            sb.AppendLine();

            if (!_wasEverObscured)
            {
                sb.Append("INCONCLUSIVE — the screen never locked during this run, so nothing was tested. " +
                          "Check that Settings > lock screen timeout is short, and don't touch the phone.");
            }
            else if (shortfall < 5)
            {
                sb.Append("PASS — the audio is as long as the run, and the screen was locked for part of it. " +
                          "MediaCapture kept writing under lock.");
            }
            else if (audioSeconds < 5)
            {
                sb.Append("FAIL — essentially nothing was captured. The pipeline died at or near the start.");
            }
            else
            {
                sb.Append(string.Format(
                    "FAIL — the audio is {0:0}s short of the run. Capture stopped part-way through; " +
                    "check the log for where it happened.", shortfall));
            }

            ResultText.Text = sb.ToString();
            ProbeLog.Append("VERDICT: " + sb.ToString().Replace(Environment.NewLine, " | "));

            StatusText.Text = "Saved to Music\\recordings.";
        }

        private void Capture_Failed(MediaCapture sender, MediaCaptureFailedEventArgs errorEventArgs)
        {
            var message = errorEventArgs == null ? "unknown" : errorEventArgs.Message;

            // Arrives off the UI thread. Log first — that's the part that must
            // not be lost — then marshal for anything that touches a control.
            ProbeLog.Append("CAPTURE FAILED: " + message);

            Dispatcher.BeginInvoke(() =>
            {
                _isRecording = false;
                StopUiTimer();
                StopHeartbeatTimer();
                ResultText.Text = "FAIL — the capture pipeline reported: " + message;
                StopButton.IsEnabled = false;
                StartButton.IsEnabled = true;
            });
        }

        private void DisposeCapture()
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

            try
            {
                capture.Dispose();
            }
            catch
            {
            }
        }

        #endregion

        #region Timers

        private void StartUiTimer()
        {
            StopUiTimer();

            // Note the Silverlight signature: Tick is EventHandler, so the
            // handler takes (object, EventArgs) — not the (object, object)
            // that the WinRT DispatcherTimer uses. This is one of the small
            // mechanical differences that will show up all over the real port.
            _uiTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
            _uiTimer.Tick += UiTimer_Tick;
            _uiTimer.Start();
        }

        private void StopUiTimer()
        {
            if (_uiTimer != null)
            {
                _uiTimer.Stop();
                _uiTimer.Tick -= UiTimer_Tick;
                _uiTimer = null;
            }
        }

        private void UiTimer_Tick(object sender, EventArgs e)
        {
            UpdateElapsedText();
        }

        private void UpdateElapsedText()
        {
            var t = _elapsed.Elapsed;
            ElapsedText.Text = string.Format("{0:00}:{1:00}:{2:00}", (int)t.TotalHours, t.Minutes, t.Seconds);
        }

        /// <summary>
        /// Keeps running while the screen is locked — that's the point. Each
        /// line landing on disk five seconds after the last is the evidence
        /// that the process is still alive; a gap in the timestamps is the
        /// evidence that it wasn't.
        /// </summary>
        private void StartHeartbeatTimer()
        {
            StopHeartbeatTimer();

            _heartbeatTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(HeartbeatSeconds) };
            _heartbeatTimer.Tick += HeartbeatTimer_Tick;
            _heartbeatTimer.Start();
        }

        private void StopHeartbeatTimer()
        {
            if (_heartbeatTimer != null)
            {
                _heartbeatTimer.Stop();
                _heartbeatTimer.Tick -= HeartbeatTimer_Tick;
                _heartbeatTimer = null;
            }
        }

        private void HeartbeatTimer_Tick(object sender, EventArgs e)
        {
            ProbeLog.Append(string.Format(
                "heartbeat  elapsed={0:0}s  obscured={1}",
                _elapsed.Elapsed.TotalSeconds,
                _isObscured));
        }

        #endregion

        #region Log export

        private async void ExportLogButton_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                await ExportLogAsync();
            }
            catch (Exception ex)
            {
                StatusText.Text = "Couldn't export the log: " + ex.Message;
            }
        }

        private async Task ExportLogAsync()
        {
            var text = ProbeLog.BuildHeader() + ProbeLog.ReadAll();

            if (string.IsNullOrEmpty(text))
            {
                StatusText.Text = "Nothing in the log yet.";
                return;
            }

            var folder = _recordingFolder ?? await KnownFolders.MusicLibrary.CreateFolderAsync(
                "recordings", CreationCollisionOption.OpenIfExists);

            var name = "locktest-" + DateTime.Now.ToString("yyyyMMdd-HHmmss") + "-log.txt";
            var file = await folder.CreateFileAsync(name, CreationCollisionOption.GenerateUniqueName);

            await FileIO.WriteTextAsync(file, text);

            StatusText.Text = "Log written to Music\\recordings\\" + name;
            RefreshLogTail();
        }

        private void ClearLogButton_Click(object sender, RoutedEventArgs e)
        {
            ProbeLog.Clear();
            RefreshLogTail();
            StatusText.Text = "Log cleared.";
        }

        /// <summary>
        /// Shows the last handful of lines so the phone can be checked at a
        /// glance without pulling files off it.
        /// </summary>
        private void RefreshLogTail()
        {
            var lines = ProbeLog.ReadLines();

            if (lines.Count == 0)
            {
                LogTailText.Text = "(empty)";
                return;
            }

            var sb = new StringBuilder();
            var start = Math.Max(0, lines.Count - 12);

            for (int i = start; i < lines.Count; i++)
            {
                sb.AppendLine(lines[i]);
            }

            LogTailText.Text = sb.ToString();
        }

        #endregion

        /// <summary>
        /// Back would tear down the page mid-test. Suppressed for the same
        /// reason the real app suppresses it for the whole of a take.
        /// </summary>
        protected override void OnBackKeyPress(CancelEventArgs e)
        {
            if (_isRecording)
            {
                e.Cancel = true;
                StatusText.Text = "Back is disabled while the test is running. Use Stop and save.";
                return;
            }

            base.OnBackKeyPress(e);
        }
    }
}
