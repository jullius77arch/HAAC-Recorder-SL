using System;
using System.Collections.Generic;
using System.Runtime.InteropServices.WindowsRuntime;
using System.Threading.Tasks;
using Windows.Storage;

namespace HAAC_Recorder_SL
{
    /// <summary>
    /// Result of analyzing a probe recording's channels: how many were
    /// genuinely distinct, which (if any) were completely silent, and which
    /// (if any) duplicated an earlier channel.
    ///
    /// Carrying this much detail — rather than a bare distinct-count — is
    /// what lets the detection log say exactly why a candidate was rejected
    /// instead of only that it was.
    /// </summary>
    public sealed class ChannelAnalysis
    {
        public readonly int RequestedChannels;
        public readonly int DistinctCount;
        public readonly bool[] IsEmpty;

        // DuplicateOf[i] is the index of the earlier channel that channel i
        // is byte-identical to, or -1 if channel i isn't a duplicate.
        public readonly int[] DuplicateOf;

        // False when the probe file couldn't be read back or parsed at all,
        // as opposed to being read fine and simply failing verification.
        // Kept separate so the log can tell "we couldn't check" apart from
        // "we checked and it failed".
        public readonly bool ReadOk;

        public ChannelAnalysis(
            int requestedChannels, int distinctCount, bool[] isEmpty, int[] duplicateOf, bool readOk)
        {
            this.RequestedChannels = requestedChannels;
            this.DistinctCount = distinctCount;
            this.IsEmpty = isEmpty;
            this.DuplicateOf = duplicateOf;
            this.ReadOk = readOk;
        }

        /// <summary>
        /// A mode passes only when every requested channel came back
        /// distinct AND none of them was digital silence. The silence half
        /// matters: two genuinely independent but empty channels are
        /// byte-identical, so without it a phantom channel that never
        /// carries signal would pass as real.
        /// </summary>
        public bool Verified
        {
            get
            {
                if (!this.ReadOk || this.DistinctCount != this.RequestedChannels)
                {
                    return false;
                }

                if (this.IsEmpty != null)
                {
                    for (int i = 0; i < this.IsEmpty.Length; i++)
                    {
                        if (this.IsEmpty[i])
                        {
                            return false;
                        }
                    }
                }

                return true;
            }
        }

        public string DescribeOutcome()
        {
            if (!this.ReadOk)
            {
                return "rejected - probe recording could not be read back.";
            }

            if (this.Verified)
            {
                return string.Format(
                    "verified - all {0} channels distinct and non-silent.", this.RequestedChannels);
            }

            var reasons = new List<string>();

            if (this.IsEmpty != null)
            {
                for (int i = 0; i < this.IsEmpty.Length; i++)
                {
                    if (this.IsEmpty[i])
                    {
                        reasons.Add("channel " + i + " is silent (all-zero)");
                    }
                }
            }

            if (this.DuplicateOf != null)
            {
                for (int i = 0; i < this.DuplicateOf.Length; i++)
                {
                    // Don't also report a silent channel as a duplicate: two
                    // silent channels are byte-identical to each other, so
                    // without this check every empty channel past the first
                    // would list both reasons for the same problem.
                    bool alreadyReportedEmpty = this.IsEmpty != null && this.IsEmpty[i];

                    if (this.DuplicateOf[i] >= 0 && !alreadyReportedEmpty)
                    {
                        reasons.Add("channel " + i + " is identical to channel " + this.DuplicateOf[i]);
                    }
                }
            }

            if (reasons.Count == 0)
            {
                reasons.Add(string.Format(
                    "only {0} of {1} requested channels came back distinct",
                    this.DistinctCount, this.RequestedChannels));
            }

            return "rejected - " + string.Join("; ", reasons) + ".";
        }
    }

    /// <summary>
    /// Reads back a just-recorded multi-channel WAV and works out whether it
    /// holds genuine independent content, rather than trusting that a
    /// successful StartRecordToStorageFileAsync call means anything by
    /// itself. It doesn't: two separate failure modes hide behind a
    /// successful start, and both are checked for here.
    ///
    ///   1. Duplication - a device accepts a 2-channel request and silently
    ///      writes one mono signal into both channels, or accepts 4 while
    ///      only delivering 2 distinct signals.
    ///   2. Silence - a channel exists in the file but carries nothing at
    ///      all. This is what let a phantom 3rd channel on the Lumia 1520
    ///      pass verification before the check existed.
    ///
    /// Uses only WinRT storage APIs, all of which a Windows Phone 8.1
    /// Silverlight app can call through interop, so this came across from
    /// the WinRT build with no changes beyond being lifted out of MainPage.
    /// </summary>
    public static class WavProbe
    {
        private const int BytesPerSample = 2; // 16-bit

