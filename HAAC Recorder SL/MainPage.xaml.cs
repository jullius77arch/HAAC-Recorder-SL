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
using Windows.Devices.Enumeration;
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

        // Guards ModeListBox_SelectionChanged while the list is being filled
        // in code, so populating it doesn't register as a user pick.
        private bool _suppressModeSelectionChanged;

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
                ApplyOrientationLayout(Orientation);
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

        #endregion

        #region Orientation

        protected override void OnOrientationChanged(OrientationChangedEventArgs e)
        {
            base.OnOrientationChanged(e);

            try
            {
                ApplyOrientationLayout(e.Orientation);
            }
            catch
            {
                // A layout failure must never be allowed to reach the top of
                // the stack and terminate the process mid-take. A slightly
                // wrong layout is survivable; a lost recording is not.
            }
        }

        /// <summary>
        /// Portrait stacks the three panels in a single column. Landscape
        /// moves the buttons into the right-hand column, because roughly 480
        /// pixels of height will not take two large buttons, a secondary row
        /// and the status text stacked, and having to scroll to reach Stop
        /// mid-take is exactly what this layout exists to prevent.
        ///
        /// This method only ever sets layout properties - Grid.Row,
        /// Grid.Column, Grid.ColumnSpan, Grid.RowSpan, Margin, Height and
        /// FontSize. It never touches the engine, the recording file or any
        /// timer, so rotating the phone during a take changes nothing about
        /// the take itself.
        /// </summary>
        private void ApplyOrientationLayout(PageOrientation orientation)
        {
            var isLandscape =
                (orientation & PageOrientation.Landscape) == PageOrientation.Landscape;

            if (isLandscape)
            {
                // Tighten the header: every pixel it gives up is a pixel the
                // two columns below get to keep.
                TitlePanel.Margin = new Thickness(16, 8, 16, 4);
                SubtitleText.FontSize = 17;
                LockModeText.FontSize = 13;

                // Head and tail keep the left column. The buttons take the
                // right one and span all three rows, so the column's height
                // is theirs regardless of how much text is on the left.
                SetCell(HeadPanel, 0, 0, 1);
                SetCell(TailPanel, 1, 0, 1);
                SetCell(ButtonPanel, 0, 1, 1);
                Grid.SetRowSpan(ButtonPanel, 3);
                ButtonPanel.Margin = new Thickness(12, 0, 0, 0);

                StatusText.FontSize = 22;
                FreeSpaceText.FontSize = 15;
                WarningText.FontSize = 15;
                FileInfoText.FontSize = 15;

                // Tighter caps than portrait: there is less height to give.
                WarningText.MaxHeight = 60;
                FileInfoText.MaxHeight = 76;

                // Stack comes to 346px of the roughly 418 the column has.
                //
                // The heights are not free choices. The phone's Button
                // template puts a 12px touch-target overhang on every side of
                // the content, plus 10,3,10,5 padding and the stroke, so a
                // fixed Height of H leaves roughly H-38 for the text. At 64
                // and font 20 that is 26px against the ~27 the glyphs need,
                // and the descenders on "Start recording" were being clipped
                // flat by the bottom border on both handsets.
                SetButtonMetrics(76, 68, 20, 18);
                StartButton.Margin = new Thickness(0, 0, 0, 18);
                StopButton.Margin = new Thickness(0, 0, 0, 26);

                // Stacked, full width of the column. Side by side they get a
                // quarter of the screen each and the labels squash.
                SetCell(SettingsButton, 2, 0, 2);
                SetCell(InfoButton, 3, 0, 2);
                SettingsButton.Margin = new Thickness(0, 0, 0, 14);
                InfoButton.Margin = new Thickness(0, 0, 0, 0);

                // The tray costs about 32 pixels of width in landscape, which
                // is worth more here than the clock is. Portrait keeps it.
                SetSystemTrayVisible(false);
            }
            else
            {
                TitlePanel.Margin = new Thickness(16, 17, 16, 8);
                SubtitleText.FontSize = 20;
                LockModeText.FontSize = 15;

                SetCell(HeadPanel, 0, 0, 2);
                SetCell(ButtonPanel, 1, 0, 2);
                SetCell(TailPanel, 2, 0, 2);
                Grid.SetRowSpan(ButtonPanel, 1);
                ButtonPanel.Margin = new Thickness(0, 0, 0, 0);

                StatusText.FontSize = 26;
                FreeSpaceText.FontSize = 17;
                WarningText.FontSize = 18;
                FileInfoText.FontSize = 17;

                WarningText.MaxHeight = 88;
                FileInfoText.MaxHeight = 110;

                // 68 rather than 64 on the secondary pair: 64 cleared the
                // descenders by under two pixels, which is not a margin worth
                // relying on across firmware revisions.
                SetButtonMetrics(90, 68, 24, 18);
                StartButton.Margin = new Thickness(0, 0, 0, 12);
                StopButton.Margin = new Thickness(0, 0, 0, 20);

                // Side by side across the full width, which is wide enough
                // here for both labels.
                SetCell(SettingsButton, 2, 0, 1);
                SetCell(InfoButton, 2, 1, 1);
                SettingsButton.Margin = new Thickness(0, 0, 4, 0);
                InfoButton.Margin = new Thickness(4, 0, 0, 0);

                SetSystemTrayVisible(true);
            }
        }

        private static void SetCell(FrameworkElement element, int row, int column, int columnSpan)
        {
            Grid.SetRow(element, row);
            Grid.SetColumn(element, column);
            Grid.SetColumnSpan(element, columnSpan);
        }

        /// <summary>
        /// Start and Stop share one height and font size, Settings and About a
        /// smaller pair - their labels are secondary and should not compete
        /// with the two controls that matter during a take.
        /// </summary>
        private void SetButtonMetrics(
            double primaryHeight, double secondaryHeight,
            double primaryFontSize, double secondaryFontSize)
        {
            StartButton.Height = primaryHeight;
            StopButton.Height = primaryHeight;
            StartButton.FontSize = primaryFontSize;
            StopButton.FontSize = primaryFontSize;

            SettingsButton.Height = secondaryHeight;
            InfoButton.Height = secondaryHeight;
            SettingsButton.FontSize = secondaryFontSize;
            InfoButton.FontSize = secondaryFontSize;
        }

        private void SetSystemTrayVisible(bool visible)
        {
            try
            {
                SystemTray.SetIsVisible(this, visible);
            }
            catch
            {
                // Cosmetic only - never worth failing a layout pass over.
            }
        }

        #endregion

        #region Startup (continued)

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

                // Pending here means the first-run analysis was offered and
                // then cut short - the app was closed or a call came in - so
                // this is still the first run as far as the user is
                // concerned, and the offer is made again.
                if (AppSettings.LoadFirstRunAnalysisState() ==
                    AppSettings.FirstRunAnalysisState.Pending)
                {
                    OfferFirstRunAnalysis();
                }

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

            // Report what was actually negotiated, not what was requested.
            SubtitleText.Text = string.Format(
                "16bit PCM 48kHz - {0}ch - {1}", _engine.Channels, _engine.DeviceNameUsed);

            UpdateRecordingElapsedText();
            StartElapsedTimer();
            StartHealthTimer();

            await RunHealthCheckAsync();
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
        }

        private void RestoreIdleButtons()
        {
            StartButton.IsEnabled = !_detectionRunning;
            StopButton.IsEnabled = false;
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
        }

        /// <summary>
        /// Starts the countdown that hides the splash. It does not show it:
        /// the overlay is Visible in markup so that it is already on screen
        /// for the very first frame, which is what stops the recording screen
        /// flashing up before it. The only exception is a re-entry into this
        /// page with the overlay already hidden, which is why the visibility
        /// is still set here.
        ///
        /// One second rather than two. Now that it is genuinely the first
        /// thing on screen after the OS splash, two reads as a stall.
        /// </summary>
        private void ShowSplash()
        {
            SplashOverlay.Visibility = Visibility.Visible;

            // One timer, restarted - rapid activate/deactivate cycles would
            // otherwise leave several overlapping timers running, and a stale
            // one can hide the splash for a newer session early.
            if (_splashTimer == null)
            {
                _splashTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
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
                // The probe holds its own MediaCapture, separate from the
                // engine's. Releasing it here is what stops a screen lock
                // mid-probe from tombstoning the process with the audio
                // endpoint still held.
                AmbientProbe.AbortQuietly();

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
        /// this way; the phone's own lock screen is what covers that case now.)
        ///
        /// Replaces the WinRT build's HardwareButtons.BackPressed hook.
        /// </summary>
        protected override void OnBackKeyPress(CancelEventArgs e)
        {
            if (StopConfirmOverlay.Visibility == Visibility.Visible)
            {
                StopStopArmTimer();
                StopConfirmOverlay.Visibility = Visibility.Collapsed;
                e.Cancel = true;
                return;
            }

            if (SettingsOverlay.Visibility == Visibility.Visible)
            {
                // Back closes the open dropdown first, then the overlay, so a
                // press never skips a level.
                if (ModePickerList.Visibility == Visibility.Visible)
                {
                    CloseModePicker();
                    e.Cancel = true;
                    return;
                }

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

            if (AnalysisOverlay.Visibility == Visibility.Visible)
            {
                // Mid-run, the probe owns the microphone. Otherwise Back
                // means the same as Skip or Done: the offer is over.
                if (!_detectionRunning)
                {
                    CloseAnalysisOverlay();
                }

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
                await RunDetectionAsync(true);
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

        /// <summary>
        /// <paramref name="fromSetupScreen"/> is true when the user started
        /// this from the setup overlay rather than from Settings. Only that
        /// path can lead to the first-run analysis offer, and then only if it
        /// has never been made on this install.
        /// </summary>
        private async Task RunDetectionAsync(bool fromSetupScreen)
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

            // Verdicts from any earlier analysis still apply: they describe
            // the endpoints, which a re-detection does not change.
            _availableModes = result == null || result.Modes == null
                ? new List<CaptureAttempt>()
                : ModeRanking.ApplyIndependence(result.Modes, AppSettings.LoadIndependence());

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

            if (fromSetupScreen &&
                AppSettings.LoadFirstRunAnalysisState() != AppSettings.FirstRunAnalysisState.Done)
            {
                if (ModeRanking.EndpointsToAnalyse(_availableModes).Count > 0)
                {
                    AppSettings.SaveFirstRunAnalysisState(AppSettings.FirstRunAnalysisState.Pending);
                    OfferFirstRunAnalysis();
                }
                else
                {
                    // Mono only - the emulator, or a phone without an array.
                    // There is no channel relationship to analyse, so the
                    // offer would be a 20-second wait for nothing.
                    AppSettings.SaveFirstRunAnalysisState(AppSettings.FirstRunAnalysisState.Done);
                }
            }
        }

        #endregion

        #region First-run microphone analysis

        /// <summary>
        /// Shows the analysis overlay in its ready-to-start state, describing
        /// exactly what is about to happen to which endpoints. Nothing touches
        /// the microphone until Start is tapped.
        /// </summary>
        private void OfferFirstRunAnalysis()
        {
            if (_detectionRunning)
            {
                return;
            }

            var targets = ModeRanking.EndpointsToAnalyse(_availableModes);
            if (targets.Count == 0)
            {
                AppSettings.SaveFirstRunAnalysisState(AppSettings.FirstRunAnalysisState.Done);
                return;
            }

            int seconds = AmbientProbe.DefaultSeconds;

            AnalysisIntroText.Text = targets.Count == 1
                ? string.Format(
                    "Detection found a microphone that records in more than one channel. "
                    + "Next, the app listens to the room on it for about {0} seconds and works "
                    + "out whether those channels come from separate microphones or are a "
                    + "processed mix of the same ones.",
                    seconds)
                : string.Format(
                    "Detection found {0} microphones that record in more than one channel. "
                    + "Next, the app listens to the room on each one for about {1} seconds - "
                    + "about {2} seconds in all - and works out whether its channels come from "
                    + "separate microphones or are a processed mix of the same ones. The "
                    + "least-processed one becomes your default.",
                    targets.Count, seconds, targets.Count * seconds);

            AnalysisProgressText.Text = string.Empty;
            SetAnalysisOverlayButtons(false);
            AnalysisOverlay.Visibility = Visibility.Visible;
        }

        /// <summary>
        /// Before a run: Start and Skip. After one: Done alone, since the
        /// result is on screen and there is nothing left to decide.
        /// </summary>
        private void SetAnalysisOverlayButtons(bool finished)
        {
            AnalysisStartButton.Visibility = finished ? Visibility.Collapsed : Visibility.Visible;
            AnalysisSkipButton.Visibility = finished ? Visibility.Collapsed : Visibility.Visible;
            AnalysisDoneButton.Visibility = finished ? Visibility.Visible : Visibility.Collapsed;

            AnalysisStartButton.IsEnabled = true;
            AnalysisSkipButton.IsEnabled = true;
        }

        private async void AnalysisStartButton_Click(object sender, RoutedEventArgs e)
        {
            AnalysisStartButton.IsEnabled = false;
            AnalysisSkipButton.IsEnabled = false;

            try
            {
                var outcome = await RunMicrophoneAnalysisAsync(message =>
                {
                    AnalysisProgressText.Text = message;
                });

                AnalysisProgressText.Text = string.Join("\n", outcome.Summary.ToArray());

                if (outcome.Interrupted)
                {
                    // Still Pending, so a relaunch offers it again as well.
                    AnalysisStartButton.Content = "Try again";
                    SetAnalysisOverlayButtons(false);
                    return;
                }

                AppSettings.SaveFirstRunAnalysisState(AppSettings.FirstRunAnalysisState.Done);
                SetAnalysisOverlayButtons(true);
            }
            catch (Exception ex)
            {
                _detectionRunning = false;
                AnalysisProgressText.Text = "Analysis problem: " + ex.Message;
                AnalysisStartButton.Content = "Try again";
                SetAnalysisOverlayButtons(false);
                RestoreIdleButtons();
            }
        }

        private void AnalysisSkipButton_Click(object sender, RoutedEventArgs e)
        {
            CloseAnalysisOverlay();
        }

        private void AnalysisDoneButton_Click(object sender, RoutedEventArgs e)
        {
            CloseAnalysisOverlay();
        }

        /// <summary>
        /// Skipping and finishing both end the offer for good. The user has
        /// seen what the test does and where to find it again, which is all
        /// "only on first run" promises.
        /// </summary>
        private void CloseAnalysisOverlay()
        {
            AppSettings.SaveFirstRunAnalysisState(AppSettings.FirstRunAnalysisState.Done);
            AnalysisOverlay.Visibility = Visibility.Collapsed;
            AnalysisStartButton.Content = "Start";

            ApplyEffectiveMode();
            ReportRecordingMode();
            RestoreIdleButtons();
        }

        /// <summary>
        /// One line on the main screen saying what the next take will use,
        /// once the analysis may have changed it.
        /// </summary>
        private void ReportRecordingMode()
        {
            var mode = EffectiveMode;
            if (mode == null)
            {
                return;
            }

            StatusText.Text = string.Format("Ready. Recording will use {0}.", DescribeMode(mode));
        }

        private static string DescribeMode(CaptureAttempt mode)
        {
            var text = mode.DisplayName;

            if (mode.Channels >= 2 && AppSettings.IsConclusive(mode.Independence))
            {
                text += " (" + ModeRanking.DescribeIndependence(mode.Independence) + ")";
            }

            return text;
        }

        #endregion

        #region Microphone analysis (shared)

        private sealed class AnalysisTarget
        {
            public string DeviceId;
            public string DeviceName;
            public int Channels;
        }

        private sealed class AnalysisOutcome
        {
            public readonly List<string> Summary = new List<string>();
            public bool Interrupted;
        }

        /// <summary>
        /// The endpoints to listen to. Normally those with a verified
        /// multichannel mode, probed at that mode's channel count so the
        /// verdict describes what will actually be recorded.
        ///
        /// With no multichannel mode on the list - detection was skipped, or
        /// found only mono - every endpoint detection would consider is
        /// probed instead, at up to four channels. That keeps the Settings
        /// button useful as a diagnostic on exactly the phones where
        /// detection came up short.
        /// </summary>
        private async Task<List<AnalysisTarget>> BuildAnalysisTargetsAsync()
        {
            var targets = new List<AnalysisTarget>();

            foreach (var mode in ModeRanking.EndpointsToAnalyse(_availableModes))
            {
                targets.Add(new AnalysisTarget
                {
                    DeviceId = mode.DeviceId,
                    DeviceName = mode.DeviceName,
                    Channels = mode.Channels
                });
            }

            if (targets.Count > 0)
            {
                return targets;
            }

            var devices = await DeviceInformation.FindAllAsync(DeviceClass.AudioCapture);
            foreach (var device in devices)
            {
                if (!ModeRanking.IsExcludedFromDetection(device.Name))
                {
                    targets.Add(new AnalysisTarget
                    {
                        DeviceId = device.Id,
                        DeviceName = device.Name,
                        Channels = 4
                    });
                }
            }

            return targets;
        }

        /// <summary>
        /// Listens to the room on each target endpoint in turn, stores the
        /// verdicts, re-ranks the mode list with them and writes the full
        /// report to Music\recordings. Used by both the first-run overlay
        /// and the Settings button, so the two can never reach different
        /// conclusions from the same measurement.
        ///
        /// Replaces the speaker-probe spike. That approach is dead: WP8.1
        /// mutes playback while a capture session is live, in both possible
        /// orderings, so no known source can be got into a recording from
        /// this app. AmbientProbe needs no source at all.
        ///
        /// <paramref name="progress"/> is called on the UI thread: every
        /// await here resumes on the dispatcher, because the run is always
        /// started from a click handler.
        /// </summary>
        private async Task<AnalysisOutcome> RunMicrophoneAnalysisAsync(Action<string> progress)
        {
            var outcome = new AnalysisOutcome();

            // Borrows the detection flag, which already blocks the back key
            // and disables the main buttons. Navigating away mid-probe would
            // strand a MediaCapture exactly as deactivation would.
            _detectionRunning = true;
            RestoreIdleButtons();

            // A pass takes 20 seconds per endpoint, which on two endpoints
            // outlasts the 30 second screen timeout. Holding the display
            // awake means the test can be started and left alone rather than
            // needing lock-screen running turned on first. Restored in the
            // finally below.
            bool screenHeld = App.SuppressScreenTimeout(true);

            string screenNote = !screenHeld && !App.RunningUnderLockScreenEnabled
                ? "\nThe screen may lock during this, which would cut it short. "
                  + "Tap the screen now and then."
                : string.Empty;

            var fresh = new Dictionary<string, ChannelIndependence>(StringComparer.Ordinal);

            var fullReport = new List<string>();
            fullReport.Add("Ambient coherence probe - " + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"));
            fullReport.Add("");

            try
            {
                var targets = await BuildAnalysisTargetsAsync();

                if (targets.Count == 0)
                {
                    outcome.Summary.Add("No microphones worth analysing were found.");
                }

                for (int i = 0; i < targets.Count; i++)
                {
                    var target = targets[i];

                    progress(string.Format(
                        "Listening on {0} ({1} of {2}). About {3} seconds.{4}",
                        ShortName(target.DeviceName), i + 1, targets.Count,
                        AmbientProbe.DefaultSeconds, screenNote));

                    var result = await AmbientProbe.RunBestAsync(
                        target.DeviceId, target.DeviceName,
                        AmbientProbe.DefaultSeconds, target.Channels);

                    fullReport.Add("################ " + target.DeviceName + " ################");
                    fullReport.AddRange(result.Report.Split('\n'));
                    fullReport.Add("");

                    if (result.Interrupted)
                    {
                        outcome.Interrupted = true;
                        outcome.Summary.Add(
                            "Interrupted - the app was sent to the background. Nothing from "
                            + "the interrupted microphone was kept.");
                        break;
                    }

                    fresh[target.DeviceId] = result.Verdict;

                    outcome.Summary.Add(string.Format(
                        "{0}: {1}{2}",
                        ShortName(target.DeviceName),
                        ModeRanking.DescribeIndependence(result.Verdict),
                        string.IsNullOrEmpty(result.Reason) ? string.Empty : " - " + result.Reason));

                    // The detector's pause between probes, for the same
                    // reason: some Lumia drivers don't release the capture
                    // endpoint immediately.
                    await Task.Delay(400);
                }
            }
            catch (Exception ex)
            {
                outcome.Summary.Add("Analysis failed: " + ex.Message);
                fullReport.Add("Analysis failed: " + ex.Message);
            }
            finally
            {
                // Unconditionally, even if the suppression failed to apply:
                // letting the display sleep again is the state this app wants
                // to be in the moment the test is over.
                App.SuppressScreenTimeout(false);
                _detectionRunning = false;
            }

            // Endpoints finished before an interruption are kept - each was
            // a complete measurement in its own right.
            var merged = AppSettings.MergeIndependence(fresh);
            ApplyModeRanking(merged);

            bool anyUnclear = false;
            foreach (var verdict in fresh.Values)
            {
                if (!AppSettings.IsConclusive(verdict))
                {
                    anyUnclear = true;
                }
            }

            var effective = EffectiveMode;
            if (effective != null && fresh.Count > 0)
            {
                outcome.Summary.Add(string.Empty);
                outcome.Summary.Add(_selectedMode == null
                    ? "Recording will use " + DescribeMode(effective) + "."
                    : "You picked " + DescribeMode(effective) + " in Settings, so that is still used.");
            }

            if (anyUnclear)
            {
                outcome.Summary.Add(
                    "An unclear result doesn't replace an earlier clear one. Run it again from "
                    + "Settings somewhere with more background sound.");
            }

            fullReport.Add("Mode ranking after analysis (best first):");
            foreach (var mode in _availableModes)
            {
                fullReport.Add(string.Format(
                    "  {0} - {1}", mode.DisplayName, ModeRanking.DescribeIndependence(mode.Independence)));
            }

            var fileName = await AmbientProbe.WriteReportFileAsync(fullReport);
            outcome.Summary.Add(fileName == null
                ? "Could not write the report file."
                : "Full report: Music\\recordings\\" + fileName);

            RestoreIdleButtons();
            await UpdateEstimatedRecordingTimeAsync();

            return outcome;
        }

        /// <summary>
        /// Re-ranks the mode list with a set of verdicts, keeping a manual
        /// Settings choice selected. Every instance is replaced by the
        /// re-rank, so the choice is found again by configuration rather than
        /// by reference.
        /// </summary>
        private void ApplyModeRanking(IDictionary<string, ChannelIndependence> verdicts)
        {
            var previous = _selectedMode;

            _availableModes = ModeRanking.ApplyIndependence(_availableModes, verdicts);
            _selectedMode = null;

            if (previous != null)
            {
                foreach (var mode in _availableModes)
                {
                    if (mode.SameConfigurationAs(previous))
                    {
                        _selectedMode = mode;
                        break;
                    }
                }
            }

            ApplyEffectiveMode();
        }

        #endregion

        #region Settings and About overlays

        private void SettingsButton_Click(object sender, RoutedEventArgs e)
        {
            // Re-sync in case the effective mode changed since the overlay was
            // last open.
            CloseModePicker();
            PopulateModePicker();
            UpdateModeDeviceText();
            UpdateSettingsHintText();

            RunUnderLockCheckBox.IsChecked = AppSettings.LoadRunUnderLockScreen();
            UpdateRunUnderLockHint();

            RedetectButton.IsEnabled = !_engine.IsRecording;
            UseBestButton.IsEnabled = !_engine.IsRecording && _selectedMode != null;
            ModePickerButton.IsEnabled = !_engine.IsRecording && _availableModes.Count > 1;

            AmbientProbeButton.IsEnabled = !_engine.IsRecording;

            SettingsOverlay.Visibility = Visibility.Visible;
        }

        private void SettingsCloseButton_Click(object sender, RoutedEventArgs e)
        {
            CloseModePicker();
            SettingsOverlay.Visibility = Visibility.Collapsed;
        }

        private void PopulateModePicker()
        {
            _suppressModeSelectionChanged = true;

            try
            {
                ModeListBox.Items.Clear();

                var labels = ModeRanking.BuildModeLabels(_availableModes);
                foreach (var label in labels)
                {
                    ModeListBox.Items.Add(label);
                }

                var effective = EffectiveMode;
                if (effective != null)
                {
                    var index = _availableModes.IndexOf(effective);
                    if (index >= 0)
                    {
                        ModeListBox.SelectedIndex = index;
                    }
                }
            }
            finally
            {
                _suppressModeSelectionChanged = false;
            }

            UpdateModePickerButtonText();
        }

        /// <summary>
        /// The closed dropdown has to say what is currently selected, since
        /// there is no other place on this screen that does.
        /// </summary>
        private void UpdateModePickerButtonText()
        {
            var selected = ModeListBox.SelectedItem as string;

            ModePickerButton.Content = string.IsNullOrEmpty(selected)
                ? "No verified modes"
                : selected + "   \u25BC";
        }

        private void ModePickerButton_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                ModePickerList.Visibility =
                    ModePickerList.Visibility == Visibility.Visible
                        ? Visibility.Collapsed
                        : Visibility.Visible;
            }
            catch
            {
            }
        }

        private void CloseModePicker()
        {
            ModePickerList.Visibility = Visibility.Collapsed;
        }

        private async void ModeListBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            try
            {
                if (_suppressModeSelectionChanged || _engine.IsRecording)
                {
                    return;
                }

                var index = ModeListBox.SelectedIndex;
                if (index < 0 || index >= _availableModes.Count)
                {
                    return;
                }

                // Recorded as an explicit override even when the user picks
                // the one that was already top of the list - the display says
                // "manual" either way, which matches what they just did.
                _selectedMode = _availableModes[index];

                // A pick is the end of the interaction, so the list closes
                // itself and the button takes over showing the choice.
                CloseModePicker();
                UpdateModePickerButtonText();

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

                PopulateModePicker();
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
                await RunDetectionAsync(false);
            }
            catch (Exception ex)
            {
                StatusText.Text = "Detection problem: " + ex.Message;
                _detectionRunning = false;
                RestoreIdleButtons();
            }
        }

        /// <summary>
        /// The Settings entry point to the same analysis the first-run
        /// overlay offers - there so the result can be re-checked, or taken
        /// somewhere with better background sound. The mode picker is
        /// refreshed afterwards, since the verdicts may have re-ordered it.
        /// </summary>
        private async void AmbientProbeButton_Click(object sender, RoutedEventArgs e)
        {
            if (_engine.IsRecording || _detectionRunning)
            {
                return;
            }

            AmbientProbeButton.IsEnabled = false;
            SettingsCloseButton.IsEnabled = false;
            RedetectButton.IsEnabled = false;
            UseBestButton.IsEnabled = false;
            ModePickerButton.IsEnabled = false;
            CloseModePicker();

            try
            {
                var outcome = await RunMicrophoneAnalysisAsync(message =>
                {
                    AmbientProbeText.Text = message;
                });

                AmbientProbeText.Text = string.Join("\n", outcome.Summary.ToArray());

                // A complete manual run covers everything the first-run
                // offer would have done, so it need not be made again.
                if (!outcome.Interrupted)
                {
                    AppSettings.SaveFirstRunAnalysisState(AppSettings.FirstRunAnalysisState.Done);
                }
            }
            catch (Exception ex)
            {
                _detectionRunning = false;
                AmbientProbeText.Text = "Analysis problem: " + ex.Message;
            }
            finally
            {
                PopulateModePicker();
                UpdateModeDeviceText();
                UpdateSettingsHintText();

                AmbientProbeButton.IsEnabled = !_engine.IsRecording;
                SettingsCloseButton.IsEnabled = true;
                RedetectButton.IsEnabled = !_engine.IsRecording;
                UseBestButton.IsEnabled = !_engine.IsRecording && _selectedMode != null;
                ModePickerButton.IsEnabled = !_engine.IsRecording && _availableModes.Count > 1;
                RestoreIdleButtons();
            }
        }

        private static string ShortName(string deviceName)
        {
            var name = deviceName ?? string.Empty;

            int paren = name.IndexOf('(');
            if (paren > 0)
            {
                name = name.Substring(0, paren);
            }

            return name.Trim();
        }

        private void UpdateModeDeviceText()
        {
            var mode = EffectiveMode;

            if (mode == null)
            {
                ModeDeviceText.Text = "No verified modes. A mode will be negotiated when you tap Start.";
                return;
            }

            var text = "Endpoint: " + mode.DeviceName;

            if (mode.Channels >= 2 && mode.Independence != ChannelIndependence.Unknown)
            {
                text += "\nAnalysis: " + ModeRanking.DescribeIndependence(mode.Independence);
            }

            ModeDeviceText.Text = text;
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
            else
            {
                var mode = EffectiveMode;

                if (mode != null && mode.Independence == ChannelIndependence.Derived)
                {
                    text += _selectedMode == null
                        ? " This mode's channels are a processed mix of shared microphones - "
                          + "nothing less processed was found on this phone."
                        : " This mode's channels are a processed mix of shared microphones.";
                }
                else if (AnyMultichannelUnanalysed())
                {
                    text += " Tap Analyse microphones to find out which endpoint has separate "
                            + "microphones and rank it first.";
                }
            }

            SettingsHintText.Text = text;
        }

        private bool AnyMultichannelUnanalysed()
        {
            foreach (var mode in ModeRanking.EndpointsToAnalyse(_availableModes))
            {
                if (mode.Independence == ChannelIndependence.Unknown)
                {
                    return true;
                }
            }

            return false;
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
