using System;
using System.Collections.Generic;

namespace HAAC_Recorder_SL
{
    /// <summary>
    /// What AmbientProbe concluded about an endpoint's channels, from how
    /// their coherence decays across frequency.
    ///
    /// This is a different question from VerifiedIndependentChannels. That
    /// one is a byte comparison and only proves the channels are not copies.
    /// Four differently weighted mixes of the same two microphones pass it -
    /// which is exactly how a 1520's Surround Microphone, the most processed
    /// endpoint on the phone, came to rank first. This answers whether the
    /// channels come from physically separate elements.
    ///
    /// The numeric values are persisted, so they must not be renumbered.
    /// </summary>
    public enum ChannelIndependence
    {
        /// <summary>Never analysed.</summary>
        Unknown = 0,

        /// <summary>Coherence decays with frequency: separate elements.</summary>
        Separated = 1,

        /// <summary>Coherence holds at every frequency, or the channels are
        /// byte-identical: processed mixes of a shared set.</summary>
        Derived = 2,

        /// <summary>Some decay, but not enough to call either way.</summary>
        Ambiguous = 3,

        /// <summary>The run could not measure anything usable - a silent
        /// room, a field too loud to be diffuse, a capture that would not
        /// start. Says nothing about the hardware.</summary>
        Inconclusive = 4
    }

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

        // Per endpoint, not per channel count: it describes where the
        // channels come from, which is a property of the device. Stored
        // separately from the mode cache (see AppSettings) because it comes
        // from a different, slower test that is run less often.
        public readonly ChannelIndependence Independence;

        public CaptureAttempt(int channels, string deviceId, string deviceName, bool verified)
            : this(channels, deviceId, deviceName, verified, ChannelIndependence.Unknown)
        {
        }

        public CaptureAttempt(
            int channels, string deviceId, string deviceName, bool verified,
            ChannelIndependence independence)
        {
            this.Channels = channels;
            this.DeviceId = deviceId;
            this.DeviceName = string.IsNullOrEmpty(deviceName) ? "Default" : deviceName;
            this.VerifiedIndependentChannels = verified;
            this.Independence = independence;
        }

        /// <summary>
        /// A copy carrying a different analysis verdict. The fields stay
        /// readonly so a mode can't change under a list that was sorted by
        /// it; re-ranking builds new instances instead.
        /// </summary>
        public CaptureAttempt WithIndependence(ChannelIndependence independence)
        {
            return new CaptureAttempt(
                this.Channels, this.DeviceId, this.DeviceName,
                this.VerifiedIndependentChannels, independence);
        }

