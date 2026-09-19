using System;
using System.Collections.Generic;
using System.Runtime.InteropServices.WindowsRuntime;
using System.Text;
using System.Threading.Tasks;
using Windows.Media.Capture;
using Windows.Storage;

namespace HAAC_Recorder_SL
{
    /// <summary>
    /// Works out what an audio endpoint is actually doing with the phone's
    /// microphones, using nothing but the sound already in the room.
    ///
    /// This replaces the speaker-probe spike, which is dead: WP8.1 mutes
    /// playback while a capture session is live, in both possible orderings,
    /// while still reporting the sound as Playing. No tone can be got into a
    /// recording from this app, so any measurement needing a known source is
    /// unavailable.
    ///
    /// WHAT THIS MEASURES
    ///
    /// Magnitude-squared coherence between channel pairs, per frequency band,
    /// averaged over hundreds of short blocks. Coherence asks how much of one
    /// channel is linearly predictable from another at a given frequency, on a
    /// scale of 0 to 1.
    ///
    /// The discriminator is what happens at HIGH frequency:
    ///
    ///   Two genuinely separated microphones in a reverberant room see a
    ///   roughly diffuse sound field. Diffuse-field coherence follows a sinc
    ///   law - near 1 at low frequency, decaying to approximately zero once
    ///   the wavelength is short compared with the spacing. Simulated at 100mm
    ///   spacing this gives about 0.93 at 250 Hz and 0.00 to 0.03 at 5 kHz and
    ///   above.
    ///
    ///   Two beamformer outputs derived from the same microphones are both
    ///   linear combinations of one shared set of signals, so they stay
    ///   correlated at every frequency. Simulated, this holds around 0.6 to
    ///   0.7 at 5 kHz and above rather than decaying.
    ///
    /// That gap - roughly 0.02 against roughly 0.65 in the top bands - is the
    /// entire test, and it needs no tone, no clap and nothing from the user
    /// beyond a room that is not silent.
    ///
    /// WHY IT WANTS A LONG RUN
    ///
    /// Coherence over a single block is always exactly 1, whatever the
    /// signals: the estimate only means anything once it is averaged across
    /// many independent blocks. The bias floor for uncorrelated channels is
    /// about 1/blocks, so 20 seconds at 50ms per block gives roughly 390
    /// blocks and a floor near 0.003. Longer is strictly better, which is what
    /// makes this a good fit for a Settings action that runs for a while.
    ///
    /// WHAT IT CANNOT DO
    ///
    /// It cannot measure arrival-time geometry - that needed the speaker.
    /// It assumes the room is reverberant rather than a single loud point
    /// source, which would keep coherence high for real microphones too. The
    /// defence against that is comparison: run every endpoint back to back in
    /// the same room in one pass, and the assumption is shared by all of them,
    /// so the ranking between endpoints stays meaningful even where the
    /// absolute numbers drift.
    /// </summary>
    public static class AmbientProbe
    {
        private const int SampleRateHz = 48000;
        private const int BytesPerSample = 2;

        // 50ms. Long enough for twelve cycles at the lowest band, short enough
        // that 20 seconds yields several hundred independent blocks.
        private const int BlockSamples = 2400;

        // The driver delivers nothing, then a startup transient, before it
        // settles. The speaker spike's whole false-PASS result came from
        // measuring that transient and calling it signal, so it is discarded
        // here rather than trusted.
        private const int SkipLeadingMs = 500;

        // Roughly third-octave, bracketing the sinc half-power point for every
        // plausible microphone spacing on a phone body: 30mm lands near
        // 2500 Hz, 150mm near 500 Hz.
        private static readonly int[] Bands =
        {
            250, 500, 800, 1250, 2000, 3150, 5000, 8000, 12000
        };

        // The bands averaged into the verdict. Chosen because this is where
        // separated microphones and shared-source channels diverge hardest.
        private static readonly int[] HighBands = { 5000, 8000, 12000 };

