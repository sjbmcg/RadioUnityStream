using System;
using System.Collections.Generic;
using System.IO;
using SharpJaad.AAC;
using UnityEngine;

/// <summary>
/// Decodes AAC audio from MPEG-TS HLS segments (stream_type 0x0F).
/// Pure C# — no external TS library. Parses PAT/PMT to find the audio PID,
/// reassembles PES payloads, strips ADTS headers, and decodes with SharpJaad.
/// </summary>
public class TsAudioDecoder
{
    // ISO 13818-1 stream type for AAC in ADTS framing
    private const int StreamTypeAacAdts = 0x0F;

    // ISO 14496-3 §1.6.5.1 — indexed by sampling_frequency_index
    private static readonly int[] AdtsSampleRates =
    {
        96000, 88200, 64000, 48000, 44100, 32000,
        24000, 22050, 16000, 12000, 11025, 8000, 7350
    };

    private Decoder _aacDecoder;
    private readonly SampleBuffer _buffer = new SampleBuffer();
    private int _audioPid = -1;
    private int _sampleRate;
    private int _channels;

    public int SampleRate  => _sampleRate;
    public int Channels    => _channels;
    /// <summary>True once the audio PID has been found and the first ADTS frame decoded.</summary>
    public bool Initialised => _sampleRate > 0;

    public TsAudioDecoder()
    {
        _buffer.SetBigEndian(false);
    }

    /// <summary>
    /// Returns true if data looks like MPEG-TS: sync byte 0x47 at byte 0
    /// and (if long enough) at byte 188.
    /// </summary>
    public static bool IsTsData(byte[] data) =>
        data.Length >= 188 && data[0] == 0x47 &&
        (data.Length < 376 || data[188] == 0x47);

    /// <summary>
    /// Decodes one MPEG-TS HLS segment and returns interleaved float PCM.
    /// Audio PID is discovered automatically on the first call via PAT/PMT.
    /// SampleRate and Channels are populated after the first successful decode.
    /// </summary>
    public float[] DecodeSegment(byte[] seg)
    {
        var packets = ParsePackets(seg);

        if (_audioPid < 0)
            _audioPid = FindAudioPid(seg, packets);

        if (_audioPid < 0)
        {
            Debug.LogWarning("[TsAudioDecoder] No AAC stream (0x0F) found in MPEG-TS PMT.");
            return Array.Empty<float>();
        }

        byte[] pesPayload = AssemblePesPayload(seg, packets, _audioPid);
        return DecodeAdts(pesPayload);
    }

    // ── TS packet parsing ─────────────────────────────────────────────────────

    private readonly struct TsPacket
    {
        public readonly int  Pid;
        public readonly bool Pusi;           // payload_unit_start_indicator
        public readonly int  PayloadOffset;  // byte offset into the segment array
        public readonly int  PayloadLength;

        public TsPacket(int pid, bool pusi, int offset, int length)
        {
            Pid = pid; Pusi = pusi; PayloadOffset = offset; PayloadLength = length;
        }
    }

    private static List<TsPacket> ParsePackets(byte[] seg)
    {
        var list = new List<TsPacket>(seg.Length / 188);
        int pos = 0;

        while (pos + 188 <= seg.Length)
        {
            if (seg[pos] != 0x47) { pos++; continue; }

            bool pusi = (seg[pos + 1] & 0x40) != 0;
            int  pid  = ((seg[pos + 1] & 0x1F) << 8) | seg[pos + 2];
            int  afc  = (seg[pos + 3] >> 4) & 0x03;  // adaptation_field_control

            int payloadStart = pos + 4;

            if (afc == 0x02) { pos += 188; continue; }  // adaptation only, no payload

            if (afc == 0x03)                             // adaptation + payload
            {
                int afLen = seg[payloadStart];
                payloadStart += 1 + afLen;
            }

            int payloadLen = pos + 188 - payloadStart;
            if (payloadLen > 0)
                list.Add(new TsPacket(pid, pusi, payloadStart, payloadLen));

            pos += 188;
        }

        return list;
    }

    // ── PAT / PMT parsing ─────────────────────────────────────────────────────

    private static int FindAudioPid(byte[] seg, List<TsPacket> packets)
    {
        int pmtPid = FindPmtPid(seg, packets);
        if (pmtPid < 0) return -1;
        return FindAudioPidFromPmt(seg, packets, pmtPid);
    }

    private static int FindPmtPid(byte[] seg, List<TsPacket> packets)
    {
        foreach (var pkt in packets)
        {
            if (pkt.Pid != 0 || !pkt.Pusi) continue;

            // PSI packets start with a pointer field
            int off = pkt.PayloadOffset;
            off += 1 + seg[off];                          // skip pointer field
            if (seg[off] != 0x00) continue;               // table_id must be 0x00 (PAT)

            int sectionLen = ((seg[off + 1] & 0x0F) << 8) | seg[off + 2];
            int sectionEnd = off + 3 + sectionLen - 4;    // -4 excludes CRC
            off += 8;                                     // skip fixed PAT header

            while (off + 4 <= sectionEnd)
            {
                int programNumber = (seg[off] << 8) | seg[off + 1];
                int pmtPid        = ((seg[off + 2] & 0x1F) << 8) | seg[off + 3];
                if (programNumber != 0)
                    return pmtPid;
                off += 4;
            }
        }
        return -1;
    }

