using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using Microsoft.Phone.Controls;
using Microsoft.Phone.Shell;
using Windows.Phone.Devices.Power;
using Windows.Storage;

namespace HAAC_Recorder_SL
{
    /// <summary>
    /// The Silverlight port of the WinRT recorder's main page.
    ///
    /// The app is built around one assumption: a take runs unattended for one
    /// to four hours and must not be lost. Three rules follow, and they
    /// explain most of the code below.
    ///
    ///   1. Nothing may terminate the process while recording. Every
    ///      async void handler is wrapped, and every event that arrives off
    ///      the UI thread is marshalled onto it.
    ///   2. Nothing the app does may be audible. No sounds, and no system
    ///      dialogs - confirmation is a plain in-app overlay.
    ///   3. No single tap may end a recording.
    ///
    /// Four things genuinely differ from the WinRT version, as opposed to
    /// merely being spelled differently:
    ///
    ///   * The lock screen. With ApplicationIdleDetectionMode disabled (see
    ///     App.xaml.cs) a take survives the screen locking, so there is no
    ///     DisplayRequest here and nothing tries to hold the display awake.
    ///     Letting the panel sleep is the largest battery saving available on
    ///     a four-hour take, and it is now free.
    ///   * Deactivation. Silverlight's Deactivated has no deferral and no
    ///     reliable way to await, so the WAV is finalized by a blocking stop
    ///     with a timeout (RecordingEngine.StopBlocking).
    ///   * Obscured/Unobscured replaces the suspend/resume pair for the
    ///     screen-locking case, and is now a UI-power signal rather than an
    ///     end-of-take signal.
    ///   * The back key is suppressed by overriding OnBackKeyPress rather
    ///     than by hooking HardwareButtons.BackPressed.
    /// </summary>
    public partial class MainPage : PhoneApplicationPage
    {
        #region Constants

        // Free space and battery are re-checked on this interval while
        // recording. Both are cheap metadata reads that never touch the
        // recording file itself. This timer also drives the 4 GiB rollover
        // check, so it keeps running while the screen is off.
        private const int HealthCheckSeconds = 60;

        private const ulong LowSpaceWarningSeconds = 600; // 10 minutes
        private const int LowBatteryWarningPercent = 20;

        // How long the destructive button on the stop-confirmation overlay
        // stays disabled after the overlay appears. Long enough that a fast
        // double-tap on Stop cannot possibly reach it.
        private const int StopConfirmArmDelayMs = 1200;

        // The deactivation budget is about ten seconds. Seven leaves room for
        // the rest of the handler and for the OS to do its own work.
        private const int DeactivationStopTimeoutMs = 7000;

        #endregion

        #region Fields

        private readonly RecordingEngine _engine = new RecordingEngine();

        // Every mode this phone accepted, in preference order (best first).
        // Empty if detection couldn't run, in which case Start negotiates
        // live.
        private List<CaptureAttempt> _availableModes = new List<CaptureAttempt>();

        // The user's Settings choice, or null when the top of _availableModes
        // is being used automatically.
        private CaptureAttempt _selectedMode;

        // Guards ModeComboBox_SelectionChanged while the list is being filled
        // in code, so populating it doesn't register as a user pick.
        private bool _suppressModeSelectionChanged;

        private bool _autoLockOnStart;

        private DispatcherTimer _elapsedTimer;
        private DispatcherTimer _healthTimer;
        private DispatcherTimer _splashTimer;
        private DispatcherTimer _stopArmTimer;

        private bool _lowSpaceWarned;
        private bool _lowBatteryWarned;
        private bool _wavLimitWarned;

        private bool _isObscured;
        private bool _detectionRunning;
        private bool _lifecycleHooked;

        #endregion

        public MainPage()
        {
            InitializeComponent();

            _autoLockOnStart = AppSettings.LoadAutoLockOnStart();
            _engine.Failed += Engine_Failed;

            Loaded += MainPage_Loaded;
        }

        #region Startup

        private async void MainPage_Loaded(object sender, RoutedEventArgs e)
        {
            // Every async void handler in this file is wrapped like this. An
            // unhandled exception in one of them terminates the process, and
            // during a take that means losing the recording.
            try
            {
                ShowSplash();
                ReportLockModeState();
                HookLifecycle();

                await InitializeModesAsync();
                await UpdateEstimatedRecordingTimeAsync();

                ReportInterruptedTake();
            }
            catch (Exception ex)
            {
                StatusText.Text = "Startup problem: " + ex.Message;
            }
            finally
            {
                // Whatever happened above, the app must not come up with a
                // dead Start button. Live negotiation at Start time is a
                // perfectly good fallback.
                RestoreIdleButtons();
            }
        }

