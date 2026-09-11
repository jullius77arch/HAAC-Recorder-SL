using System;
using System.Diagnostics;
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
        /// MainPage hooks its Obscured/Unobscured events to tell when the
        /// screen has locked.
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
        /// Non-null if disabling idle detection threw. Surfaced in the UI so a
        /// failure here is visible rather than silently turning lock-screen
        /// recording into a no-op three hours into a take.
        /// </summary>
        public static string LockScreenSetupError { get; private set; }

        /// <summary>
        /// Set by MainPage when a take was cut short by deactivation, so the
        /// next activation can say what happened. A plain static rather than
        /// PhoneApplicationService.State: it only has to survive a fast
        /// app-switch back, and after a real tombstone the recording is over
        /// and already reported on disk anyway.
        /// </summary>
        public static bool TakeEndedByDeactivation { get; set; }

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
            // no equivalent in a Windows Phone 8.1 Store/WinRT app - which is
            // why the app moved to Silverlight in the first place.
            //
            // Set here, in the constructor, so it is in force before MainPage
            // loads and well before a recording can start.
            if (AppSettings.LoadRunUnderLockScreen())
            {
                SetRunUnderLockScreen();
            }

            if (Debugger.IsAttached)
            {
                Application.Current.Host.Settings.EnableFrameRateCounter = true;

                // NOTE: the stock VS2013 template sets
                //
                //     PhoneApplicationService.Current.UserIdleDetectionMode
                //         = IdleDetectionMode.Disabled;
                //
                // right here, "to prevent the screen from turning off while
                // under the debugger". That line is deliberately absent.
                //
                // UserIdleDetectionMode and ApplicationIdleDetectionMode are
                // different things. UserIdleDetectionMode.Disabled stops the
                // phone locking at all, which would mask exactly the behaviour
                // this app depends on getting right - and on a four-hour take
                // a display that never sleeps is also the largest avoidable
                // battery drain there is.
            }
        }

        /// <summary>
        /// Requests that the OS not deactivate this app when the phone locks.
        ///
        /// Two things worth knowing about this property:
        ///
        /// 1. It is one-way. Setting it to Disabled is permitted at any point,
        ///    but setting it back to Enabled in the same session throws
        ///    InvalidOperationException. That is why the Settings checkbox is
        ///    a persisted preference applied once at launch, with "off" taking
        ///    effect on the next run - not a live toggle.
        ///
        /// 2. It does not make the app immortal. An incoming phone call, a
        ///    depleted battery, or the user launching something else still
        ///    deactivates it. It covers exactly one case: the screen locking.
        ///    That case is the common one for a recorder, which is why it is
        ///    worth the whole port.
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

        private void Application_Launching(object sender, LaunchingEventArgs e)
        {
        }

        private void Application_Activated(object sender, ActivatedEventArgs e)
        {
        }

        /// <summary>
        /// MainPage subscribes to PhoneApplicationService.Current.Deactivated
        /// separately and does the real work there - finalizing the WAV with a
        /// blocking stop, since Silverlight gives no deferral and roughly ten
        /// seconds of wall clock before the process may be gone.
        /// </summary>
        private void Application_Deactivated(object sender, DeactivatedEventArgs e)
        {
        }

        private void Application_Closing(object sender, ClosingEventArgs e)
        {
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
