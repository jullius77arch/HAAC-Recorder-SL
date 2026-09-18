using System;
using System.Collections.Generic;
using System.Runtime.InteropServices.WindowsRuntime;
using System.Text;
using System.Threading.Tasks;
using System.Windows.Threading;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Audio;
using Windows.Media.Capture;
using Windows.Storage;

namespace HAAC_Recorder_SL
{
    /// <summary>
    /// THROWAWAY SPIKE. Not part of the shipping app, not wired into the UI,
    /// and not written to the standard of the rest of the project. It exists
    /// to answer one question before any real work is spent on the speaker
    /// probe feature:
    ///
    ///     Can this phone play a tone through its own loudspeaker while a
    ///     MediaCapture session is recording, and does that tone actually
    ///     reach the recorded file?
    ///
    /// If the answer is no - because WP8.1's audio policy ducks or mutes
    /// playback while a capture session is live, or because the driver's
    /// echo canceller removes the phone's own output from the mic feed - then
    /// the whole self-calibrating idea is dead and nothing further should be
    /// built on it. That is a cheap thing to learn now and an expensive thing
    /// to learn after a week of analysis code.
    ///
    /// The sequence is deliberately short and deliberately dumb:
    ///
    ///     start capture -> 400ms silence -> 1500ms tone -> 400ms silence -> stop
    ///
    /// then the file is read back and every 100ms block is measured at the
    /// test frequency with a Goertzel filter. A working result looks like a
    /// clear step up in level across the middle of the block table and a step
    /// back down at the end. A flat table means the tone never made it.
    ///
    /// The leading and trailing silence are not padding - they are the
    /// control. Without a measured noise floor from the same file, a mediocre
    /// tone level is uninterpretable.
    ///
    /// REQUIRES: a project reference to Microsoft.Xna.Framework. The rest of
    /// the app does not use XNA at all, so this is the only place it is
    /// needed and the reference can come straight back out if the spike says
    /// no.
    ///
    /// MUST be called from the UI thread - it starts a DispatcherTimer to
    /// pump FrameworkDispatcher, without which XNA audio silently never
    /// plays in a Silverlight app.
    ///
    /// C# 5 throughout (VS2013's default compiler): no string interpolation,
    /// no expression-bodied members, no await inside catch or finally.
    /// </summary>
    public static class SpeakerProbeSpike
    {
        private const int SampleRateHz = 48000;
        private const int BytesPerSample = 2;

        // 1500 Hz, and the reason is the aliasing limit rather than anything
        // acoustic.
        //
        // A single-frequency phase measurement can only resolve delays up to
        // half a cycle before it wraps. At 4 kHz that is +/-6 samples - and
        // the path differences expected across a 1520's mic positions are
        // around 14 samples, so 4 kHz would alias and quietly report a small
        // wrong number instead of a large right one. At 1500 Hz a cycle is 32
        // samples, giving +/-16 samples of unambiguous range, which covers
        // the expected geometry with room to spare.
        //
        // The cost is real: higher frequencies would reject chassis-borne
        // vibration better and resolve more finely. The eventual feature
        // wants both - a low tone to establish the delay unambiguously and a
        // high one to refine it - but a spike that reports one honest number
        // beats one that reports a precise wrong one.
        //
        // 1500 divides 48000 exactly: 32 samples per cycle.
        private const int DefaultToneHz = 1500;

        // 250ms of tone, looped. 1500 Hz at 48 kHz gives exactly 375 whole
        // cycles in that buffer, so the loop point is phase-continuous and
        // the loop itself introduces no click - which would otherwise show up
        // as broadband energy every 250ms and pollute the measurement.
        private const int ToneBufferMs = 250;

        private const int PreSilenceMs = 400;
        private const int ToneMs = 1500;
        private const int PostSilenceMs = 400;

        private const int AnalysisBlockMs = 100;

        // Loud enough for a usable signal-to-noise ratio against room noise,
        // short of the level where the speaker's own distortion starts
        // generating harmonics that muddy the picture.
        private const float PlaybackVolume = 0.8f;

