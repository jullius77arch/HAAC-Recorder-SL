using System;
using System.Collections.Generic;
using System.Text;
using System.Threading.Tasks;
using Windows.Storage;

namespace HAAC_Recorder_SL
{
    /// <summary>
    /// Everything the app remembers between launches, plus the one shared
    /// folder helper.
    ///
    /// Uses ApplicationData.Current.LocalSettings rather than Silverlight's
    /// own IsolatedStorageSettings. Both work in a Windows Phone 8.1
    /// Silverlight app; LocalSettings is used here so the storage format
    /// stays byte-identical to the WinRT build's, which means the mode cache
    /// written by one is readable by the other during the changeover.
    /// </summary>
    public static class AppSettings
    {
        // Detection results are cached so a normal launch does not touch the
        // microphone at all. Four probe cycles per cold start is four
        // chances to leave the audio endpoint in a bad state, and the answer
        // never changes for a given handset.
        private const string ModeCacheKey = "HaacRecorder.ModeCache";
        private const string AutoLockKey = "HaacRecorder.AutoLockOnStart";
        private const string RunUnderLockKey = "HaacRecorder.RunUnderLockScreen";

        /// <summary>
        /// Opens (creating if necessary) the shared "Music\recordings"
        /// folder. Used for finished takes and for the mode-detection
        /// troubleshooting log alike, so the two can't drift to different
        /// locations.
        /// </summary>
        public static async Task<StorageFolder> GetRecordingsFolderAsync()
        {
            return await KnownFolders.MusicLibrary.CreateFolderAsync(
                "recordings", CreationCollisionOption.OpenIfExists);
        }

        #region Mode cache

        public static List<CaptureAttempt> LoadModeCache()
        {
            try
            {
                object stored;
                if (!ApplicationData.Current.LocalSettings.Values.TryGetValue(ModeCacheKey, out stored))
                {
                    return null;
                }

                var text = stored as string;
                if (string.IsNullOrEmpty(text))
                {
                    return null;
                }

                var list = new List<CaptureAttempt>();
                foreach (var part in text.Split(';'))
                {
                    // channels:verifiedFlag:base64(deviceId):base64(deviceName)
                    var bits = part.Split(':');
                    if (bits.Length != 4)
                    {
                        continue;
                    }

                    int channels;
                    int verifiedFlag;
                    if (!int.TryParse(bits[0], out channels) ||
                        !int.TryParse(bits[1], out verifiedFlag))
                    {
                        continue;
                    }

                    if (channels < 1 || channels > 8)
                    {
                        continue;
                    }

                    string deviceId;
                    string deviceName;
                    if (!TryDecodeBase64(bits[2], out deviceId) || !TryDecodeBase64(bits[3], out deviceName))
                    {
                        continue;
                    }

                    list.Add(new CaptureAttempt(channels, deviceId, deviceName, verifiedFlag == 1));
                }

                // A cache written by an older build can hold modes this one
                // wouldn't offer, in an order it wouldn't produce, so the
                // same filter and sort the detector uses run here too.
                list = ModeRanking.FilterRedundantModes(list, null);
                list.Sort(ModeRanking.CompareModes);

                // An empty or unreadable cache means "we don't know", not
                // "nothing works" - fall through to a real probe.
                return list.Count > 0 ? list : null;
            }
            catch
            {
                return null;
            }
        }

        public static void SaveModeCache(List<CaptureAttempt> modes)
        {
            try
            {
                var parts = new List<string>();
                foreach (var mode in modes)
                {
                    parts.Add(string.Format(
                        "{0}:{1}:{2}:{3}",
                        mode.Channels,
                        mode.VerifiedIndependentChannels ? 1 : 0,
                        EncodeBase64(mode.DeviceId),
                        EncodeBase64(mode.DeviceName)));
                }

                ApplicationData.Current.LocalSettings.Values[ModeCacheKey] = string.Join(";", parts);
            }
            catch
            {
                // A cache miss next launch is a two-second delay, not a bug.
            }
        }

        public static void ClearModeCache()
        {
            try
            {
                ApplicationData.Current.LocalSettings.Values.Remove(ModeCacheKey);
            }
            catch
            {
            }
        }

        /// <summary>
        /// Device Ids and names can contain characters (backslashes, braces,
        /// colons in principle) that would collide with the plain ":"/";"
        /// cache format above, so they're base64-encoded rather than written
        /// raw.
        /// </summary>
        private static string EncodeBase64(string value)
        {
            return Convert.ToBase64String(Encoding.UTF8.GetBytes(value ?? string.Empty));
        }

        private static bool TryDecodeBase64(string value, out string result)
        {
            try
            {
                var bytes = Convert.FromBase64String(value);

                // The 3-argument overload: the profile this project targets
                // doesn't expose the 1-argument GetString(byte[]) convenience
                // overload desktop .NET has.
                result = Encoding.UTF8.GetString(bytes, 0, bytes.Length);
                return true;
            }
            catch
            {
                result = null;
                return false;
            }
        }

        #endregion

        #region Simple preferences

        /// <summary>
        /// Show the black tap-guard overlay as soon as a take starts, so the
        /// phone can go straight into a pocket with no live buttons on
        /// screen.
        /// </summary>
        public static bool LoadAutoLockOnStart()
        {
            return LoadBool(AutoLockKey, false);
        }

        public static void SaveAutoLockOnStart(bool value)
        {
            SaveBool(AutoLockKey, value);
        }

        /// <summary>
        /// Whether to disable ApplicationIdleDetectionMode at launch, which
        /// is what keeps the app alive under the lock screen.
        ///
        /// This has to be a persisted preference read once at startup rather
        /// than a live toggle, because the property is strictly one-way:
        /// setting it to Disabled is allowed at any point, but setting it
        /// back to Enabled in the same session throws
        /// InvalidOperationException. Turning it off therefore takes effect
        /// on the next launch, and the Settings screen says so.
        ///
        /// Defaults to true. It is the entire reason this app exists as a
        /// Silverlight project.
        /// </summary>
        public static bool LoadRunUnderLockScreen()
        {
            return LoadBool(RunUnderLockKey, true);
        }

        public static void SaveRunUnderLockScreen(bool value)
        {
            SaveBool(RunUnderLockKey, value);
        }

        private static bool LoadBool(string key, bool fallback)
        {
            try
            {
                object stored;
                if (ApplicationData.Current.LocalSettings.Values.TryGetValue(key, out stored) &&
                    stored is bool)
                {
                    return (bool)stored;
                }
            }
            catch
            {
            }

            return fallback;
        }

        private static void SaveBool(string key, bool value)
        {
            try
            {
                ApplicationData.Current.LocalSettings.Values[key] = value;
            }
            catch
            {
            }
        }

        #endregion
    }
}