        // The validity gate. Two microphones a few centimetres apart are
        // almost perfectly coherent at 250-500 Hz whatever else is true of
        // them - the wavelength is metres long, so both see the same
        // pressure. If the low bands are NOT coherent, the measurement is
        // dominated by something uncorrelated, which in practice means the
        // ambient sound sat below the microphones' own self-noise and
        // self-noise is what got measured.
        //
        // This matters because self-noise is uncorrelated at every frequency,
        // so a noise-dominated run looks exactly like separated microphones
        // to the high-band test. Seven runs on a 1520 showed precisely that:
        // six sat at -57 to -61 dBFS and read near zero in every band
        // including 250 Hz, while the one run at -51.9 dBFS produced a
        // textbook decay from 0.92 down. All seven were reported as separate
        // microphones; only one had earned it.
        private static readonly int[] LowBands = { 250, 500 };

        private const double LowBandValidityFloor = 0.50;

        private const double SeparateMicsCeiling = 0.15;
        private const double SharedSourceFloor = 0.50;

        // Measured, not guessed. On the runs above, -51.9 dBFS produced a
        // usable result and -57 dBFS did not, so the warning belongs between
        // them rather than at the -70 dBFS it started at - which called six
        // failed measurements healthy.
        private const double QuietRoomWarningDb = -55.0;

        public const int DefaultSeconds = 20;

        // The capture belonging to a run that is currently in flight, so
        // deactivation can tear it down.
        //
        // This exists because the probe holds a MediaCapture that
        // MainPage.Service_Deactivated knows nothing about: that handler only
        // looks at RecordingEngine, and returns immediately when no take is
        // running. Without this, locking the screen mid-probe (with the
        // lock-screen preference off) would tombstone the process with the
        // audio endpoint still held - the exact state the detector's pauses
        // and dispose-on-failure paths exist to avoid.
        private static MediaCapture _inFlight;

        /// <summary>
        /// Stops and releases a probe capture that is still running. Safe to
        /// call at any time, including when nothing is in flight. Synchronous
        /// on purpose: the caller is Deactivated, which has no deferral and no
        /// reliable way to await.
        /// </summary>
        public static void AbortQuietly()
        {
            var capture = _inFlight;
            _inFlight = null;

            if (capture == null)
            {
                return;
            }

            try
            {
                // Not awaited - there is no time. Disposing is what actually
                // releases the endpoint, and it follows immediately.
                capture.StopRecordAsync();
            }
            catch
            {
            }

            ModeDetector.DisposeQuietly(capture);
        }

        /// <summary>
        /// True while a run is in flight, so the UI can refuse to start a
        /// second one.
        /// </summary>
        public static bool IsRunning
        {
            get { return _inFlight != null; }
        }

        // Highest first. Four channels is the interesting case and the one the
        // app's own mode ranking prefers, so it is what gets probed when the
        // endpoint will accept it. An endpoint that accepts four and fills two
        // of them with silence is still worth probing at four: the silent
        // channels are named as such and their pairs skipped, which says
        // something true about the endpoint rather than hiding it.
        private static readonly int[] ChannelPreference = { 4, 2 };

        /// <summary>
        /// Probes an endpoint at the most channels it will accept, falling
        /// back down the preference list when a start is refused. Returns the
        /// report for whichever attempt got a recording going, or every
        /// failure if none did.
        /// </summary>
        public static async Task<string> RunBestAsync(
            string deviceId, string deviceName, int seconds)
        {
            var failures = new List<string>();

            for (int i = 0; i < ChannelPreference.Length; i++)
            {
                var outcome = await RunOnceAsync(
                    deviceId, deviceName, ChannelPreference[i], seconds);

                if (outcome.Started)
                {
                    return outcome.Report;
                }

                failures.Add(outcome.Report);

                // The detector's pause. A refused start still touched the
                // endpoint, and some Lumia drivers need a moment afterwards.
                await Task.Delay(400);
            }

            return string.Join(Environment.NewLine, failures.ToArray());
        }

        private sealed class RunOutcome
        {
            public string Report;
            public bool Started;
        }

        /// <summary>
        /// Records ambient sound on one endpoint at one channel count and
        /// reports what the channel relationships say about it. Never throws.
        /// </summary>
        public static async Task<string> RunAsync(
            string deviceId, string deviceName, int channels, int seconds)
        {
            var outcome = await RunOnceAsync(deviceId, deviceName, channels, seconds);
            return outcome.Report;
        }