        /// <summary>
        /// Runs the spike against one capture endpoint and returns a
        /// human-readable report. The report is also appended to ProbeLog, so
        /// a run that kills the app still leaves its findings on disk.
        ///
        /// Never throws. A spike that crashes the app teaches nothing, so
        /// every failure path returns a report saying what failed and where.
        /// </summary>
        public static async Task<string> RunAsync(string deviceId, int channels, int toneHz)
        {
            if (toneHz <= 0)
            {
                toneHz = DefaultToneHz;
            }

            var report = new List<string>();
            report.Add("=== Speaker probe spike ===");
            report.Add(string.Format("Device Id : {0}", string.IsNullOrEmpty(deviceId) ? "(default)" : deviceId));
            report.Add(string.Format("Channels  : {0}", channels));
            report.Add(string.Format("Tone      : {0} Hz at volume {1:0.00}", toneHz, PlaybackVolume));
            report.Add("");

            MediaCapture capture = null;
            SoundEffectInstance toneInstance = null;
            DispatcherTimer pump = null;
            StorageFile probeFile = null;

            try
            {
                // ---- 1. XNA audio setup -------------------------------
                //
                // FrameworkDispatcher.Update must be called regularly or XNA
                // audio in a Silverlight app never produces a sound and never
                // reports an error either. It is pumped on a timer for the
                // duration of the run and torn down afterwards.
                try
                {
                    FrameworkDispatcher.Update();

                    pump = new DispatcherTimer();
                    pump.Interval = TimeSpan.FromMilliseconds(33);
                    pump.Tick += PumpTick;
                    pump.Start();
                }
                catch (Exception ex)
                {
                    report.Add("FAILED: could not start the XNA FrameworkDispatcher pump.");
                    report.Add("  " + ex.Message);
                    report.Add("  Check that Microsoft.Xna.Framework is referenced and that this ran on the UI thread.");
                    return Finish(report);
                }

                try
                {
                    byte[] pcm = BuildToneBuffer(toneHz, ToneBufferMs);

                    var effect = new SoundEffect(pcm, SampleRateHz, AudioChannels.Mono);

                    toneInstance = effect.CreateInstance();
                    toneInstance.IsLooped = true;
                    toneInstance.Volume = PlaybackVolume;
                }
                catch (Exception ex)
                {
                    report.Add("FAILED: could not build or create the tone SoundEffect.");
                    report.Add("  " + ex.Message);
                    return Finish(report);
                }

                // ---- 2. Start capturing -------------------------------
                capture = await TryInitializeAsync(deviceId);

                if (capture == null)
                {
                    report.Add("FAILED: MediaCapture.InitializeAsync was rejected for this endpoint.");
                    return Finish(report);
                }

                try
                {
                    probeFile = await ApplicationData.Current.TemporaryFolder.CreateFileAsync(
                        "speaker-probe-spike.wav", CreationCollisionOption.ReplaceExisting);

                    await capture.StartRecordToStorageFileAsync(
                        ModeDetector.CreateProfile(channels), probeFile);
                }
                catch (Exception ex)
                {
                    report.Add(string.Format(
                        "FAILED: could not start a {0}-channel recording on this endpoint.", channels));
                    report.Add("  " + ex.Message);
                    return Finish(report);
                }

                // ---- 3. Silence, tone, silence ------------------------
                //
                // Every delay here is wall-clock rather than sample-counted,
                // so the boundaries in the recorded file are approximate -
                // StartRecordToStorageFileAsync does not begin writing the
                // instant it returns. That is exactly why the analysis below
                // reports every block rather than trying to slice out "the
                // tone window": the step is found by looking at the numbers,
                // not by trusting the clock.
                await Task.Delay(PreSilenceMs);

                bool playThrew = false;
                string playError = null;

                try
                {
                    toneInstance.Play();
                }
                catch (Exception ex)
                {
                    playThrew = true;
                    playError = ex.Message;
                }

                if (playThrew)
                {
                    report.Add("FAILED: SoundEffectInstance.Play() threw while a capture was live.");
                    report.Add("  " + playError);
                    report.Add("  This is the interesting negative result - playback and capture cannot coexist.");
                }

                await Task.Delay(ToneMs);

                // State is worth recording: if WP8.1's audio policy stopped
                // the instance behind our back, that is the answer to the
                // whole spike and it will not show up any other way.
                string stateAtEnd = "unknown";
                try
                {
                    stateAtEnd = toneInstance.State.ToString();
                }
                catch
                {
                }

                try
                {
                    toneInstance.Stop();
                }
                catch
                {
                }

                await Task.Delay(PostSilenceMs);

                report.Add(string.Format("SoundEffectInstance.State at end of tone: {0}", stateAtEnd));
                report.Add("  (Playing = the app believes it played. Stopped/Paused = policy intervened.)");
                report.Add("");

                // ---- 4. Stop and analyse ------------------------------
                await StopQuietlyAsync(capture);

                ModeDetector.DisposeQuietly(capture);
                capture = null;

                // The capture must be disposed before the file is read back -
                // on this hardware a live MediaCapture can keep a lock on the
                // file it has just finished writing.
                AppendAnalysis(report, await ReadPcmAsync(probeFile), channels, toneHz);

                return Finish(report);
            }
            catch (Exception ex)
            {
                report.Add("FAILED: unexpected exception.");
                report.Add("  " + ex.Message);
                return Finish(report);
            }
            finally
            {
                if (pump != null)
                {
                    try
                    {
                        pump.Stop();
                        pump.Tick -= PumpTick;
                    }
                    catch
                    {
                    }
                }

                if (toneInstance != null)
                {
                    try
                    {
                        toneInstance.Dispose();
                    }
                    catch
                    {
                    }
                }

                ModeDetector.DisposeQuietly(capture);
            }
        }

