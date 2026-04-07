using System;
using System.Collections.Generic;
using SharpJaad.AAC;
using SharpJaad.MP4;
using SharpJaad.MP4.API;

/// <summary>
/// Decodes AAC audio from fMP4 (CMAF) HLS segments using SharpJaad.
/// Handles the init segment + media segment pattern used by modern HLS streams.
/// HE-AAC (64k) is not supported by SharpJaad — use 128k or higher (AAC-LC).
/// </summary>
public class FMp4Decoder
{
    // ISO BMFF trun box flag bits (ISO 14496-12 §8.8.8.1)
    private const int TrunFlagDataOffset        = 0x001;
    private const int TrunFlagFirstSampleFlags  = 0x004;
    private const int TrunFlagSampleDuration    = 0x100;
    private const int TrunFlagSampleSize        = 0x200;
    private const int TrunFlagSampleFlags       = 0x400;
    private const int TrunFlagSampleCts         = 0x800;

    // Standard AAC-LC frame size in samples (ISO 14496-3)
    private const int AacFrameSamples = 1024;

    private Decoder _decoder;
    private readonly SampleBuffer _buffer = new SampleBuffer();
    private int _sampleRate;
    private int _channels;

    public int SampleRate => _sampleRate;
    public int Channels => _channels;
    public bool Initialised => _decoder != null;

    public FMp4Decoder()
    {
        _buffer.SetBigEndian(false);
    }

    /// <summary>
    /// Parses the init segment (.mp4) to configure the AAC decoder.
    /// Must be called once before DecodeSegment.
    /// </summary>
    public void LoadInitSegment(byte[] initBytes)
    {
        using var ms = new System.IO.MemoryStream(initBytes);
        var container = new MP4Container(ms);
        var movie = container.GetMovie();
        var tracks = movie.GetTracks(AudioTrack.AudioCodec.AAC);

        if (tracks.Count == 0)
            throw new InvalidOperationException("No AAC track found in init segment.");

        var track = (AudioTrack)tracks[0];
        _sampleRate = (int)track.GetSampleRate();
        _channels   = track.GetChannelCount();
        _decoder    = new Decoder(track.GetDecoderSpecificInfo());
    }

    /// <summary>
    /// Decodes one fMP4 media segment (.m4s) and returns interleaved float PCM samples.
    /// </summary>
    public float[] DecodeSegment(byte[] segBytes)
    {
        if (_decoder == null)
            throw new InvalidOperationException("Call LoadInitSegment before DecodeSegment.");

        var frames = ExtractFrames(segBytes);
        var result = new List<float>(frames.Count * AacFrameSamples);

        foreach (var frame in frames)
        {
            _decoder.DecodeFrame(frame, _buffer);
            int shortCount = _buffer.Data.Length / 2;
            for (int i = 0; i < shortCount; i++)
                result.Add(BitConverter.ToInt16(_buffer.Data, i * 2) / 32768f);
        }

        return result.ToArray();
    }

    // ── fMP4 box parsing ──────────────────────────────────────────────────────

    private static List<byte[]> ExtractFrames(byte[] seg)
    {
        var frames = new List<byte[]>();
        int pos = 0;
        int moofStart = -1;
        int[] sampleSizes = null;
        int trunDataOffset = 0;

        while (pos + 8 <= seg.Length)
        {
            int boxSize = ReadInt32(seg, pos);
            string type = System.Text.Encoding.ASCII.GetString(seg, pos + 4, 4);

            if (type == "moof")
            {
                moofStart     = pos;
                sampleSizes   = ParseTrun(seg, pos + 8, pos + boxSize, out trunDataOffset);
            }
            else if (type == "mdat" && moofStart >= 0 && sampleSizes != null)
            {
                int offset = moofStart + trunDataOffset;
                foreach (int size in sampleSizes)
                {
                    if (offset + size > seg.Length) break;
                    var frame = new byte[size];
                    Buffer.BlockCopy(seg, offset, frame, 0, size);
                    frames.Add(frame);
                    offset += size;
                }
            }

            if (boxSize < 8) break;
            pos += boxSize;
        }

        return frames;
    }

    private static int[] ParseTrun(byte[] data, int start, int end, out int dataOffset)
    {
        dataOffset = 0;
        int pos = start;

        while (pos + 8 <= end)
        {
            int boxSize = ReadInt32(data, pos);
            string type = System.Text.Encoding.ASCII.GetString(data, pos + 4, 4);

            if (type == "traf")
                return ParseTrun(data, pos + 8, pos + boxSize, out dataOffset);

            if (type == "trun")
            {
                int p = pos + 8;
                int flags = (data[p + 1] << 16) | (data[p + 2] << 8) | data[p + 3];
                p += 4;

                int count = ReadInt32(data, p); p += 4;

                if ((flags & TrunFlagDataOffset)       != 0) { dataOffset = ReadInt32(data, p); p += 4; }
                if ((flags & TrunFlagFirstSampleFlags)  != 0) p += 4;

                bool hasDuration = (flags & TrunFlagSampleDuration) != 0;
                bool hasSize     = (flags & TrunFlagSampleSize)     != 0;
                bool hasFlags    = (flags & TrunFlagSampleFlags)    != 0;
                bool hasCts      = (flags & TrunFlagSampleCts)      != 0;

                var sizes = new int[count];
                for (int i = 0; i < count; i++)
                {
                    if (hasDuration) p += 4;
                    if (hasSize) { sizes[i] = ReadInt32(data, p); p += 4; }
                    if (hasFlags)   p += 4;
                    if (hasCts)     p += 4;
                }
                return sizes;
            }

            if (boxSize < 8) break;
            pos += boxSize;
        }

        return Array.Empty<int>();
    }

    private static int ReadInt32(byte[] d, int i) =>
        (d[i] << 24) | (d[i + 1] << 16) | (d[i + 2] << 8) | d[i + 3];
}