        private static async Task<RunOutcome> RunOnceAsync(
            string deviceId, string deviceName, int channels, int seconds)
        {
            var result = new RunOutcome();
            result.Report = await RunCoreAsync(deviceId, deviceName, channels, seconds, result);
            return result;
        }

        private static async Task<string> RunCoreAsync(
            string deviceId, string deviceName, int channels, int seconds, RunOutcome outcome)
        {
            if (seconds <= 0)
            {
                seconds = DefaultSeconds;
            }

            var report = new List<string>();
            report.Add("=== Ambient coherence probe ===");
            report.Add(string.Format("Endpoint : {0}", deviceName));
            report.Add(string.Format("Channels : {0}", channels));
            report.Add(string.Format("Requested: {0}s", seconds));
            report.Add("");

            MediaCapture capture = null;
            StorageFile probeFile = null;

            try
            {
                capture = await TryInitializeAsync(deviceId);

                if (capture == null)
                {
                    report.Add("FAILED: MediaCapture.InitializeAsync was rejected for this endpoint.");
                    return Join(report);
                }

                try
                {
                    probeFile = await ApplicationData.Current.TemporaryFolder.CreateFileAsync(
                        "ambient-probe.wav", CreationCollisionOption.ReplaceExisting);

                    await capture.StartRecordToStorageFileAsync(
                        ModeDetector.CreateProfile(channels), probeFile);

                    _inFlight = capture;
                    outcome.Started = true;
                }
                catch (Exception ex)
                {
                    report.Add(string.Format(
                        "FAILED: could not start a {0}-channel recording here.", channels));
                    report.Add("  " + ex.Message);
                    return Join(report);
                }

                await Task.Delay(seconds * 1000);

                // Deactivation may have torn the capture down while this was
                // waiting. Anything written so far is still on disk and worth
                // analysing, but stopping a disposed capture is not.
                if (_inFlight == null)
                {
                    report.Add("INTERRUPTED: the capture was torn down mid-run, most likely by");
                    report.Add("  the screen locking with lock-screen running turned off. The");
                    report.Add("  partial recording is analysed below for what it is worth.");
                    report.Add("");
                    capture = null;
                }
                else
                {
                    _inFlight = null;

                    await StopQuietlyAsync(capture);
                    ModeDetector.DisposeQuietly(capture);
                    capture = null;
                }

                var started = DateTime.Now;
                AppendAnalysis(report, await ReadPcmAsync(probeFile), channels);
                report.Add("");
                report.Add(string.Format("Analysis took {0:0.0}s.",
                    (DateTime.Now - started).TotalSeconds));

                return Join(report);
            }
            catch (Exception ex)
            {
                report.Add("FAILED: unexpected exception.");
                report.Add("  " + ex.Message);
                return Join(report);
            }
            finally
            {
                _inFlight = null;
                ModeDetector.DisposeQuietly(capture);
            }
        }

        #region Analysis