        private static void PumpTick(object sender, EventArgs e)
        {
            try
            {
                FrameworkDispatcher.Update();
            }
            catch
            {
            }
        }

        #region Tone generation

        /// <summary>
        /// A mono 16-bit PCM sine at the given frequency, trimmed to a whole
        /// number of cycles so that looping it is seamless.
        /// </summary>
        private static byte[] BuildToneBuffer(int toneHz, int durationMs)
        {
            int requested = (SampleRateHz * durationMs) / 1000;

            // Trim to whole cycles. A partial final cycle makes the loop
            // point a discontinuity, which is an impulse, which spreads
            // energy across every frequency - including the one being
            // measured - once every loop.
            double samplesPerCycle = (double)SampleRateHz / toneHz;
            int cycles = (int)Math.Floor(requested / samplesPerCycle);
            if (cycles < 1)
            {
                cycles = 1;
            }

            int sampleCount = (int)Math.Round(cycles * samplesPerCycle);
            var bytes = new byte[sampleCount * BytesPerSample];

            double w = 2.0 * Math.PI * toneHz / SampleRateHz;

            // 0.9 rather than full scale: leaves headroom so that the
            // playback path's own gain staging cannot clip the tone before it
            // reaches the speaker.
            const double amplitude = 0.9 * short.MaxValue;

            for (int i = 0; i < sampleCount; i++)
            {
                var sample = (short)Math.Round(amplitude * Math.Sin(w * i));

                bytes[(i * 2) + 0] = (byte)(sample & 0xFF);
                bytes[(i * 2) + 1] = (byte)((sample >> 8) & 0xFF);
            }

            return bytes;
        }

        #endregion

        #region Analysis

        /// <summary>
        /// The recorded PCM payload, or null if the file could not be read or
        /// parsed. Reuses WavProbe's chunk walker rather than reimplementing
        /// it - MediaCapture's WAV header is not a fixed 44 bytes.
        /// </summary>
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

