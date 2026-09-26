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
    /// The discriminator is the DECAY across frequency - how far coherence
    /// falls between the low bands and the high ones:
    ///
    ///   Two genuinely separated microphones in a reverberant room see a
    ///   roughly diffuse sound field. Diffuse-field coherence follows a sinc
    ///   law: near 1 at low frequency, where the wavelength dwarfs the
    ///   spacing and both elements see the same pressure, falling away once
    ///   it does not.
    ///
    ///   Two beamformer outputs derived from the same microphones are both
    ///   linear combinations of one shared set of signals, so they stay
    ///   correlated at every frequency. They start high and stay there.
    ///
    /// The decay, not the high band on its own, is what carries the answer.
    /// Where the coherence sits at 12 kHz depends on how far apart the
    /// elements are, and therefore on how big the phone is - a 928 holds more
    /// coherence up there than a 1520 simply by being 133mm rather than
    /// 163mm long. How far it FELL does not depend on that. Measured across
    /// both handsets, separated elements decay by 0.58 to 0.90 and derived
    /// channels by 0.00 to 0.38.
    ///
    /// It needs no tone, no clap and nothing from the user beyond a room that
    /// is not silent.
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
    ///
    /// It assumes the room is reverberant rather than dominated by one loud
    /// source. That assumption fails in both directions. Too quiet and the
    /// ambient drops under the elements' own self-noise, which is
    /// uncorrelated and mimics separation; the low-band gate catches that.
    /// Too directional - a PA, a band, a single close talker - and both
    /// elements see the same wavefront, which mimics derivation; nothing
    /// catches that automatically, because the low bands look perfect, so a
    /// loud level only raises it as a possibility.
    ///
    /// The defence in both cases is comparison: run every endpoint back to
    /// back in one pass and the assumption is shared by all of them, so the
    /// ranking between endpoints holds even where the absolute numbers drift.
    /// At a gig, run it during applause - loud, broadband and arriving from
    /// every direction, which is the best field this test can have.
    ///
    /// The per-channel levels and clipping figures rest on none of this and
    /// are valid at any level.
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

        // The verdict is taken from the DECAY - low-band mean minus
        // high-band mean - rather than from the high band alone.
        //
        // An absolute high-band threshold conflates two different things. A
        // 1520's Microphone Array reads 0.01 up there; a 928's Enhanced Audio
        // Recording Device reads 0.12 to 0.33 across seven runs and kept
        // landing on "inconclusive". But the 928 is a 133mm phone against the
        // 1520's 163mm, so its elements are closer together, and closer
        // elements legitimately hold more coherence at 5-12 kHz. Penalising
        // that is measuring the phone's size, not its microphones.
        //
        // What separated elements always do, whatever their spacing, is
        // DECAY: coherent at 250-500 Hz where the wavelength dwarfs the
        // spacing, incoherent once it does not. Channels derived from a
        // shared set stay wherever they started. Measured across both
        // handsets:
        //
        //   1520 Microphone Array   decay 0.90    separated
        //   928  Enhanced Audio     decay 0.58-0.67  separated
        //   1520 Surround Microphone decay 0.13 mean  derived
        //   any mono-duplicated pair decay 0.00       derived
        //
        // Those clusters are far enough apart to threshold between.
        private const double SeparatedDecayFloor = 0.45;
        private const double DerivedDecayCeiling = 0.25;

        // Measured, not guessed. On the runs above, -51.9 dBFS produced a
        // usable result and -57 dBFS did not, so the warning belongs between
        // them rather than at the -70 dBFS it started at - which called six
        // failed measurements healthy.
        private const double QuietRoomWarningDb = -55.0;

        // The opposite failure. The coherence test assumes a roughly diffuse
        // field; a loud one usually is not, because what made it loud is a
        // PA or a band in one direction. Two elements then see nearly the
        // same wavefront, and a pure delay does not reduce coherence at any
        // frequency, so separated microphones read as derived. Nothing
        // catches this automatically - the low bands look perfect - so the
        // level is used to raise the possibility.
        private const double LoudFieldWarningDb = -30.0;

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

        /// <summary>
        /// One endpoint's result: the full text report for the log file, and
        /// the verdict the mode ranking acts on.
        /// </summary>
        public sealed class ProbeResult
        {
            public string Report;
            public ChannelIndependence Verdict;

            // A plain-language reason to show next to an inconclusive
            // verdict, so the user knows what to change before re-running.
            // Null when the verdict needs no explanation.
            public string Reason;

            // The capture was torn down mid-run - the app was deactivated.
            // Whatever the partial recording said, it is not trusted, and a
            // caller analysing several endpoints should stop rather than
            // start the next one against a backgrounded app.
            public bool Interrupted;
        }

        /// <summary>
        /// Probes an endpoint at <paramref name="maxChannels"/>, falling back
        /// to two when that start is refused. Returns the result for
        /// whichever attempt got a recording going, or every failure (as
        /// Inconclusive) if none did.
        ///
        /// Probed at the channel count detection verified rather than always
        /// at four, so the verdict describes the mode that will actually be
        /// recorded. Asking a 2-channel endpoint for four works, but only
        /// adds two empty channels to skip.
        /// </summary>
        public static async Task<ProbeResult> RunBestAsync(
            string deviceId, string deviceName, int seconds, int maxChannels)
        {
            var order = new List<int>();
            order.Add(maxChannels < 2 ? 2 : maxChannels);
            if (order[0] > 2)
            {
                order.Add(2);
            }

            var failures = new List<string>();

            for (int i = 0; i < order.Count; i++)
            {
                var outcome = await RunOnceAsync(deviceId, deviceName, order[i], seconds);

                if (outcome.Started)
                {
                    return outcome.Result;
                }

                failures.Add(outcome.Result.Report);

                // The detector's pause. A refused start still touched the
                // endpoint, and some Lumia drivers need a moment afterwards.
                await Task.Delay(400);
            }

            var failed = new ProbeResult();
            failed.Report = string.Join(Environment.NewLine, failures.ToArray());
            failed.Verdict = ChannelIndependence.Inconclusive;
            failed.Reason = "the microphone would not start recording";
            return failed;
        }

        private sealed class RunOutcome
        {
            public ProbeResult Result;
            public bool Started;
        }

        private static async Task<RunOutcome> RunOnceAsync(
            string deviceId, string deviceName, int channels, int seconds)
        {
            var outcome = new RunOutcome();
            outcome.Result = new ProbeResult();
            outcome.Result.Verdict = ChannelIndependence.Inconclusive;
            outcome.Result.Report = await RunCoreAsync(deviceId, deviceName, channels, seconds, outcome);
            return outcome;
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
                    outcome.Result.Reason = "the microphone would not open";
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
                    outcome.Result.Reason = "the microphone would not start recording";
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
                    report.Add("  Its verdict is NOT stored.");
                    report.Add("");
                    capture = null;
                    outcome.Result.Interrupted = true;
                }
                else
                {
                    _inFlight = null;

                    await StopQuietlyAsync(capture);
                    ModeDetector.DisposeQuietly(capture);
                    capture = null;
                }

                var started = DateTime.Now;

                string reason;
                var verdict = AppendAnalysis(report, await ReadPcmAsync(probeFile), channels, out reason);

                if (outcome.Result.Interrupted)
                {
                    verdict = ChannelIndependence.Inconclusive;
                    reason = "the analysis was interrupted";
                }

                outcome.Result.Verdict = verdict;
                outcome.Result.Reason = reason;

                report.Add("");
                report.Add(string.Format("ENDPOINT VERDICT: {0}{1}",
                    ModeRanking.DescribeIndependence(verdict),
                    string.IsNullOrEmpty(reason) ? string.Empty : " - " + reason));
                report.Add(string.Format("Analysis took {0:0.0}s.",
                    (DateTime.Now - started).TotalSeconds));

                return Join(report);
            }
            catch (Exception ex)
            {
                report.Add("FAILED: unexpected exception.");
                report.Add("  " + ex.Message);
                outcome.Result.Verdict = ChannelIndependence.Inconclusive;
                outcome.Result.Reason = "the analysis hit an unexpected error";
                return Join(report);
            }
            finally
            {
                _inFlight = null;
                ModeDetector.DisposeQuietly(capture);
            }
        }

        #region Analysis

        /// <summary>
        /// Writes the full analysis to the report and returns the endpoint's
        /// verdict, combined across every channel pair that carries signal.
        ///
        /// The combination is deliberately pessimistic. One byte-identical
        /// pair or one shared-source pair makes the whole endpoint a
        /// processed mix, because a mode is only as raw as its least
        /// independent channel. Every pair has to read as separate for the
        /// endpoint to earn that verdict, and a pair whose measurement failed
        /// the low-band gate stops that from happening - the same room sits
        /// under every pair, so one failing is a warning about all of them.
        /// </summary>
        private static ChannelIndependence AppendAnalysis(
            List<string> report, PcmPayload pcm, int channels, out string reason)
        {
            reason = null;

            if (pcm == null)
            {
                report.Add("FAILED: the probe file could not be read back or had no data chunk.");
                reason = "the recording could not be read back";
                return ChannelIndependence.Inconclusive;
            }

            int bytesPerFrame = channels * BytesPerSample;
            int totalFrames = pcm.DataLength / bytesPerFrame;

            int skipFrames = (SampleRateHz * SkipLeadingMs) / 1000;
            int usableFrames = totalFrames - skipFrames;

            if (usableFrames < BlockSamples * 20)
            {
                report.Add(string.Format(
                    "FAILED: only {0} usable frames - far too short to average.", usableFrames));
                reason = "the recording was too short";
                return ChannelIndependence.Inconclusive;
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
                reason = "the room was too quiet";
                return ChannelIndependence.Inconclusive;
            }

            double rms = Math.Sqrt(sumSquares / (used * (double)BlockSamples * channels));
            double rmsDb = 20.0 * Math.Log10(Math.Max(rms, 1e-9) / short.MaxValue);
            double floor = 1.0 / used;

            report.Add(string.Format("Blocks   : {0} used, {1} skipped as silent", used, skippedSilent));
            report.Add(string.Format("Level    : {0:0.0} dBFS RMS across all channels", rmsDb));
            report.Add(string.Format("Bias floor for uncorrelated channels: {0:0.000}", floor));

            if (rmsDb > LoudFieldWarningDb)
            {
                report.Add("NOTE: loud enough that the field is probably directional - a PA, a");
                report.Add("  band, one dominant source. Two microphones then see nearly the same");
                report.Add("  wavefront, a pure delay does not reduce coherence at all, and real");
                report.Add("  separated elements read as SHARED SOURCE. The per-channel levels and");
                report.Add("  clipping below are unaffected and are the numbers to read here.");
                report.Add("  For a usable coherence result at a gig, run this during applause:");
                report.Add("  loud, broadband and arriving from everywhere at once.");
            }

            if (rmsDb < QuietRoomWarningDb)
            {
                report.Add(string.Format(
                    "WARNING: below {0:0} dBFS the ambient tends to sit under the microphones'",
                    QuietRoomWarningDb));
                report.Add("  own self-noise, and self-noise is what gets measured. Expect the");
                report.Add("  low-band validity gate below to reject this run.");
            }

            report.Add("");
            var silent = FindSilentChannels(pcm, channels);

            bool anyDuplicate = AppendIdentityCheck(report, pcm, channels, silent);

            report.Add("");
            AppendLevelReport(report, pcm, channels, silent);

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

            int separatedPairs = 0;
            int derivedPairs = 0;
            int ambiguousPairs = 0;
            int failedPairs = 0;

            idx = 0;
            for (int a = 0; a < channels; a++)
            {
                for (int c = a + 1; c < channels; c++)
                {
                    report.Add("");

                    if (silent[a] || silent[c])
                    {
                        report.Add(string.Format(
                            "Pair {0}-{1}: skipped, {2} no signal at all.",
                            a, c, silent[a] && silent[c]
                                ? "neither channel carries"
                                : "ch" + (silent[a] ? a : c) + " carries"));
                    }
                    else
                    {
                        switch (AppendPairVerdict(report, a, c, coherence[idx]))
                        {
                            case ChannelIndependence.Separated:
                                separatedPairs++;
                                break;
                            case ChannelIndependence.Derived:
                                derivedPairs++;
                                break;
                            case ChannelIndependence.Ambiguous:
                                ambiguousPairs++;
                                break;
                            default:
                                failedPairs++;
                                break;
                        }
                    }

                    idx++;
                }
            }

            // Byte-identical channels are a processed endpoint whatever the
            // room was like - nothing about the field makes two microphones
            // produce the same samples.
            if (anyDuplicate)
            {
                return ChannelIndependence.Derived;
            }

            if (derivedPairs > 0)
            {
                // The one failure the level can flag but nothing else can:
                // a loud, directional field holds real microphones coherent
                // at every frequency, which is exactly what derived channels
                // look like. Not stored as a finding about the hardware.
                if (rmsDb > LoudFieldWarningDb)
                {
                    reason = "one loud sound source dominated the room";
                    return ChannelIndependence.Inconclusive;
                }

                return ChannelIndependence.Derived;
            }

            if (failedPairs > 0)
            {
                reason = "the room was too quiet";
                return ChannelIndependence.Inconclusive;
            }

            if (ambiguousPairs > 0)
            {
                reason = "the result was borderline - try a room with more background sound";
                return ChannelIndependence.Ambiguous;
            }

            if (separatedPairs > 0)
            {
                return ChannelIndependence.Separated;
            }

            // Every pair involved an empty channel: fewer than two channels
            // carried anything, so there was no relationship to measure.
            reason = "fewer than two channels carried signal";
            return ChannelIndependence.Inconclusive;
        }

        /// <summary>
        /// Per-channel peak, RMS, crest factor and clipping.
        ///
        /// This is the part of the report that works at a concert. The
        /// coherence test above leans on the sound field being roughly
        /// diffuse, which a PA-dominated room is not - during a song two
        /// microphones see nearly the same wavefront from nearly the same
        /// direction, a pure delay does not reduce coherence at all, and
        /// genuinely separate elements will read as shared. None of the
        /// numbers below care about any of that.
        ///
        /// What they answer, for a recorder built around microphones rated
        /// for very high SPL:
        ///
        ///   Clipping - whether the endpoint preserves that headroom or
        ///   throws it away. A run of consecutive samples pinned at full
        ///   scale is the signature; isolated full-scale samples are not, and
        ///   are counted separately so a single loud transient is not
        ///   reported as a fault.
        ///
        ///   Crest factor (peak minus RMS) - whether something is limiting.
        ///   Read it by COMPARING endpoints in the same field rather than
        ///   against an absolute number, since it depends on the material:
        ///   an endpoint that returns a markedly lower crest factor than
        ///   another on the same sound is compressing it.
        ///
        ///   Per-channel RMS - whether the level gap between endpoints
        ///   closes when the room gets loud. A gap that closes was
        ///   level-dependent noise suppression; a gap that holds is just how
        ///   that endpoint is calibrated.
        /// </summary>
        private static void AppendLevelReport(
            List<string> report, PcmPayload pcm, int channels, bool[] silent)
        {
            int bytesPerFrame = channels * BytesPerSample;
            int frames = pcm.DataLength / bytesPerFrame;

            report.Add("Per channel:");
            report.Add("     peak dBFS   rms dBFS   crest dB   full-scale samples   longest run");

            bool anyClipping = false;

            for (int ch = 0; ch < channels; ch++)
            {
                if (silent[ch])
                {
                    report.Add(string.Format("  ch{0}        (empty)", ch));
                    continue;
                }

                int peak = 0;
                double sumSquares = 0.0;
                int clippedSamples = 0;
                int clippedRuns = 0;
                int longestRun = 0;
                int run = 0;

                for (int frame = 0; frame < frames; frame++)
                {
                    int at = pcm.DataOffset + (frame * bytesPerFrame) + (ch * BytesPerSample);
                    var sample = (short)(pcm.Bytes[at] | (pcm.Bytes[at + 1] << 8));

                    int magnitude = sample == short.MinValue
                        ? short.MaxValue
                        : Math.Abs(sample);

                    if (magnitude > peak)
                    {
                        peak = magnitude;
                    }

                    sumSquares += (double)sample * sample;

                    if (magnitude >= short.MaxValue)
                    {
                        clippedSamples++;
                        run++;

                        if (run > longestRun)
                        {
                            longestRun = run;
                        }

                        // Three in a row is the threshold for calling it
                        // clipping rather than a transient that happened to
                        // touch full scale once. Counted at exactly 3 so a
                        // single long run increments this once, not once per
                        // sample.
                        if (run == 3)
                        {
                            clippedRuns++;
                        }
                    }
                    else
                    {
                        run = 0;
                    }
                }

                double rms = Math.Sqrt(sumSquares / Math.Max(frames, 1));
                double peakDb = 20.0 * Math.Log10(Math.Max(peak, 1) / (double)short.MaxValue);
                double rmsDb = 20.0 * Math.Log10(Math.Max(rms, 1e-9) / short.MaxValue);

                if (clippedRuns > 0)
                {
                    anyClipping = true;
                }

                report.Add(string.Format(
                    "  ch{0}     {1,8:0.0}   {2,8:0.0}   {3,8:0.0}   {4,18}   {5,11}",
                    ch, peakDb, rmsDb, peakDb - rmsDb, clippedSamples, longestRun));
            }

            if (anyClipping)
            {
                report.Add("  CLIPPING - full scale held for three or more samples in a row.");
                report.Add("    Whatever the microphones can take, this path is throwing the top");
                report.Add("    of it away.");
            }
            else
            {
                report.Add("  No clipping (no run of three or more samples at full scale).");
            }

            report.Add("  Crest factor is only meaningful compared against another endpoint on");
            report.Add("  the same sound: the lower one is compressing.");
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

        /// <summary>
        /// Reports one pair and returns its verdict: Separated, Derived,
        /// Ambiguous, or Inconclusive when the low-band gate says the
        /// measurement itself failed.
        /// </summary>
        private static ChannelIndependence AppendPairVerdict(
            List<string> report, int a, int c, double[] coherence)
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
                return ChannelIndependence.Inconclusive;
            }

            double decay = lowMean - highMean;

            report.Add(string.Format("  Decay (low minus high):          {0:0.00}", decay));

            if (decay >= SeparatedDecayFloor)
            {
                report.Add("  SEPARATE MICROPHONES. Coherence is high where the wavelength dwarfs");
                report.Add("    any spacing that fits on a phone and falls away once it does not.");
                report.Add("    Channels built from one shared set of signals do not do that -");
                report.Add("    they stay wherever they started.");

                AppendSpacingEstimate(report, coherence);
                return ChannelIndependence.Separated;
            }

            if (decay <= DerivedDecayCeiling)
            {
                report.Add("  SHARED SOURCE. The channels stay about as correlated at 12 kHz as at");
                report.Add("    250 Hz, so their relationship is not being set by the distance");
                report.Add("    between two microphones. They are near-certainly processed mixes");
                report.Add("    built from the same elements rather than separate elements.");
                return ChannelIndependence.Derived;
            }

            report.Add("  AMBIGUOUS. Some decay, but less than separated elements produce and");
            report.Add("    more than shared mixes do. Most likely separated elements with");
            report.Add("    shared processing across them - or a room dominated by one loud");
            report.Add("    source, which holds real microphones correlated. Re-run somewhere");
            report.Add("    with more diffuse background sound before concluding anything.");
            return ChannelIndependence.Ambiguous;
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

                    // A diffuse-field sinc stays down once it has fallen. If
                    // coherence climbs back over half power in a higher band,
                    // the field is not behaving diffusely and the crossing is
                    // not a spacing. A 928 produced exactly this - 0.76, 0.64,
                    // 0.05 and then back up to 0.59 at 3150 Hz - which yielded
                    // "136 mm" on a phone only 133 mm long.
                    for (int later = f + 1; later < Bands.Length; later++)
                    {
                        if (coherence[later] > 0.5)
                        {
                            report.Add(string.Format(
                                "    Spacing not readable: coherence falls by {0:0} Hz but climbs",
                                highF));
                            report.Add(string.Format(
                                "    back to {0:0.00} at {1:0} Hz. A diffuse field does not do that,",
                                coherence[later], Bands[later]));
                            report.Add("    so the crossing is not a spacing.");
                            return;
                        }
                    }

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
        /// 1.00 everywhere and means nothing. Returns true when any pair of
        /// non-empty channels is byte-identical.
        /// </summary>
        private static bool AppendIdentityCheck(
            List<string> report, PcmPayload pcm, int channels, bool[] silent)
        {
            int bytesPerFrame = channels * BytesPerSample;
            int frames = pcm.DataLength / bytesPerFrame;
            int check = Math.Min(frames, SampleRateHz * 2);

            var duplicates = new List<string>();
            var empties = new List<string>();

            for (int ch = 0; ch < channels; ch++)
            {
                if (silent[ch])
                {
                    empties.Add("ch" + ch);
                }
            }

            for (int i = 1; i < channels; i++)
            {
                // Two empty channels are byte-identical by definition, and
                // calling that a duplicate is worse than saying nothing: it
                // reads as a driver copying a real signal when in fact there
                // is no signal at all. Requesting more channels than an
                // endpoint has produces exactly this - a 1520's Microphone
                // Array takes a 4-channel request and returns two real
                // channels plus two empty ones, which was being reported as
                // "ch3 is a byte-exact copy of ch2".
                if (silent[i])
                {
                    continue;
                }

                for (int j = 0; j < i; j++)
                {
                    if (silent[j])
                    {
                        continue;
                    }

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

            if (empties.Count > 0)
            {
                report.Add(string.Format(
                    "EMPTY CHANNELS: {0} carry no signal at all - this endpoint has fewer",
                    string.Join(", ", empties.ToArray())));
                report.Add(string.Format(
                    "  real channels than the {0} requested.", channels));
            }

            if (duplicates.Count == 0)
            {
                report.Add(empties.Count > 0
                    ? "The remaining channels are byte-distinct."
                    : "Channels are byte-distinct.");
            }
            else
            {
                report.Add("DUPLICATED:");
                foreach (var d in duplicates)
                {
                    report.Add("  " + d);
                }
                report.Add("  Coherence for those pairs reads 1.00 and means nothing.");
            }

            return duplicates.Count > 0;
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