        /// <summary>
        /// If disabling idle detection was refused, a take will not survive
        /// the screen locking - the whole reason this build exists. That is
        /// worth stating at the top of the screen in red rather than leaving
        /// the user to discover it three hours into a recording.
        /// </summary>
        private void ReportLockModeState()
        {
            if (App.RunningUnderLockScreenEnabled)
            {
                LockModeText.Foreground = new SolidColorBrush(Colors.Green);
                LockModeText.Text = "Recording continues under the lock screen.";
            }
            else if (!AppSettings.LoadRunUnderLockScreen())
            {
                LockModeText.Foreground = new SolidColorBrush(Colors.Orange);
                LockModeText.Text = "Lock-screen recording is switched off in Settings - "
                                    + "locking the phone will end a take.";
            }
            else
            {
                LockModeText.Foreground = new SolidColorBrush(Colors.Red);
                LockModeText.Text = "Couldn't keep the app alive under the lock screen. "
                                    + (App.LockScreenSetupError ?? string.Empty);
            }
        }

        private void HookLifecycle()
        {
            if (_lifecycleHooked)
            {
                return;
            }

            _lifecycleHooked = true;

            // Obscured fires when anything covers the app - the lock screen,
            // but also an incoming call screen or a toast. IsLocked
            // distinguishes them.
            if (App.RootFrame != null)
            {
                App.RootFrame.Obscured += Frame_Obscured;
                App.RootFrame.Unobscured += Frame_Unobscured;
            }

            PhoneApplicationService.Current.Deactivated += Service_Deactivated;
            PhoneApplicationService.Current.Closing += Service_Closing;
        }

        private void ReportInterruptedTake()
        {
            if (!App.TakeEndedByDeactivation)
            {
                return;
            }

            App.TakeEndedByDeactivation = false;

            StatusText.Text = "Previous recording ended - the app was pushed to the background. It was saved.";
            WarningText.Text = "Tip: Airplane Mode stops calls from interrupting a take.";
        }

        #endregion

        #region Modes

        /// <summary>
        /// The mode the next recording will use: the user's Settings choice
        /// if there is one, otherwise the best mode detection found. Null when
        /// detection produced nothing, which means Start negotiates live.
        /// </summary>
        private CaptureAttempt EffectiveMode
        {
            get
            {
                if (_selectedMode != null)
                {
                    return _selectedMode;
                }

                return _availableModes.Count > 0 ? _availableModes[0] : null;
            }
        }

        private int EstimateChannels
        {
            get
            {
                if (_engine.IsRecording)
                {
                    return _engine.Channels;
                }

                var mode = EffectiveMode;
                return mode == null ? 2 : mode.Channels;
            }
        }

        private long EstimateBytesPerSecond
        {
            get
            {
                return RecordingEngine.SampleRateHz * EstimateChannels * RecordingEngine.BytesPerSample;
            }
        }

        /// <summary>
        /// Loads cached detection results and wires them up. On a first launch
        /// there is nothing cached, and rather than seizing the microphone
        /// unannounced - which also tends to produce a bad result, since
        /// verification needs actual sound to tell real channels from
        /// duplicates - the setup screen is shown and the user starts
        /// detection themselves.
        /// </summary>
        private async Task InitializeModesAsync()
        {
            var cached = AppSettings.LoadModeCache();
            if (cached != null)
            {
                _availableModes = cached;
                _selectedMode = null;
                FinishModeSetup(true);
                return;
            }

            ShowSetupOverlay();

            // Nothing to await on this path - the user drives it from here -
            // but the signature stays async so the caller is unchanged.
            await Task.FromResult(0);
        }

        private void FinishModeSetup(bool fromCache)
        {
            ApplyEffectiveMode();

            if (_availableModes.Count > 0)
            {
                StatusText.Text = fromCache
                    ? "Ready. Airplane Mode and a charger are recommended for long takes."
                    : string.Format(
                        "Ready. {0} mode{1} verified and saved for next time.",
                        _availableModes.Count, _availableModes.Count == 1 ? "" : "s");
            }
            else
            {
                StatusText.Text = "Ready. No modes detected - the best available will be picked at Start. "
                                  + "You can run detection from Settings.";
            }

            RestoreIdleButtons();
        }