        private static void AppendAnalysis(
            List<string> report, PcmPayload pcm, int channels, int toneHz)
        {
            if (pcm == null)
            {
                report.Add("FAILED: the probe file could not be read back or had no data chunk.");
                return;
            }

            int bytesPerFrame = channels * BytesPerSample;
            int totalFrames = pcm.DataLength / bytesPerFrame;
            int blockFrames = (SampleRateHz * AnalysisBlockMs) / 1000;

            if (totalFrames < blockFrames)
            {
                report.Add(string.Format(
                    "FAILED: only {0} frames captured - too short to analyse.", totalFrames));
                return;
            }

            report.Add(string.Format(
                "Captured {0} frames ({1:0.00}s) across {2} channel(s).",
                totalFrames, (double)totalFrames / SampleRateHz, channels));
            report.Add("");
            report.Add(string.Format("Level at {0} Hz, per 100ms block, dBFS:", toneHz));

            var header = new StringBuilder();
            header.Append("  time  ");
            for (int ch = 0; ch < channels; ch++)
            {
                header.Append(string.Format("   ch{0}  ", ch));
            }
            report.Add(header.ToString());

            int blockCount = totalFrames / blockFrames;

            double bestMagnitude = -1.0;
            int bestBlock = -1;

            var blockLevels = new double[blockCount][];
            var blockPhases = new double[blockCount][];

            for (int b = 0; b < blockCount; b++)
            {
                int frameStart = b * blockFrames;

                blockLevels[b] = new double[channels];
                blockPhases[b] = new double[channels];

                var line = new StringBuilder();
                line.Append(string.Format("  {0,4}ms", b * AnalysisBlockMs));

                double blockPeak = 0.0;

                for (int ch = 0; ch < channels; ch++)
                {
                    double magnitude;
                    double phase;

                    Goertzel(
                        pcm, channels, ch, frameStart, blockFrames, toneHz,
                        out magnitude, out phase);

                    // Goertzel magnitude for a sinusoid of amplitude A over N
                    // samples is A*N/2, so this recovers the amplitude in raw
                    // sample units before converting to dBFS.
                    double amplitude = (2.0 * magnitude) / blockFrames;
                    double dbfs = 20.0 * Math.Log10(Math.Max(amplitude, 1e-9) / short.MaxValue);

                    blockLevels[b][ch] = dbfs;
                    blockPhases[b][ch] = phase;

                    if (amplitude > blockPeak)
                    {
                        blockPeak = amplitude;
                    }

                    line.Append(string.Format(" {0,7:0.0}", dbfs));
                }

                report.Add(line.ToString());

                if (blockPeak > bestMagnitude)
                {
                    bestMagnitude = blockPeak;
                    bestBlock = b;
                }
            }

            report.Add("");
            AppendVerdict(report, blockLevels, channels, bestBlock);

            if (channels > 1 && bestBlock >= 0)
            {
                AppendPhase(report, blockPhases[bestBlock], channels, toneHz, bestBlock);
            }
        }

        /// <summary>
        /// The whole point of the spike, stated in one line. The comparison
        /// is between the loudest block and the quietest - a real tone
        /// produces a large gap, a ducked or cancelled one does not.
        /// </summary>
        private static void AppendVerdict(
            List<string> report, double[][] blockLevels, int channels, int bestBlock)
        {
            if (bestBlock < 0)
            {
                report.Add("VERDICT: no blocks analysed.");
                return;
            }

            double loudest = double.NegativeInfinity;
            double quietest = double.PositiveInfinity;

            for (int b = 0; b < blockLevels.Length; b++)
            {
                for (int ch = 0; ch < channels; ch++)
                {
                    double v = blockLevels[b][ch];

                    if (v > loudest)
                    {
                        loudest = v;
                    }

                    if (v < quietest)
                    {
                        quietest = v;
                    }
                }
            }

            double range = loudest - quietest;

            report.Add(string.Format(
                "Loudest block {0:0.0} dBFS, quietest {1:0.0} dBFS, range {2:0.0} dB.",
                loudest, quietest, range));

            if (range >= 20.0)
            {
                report.Add("VERDICT: PASS - the tone is clearly present and clearly absent in the");
                report.Add("  silent blocks. Playback and capture coexist on this endpoint, and the");
                report.Add("  self-calibrating probe is worth building.");
            }
            else if (range >= 8.0)
            {
                report.Add("VERDICT: MARGINAL - the tone is detectable but weak against the noise");
                report.Add("  floor. Raise PlaybackVolume, lay the phone on a hard surface, and");
                report.Add("  re-run before drawing any conclusion.");
            }
            else
            {
                report.Add("VERDICT: FAIL - no meaningful level difference between the tone and");
                report.Add("  silent sections. Either playback was ducked while the capture was");
                report.Add("  live, or the driver cancelled the phone's own output. Check the");
                report.Add("  SoundEffectInstance.State line above to tell those two apart:");
                report.Add("  Playing means the app played it and the capture path removed it.");
            }
        }

