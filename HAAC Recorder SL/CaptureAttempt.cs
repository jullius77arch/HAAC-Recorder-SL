using System;
using System.Collections.Generic;

namespace HAAC_Recorder_SL
{
    /// <summary>
    /// One capture configuration the app can attempt: a channel count on a
    /// specific audio endpoint, plus whether that combination was actually
    /// proven to deliver independent, non-silent channels.
    ///
    /// Ported unchanged in meaning from the WinRT build. Nothing in this file
    /// touches a Silverlight or WinRT API, which is why it came across
    /// verbatim — it is the part of the app that is genuinely portable.
    ///
    /// VerifiedIndependentChannels is the important field. A device accepting
    /// a 4-channel request means nothing on this hardware; several Lumia
    /// endpoints accept the request and then write one mono signal into every
    /// channel. Only a mode that survived ChannelAnalysis gets credit here.
    /// </summary>
    public sealed class CaptureAttempt
    {
        public readonly int Channels;

        // Null only for the last-resort attempts built when device
        // enumeration itself failed, where AudioDeviceRole.Default is
        // resolved internally instead.
        public readonly string DeviceId;

        public readonly string DeviceName;
        public readonly bool VerifiedIndependentChannels;

        public CaptureAttempt(int channels, string deviceId, string deviceName, bool verified)
        {
            this.Channels = channels;
            this.DeviceId = deviceId;
            this.DeviceName = string.IsNullOrEmpty(deviceName) ? "Default" : deviceName;
            this.VerifiedIndependentChannels = verified;
        }

        /// <summary>
        /// The device name trimmed down to something that fits on a phone
        /// screen and in a filename. A regular get-only property rather than
        /// an expression-bodied member: this project builds with VS2013's
        /// default C# 5 compiler, which has no such syntax.
        /// </summary>
        public string ShortDeviceName
        {
            get
            {
                var name = this.DeviceName ?? string.Empty;

                int paren = name.IndexOf('(');
                if (paren > 0)
                {
                    name = name.Substring(0, paren);
                }

                name = name.Trim();
                name = name.Replace("Microphone", "Mic");

                return name.Length == 0 ? "Default" : name;
            }
        }

        public string DisplayName
        {
            get { return string.Format("{0}ch - {1}", this.Channels, this.ShortDeviceName); }
        }
    }

    /// <summary>
    /// The ordering, filtering and labelling rules applied to a set of
    /// CaptureAttempts. Static and UI-free so the detector, the settings
    /// screen and the cache loader all reach exactly the same conclusions
    /// about the same list — the numbering shown in Settings and the "D1"/
    /// "D2" suffix written into filenames both come from
    /// ComputeDeviceOccurrenceNumbers for that reason, and so cannot
    /// disagree.
    /// </summary>
    public static class ModeRanking
    {
        /// <summary>
        /// How many channels an attempt can be trusted to deliver: its
        /// channel count if that was verified against real recorded content,
        /// otherwise 1. A multi-channel request that was merely accepted
        /// earns no credit — that is the exact bug this whole ranking exists
        /// to prevent.
        /// </summary>
        public static int EffectiveChannelRank(CaptureAttempt attempt)
        {
            return attempt.VerifiedIndependentChannels ? attempt.Channels : 1;
        }

        /// <summary>
        /// Best-first: more verified channels wins, then device name, then
        /// device Id. The last two express no preference at all — one
        /// endpoint is not better than another because of its name. They
        /// exist because List.Sort is unstable, and on this hardware the four
        /// microphones are two stereo pairs on opposite faces of the phone
        /// that can verify at the same channel count. Without a tiebreak,
        /// which face the app records from by default would be decided by the
        /// sort's internal partitioning and could move between runs.
        /// </summary>
        public static int CompareModes(CaptureAttempt a, CaptureAttempt b)
        {
            int byChannels = EffectiveChannelRank(b).CompareTo(EffectiveChannelRank(a));
            if (byChannels != 0)
            {
                return byChannels;
            }

            int byName = string.Compare(a.DeviceName, b.DeviceName, StringComparison.OrdinalIgnoreCase);
            if (byName != 0)
            {
                return byName;
            }

            return string.Compare(a.DeviceId ?? string.Empty, b.DeviceId ?? string.Empty, StringComparison.Ordinal);
        }