        private static void AppendAnalysis(List<string> report, PcmPayload pcm, int channels)
        {
            if (pcm == null)
            {
                report.Add("FAILED: the probe file could not be read back or had no data chunk.");
                return;
            }

            int bytesPerFrame = channels * BytesPerSample;
            int totalFrames = pcm.DataLength / bytesPerFrame;

            int skipFrames = (SampleRateHz * SkipLeadingMs) / 1000;
            int usableFrames = totalFrames - skipFrames;

            if (usableFrames < BlockSamples * 20)
            {
                report.Add(string.Format(
                    "FAILED: only {0} usable frames - far too short to average.", usableFrames));
                return;
            }

            int blockCount = usableFrames / BlockSamples;

            // Pull every channel's samples for one block into a float array
            // once, then run all nine Goertzels over it. Deinterleaving inside
            // each Goertzel instead would multiply the indexing arithmetic by
            // the number of bands for no benefit.
            var block = new double[channels][];
            for (int ch = 0; ch < channels; ch++)
            {
                block[ch] = new double[BlockSamples];
            }

            int bandCount = Bands.Length;
            int pairCount = (channels * (channels - 1)) / 2;

            // Accumulators for Welch-style averaging.
            var sxx = new double[channels][];
            for (int ch = 0; ch < channels; ch++)
            {
                sxx[ch] = new double[bandCount];
            }

            var sxyReal = new double[pairCount][];
            var sxyImag = new double[pairCount][];
            for (int p = 0; p < pairCount; p++)
            {
                sxyReal[p] = new double[bandCount];
                sxyImag[p] = new double[bandCount];
            }

            var re = new double[channels][];
            var im = new double[channels][];
            for (int ch = 0; ch < channels; ch++)
            {
                re[ch] = new double[bandCount];
                im[ch] = new double[bandCount];
            }

            double sumSquares = 0.0;
            int used = 0;
            int skippedSilent = 0;

            for (int b = 0; b < blockCount; b++)
            {
                int frameStart = skipFrames + (b * BlockSamples);

                LoadBlock(pcm, channels, frameStart, block);

                // A block of pure digital zero carries no phase information
                // and would drag the average toward a meaningless value.
                if (IsSilent(block, channels))
                {
                    skippedSilent++;
                    continue;
                }

                for (int ch = 0; ch < channels; ch++)
                {
                    for (int k = 0; k < BlockSamples; k++)
                    {
                        sumSquares += block[ch][k] * block[ch][k];
                    }

                    for (int f = 0; f < bandCount; f++)
                    {
                        double r, i;
                        Goertzel(block[ch], Bands[f], out r, out i);
                        re[ch][f] = r;
                        im[ch][f] = i;
                        sxx[ch][f] += (r * r) + (i * i);
                    }
                }

                int pair = 0;
                for (int a = 0; a < channels; a++)
                {
                    for (int c = a + 1; c < channels; c++)
                    {
                        for (int f = 0; f < bandCount; f++)
                        {
                            // Cross-spectrum X * conj(Y). The per-block phase
                            // reference is common to both channels and
                            // cancels here, which is exactly why this can be
                            // averaged across blocks at all.
                            sxyReal[pair][f] += (re[a][f] * re[c][f]) + (im[a][f] * im[c][f]);
                            sxyImag[pair][f] += (im[a][f] * re[c][f]) - (re[a][f] * im[c][f]);
                        }

                        pair++;
                    }
                }

                used++;
            }

            if (used < 20)
            {
                report.Add(string.Format(
                    "FAILED: only {0} non-silent blocks. The room was effectively silent.", used));
                return;
            }

            double rms = Math.Sqrt(sumSquares / (used * (double)BlockSamples * channels));
            double rmsDb = 20.0 * Math.Log10(Math.Max(rms, 1e-9) / short.MaxValue);
            double floor = 1.0 / used;

            report.Add(string.Format("Blocks   : {0} used, {1} skipped as silent", used, skippedSilent));
            report.Add(string.Format("Level    : {0:0.0} dBFS RMS across all channels", rmsDb));
            report.Add(string.Format("Bias floor for uncorrelated channels: {0:0.000}", floor));

            if (rmsDb < QuietRoomWarningDb)
            {
                report.Add(string.Format(
                    "WARNING: below {0:0} dBFS the ambient tends to sit under the microphones'",
                    QuietRoomWarningDb));
                report.Add("  own self-noise, and self-noise is what gets measured. Expect the");
                report.Add("  low-band validity gate below to reject this run.");
            }

            report.Add("");
            AppendIdentityCheck(report, pcm, channels);

            report.Add("");
            report.Add("Magnitude-squared coherence (0 = independent, 1 = same signal):");

            var header = new StringBuilder();
            header.Append("  pair ");
            for (int f = 0; f < bandCount; f++)
            {
                header.Append(string.Format("{0,7}", Bands[f]));
            }
            report.Add(header.ToString());

            var coherence = new double[pairCount][];

            int idx = 0;
            for (int a = 0; a < channels; a++)
            {
                for (int c = a + 1; c < channels; c++)
                {
                    coherence[idx] = new double[bandCount];

                    var line = new StringBuilder();
                    line.Append(string.Format("  {0}-{1}  ", a, c));

                    for (int f = 0; f < bandCount; f++)
                    {
                        double num = (sxyReal[idx][f] * sxyReal[idx][f])
                            + (sxyImag[idx][f] * sxyImag[idx][f]);
                        double den = sxx[a][f] * sxx[c][f];

                        double g = den <= 0.0 ? 0.0 : num / den;

                        if (g > 1.0)
                        {
                            g = 1.0;
                        }

                        coherence[idx][f] = g;
                        line.Append(string.Format("{0,7:0.00}", g));
                    }

                    report.Add(line.ToString());
                    idx++;
                }
            }

            var silent = FindSilentChannels(pcm, channels);

            idx = 0;
            for (int a = 0; a < channels; a++)
            {
                for (int c = a + 1; c < channels; c++)
                {
                    report.Add("");

                    if (silent[a] || silent[c])
                    {
                        report.Add(string.Format(
                            "Pair {0}-{1}: skipped, {2} carries no signal at all.",
                            a, c, silent[a] && silent[c]
                                ? "both channels"
                                : "ch" + (silent[a] ? a : c)));
                    }
                    else
                    {
                        AppendPairVerdict(report, a, c, coherence[idx]);
                    }

                    idx++;
                }
            }
        }