        /// <summary>
        /// Puts the mode the *next* recording will use into the subtitle.
        /// While recording, the subtitle reports what was actually negotiated
        /// instead, which can be a fallback rather than what was asked for.
        /// </summary>
        private void ApplyEffectiveMode()
        {
            var mode = EffectiveMode;

            if (mode == null)
            {
                SubtitleText.Text = "16bit PCM 48kHz - mode chosen at Start";
                return;
            }

            SubtitleText.Text = string.Format(
                "16bit PCM 48kHz - {0}ch - {1}{2}",
                mode.Channels,
                mode.ShortDeviceName,
                _selectedMode == null ? string.Empty : "  (manual)");
        }

        #endregion

        #region Start

        private async void StartButton_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                await StartRecordingAsync();
            }
            catch (Exception ex)
            {
                StatusText.Text = "Failed to start: " + ex.Message;
                _engine.DisposeCapture();
                RestoreIdleButtons();
            }
        }

        private async Task StartRecordingAsync()
        {
            StartButton.IsEnabled = false;
            SettingsButton.IsEnabled = false;
            InfoButton.IsEnabled = false;
            WarningText.Text = string.Empty;
            FileInfoText.Text = string.Empty;
            StatusText.Text = "Opening the microphone...";

            var order = await RecordingEngine.BuildStartOrderAsync(_availableModes, EffectiveMode);
            var error = await _engine.StartAsync(order, _availableModes);

            if (error != null)
            {
                StatusText.Text = "Failed to start: " + error;
                RestoreIdleButtons();
                return;
            }

            _lowSpaceWarned = false;
            _lowBatteryWarned = false;
            _wavLimitWarned = false;

            StopButton.IsEnabled = true;
            LockButton.IsEnabled = true;

            // Report what was actually negotiated, not what was requested.
            SubtitleText.Text = string.Format(
                "16bit PCM 48kHz - {0}ch - {1}", _engine.Channels, _engine.DeviceNameUsed);

            UpdateRecordingElapsedText();
            StartElapsedTimer();
            StartHealthTimer();

            await RunHealthCheckAsync();

            if (_autoLockOnStart)
            {
                ShowLockOverlay();
            }
        }

        #endregion

        #region Stop

        /// <summary>
        /// Opens the confirmation overlay. Deliberately not a MessageBox: a
        /// second one while a dialog is already showing is trouble on WP8.1,
        /// and an overlay is guaranteed silent on every firmware, so nothing
        /// the app does can end up in the recording.
        /// </summary>
        private void StopButton_Click(object sender, RoutedEventArgs e)
        {
            if (StopConfirmOverlay.Visibility == Visibility.Visible)
            {
                return;
            }

            // The destructive button starts disabled and sits at the opposite
            // end of the screen from Stop, so neither a double-tap nor a
            // mis-aimed tap can reach it.
            ConfirmStopButton.IsEnabled = false;
            StopConfirmOverlay.Visibility = Visibility.Visible;

            StopStopArmTimer();
            _stopArmTimer = new DispatcherTimer
            {
                Interval = TimeSpan.FromMilliseconds(StopConfirmArmDelayMs)
            };
            _stopArmTimer.Tick += StopArmTimer_Tick;
            _stopArmTimer.Start();
        }

        private void StopArmTimer_Tick(object sender, EventArgs e)
        {
            StopStopArmTimer();
            ConfirmStopButton.IsEnabled = true;
        }

        private void StopStopArmTimer()
        {
            if (_stopArmTimer != null)
            {
                _stopArmTimer.Stop();
                _stopArmTimer.Tick -= StopArmTimer_Tick;
                _stopArmTimer = null;
            }
        }

        private void KeepRecordingButton_Click(object sender, RoutedEventArgs e)
        {
            // User backed out - keep recording, don't touch any state.
            StopStopArmTimer();
            StopConfirmOverlay.Visibility = Visibility.Collapsed;
        }

        private async void ConfirmStopButton_Click(object sender, RoutedEventArgs e)
        {
            StopStopArmTimer();
            StopConfirmOverlay.Visibility = Visibility.Collapsed;
            StopButton.IsEnabled = false;

            try
            {
                await StopRecordingAsync();
            }
            catch (Exception ex)
            {
                StatusText.Text = "Failed to stop: " + ex.Message;
                CleanupCaptureUi();
                _engine.DisposeCapture();
                RestoreIdleButtons();
            }
        }

        private async Task StopRecordingAsync()
        {
            var error = await _engine.StopAsync();

            StatusText.Text = error == null ? "Stopped." : "Failed to stop cleanly: " + error;
            await ReportSavedFileAsync(error == null ? "Saved to" : "File may need repair");

            CleanupCaptureUi();
            RestoreIdleButtons();

            // Nothing is being recorded now, so the subtitle goes back to
            // advertising the mode the *next* recording will use.
            ApplyEffectiveMode();

            // Free space just changed, and the estimate now reflects the
            // channel count restored above.
            await UpdateEstimatedRecordingTimeAsync();
        }

        /// <summary>
        /// A capture fault mid-take. Arrives off the UI thread, so everything
        /// that touches a control is marshalled.
        /// </summary>
        private void Engine_Failed(string message)
        {
            Dispatcher.BeginInvoke(() =>
            {
                try
                {
                    StatusText.Text = "Recording stopped - the capture pipeline reported: " + message;
                    WarningText.Text = "Everything recorded up to this point has been saved.";

                    CleanupCaptureUi();
                    _engine.DisposeCapture();
                    RestoreIdleButtons();
                    ApplyEffectiveMode();
                }
                catch
                {
                }
            });
        }

        private async Task ReportSavedFileAsync(string prefix)
        {
            var file = _engine.CurrentFile;
            if (file == null)
            {
                return;
            }

            // A take that rolled over is several files, and reporting only the
            // last one would look like the earlier hours went missing.
            string partNote = _engine.SegmentNumber > 1
                ? string.Format("\nRecorded across {0} files (last shown)", _engine.SegmentNumber)
                : string.Empty;

            try
            {
                var props = await file.GetBasicPropertiesAsync();
                var sizeMb = props.Size / (1024.0 * 1024.0);

                FileInfoText.Text = string.Format(
                    "{0}:  \\Music\\recordings\\\n{1}\nSize: {2:N2} MB{3}",
                    prefix, file.Name, sizeMb, partNote);
            }
            catch
            {
                FileInfoText.Text = string.Format(
                    "{0}:  \\Music\\recordings\\\n{1}{2}", prefix, file.Name, partNote);
            }
        }

        private void CleanupCaptureUi()
        {
            StopElapsedTimer();
            StopHealthTimer();
            HideLockOverlay();
        }

        private void RestoreIdleButtons()
        {
            StartButton.IsEnabled = !_detectionRunning;
            StopButton.IsEnabled = false;
            LockButton.IsEnabled = false;
            SettingsButton.IsEnabled = !_detectionRunning;
            InfoButton.IsEnabled = !_detectionRunning;
        }

        #endregion

        #region Health, timers and estimates

        private void StartElapsedTimer()
        {
            StopElapsedTimer();

            _elapsedTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
            _elapsedTimer.Tick += ElapsedTimer_Tick;
            _elapsedTimer.Start();
        }

        private void StopElapsedTimer()
        {
            if (_elapsedTimer != null)
            {
                _elapsedTimer.Stop();
                _elapsedTimer.Tick -= ElapsedTimer_Tick;
                _elapsedTimer = null;
            }
        }

        // Note the Silverlight signature: DispatcherTimer.Tick is a plain
        // EventHandler here, so handlers take (object, EventArgs) - not the
        // (object, object) the WinRT DispatcherTimer uses. This is one of the
        // small mechanical differences that shows up all over the port.
        private void ElapsedTimer_Tick(object sender, EventArgs e)
        {
            UpdateRecordingElapsedText();
        }

        private void StartHealthTimer()
        {
            StopHealthTimer();

            _healthTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(HealthCheckSeconds) };
            _healthTimer.Tick += HealthTimer_Tick;
            _healthTimer.Start();
        }

        private void StopHealthTimer()
        {
            if (_healthTimer != null)
            {
                _healthTimer.Stop();
                _healthTimer.Tick -= HealthTimer_Tick;
                _healthTimer = null;
            }
        }

        private async void HealthTimer_Tick(object sender, EventArgs e)
        {
            try
            {
                await RunHealthCheckAsync();
            }
            catch
            {
                // A failed status refresh must never disturb the recording.
            }
        }

        /// <summary>
        /// Once a minute during a take: roll the file over if it is nearing
        /// the 4 GiB WAV ceiling, then report how much recording time is still
        /// available and how much battery is left. Storage and battery are the
        /// realistic ways a long unattended take dies now that the file-size
        /// limit is handled automatically, and both are worth warning about
        /// before they happen. Nothing here touches the recording file itself
        /// - it reads folder metadata only.
        ///
        /// This keeps running while the screen is off, unlike the elapsed
        /// timer, because the rollover check depends on it.
        /// </summary>
        private async Task RunHealthCheckAsync()
        {
            if (!_engine.IsRecording)
            {
                return;
            }

            if (_engine.ShouldRollOver())
            {
                var rolloverError = await _engine.TryRotateAsync();

                if (rolloverError != null)
                {
                    StatusText.Text = rolloverError;
                    CleanupCaptureUi();
                    RestoreIdleButtons();
                    ApplyEffectiveMode();
                    await ReportSavedFileAsync("Saved to");
                    return;
                }
            }

            var bytesPerSecond = (ulong)_engine.BytesPerSecond;

            ulong freeSpaceBytes = 0;
            bool haveFreeSpace = false;

            try
            {
                var properties = await KnownFolders.MusicLibrary.Properties.RetrievePropertiesAsync(
                    new[] { "System.FreeSpace" });
                freeSpaceBytes = (ulong)properties["System.FreeSpace"];
                haveFreeSpace = true;
            }
            catch
            {
            }

            int batteryPercent = ReadBatteryPercent();

            // Time left is bounded by storage alone: hitting the WAV ceiling
            // just starts another file rather than ending the take.
            string status;
            if (haveFreeSpace)
            {
                var secondsLeft = freeSpaceBytes / bytesPerSecond;
                status = string.Format("Time left: {0}h {1:00}m", secondsLeft / 3600, (secondsLeft % 3600) / 60);
            }
            else
            {
                status = "Recording";
            }

            if (_engine.SegmentNumber > 1)
            {
                status += string.Format("  -  Part {0}", _engine.SegmentNumber);
            }

            if (batteryPercent >= 0)
            {
                status += string.Format("  -  Battery {0}%", batteryPercent);
            }

            FreeSpaceText.Text = status;

            // Warnings are latched so the text doesn't flicker on and off, and
            // so a warning already on screen isn't overwritten by a less
            // important one.
            if (haveFreeSpace && !_lowSpaceWarned && freeSpaceBytes / bytesPerSecond < LowSpaceWarningSeconds)
            {
                _lowSpaceWarned = true;
                WarningText.Text = "Storage almost full - stop and save soon.";
            }
            else if (!_wavLimitWarned &&
                     _engine.EstimatedSegmentBytes >=
                     RecordingEngine.SegmentRolloverBytes - (LowSpaceWarningSeconds * bytesPerSecond))
            {
                // Informational rather than a call to action: the app handles
                // this itself, but a new file appearing mid-take is worth
                // explaining before it happens.
                _wavLimitWarned = true;
                WarningText.Text = "Approaching the 4 GB per-file limit - recording will continue "
                                   + "automatically in a new file.";
            }
            else if (!_lowBatteryWarned && batteryPercent >= 0 && batteryPercent <= LowBatteryWarningPercent)
            {
                _lowBatteryWarned = true;
                WarningText.Text = string.Format(
                    "Battery at {0}% - connect a charger to protect this take.", batteryPercent);
            }
        }

        private int ReadBatteryPercent()
        {
            try
            {
                var battery = Battery.GetDefault();
                return battery == null ? -1 : battery.RemainingChargePercent;
            }
            catch
            {
                return -1;
            }
        }

        /// <summary>
        /// Idle-state estimate, bounded by free storage alone: a take that
        /// reaches the 4 GiB single-file ceiling rolls over into a new file
        /// rather than ending, so that ceiling no longer caps how long the
        /// phone can record for.
        /// </summary>
        private async Task UpdateEstimatedRecordingTimeAsync()
        {
            try
            {
                // There is no DriveInfo here - querying this extra property on
                // a storage folder is the standard way to find out how much
                // free space is on the drive it lives on.
                var properties = await KnownFolders.MusicLibrary.Properties.RetrievePropertiesAsync(
                    new[] { "System.FreeSpace" });

                var freeSpaceBytes = (ulong)properties["System.FreeSpace"];

                var totalSeconds = freeSpaceBytes / (ulong)EstimateBytesPerSecond;
                var hours = totalSeconds / 3600;
                var minutes = (totalSeconds % 3600) / 60;

                bool willSpanFiles = freeSpaceBytes > RecordingEngine.SegmentRolloverBytes;

                FreeSpaceText.Text = string.Format(
                    "Recording time available: {0}h {1:00}m{2}",
                    hours, minutes, willSpanFiles ? "  (across multiple files)" : string.Empty);
            }
            catch
            {
                // Free-space lookup is a nice-to-have. If it fails, leave the
                // field blank rather than blocking the rest of the UI.
                FreeSpaceText.Text = string.Empty;
            }
        }

        private void UpdateRecordingElapsedText()
        {
            var elapsed = _engine.TakeElapsed;
            var text = string.Format(
                "RECORDING  {0:00}:{1:00}:{2:00}",
                (int)elapsed.TotalHours, elapsed.Minutes, elapsed.Seconds);

            StatusText.Text = text;
            LockElapsedText.Text = text;
        }

        private void ShowSplash()
        {
            SplashOverlay.Visibility = Visibility.Visible;

            // One timer, restarted - rapid activate/deactivate cycles would
            // otherwise leave several overlapping timers running, and a stale
            // one can hide the splash for a newer session early.
            if (_splashTimer == null)
            {
                _splashTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
                _splashTimer.Tick += SplashTimer_Tick;
            }

            _splashTimer.Stop();
            _splashTimer.Start();
        }

        private void SplashTimer_Tick(object sender, EventArgs e)
        {
            _splashTimer.Stop();
            SplashOverlay.Visibility = Visibility.Collapsed;
        }

        #endregion

        #region Lock overlay

        private void LockButton_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                ShowLockOverlay();
            }
            catch (Exception ex)
            {
                WarningText.Text = "Couldn't show the lock guard: " + ex.Message;
            }
        }

        /// <summary>
        /// A tap guard, not a power feature. Nothing here stops the phone from
        /// locking - with idle detection disabled the take continues either
        /// way, and letting the screen sleep is the point.
        /// </summary>
        private void ShowLockOverlay()
        {
            UnlockSlider.Value = 0;
            LockOverlay.Visibility = Visibility.Visible;
            UpdateRecordingElapsedText();

            try
            {
                // Silverlight's equivalent of hiding the WinRT StatusBar.
                // Cosmetic only - never worth failing the lock over.
                SystemTray.SetIsVisible(this, false);
            }
            catch
            {
            }
        }

        private void HideLockOverlay()
        {
            if (LockOverlay.Visibility != Visibility.Visible)
            {
                return;
            }

            LockOverlay.Visibility = Visibility.Collapsed;
            UnlockSlider.Value = 0;

            try
            {
                SystemTray.SetIsVisible(this, true);
            }
            catch
            {
            }
        }

        // Silverlight's Slider raises RoutedPropertyChangedEventArgs<double>
        // rather than WinRT's RangeBaseValueChangedEventArgs.
        private void UnlockSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            try
            {
                if (e.NewValue < 95)
                {
                    return;
                }

                HideLockOverlay();
            }
            catch
            {
            }
        }

        #endregion

        #region Obscured, deactivation and the back key

        /// <summary>
        /// The screen locked (or a call screen or toast covered the app). The
        /// take carries on regardless; all that changes is that the per-second
        /// UI clock is pointless while nothing can see it, and stopping it
        /// avoids waking the CPU 3,600 times an hour on a long take.
        ///
        /// The health timer deliberately keeps running - it drives the 4 GiB
        /// rollover, which must happen whether anyone is watching or not.
        /// </summary>
        private void Frame_Obscured(object sender, ObscuredEventArgs e)
        {
            try
            {
                _isObscured = true;

                if (_engine.IsRecording)
                {
                    StopElapsedTimer();
                }
            }
            catch
            {
            }
        }

        private void Frame_Unobscured(object sender, EventArgs e)
        {
            try
            {
                // Only act on a real transition. Unobscured can arrive without
                // a matching Obscured (a toast dismissing itself, for
                // instance), and restarting the clock on every one of those
                // would leave duplicate timers running through a long take.
                if (!_isObscured)
                {
                    return;
                }

                _isObscured = false;

                if (_engine.IsRecording)
                {
                    UpdateRecordingElapsedText();
                    StartElapsedTimer();
                }
            }
            catch
            {
            }
        }

        /// <summary>
        /// The app is being pushed to the background - an incoming call, the
        /// user launching something else, the Start button. With idle
        /// detection disabled this no longer fires merely because the screen
        /// locked, so by the time it does, the take really is ending.
        ///
        /// There is no deferral here and no reliable way to await, so the
        /// blocking stop is the only way to get the RIFF sizes written before
        /// the process goes away.
        /// </summary>
        private void Service_Deactivated(object sender, DeactivatedEventArgs e)
        {
            try
            {
                if (!_engine.IsRecording)
                {
                    return;
                }

                App.TakeEndedByDeactivation = true;
                _engine.StopBlocking(DeactivationStopTimeoutMs);
            }
            catch
            {
            }
        }

        private void Service_Closing(object sender, ClosingEventArgs e)
        {
            try
            {
                _engine.StopBlocking(DeactivationStopTimeoutMs);
            }
            catch
            {
            }
        }

        /// <summary>
        /// Back closes whatever overlay is open, and is otherwise swallowed
        /// for the whole recording - a stray tap should never be able to exit
        /// or navigate away mid-take. (Start and Search can't be suppressed
        /// this way, which is what the lock guard is for.)
        ///
        /// Replaces the WinRT build's HardwareButtons.BackPressed hook.
        /// </summary>
        protected override void OnBackKeyPress(CancelEventArgs e)
        {
            if (LockOverlay.Visibility == Visibility.Visible)
            {
                // Deliberately not an unlock. The guard exists precisely so a
                // pocketed phone can't end a take, and Back is the easiest
                // accidental press there is.
                e.Cancel = true;
                return;
            }

            if (StopConfirmOverlay.Visibility == Visibility.Visible)
            {
                StopStopArmTimer();
                StopConfirmOverlay.Visibility = Visibility.Collapsed;
                e.Cancel = true;
                return;
            }

            if (SettingsOverlay.Visibility == Visibility.Visible)
            {
                SettingsOverlay.Visibility = Visibility.Collapsed;
                e.Cancel = true;
                return;
            }

            if (InfoOverlay.Visibility == Visibility.Visible)
            {
                InfoOverlay.Visibility = Visibility.Collapsed;
                e.Cancel = true;
                return;
            }

            if (SetupOverlay.Visibility == Visibility.Visible)
            {
                // A detection run in progress owns the microphone; leaving
                // mid-probe is how endpoints get left in a bad state.
                e.Cancel = _detectionRunning;
                return;
            }

            if (_engine.IsRecording)
            {
                e.Cancel = true;
                return;
            }

            base.OnBackKeyPress(e);
        }

        #endregion

        #region Setup and detection

        private void ShowSetupOverlay()
        {
            SetupProgressText.Text = string.Empty;
            SetupDetectButton.IsEnabled = true;
            SetupSkipButton.IsEnabled = true;
            SetupOverlay.Visibility = Visibility.Visible;
        }

        private async void SetupDetectButton_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                await RunDetectionAsync();
            }
            catch (Exception ex)
            {
                SetupStatusText.Text = "Detection problem: " + ex.Message;
                SetupDetectButton.IsEnabled = true;
                SetupSkipButton.IsEnabled = true;
                _detectionRunning = false;
                RestoreIdleButtons();
            }
        }

        private void SetupSkipButton_Click(object sender, RoutedEventArgs e)
        {
            SetupOverlay.Visibility = Visibility.Collapsed;
            FinishModeSetup(false);
        }

        private async Task RunDetectionAsync()
        {
            _detectionRunning = true;
            SetupDetectButton.IsEnabled = false;
            SetupSkipButton.IsEnabled = false;
            SetupOverlay.Visibility = Visibility.Visible;
            SetupProgressText.Text = "Starting...";
            RestoreIdleButtons();

            DetectionResult result = null;

            try
            {
                result = await ModeDetector.DetectAsync(message =>
                {
                    // The callback arrives on whatever thread the awaits
                    // resumed on, so it is marshalled rather than assumed.
                    Dispatcher.BeginInvoke(() => { SetupProgressText.Text = message; });
                });
            }
            catch
            {
                // Mic in use, permission trouble, anything unexpected. An
                // empty list just means Start negotiates live, so there is
                // nothing to report to the user here.
            }

            _detectionRunning = false;

            _availableModes = result == null || result.Modes == null
                ? new List<CaptureAttempt>()
                : result.Modes;

            _selectedMode = null;

            if (_availableModes.Count > 0)
            {
                AppSettings.SaveModeCache(_availableModes);
            }

            SetupProgressText.Text = string.Empty;
            SetupDetectButton.IsEnabled = true;
            SetupSkipButton.IsEnabled = true;
            SetupOverlay.Visibility = Visibility.Collapsed;

            FinishModeSetup(false);

            if (result != null && result.LogLines != null && result.LogLines.Count > 0)
            {
                await ModeDetector.WriteDetectionLogAsync(result.LogLines);
            }

            // The chosen mode's channel count feeds the byte rate, so the
            // "recording time available" figure has to move with it.
            await UpdateEstimatedRecordingTimeAsync();
        }

        #endregion

        #region Settings and About overlays

        private void SettingsButton_Click(object sender, RoutedEventArgs e)
        {
            // Re-sync in case the effective mode changed since the overlay was
            // last open.
            PopulateModeComboBox();
            UpdateModeDeviceText();
            UpdateSettingsHintText();

            AutoLockCheckBox.IsChecked = _autoLockOnStart;
            RunUnderLockCheckBox.IsChecked = AppSettings.LoadRunUnderLockScreen();
            UpdateRunUnderLockHint();

            RedetectButton.IsEnabled = !_engine.IsRecording;
            UseBestButton.IsEnabled = !_engine.IsRecording && _selectedMode != null;
            ModeComboBox.IsEnabled = !_engine.IsRecording && _availableModes.Count > 1;

            SettingsOverlay.Visibility = Visibility.Visible;
        }

        private void SettingsCloseButton_Click(object sender, RoutedEventArgs e)
        {
            SettingsOverlay.Visibility = Visibility.Collapsed;
        }

        private void PopulateModeComboBox()
        {
            _suppressModeSelectionChanged = true;

            try
            {
                ModeComboBox.Items.Clear();

                var labels = ModeRanking.BuildModeLabels(_availableModes);
                foreach (var label in labels)
                {
                    ModeComboBox.Items.Add(label);
                }

                var effective = EffectiveMode;
                if (effective != null)
                {
                    var index = _availableModes.IndexOf(effective);
                    if (index >= 0)
                    {
                        ModeComboBox.SelectedIndex = index;
                    }
                }
            }
            finally
            {
                _suppressModeSelectionChanged = false;
            }
        }

        private async void ModeComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            try
            {
                if (_suppressModeSelectionChanged || _engine.IsRecording)
                {
                    return;
                }

                var index = ModeComboBox.SelectedIndex;
                if (index < 0 || index >= _availableModes.Count)
                {
                    return;
                }

                // Recorded as an explicit override even when the user picks
                // the one that was already top of the list - the display says
                // "manual" either way, which matches what they just did.
                _selectedMode = _availableModes[index];

                ApplyEffectiveMode();
                UpdateModeDeviceText();
                UpdateSettingsHintText();
                UseBestButton.IsEnabled = true;

                // Mono halves the byte rate, so the "recording time available"
                // figure has to move with the selection.
                await UpdateEstimatedRecordingTimeAsync();
            }
            catch
            {
            }
        }

        private async void UseBestButton_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                _selectedMode = null;

                PopulateModeComboBox();
                ApplyEffectiveMode();
                UpdateModeDeviceText();
                UpdateSettingsHintText();
                UseBestButton.IsEnabled = false;

                await UpdateEstimatedRecordingTimeAsync();
            }
            catch
            {
            }
        }

        private async void RedetectButton_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                SettingsOverlay.Visibility = Visibility.Collapsed;
                AppSettings.ClearModeCache();

                SetupStatusText.Text = "Running detection again. Make some noise while it works.";
                await RunDetectionAsync();
            }
            catch (Exception ex)
            {
                StatusText.Text = "Detection problem: " + ex.Message;
                _detectionRunning = false;
                RestoreIdleButtons();
            }
        }

        private void UpdateModeDeviceText()
        {
            var mode = EffectiveMode;

            ModeDeviceText.Text = mode == null
                ? "No verified modes. A mode will be negotiated when you tap Start."
                : "Endpoint: " + mode.DeviceName;
        }

        private void UpdateSettingsHintText()
        {
            var text = _selectedMode == null
                ? "Using the best mode found automatically."
                : "Using a mode you picked. Tap \"Use best available\" to go back to automatic.";

            if (ModeRanking.IsMonoOnly(_availableModes))
            {
                text += " No multi-channel mode could be verified here, so single-channel capture "
                        + "is all that's listed.";
            }

            SettingsHintText.Text = text;
        }

        private void UpdateRunUnderLockHint()
        {
            // The property behind this is one-way within a session: it can be
            // disabled at any point but never re-enabled, so both directions
            // are described as taking effect next launch rather than
            // pretending one of them is live.
            RunUnderLockHintText.Text = App.RunningUnderLockScreenEnabled
                ? "On. Takes effect at launch, so switching this off applies the next time the app starts."
                : "Off for this session. Takes effect the next time the app starts.";
        }

        private void AutoLockCheckBox_Changed(object sender, RoutedEventArgs e)
        {
            try
            {
                _autoLockOnStart = AutoLockCheckBox.IsChecked == true;
                AppSettings.SaveAutoLockOnStart(_autoLockOnStart);
            }
            catch
            {
            }
        }

        private void RunUnderLockCheckBox_Changed(object sender, RoutedEventArgs e)
        {
            try
            {
                AppSettings.SaveRunUnderLockScreen(RunUnderLockCheckBox.IsChecked == true);
                UpdateRunUnderLockHint();
                ReportLockModeState();
            }
            catch
            {
            }
        }

        private void InfoButton_Click(object sender, RoutedEventArgs e)
        {
            InfoDiagnosticsText.Text = string.Format(
                "Lock-screen recording: {0}\nVerified modes: {1}\nDetection logs are written to Music\\recordings.",
                App.RunningUnderLockScreenEnabled ? "active" : "not active",
                _availableModes.Count);

            InfoOverlay.Visibility = Visibility.Visible;
        }

        private void InfoCloseButton_Click(object sender, RoutedEventArgs e)
        {
            InfoOverlay.Visibility = Visibility.Collapsed;
        }

        #endregion
    }
}