        public static async Task<ChannelAnalysis> AnalyzeChannelsAsync(StorageFile file, int channels)
        {
            var isEmpty = new bool[Math.Max(channels, 0)];
            var duplicateOf = new int[Math.Max(channels, 0)];
            for (int i = 0; i < duplicateOf.Length; i++)
            {
                duplicateOf[i] = -1;
            }

            try
            {
                if (channels < 1)
                {
                    return new ChannelAnalysis(channels, 0, isEmpty, duplicateOf, false);
                }

                var buffer = await FileIO.ReadBufferAsync(file);
                byte[] bytes = buffer.ToArray();

                int dataOffset;
                int dataLength;
                if (!TryFindWavDataChunk(bytes, out dataOffset, out dataLength))
                {
                    return new ChannelAnalysis(channels, 0, isEmpty, duplicateOf, false);
                }

                int bytesPerFrame = channels * BytesPerSample;
                int frameCount = dataLength / bytesPerFrame;

                if (frameCount == 0)
                {
                    return new ChannelAnalysis(channels, 0, isEmpty, duplicateOf, false);
                }

                // Silence check. Deliberately exact - every sample byte must
                // be 0x00 - rather than a near-zero tolerance, matching the
                // byte-exact philosophy the duplicate check relies on.
                for (int ch = 0; ch < channels; ch++)
                {
                    bool allZero = true;

                    for (int frame = 0; frame < frameCount && allZero; frame++)
                    {
                        int sampleStart = dataOffset + (frame * bytesPerFrame) + (ch * BytesPerSample);

                        if (bytes[sampleStart] != 0 || bytes[sampleStart + 1] != 0)
                        {
                            allZero = false;
                        }
                    }

                    isEmpty[ch] = allZero;
                }

                // Duplicate check. A channel counts as a duplicate if it
                // matches any earlier channel that isn't itself already a
                // duplicate, so a "4-channel" file that is really two signals
                // each written twice reports 2 distinct channels, not 4.
                var isDuplicate = new bool[channels];

                for (int i = 1; i < channels; i++)
                {
                    for (int j = 0; j < i; j++)
                    {
                        if (isDuplicate[j])
                        {
                            continue;
                        }

                        bool identical = true;

                        for (int frame = 0; frame < frameCount; frame++)
                        {
                            int frameStart = dataOffset + (frame * bytesPerFrame);
                            int offsetI = frameStart + (i * BytesPerSample);
                            int offsetJ = frameStart + (j * BytesPerSample);

                            if (bytes[offsetI] != bytes[offsetJ] ||
                                bytes[offsetI + 1] != bytes[offsetJ + 1])
                            {
                                // One differing sample is enough to prove
                                // these two aren't a copy.
                                identical = false;
                                break;
                            }
                        }

                        if (identical)
                        {
                            isDuplicate[i] = true;
                            duplicateOf[i] = j;
                            break;
                        }
                    }
                }

                int distinct = 0;
                for (int i = 0; i < channels; i++)
                {
                    if (!isDuplicate[i])
                    {
                        distinct++;
                    }
                }

                return new ChannelAnalysis(channels, distinct, isEmpty, duplicateOf, true);
            }
            catch
            {
                // If the file can't be read back for any reason, don't claim
                // anything was verified. ReadOk = false makes the caller fall
                // back rather than trust an unconfirmed result.
                return new ChannelAnalysis(channels, 0, isEmpty, duplicateOf, false);
            }
        }

        /// <summary>
        /// Walks a WAV file's RIFF chunk structure to find the "data" chunk
        /// rather than assuming a fixed 44-byte header. MediaEncodingProfile's
        /// WAV writer doesn't guarantee that exact size, and a wrong
        /// assumption here would silently compare the wrong bytes.
        /// </summary>
        public static bool TryFindWavDataChunk(byte[] bytes, out int dataOffset, out int dataLength)
        {
            dataOffset = 0;
            dataLength = 0;

            // RIFF header: "RIFF" (4) + size (4) + "WAVE" (4) = 12 bytes.
            if (bytes.Length < 12 ||
                bytes[0] != (byte)'R' || bytes[1] != (byte)'I' || bytes[2] != (byte)'F' || bytes[3] != (byte)'F' ||
                bytes[8] != (byte)'W' || bytes[9] != (byte)'A' || bytes[10] != (byte)'V' || bytes[11] != (byte)'E')
            {
                return false;
            }

            int offset = 12;
            while (offset + 8 <= bytes.Length)
            {
                bool isData =
                    bytes[offset] == (byte)'d' && bytes[offset + 1] == (byte)'a' &&
                    bytes[offset + 2] == (byte)'t' && bytes[offset + 3] == (byte)'a';

                int chunkSize = bytes[offset + 4]
                    | (bytes[offset + 5] << 8)
                    | (bytes[offset + 6] << 16)
                    | (bytes[offset + 7] << 24);

                int chunkDataStart = offset + 8;

                if (isData)
                {
                    dataOffset = chunkDataStart;
                    dataLength = Math.Min(chunkSize, bytes.Length - chunkDataStart);
                    return dataLength > 0;
                }

                // Chunks are padded to even byte boundaries.
                int advance = chunkSize + (chunkSize % 2);
                offset = chunkDataStart + advance;
            }

            return false;
        }
    }
}