        /// <summary>
        /// Channels that are digital zero for the whole recording. A request
        /// for more channels than the endpoint has can be accepted and then
        /// filled with silence - on a 1520, Microphone Array takes a
        /// four-channel request and delivers two real channels plus two empty
        /// ones. Coherence against an empty channel is meaningless, so those
        /// pairs are named and skipped rather than scored.
        /// </summary>
        private static bool[] FindSilentChannels(PcmPayload pcm, int channels)
        {
            var silent = new bool[channels];
            int bytesPerFrame = channels * BytesPerSample;
            int frames = pcm.DataLength / bytesPerFrame;

            for (int ch = 0; ch < channels; ch++)
            {
                bool allZero = true;

                for (int frame = 0; frame < frames && allZero; frame++)
                {
                    int at = pcm.DataOffset + (frame * bytesPerFrame) + (ch * BytesPerSample);

                    if (pcm.Bytes[at] != 0 || pcm.Bytes[at + 1] != 0)
                    {
                        allZero = false;
                    }
                }

                silent[ch] = allZero;
            }

            return silent;
        }

        private static void AppendPairVerdict(List<string> report, int a, int c, double[] coherence)
        {
            double highMean = 0.0;
            int highCount = 0;

            for (int f = 0; f < Bands.Length; f++)
            {
                for (int h = 0; h < HighBands.Length; h++)
                {
                    if (Bands[f] == HighBands[h])
                    {
                        highMean += coherence[f];
                        highCount++;
                    }
                }
            }

            highMean = highCount == 0 ? 0.0 : highMean / highCount;

            double lowMean = 0.0;
            int lowCount = 0;

            for (int f = 0; f < Bands.Length; f++)
            {
                for (int l = 0; l < LowBands.Length; l++)
                {
                    if (Bands[f] == LowBands[l])
                    {
                        lowMean += coherence[f];
                        lowCount++;
                    }
                }
            }

            lowMean = lowCount == 0 ? 0.0 : lowMean / lowCount;

            report.Add(string.Format("Pair {0}-{1}:", a, c));
            report.Add(string.Format("  Low-frequency mean  (250/500):   {0:0.00}", lowMean));
            report.Add(string.Format("  High-frequency mean (5k/8k/12k): {0:0.00}", highMean));

            if (lowMean < LowBandValidityFloor)
            {
                report.Add("  MEASUREMENT FAILED - not a finding about the hardware.");
                report.Add("    Two microphones this close together are always coherent at 250-500");
                report.Add("    Hz, where the wavelength is over a metre. These are not, so what");
                report.Add("    was measured is uncorrelated noise rather than the room - the");
                report.Add("    ambient sat below the microphones' own self-noise.");
                report.Add("    Re-run with more sound in the room. Nothing below is usable, and");
                report.Add("    in particular this must NOT be read as 'separate microphones':");
                report.Add("    self-noise is uncorrelated at every frequency and looks identical");
                report.Add("    to separation in the high bands.");
                return;
            }

            if (highMean <= SeparateMicsCeiling)
            {
                report.Add("  SEPARATE MICROPHONES. Coherence decays to the floor at high");
                report.Add("    frequency, which is what physically separated elements in a");
                report.Add("    reverberant room do, and what two mixes of one shared signal");
                report.Add("    cannot do.");

                AppendSpacingEstimate(report, coherence);
            }
            else if (highMean >= SharedSourceFloor)
            {
                report.Add("  SHARED SOURCE. The channels stay strongly correlated even where");
                report.Add("    the wavelength is far shorter than any spacing that fits on this");
                report.Add("    phone. They are near-certainly two processed mixes built from");
                report.Add("    the same microphones rather than two microphones.");
            }
            else
            {
                report.Add("  INCONCLUSIVE. Between the two thresholds. Most likely separated");
                report.Add("    elements with shared processing applied across them - or a room");
                report.Add("    dominated by one loud source, which keeps real microphones");
                report.Add("    correlated. Re-run somewhere with more diffuse background sound.");
            }
        }