        /// <summary>
        /// A first look at the measurement the real feature would be built
        /// on. Inter-channel phase at a known frequency converts directly to
        /// a time delay, and a time delay is what physical mic separation
        /// produces and what a beamformer removes.
        ///
        /// Only meaningful if the verdict above was PASS. Phase measured on
        /// noise is noise.
        /// </summary>
        private static void AppendPhase(
            List<string> report, double[] phases, int channels, int toneHz, int bestBlock)
        {
            double samplesPerCycle = (double)SampleRateHz / toneHz;

            report.Add("");
            report.Add(string.Format(
                "Inter-channel phase in the loudest block ({0}ms), relative to ch0:",
                bestBlock * AnalysisBlockMs));
            report.Add("  (positive = arrived LATER than ch0, i.e. further from the speaker.");
            report.Add("   Delay wraps beyond +/- half a cycle - at this frequency that is");
            report.Add(string.Format(
                "   +/-{0:0.0} samples, so treat anything near the limit as unresolved.)",
                samplesPerCycle / 2.0));

            for (int ch = 1; ch < channels; ch++)
            {
                double d = phases[ch] - phases[0];

                while (d > Math.PI)
                {
                    d -= 2.0 * Math.PI;
                }

                while (d <= -Math.PI)
                {
                    d += 2.0 * Math.PI;
                }

                // Negated deliberately. A signal that arrives LATER has a
                // more negative phase, so the raw phase difference carries
                // the opposite sign to the delay it represents. Without this
                // the report names the wrong channel as the near one - a
                // mistake that would look entirely plausible in the output.
                double delaySamples = -(d / (2.0 * Math.PI)) * samplesPerCycle;

                report.Add(string.Format(
                    "  ch{0} - ch0 : {1,7:0.00} rad  =  {2,6:0.00} samples  =  {3,6:0.000} ms",
                    ch, d, delaySamples, (delaySamples * 1000.0) / SampleRateHz));
            }

            report.Add("");
            report.Add("  Delays spread over several samples and differing per channel suggest");
            report.Add("  genuinely separated microphones. Everything clustered near zero");
            report.Add("  suggests time-aligned beamformer outputs.");
            report.Add("  One block proves nothing on its own - this is a preview of the");
            report.Add("  measurement, not a result.");
        }

        /// <summary>
        /// Single-bin DFT by Goertzel's method over one channel of an
        /// interleaved buffer. Chosen over an FFT because it needs no
        /// allocation, no library and about ten lines, while giving both the
        /// magnitude and the phase at the one frequency that matters.
        /// </summary>
        private static void Goertzel(
            PcmPayload pcm, int channels, int channel, int frameStart, int frameCount,
            int toneHz, out double magnitude, out double phase)
        {
            double w = 2.0 * Math.PI * toneHz / SampleRateHz;
            double cosw = Math.Cos(w);
            double sinw = Math.Sin(w);
            double coeff = 2.0 * cosw;

            double s1 = 0.0;
            double s2 = 0.0;

            int bytesPerFrame = channels * BytesPerSample;
            byte[] bytes = pcm.Bytes;

            for (int i = 0; i < frameCount; i++)
            {
                int offset = pcm.DataOffset
                    + ((frameStart + i) * bytesPerFrame)
                    + (channel * BytesPerSample);

                short sample = (short)(bytes[offset] | (bytes[offset + 1] << 8));

                double s0 = sample + (coeff * s1) - s2;
                s2 = s1;
                s1 = s0;
            }

            double real = s1 - (s2 * cosw);
            double imag = s2 * sinw;

            magnitude = Math.Sqrt((real * real) + (imag * imag));
            phase = Math.Atan2(imag, real);
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

        /// <summary>
        /// Same initialisation the detector uses, so the spike measures the
        /// path the app actually records through rather than some other one.
        /// </summary>
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

        private static string Finish(List<string> report)
        {
            var text = string.Join(Environment.NewLine, report.ToArray());

            try
            {
                ProbeLog.Append("Speaker probe spike:" + Environment.NewLine + text);
            }
            catch
            {
            }

            return text;
        }

        #endregion
    }
}