        /// <summary>
        /// True when the two describe the same capture configuration, however
        /// they were constructed. Used to keep a manual Settings choice
        /// selected across a re-rank, which replaces every instance.
        /// </summary>
        public bool SameConfigurationAs(CaptureAttempt other)
        {
            return other != null
                && this.Channels == other.Channels
                && string.Equals(this.DeviceId, other.DeviceId, StringComparison.Ordinal);
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
        /// How much the analysis verdict counts for, highest best. Separate
        /// microphones beat anything unanalysed or unclear, and those beat a
        /// known processed mix - which stays on the list, because on some
        /// handset it may be the only multichannel endpoint there is.
        ///
        /// Only multichannel modes are tiered. A mono mode has no channel
        /// relationship to judge, and the verdict for its endpoint was
        /// measured at a higher channel count anyway.
        /// </summary>
        public static int IndependenceTier(CaptureAttempt attempt)
        {
            if (EffectiveChannelRank(attempt) < 2)
            {
                return 1;
            }

            switch (attempt.Independence)
            {
                case ChannelIndependence.Separated:
                    return 2;
                case ChannelIndependence.Derived:
                    return 0;
                default:
                    return 1;
            }
        }

        /// <summary>
        /// Best-first: separate microphones win over processed mixes, then
        /// more verified channels wins, then device name, then device Id.
        ///
        /// The verdict comes first on purpose. This app exists to record the
        /// least-processed audio the hardware will give, and on a 1520 the
        /// 4-channel Surround Microphone is four noise-suppressed mixes of
        /// the same elements, running ~18 dB quieter, while the 2-channel
        /// Microphone Array is two real microphones. Channel count alone
        /// picks the wrong one.
        ///
        /// Device name and Id express no preference at all — one
        /// endpoint is not better than another because of its name. They
        /// exist because List.Sort is unstable, and on this hardware the four
        /// microphones are two stereo pairs on opposite faces of the phone
        /// that can verify at the same channel count. Without a tiebreak,
        /// which face the app records from by default would be decided by the
        /// sort's internal partitioning and could move between runs.
        /// </summary>
        public static int CompareModes(CaptureAttempt a, CaptureAttempt b)
        {
            int byIndependence = IndependenceTier(b).CompareTo(IndependenceTier(a));
            if (byIndependence != 0)
            {
                return byIndependence;
            }

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
        /// Attaches each endpoint's stored verdict and re-sorts. Returns new
        /// instances; the input list is left as it was. A null or empty
        /// lookup clears every verdict back to Unknown, which is what a mode
        /// list read from a cache with no analysis behind it should say.
        /// </summary>
        public static List<CaptureAttempt> ApplyIndependence(
            List<CaptureAttempt> modes, IDictionary<string, ChannelIndependence> verdicts)
        {
            var result = new List<CaptureAttempt>();

            foreach (var mode in modes)
            {
                ChannelIndependence verdict;
                if (verdicts == null ||
                    mode.DeviceId == null ||
                    !verdicts.TryGetValue(mode.DeviceId, out verdict))
                {
                    verdict = ChannelIndependence.Unknown;
                }

                result.Add(mode.WithIndependence(verdict));
            }

            result.Sort(CompareModes);
            return result;
        }

        /// <summary>
        /// The endpoints worth running the ambient analysis on: those with at
        /// least one verified multichannel mode, each listed once, in ranking
        /// order. A mono-only endpoint has no channel relationship to measure,
        /// and every capture cycle skipped is 20 seconds saved and one less
        /// chance to leave a Lumia endpoint in a bad state.
        /// </summary>
        public static List<CaptureAttempt> EndpointsToAnalyse(List<CaptureAttempt> modes)
        {
            var result = new List<CaptureAttempt>();

            foreach (var mode in modes)
            {
                if (EffectiveChannelRank(mode) < 2 || string.IsNullOrEmpty(mode.DeviceId))
                {
                    continue;
                }

                bool already = false;
                for (int i = 0; i < result.Count; i++)
                {
                    if (string.Equals(result[i].DeviceId, mode.DeviceId, StringComparison.Ordinal))
                    {
                        // Keep the highest channel count seen for the device,
                        // since that is what gets probed.
                        if (mode.Channels > result[i].Channels)
                        {
                            result[i] = mode;
                        }

                        already = true;
                        break;
                    }
                }

                if (!already)
                {
                    result.Add(mode);
                }
            }

            return result;
        }

        /// <summary>
        /// The few words Settings and the analysis summary use for a verdict.
        /// Plain terms rather than the probe's own vocabulary: the user is
        /// choosing a microphone, not reading a coherence plot.
        /// </summary>
        public static string DescribeIndependence(ChannelIndependence independence)
        {
            switch (independence)
            {
                case ChannelIndependence.Separated:
                    return "separate mics";
                case ChannelIndependence.Derived:
                    return "processed mix";
                case ChannelIndependence.Ambiguous:
                    return "unclear";
                case ChannelIndependence.Inconclusive:
                    return "not measured";
                default:
                    return "not analysed";
            }
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
                else if (mode.Channels >= 2 &&
                         (mode.Independence == ChannelIndependence.Separated ||
                          mode.Independence == ChannelIndependence.Derived))
                {
                    // Only the two conclusive verdicts are worth the width.
                    // "Unclear" on a picker entry reads as a fault in the
                    // mode rather than in the room it was measured in.
                    label += "  (" + DescribeIndependence(mode.Independence) + ")";
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