        /// <summary>
        /// Distance between the two elements, from where diffuse-field
        /// coherence passes half power. Uses sinc(2*pi*f*d/c) = 0.707 at
        /// 2*pi*f*d/c = 1.392, so d = 75.98 / f metres.
        ///
        /// Rough on purpose. It assumes a genuinely diffuse field and
        /// omnidirectional elements, and the phone's own body obeys neither
        /// exactly. Worth reporting because the order of magnitude is
        /// checkable against the handset - tens of millimetres is plausible,
        /// a metre is not.
        /// </summary>
        private static void AppendSpacingEstimate(List<string> report, double[] coherence)
        {
            for (int f = 1; f < Bands.Length; f++)
            {
                if (coherence[f - 1] > 0.5 && coherence[f] <= 0.5)
                {
                    double lowF = Bands[f - 1];
                    double highF = Bands[f];

                    // Interpolate in log frequency, which is where the bands
                    // are evenly spaced.
                    double t = (coherence[f - 1] - 0.5) / (coherence[f - 1] - coherence[f]);
                    double crossing = Math.Exp(
                        Math.Log(lowF) + (t * (Math.Log(highF) - Math.Log(lowF))));

                    double millimetres = 75980.0 / crossing;

                    report.Add(string.Format(
                        "    Implied spacing ~{0:0} mm (half power near {1:0} Hz). Rough:",
                        millimetres, crossing));
                    report.Add("    assumes a diffuse field and omnidirectional elements.");
                    return;
                }
            }

            report.Add("    Spacing not estimable - coherence never crosses 0.5 inside the");
            report.Add("    measured bands.");
        }

        /// <summary>
        /// The byte-exact duplicate test, same rule WavProbe applies, repeated
        /// here so a mono-duplicated endpoint is named as such before any
        /// coherence number is read. Coherence on two copies of one signal is
        /// 1.00 everywhere and means nothing.
        /// </summary>
        private static void AppendIdentityCheck(List<string> report, PcmPayload pcm, int channels)
        {
            int bytesPerFrame = channels * BytesPerSample;
            int frames = pcm.DataLength / bytesPerFrame;
            int check = Math.Min(frames, SampleRateHz * 2);

            var duplicates = new List<string>();

            for (int i = 1; i < channels; i++)
            {
                for (int j = 0; j < i; j++)
                {
                    bool identical = true;

                    for (int frame = 0; frame < check && identical; frame++)
                    {
                        int at = pcm.DataOffset + (frame * bytesPerFrame);
                        int oi = at + (i * BytesPerSample);
                        int oj = at + (j * BytesPerSample);

                        if (pcm.Bytes[oi] != pcm.Bytes[oj] ||
                            pcm.Bytes[oi + 1] != pcm.Bytes[oj + 1])
                        {
                            identical = false;
                        }
                    }

                    if (identical)
                    {
                        duplicates.Add(string.Format("ch{0} is a byte-exact copy of ch{1}", i, j));
                    }
                }
            }

            if (duplicates.Count == 0)
            {
                report.Add("Channels are byte-distinct.");
            }
            else
            {
                report.Add("MONO DUPLICATED:");
                foreach (var d in duplicates)
                {
                    report.Add("  " + d);
                }
                report.Add("  Coherence below will read 1.00 everywhere and means nothing here.");
            }
        }

