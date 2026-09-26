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
        private const string RunUnderLockKey = "HaacRecorder.RunUnderLockScreen";

        // Ambient-analysis verdicts, per endpoint. Kept apart from the mode
        // cache on purpose: "Run detection again" clears that cache, and
        // throwing away a minute-long measurement of the hardware because
        // the half-second one was repeated would be a poor trade.
        private const string IndependenceKey = "HaacRecorder.EndpointIndependence";

        // Absent on a fresh install. Set to Pending the moment first-run
        // detection finishes, and to Done once the analysis offer has been
        // completed or declined. Pending surviving to a later launch means
        // the offer was interrupted - the app was closed, or a call came in -
        // and it is made again. An install that already had a mode cache
        // before this setting existed never gets it, which is what "only on
        // first run" asks for; those users have Settings.
        private const string FirstRunAnalysisKey = "HaacRecorder.FirstRunAnalysis";

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
                // same filter the detector uses runs here too - and the
                // analysis verdicts are attached before sorting, since they
                // decide the order as much as the channel count does.
                list = ModeRanking.FilterRedundantModes(list, null);
                list = ModeRanking.ApplyIndependence(list, LoadIndependence());

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

        #region Analysis verdicts

        /// <summary>
        /// Every stored verdict, keyed by device Id. Never null; an
        /// unreadable store is treated as empty, which ranks exactly as the
        /// app did before the analysis existed.
        /// </summary>
        public static Dictionary<string, ChannelIndependence> LoadIndependence()
        {
            var result = new Dictionary<string, ChannelIndependence>(StringComparer.Ordinal);

            try
            {
                object stored;
                if (!ApplicationData.Current.LocalSettings.Values.TryGetValue(IndependenceKey, out stored))
                {
                    return result;
                }

                var text = stored as string;
                if (string.IsNullOrEmpty(text))
                {
                    return result;
                }

                foreach (var part in text.Split(';'))
                {
                    // base64(deviceId):verdict
                    var bits = part.Split(':');
                    if (bits.Length != 2)
                    {
                        continue;
                    }

                    string deviceId;
                    int value;
                    if (!TryDecodeBase64(bits[0], out deviceId) ||
                        string.IsNullOrEmpty(deviceId) ||
                        !int.TryParse(bits[1], out value) ||
                        !Enum.IsDefined(typeof(ChannelIndependence), value))
                    {
                        continue;
                    }

                    result[deviceId] = (ChannelIndependence)value;
                }
            }
            catch
            {
            }

            return result;
        }

        /// <summary>
        /// Merges one run's verdicts into the store and returns the merged
        /// set.
        ///
        /// A conclusive verdict (separate or processed) always replaces
        /// what was there, so re-running is a real re-check. An unclear or
        /// failed one only fills a gap: a later run in a silent room must not
        /// erase a good measurement taken somewhere better, because the
        /// failure is a fact about the room and not about the microphones.
        /// </summary>
        public static Dictionary<string, ChannelIndependence> MergeIndependence(
            IDictionary<string, ChannelIndependence> fresh)
        {
            var merged = LoadIndependence();

            if (fresh != null)
            {
                foreach (var pair in fresh)
                {
                    if (string.IsNullOrEmpty(pair.Key) || pair.Value == ChannelIndependence.Unknown)
                    {
                        continue;
                    }

                    ChannelIndependence existing;
                    bool hasExisting = merged.TryGetValue(pair.Key, out existing);

                    if (IsConclusive(pair.Value) || !hasExisting || !IsConclusive(existing))
                    {
                        merged[pair.Key] = pair.Value;
                    }
                }
            }

            try
            {
                var parts = new List<string>();
                foreach (var pair in merged)
                {
                    parts.Add(EncodeBase64(pair.Key) + ":" + ((int)pair.Value).ToString());
                }

                ApplicationData.Current.LocalSettings.Values[IndependenceKey] = string.Join(";", parts);
            }
            catch
            {
                // Losing this costs a re-run from Settings, not a recording.
            }

            return merged;
        }

        public static bool IsConclusive(ChannelIndependence independence)
        {
            return independence == ChannelIndependence.Separated
                || independence == ChannelIndependence.Derived;
        }

        #endregion

        #region First-run analysis offer

        public enum FirstRunAnalysisState
        {
            NotStarted = 0,
            Pending = 1,
            Done = 2
        }

        public static FirstRunAnalysisState LoadFirstRunAnalysisState()
        {
            try
            {
                object stored;
                if (ApplicationData.Current.LocalSettings.Values.TryGetValue(FirstRunAnalysisKey, out stored) &&
                    stored is int)
                {
                    var value = (int)stored;
                    if (Enum.IsDefined(typeof(FirstRunAnalysisState), value))
                    {
                        return (FirstRunAnalysisState)value;
                    }
                }
            }
            catch
            {
            }

            return FirstRunAnalysisState.NotStarted;
        }

        public static void SaveFirstRunAnalysisState(FirstRunAnalysisState state)
        {
            try
            {
                ApplicationData.Current.LocalSettings.Values[FirstRunAnalysisKey] = (int)state;
            }
            catch
            {
            }
        }

        #endregion

        #region Simple preferences

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
