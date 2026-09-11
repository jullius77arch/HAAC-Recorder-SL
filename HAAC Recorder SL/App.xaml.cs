using System;
using System.Diagnostics;
using System.Resources;
using System.Windows;
using System.Windows.Markup;
using System.Windows.Navigation;
using Microsoft.Phone.Controls;
using Microsoft.Phone.Shell;
using HAAC_Recorder_SL.Resources;

namespace HAAC_Recorder_SL
{
    public partial class App : Application
    {
        /// <summary>
        /// Provides easy access to the root frame of the Phone Application.
        /// </summary>
        public static PhoneApplicationFrame RootFrame { get; private set; }

        /// <summary>
        /// True if ApplicationIdleDetectionMode was successfully set to
        /// Disabled for this session. Read-only afterwards, because the
        /// property is strictly one-way: setting it back to Enabled throws
        /// InvalidOperationException. See SetRunUnderLockScreen below.
        /// </summary>
        public static bool RunningUnderLockScreenEnabled { get; private set; }

        /// <summary>
        /// Non-null if disabling idle detection threw. Surfaced in the UI so
        /// a failure here is visible rather than silently turning the test
        /// into a no-op.
        /// </summary>
        public static string LockScreenSetupError { get; private set; }

        public App()
        {
            // Global handler for uncaught exceptions.
            UnhandledException += Application_UnhandledException;

            // Standard XAML initialization
            InitializeComponent();

            // Phone-specific initialization
            InitializePhoneApplication();

            // Language display initialization
            InitializeLanguage();

            // THE FEATURE. This is the whole mechanism behind Audio Recorder
            // Pro's "Enable recording under screen lock" checkbox, and it has
            // no equivalent in a Windows Phone 8.1 Store/WinRT app — which is
            // why the app is being moved to Silverlight in the first place.
            //
            // Set here, in the constructor, so it is in force before MainPage
            // ever loads and well before a recording can be started.
            SetRunUnderLockScreen();

            if (Debugger.IsAttached)
            {
                // Display the current frame rate counters.
                Application.Current.Host.Settings.EnableFrameRateCounter = true;

                // NOTE: the stock VS2013 template sets
                //
                //     PhoneApplicationService.Current.UserIdleDetectionMode
                //         = IdleDetectionMode.Disabled;
                //
                // right here, "to prevent the screen from turning off while
                // under the debugger". That line has been deliberately
                // removed.
                //
                // UserIdleDetectionMode and ApplicationIdleDetectionMode are
                // different things. UserIdleDetectionMode.Disabled stops the
                // phone from locking at all — which would mean the screen
                // never locks, the app is never obscured, and this test would
                // report a glowing success without having exercised the
                // feature even once. The screen must be allowed to lock
                // normally for any of this to mean anything.
            }
        }

        /// <summary>
        /// Requests that the OS not deactivate this app when the phone locks.
        ///
        /// Two things worth knowing about this property:
        ///
        /// 1. It is one-way. Setting it to Disabled is permitted at any point,
        ///    but setting it back to Enabled in the same session throws
        ///    InvalidOperationException. That is why the real app's checkbox
        ///    has to be a persisted preference applied once at launch, with
        ///    "off" taking effect on the next run — not a live toggle.
        ///
        /// 2. It does not make the app immortal. An incoming phone call, a
        ///    depleted battery, or the user launching something else still
        ///    deactivates it. It covers exactly one case: the screen locking.
        /// </summary>
        private static void SetRunUnderLockScreen()
        {
            try
            {
                PhoneApplicationService.Current.ApplicationIdleDetectionMode =
                    IdleDetectionMode.Disabled;

                RunningUnderLockScreenEnabled = true;
            }
            catch (Exception ex)
            {
                RunningUnderLockScreenEnabled = false;
                LockScreenSetupError = ex.Message;
            }
        }

        private void Application_ContractActivated(object sender, Windows.ApplicationModel.Activation.IActivatedEventArgs e)
        {
        }