        private static void LoadBlock(PcmPayload pcm, int channels, int frameStart, double[][] into)
        {
            int bytesPerFrame = channels * BytesPerSample;
            byte[] bytes = pcm.Bytes;

            for (int k = 0; k < BlockSamples; k++)
            {
                int at = pcm.DataOffset + ((frameStart + k) * bytesPerFrame);

                for (int ch = 0; ch < channels; ch++)
                {
                    int o = at + (ch * BytesPerSample);
                    into[ch][k] = (short)(bytes[o] | (bytes[o + 1] << 8));
                }
            }
        }

        private static bool IsSilent(double[][] block, int channels)
        {
            for (int ch = 0; ch < channels; ch++)
            {
                for (int k = 0; k < BlockSamples; k++)
                {
                    if (block[ch][k] != 0.0)
                    {
                        return false;
                    }
                }
            }

            return true;
        }

        private static void Goertzel(double[] samples, int frequency, out double real, out double imag)
        {
            double w = 2.0 * Math.PI * frequency / SampleRateHz;
            double cosw = Math.Cos(w);
            double sinw = Math.Sin(w);
            double coeff = 2.0 * cosw;

            double s1 = 0.0;
            double s2 = 0.0;

            for (int i = 0; i < samples.Length; i++)
            {
                double s0 = samples[i] + (coeff * s1) - s2;
                s2 = s1;
                s1 = s0;
            }

            real = s1 - (s2 * cosw);
            imag = s2 * sinw;
        }

        #endregion

        #region Plumbing

        private sealed class PcmPayload
        {
            public readonly byte[] Bytes;
            public readonly int DataOffset;
            public readonly int DataLength;

            public PcmPayload(byte[] bytes, int dataOffset, int dataLength)
            {
                this.Bytes = bytes;
                this.DataOffset = dataOffset;
                this.DataLength = dataLength;
            }
        }

        private static async Task<PcmPayload> ReadPcmAsync(StorageFile file)
        {
            if (file == null)
            {
                return null;
            }

            try
            {
                var buffer = await FileIO.ReadBufferAsync(file);
                byte[] bytes = buffer.ToArray();

                int dataOffset;
                int dataLength;
                if (!WavProbe.TryFindWavDataChunk(bytes, out dataOffset, out dataLength))
                {
                    return null;
                }

                return new PcmPayload(bytes, dataOffset, dataLength);
            }
            catch
            {
                return null;
            }
        }

        private static async Task<MediaCapture> TryInitializeAsync(string deviceId)
        {
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

        private static async Task StopQuietlyAsync(MediaCapture capture)
        {
            try
            {
                await capture.StopRecordAsync();
            }
            catch
            {
            }
        }

        /// <summary>
        /// Writes the collected reports to a timestamped text file in
        /// Music\recordings, where USB can see it. Returns null on failure.
        /// </summary>
        public static async Task<string> WriteReportFileAsync(List<string> lines)
        {
            try
            {
                var folder = await AppSettings.GetRecordingsFolderAsync();
                var name = "ambient-probe-" + DateTime.Now.ToString("yyyy-MM-dd-HHmmss") + ".txt";

                var file = await folder.CreateFileAsync(name, CreationCollisionOption.GenerateUniqueName);
                await FileIO.WriteLinesAsync(file, lines);

                return file.Name;
            }
            catch
            {
                return null;
            }
        }

        private static string Join(List<string> report)
        {
            var text = string.Join(Environment.NewLine, report.ToArray());

            try
            {
                ProbeLog.Append("Ambient probe:" + Environment.NewLine + text);
            }
            catch
            {
            }

            return text;
        }

        #endregion
    }
}