        /// <summary>
        /// Removes the options that can only ever be worse than something
        /// else already on the list.
        ///
        /// <paramref name="removalLog"/> is optional. When supplied, a
        /// human-readable line is appended for everything dropped, so the
        /// detection log explains an absence rather than just having one.
        /// </summary>
        public static List<CaptureAttempt> FilterRedundantModes(
            List<CaptureAttempt> modes, List<string> removalLog)
        {
            var kept = new List<CaptureAttempt>();

            bool anyMultiChannel = false;
            foreach (var mode in modes)
            {
                if (EffectiveChannelRank(mode) >= 2)
                {
                    anyMultiChannel = true;
                    break;
                }
            }

            foreach (var mode in modes)
            {
                if (EffectiveChannelRank(mode) < 2)
                {
                    if (!anyMultiChannel)
                    {
                        kept.Add(mode);
                    }
                    else if (removalLog != null)
                    {
                        removalLog.Add(string.Format(
                            "  Dropped {0}: multi-channel capture verified elsewhere on this phone, so this mono fallback is redundant.",
                            mode.DisplayName));
                    }

                    continue;
                }

                if (mode.Channels == 3 && HasHigherChannelCountOnSameDevice(modes, mode))
                {
                    if (removalLog != null)
                    {
                        removalLog.Add(string.Format(
                            "  Dropped {0}: a higher channel count verified on the same device. A 3ch result can't be identified as a specific subset of a 4-mic array, and risks quietly splitting a stereo pair.",
                            mode.DisplayName));
                    }

                    continue;
                }

                kept.Add(mode);
            }

            return kept;
        }

        /// <summary>
        /// Matched on device Id rather than name, since two endpoints on this
        /// phone can share a name. The static string.Equals overload is used
        /// deliberately so two null Ids compare equal instead of throwing.
        /// </summary>
        public static bool HasHigherChannelCountOnSameDevice(
            List<CaptureAttempt> modes, CaptureAttempt mode)
        {
            foreach (var other in modes)
            {
                if (!string.Equals(other.DeviceId, mode.DeviceId, StringComparison.Ordinal))
                {
                    continue;
                }

                if (EffectiveChannelRank(other) > EffectiveChannelRank(mode))
                {
                    return true;
                }
            }

            return false;
        }

        /// <summary>
        /// True when the list is non-empty and holds nothing but
        /// single-channel modes. Worth saying out loud in Settings, since an
        /// all-mono list looks like a bug in an app whose whole point is
        /// multi-channel capture.
        /// </summary>
        public static bool IsMonoOnly(List<CaptureAttempt> modes)
        {
            foreach (var mode in modes)
            {
                if (EffectiveChannelRank(mode) >= 2)
                {
                    return false;
                }
            }

            return modes.Count > 0;
        }

        /// <summary>
        /// For each entry, its 1-based occurrence number among entries
        /// sharing the same shortened device name — or 0 when that name is
        /// unique in the list and needs no disambiguation.
        ///
        /// Both the Settings labels ("#1"/"#2") and the recording filenames
        /// ("D1"/"D2") are built from this, so the numbering can never
        /// disagree between the place the user chooses a mode and the file
        /// that mode produces.
        /// </summary>
        public static int[] ComputeDeviceOccurrenceNumbers(List<CaptureAttempt> modes)
        {
            var result = new int[modes.Count];

            var totals = new Dictionary<string, int>();
            foreach (var mode in modes)
            {
                var key = mode.ShortDeviceName;
                totals[key] = totals.ContainsKey(key) ? totals[key] + 1 : 1;
            }

            var seen = new Dictionary<string, int>();
            for (int i = 0; i < modes.Count; i++)
            {
                var key = modes[i].ShortDeviceName;

                if (totals[key] <= 1)
                {
                    result[i] = 0;
                    continue;
                }

                seen[key] = seen.ContainsKey(key) ? seen[key] + 1 : 1;
                result[i] = seen[key];
            }

            return result;
        }

        /// <summary>
        /// The strings the Settings mode picker shows, one per entry, in list
        /// order.
        /// </summary>
        public static List<string> BuildModeLabels(List<CaptureAttempt> modes)
        {
            var occurrences = ComputeDeviceOccurrenceNumbers(modes);
            var labels = new List<string>();

            for (int i = 0; i < modes.Count; i++)
            {
                var mode = modes[i];

                var label = string.Format("{0}ch - {1}", mode.Channels, mode.ShortDeviceName);

                if (occurrences[i] > 0)
                {
                    label += " #" + occurrences[i];
                }

                if (!mode.VerifiedIndependentChannels)
                {
                    label += "  (unverified)";
                }

                labels.Add(label);
            }

            return labels;
        }

        /// <summary>
        /// Endpoints not worth spending probe cycles on. "Handset" is the
        /// earpiece-side voice microphone and the bare generic default has
        /// never delivered anything better than mono on this hardware, so
        /// probing either one costs four init/start/stop/dispose cycles to
        /// learn something already known — and every extra cycle is another
        /// chance to leave a Lumia audio endpoint in a bad state.
        /// </summary>
        public static bool IsExcludedFromDetection(string deviceName)
        {
            if (string.IsNullOrEmpty(deviceName))
            {
                return true;
            }

            var name = deviceName.ToLowerInvariant();

            return name.Contains("handset")
                || name == "default"
                || name.Contains("communications");
        }
    }
}