        // Code to execute when the application is launching (eg, from Start).
        private void Application_Launching(object sender, LaunchingEventArgs e)
        {
            ProbeLog.Append("APP LAUNCHING");
        }

        // Code to execute when the application is activated (brought to foreground).
        private void Application_Activated(object sender, ActivatedEventArgs e)
        {
            ProbeLog.Append("APP ACTIVATED  (IsApplicationInstancePreserved=" +
                            e.IsApplicationInstancePreserved + ")");
        }

        /// <summary>
        /// If this fires while a test recording is running, the test has
        /// FAILED: the app was pushed to the background, which is exactly what
        /// disabling idle detection is supposed to prevent when the cause is
        /// the screen locking.
        ///
        /// Note that Silverlight's Deactivated has no deferral mechanism — the
        /// app gets roughly ten seconds and nothing here can be awaited
        /// reliably. That constraint matters for the real port (finalizing a
        /// WAV on the way out), but for the probe all that's needed is a
        /// marker in the log.
        /// </summary>
        private void Application_Deactivated(object sender, DeactivatedEventArgs e)
        {
            ProbeLog.Append("APP DEACTIVATED  (Reason=" + e.Reason + ")  <<< recording would end here");
        }

        private void Application_Closing(object sender, ClosingEventArgs e)
        {
            ProbeLog.Append("APP CLOSING");
        }

        private void RootFrame_NavigationFailed(object sender, NavigationFailedEventArgs e)
        {
            if (Debugger.IsAttached)
            {
                Debugger.Break();
            }
        }

        private void Application_UnhandledException(object sender, ApplicationUnhandledExceptionEventArgs e)
        {
            // Logged before breaking, so a crash during an unattended run
            // still leaves a trace on disk.
            try
            {
                ProbeLog.Append("UNHANDLED EXCEPTION: " + e.ExceptionObject.Message);
            }
            catch
            {
            }

            if (Debugger.IsAttached)
            {
                Debugger.Break();
            }
        }

        #region Phone application initialization

        private bool phoneApplicationInitialized = false;

        private void InitializePhoneApplication()
        {
            if (phoneApplicationInitialized)
                return;

            RootFrame = new PhoneApplicationFrame();
            RootFrame.Navigated += CompleteInitializePhoneApplication;
            RootFrame.NavigationFailed += RootFrame_NavigationFailed;
            RootFrame.Navigated += CheckForResetNavigation;

            PhoneApplicationService.Current.ContractActivated += Application_ContractActivated;

            phoneApplicationInitialized = true;
        }

        private void CompleteInitializePhoneApplication(object sender, NavigationEventArgs e)
        {
            if (RootVisual != RootFrame)
                RootVisual = RootFrame;

            RootFrame.Navigated -= CompleteInitializePhoneApplication;
        }

        private void CheckForResetNavigation(object sender, NavigationEventArgs e)
        {
            if (e.NavigationMode == NavigationMode.Reset)
                RootFrame.Navigated += ClearBackStackAfterReset;
        }

        private void ClearBackStackAfterReset(object sender, NavigationEventArgs e)
        {
            RootFrame.Navigated -= ClearBackStackAfterReset;

            if (e.NavigationMode != NavigationMode.New && e.NavigationMode != NavigationMode.Refresh)
                return;

            while (RootFrame.RemoveBackEntry() != null)
            {
                ;
            }
        }

        #endregion

        private void InitializeLanguage()
        {
            try
            {
                RootFrame.Language = XmlLanguage.GetLanguage(AppResources.ResourceLanguage);

                FlowDirection flow = (FlowDirection)Enum.Parse(typeof(FlowDirection), AppResources.ResourceFlowDirection);
                RootFrame.FlowDirection = flow;
            }
            catch
            {
                if (Debugger.IsAttached)
                {
                    Debugger.Break();
                }

                throw;
            }
        }
    }
}