    private static int FindAudioPidFromPmt(byte[] seg, List<TsPacket> packets, int pmtPid)
    {
        foreach (var pkt in packets)
        {
            if (pkt.Pid != pmtPid || !pkt.Pusi) continue;

            int off = pkt.PayloadOffset;
            off += 1 + seg[off];                          // skip pointer field
            if (seg[off] != 0x02) continue;               // table_id must be 0x02 (PMT)

            int sectionLen      = ((seg[off + 1] & 0x0F) << 8) | seg[off + 2];
            int sectionEnd      = off + 3 + sectionLen - 4;
            int programInfoLen  = ((seg[off + 10] & 0x0F) << 8) | seg[off + 11];
            off += 12 + programInfoLen;                   // skip to first ES entry

            while (off + 5 <= sectionEnd)
            {
                int streamType  = seg[off];
                int esPid       = ((seg[off + 1] & 0x1F) << 8) | seg[off + 2];
                int esInfoLen   = ((seg[off + 3] & 0x0F) << 8) | seg[off + 4];

                if (streamType == StreamTypeAacAdts)
                    return esPid;

                off += 5 + esInfoLen;
            }
        }
        return -1;
    }

    // ── PES assembly ──────────────────────────────────────────────────────────

    private static byte[] AssemblePesPayload(byte[] seg, List<TsPacket> packets, int audioPid)
    {
        using var ms = new MemoryStream();

        foreach (var pkt in packets)
        {
            if (pkt.Pid != audioPid) continue;

            int dataStart = pkt.PayloadOffset;

            // PES packets start with a 3-byte start code, stream ID, length, and header.
            // Skip the PES header on the first TS packet of each PES unit. (ISO 13818-1 §2.4.3.6)
            if (pkt.Pusi && pkt.PayloadLength >= 9)
            {
                int pesHeaderDataLen = seg[pkt.PayloadOffset + 8];
                dataStart += 9 + pesHeaderDataLen;
            }

            int dataEnd = pkt.PayloadOffset + pkt.PayloadLength;
            if (dataStart < dataEnd)
                ms.Write(seg, dataStart, dataEnd - dataStart);
        }

        return ms.ToArray();
    }

    // ── ADTS frame parsing + SharpJaad decoding ───────────────────────────────

    private float[] DecodeAdts(byte[] data)
    {
        var result = new List<float>();
        int pos = 0;

        while (pos + 7 <= data.Length)
        {
            // ADTS sync word: first 12 bits are all 1 (0xFFF)
            if (data[pos] != 0xFF || (data[pos + 1] & 0xF0) != 0xF0)
            {
                pos++;
                continue;
            }

            // protection_absent (bit 0 of byte 1): 1 = no CRC → 7-byte header, 0 = CRC → 9-byte
            bool protectionAbsent = (data[pos + 1] & 0x01) != 0;
            int  headerSize       = protectionAbsent ? 7 : 9;

            if (pos + headerSize > data.Length) break;

            // Frame length spans bits 30-43 of the ADTS header (includes header bytes)
            int frameLength = ((data[pos + 3] & 0x03) << 11) |
                               (data[pos + 4]          <<  3) |
                               (data[pos + 5]          >>  5);

            if (frameLength < headerSize || pos + frameLength > data.Length) break;

            // Initialise SharpJaad on the first frame by building a 2-byte AudioSpecificConfig
            if (_aacDecoder == null)
            {
                int audioObjectType   = ((data[pos + 2] >> 6) & 0x03) + 1;  // profile_ObjectType + 1
                int samplingFreqIndex =  (data[pos + 2] >> 2) & 0x0F;
                int channelConfig     = ((data[pos + 2] & 0x01) << 2) | ((data[pos + 3] >> 6) & 0x03);

                _sampleRate = samplingFreqIndex < AdtsSampleRates.Length
                    ? AdtsSampleRates[samplingFreqIndex] : 44100;
                _channels   = channelConfig == 0 ? 2 : channelConfig;

                // ISO 14496-3 §1.6.5 AudioSpecificConfig (2 bytes)
                byte asc0 = (byte)((audioObjectType << 3) | (samplingFreqIndex >> 1));
                byte asc1 = (byte)(((samplingFreqIndex & 0x01) << 7) | (channelConfig << 3));
                _aacDecoder = new Decoder(new[] { asc0, asc1 });
            }

            var frame = new byte[frameLength - headerSize];
            Buffer.BlockCopy(data, pos + headerSize, frame, 0, frame.Length);

            _aacDecoder.DecodeFrame(frame, _buffer);
            int shortCount = _buffer.Data.Length / 2;
            for (int i = 0; i < shortCount; i++)
                result.Add(BitConverter.ToInt16(_buffer.Data, i * 2) / 32768f);

            pos += frameLength;
        }

        return result.ToArray();
    }
}
