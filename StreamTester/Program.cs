using System;
using System.IO;
using System.IO.Pipelines;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using NLayer;
using SharpJaad.AAC;
using SharpJaad.MP4;
using SharpJaad.MP4.API;

using var cts = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };

bool hlsMode = args.Length > 0 && args[0] == "--hls";

if (hlsMode)
{
    string hlsUrl = args.Length > 1 ? args[1] : "https://hls.somafm.com/hls/groovesalad/64k/program.m3u8";
    await TestHls(hlsUrl, cts.Token);
}
else
{
    string icecastUrl = args.Length > 0 ? args[0] : "http://ice3.somafm.com/groovesalad-128-mp3";
    await TestIcecast(icecastUrl, cts.Token);
}

// ── Icecast / MP3 test ────────────────────────────────────────────────────────

async Task TestIcecast(string url, CancellationToken token)
{
    Console.WriteLine($"Connecting to: {url}");
    Console.WriteLine("Press Ctrl+C to stop.\n");

    try
    {
        using var client = new HttpClient { Timeout = Timeout.InfiniteTimeSpan };
        using var response = await client.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, token);
        response.EnsureSuccessStatusCode();

        Console.WriteLine($"Connected. Status: {(int)response.StatusCode}");
        Console.WriteLine($"Content-Type: {response.Content.Headers.ContentType}\n");

        await using var httpStream = await response.Content.ReadAsStreamAsync(token);

        var pipe = new Pipe();
        var fillTask = Task.Run(async () =>
        {
            try
            {
                var buf = new byte[65536];
                int n;
                while ((n = await httpStream.ReadAsync(buf, 0, buf.Length, token)) > 0)
                    await pipe.Writer.WriteAsync(buf.AsMemory(0, n), token);
            }
            catch (OperationCanceledException) { }
            finally { await pipe.Writer.CompleteAsync(); }
        });

        using var reader = new MpegFile(new PositionTrackingStream(pipe.Reader.AsStream()));
        Console.WriteLine($"Sample rate : {reader.SampleRate} Hz");
        Console.WriteLine($"Channels    : {reader.Channels}\n");

        var buffer = new float[reader.SampleRate * reader.Channels];
        long totalSamples = 0;
        int seconds = 0;

        while (!token.IsCancellationRequested)
        {
            int count = reader.ReadSamples(buffer, 0, buffer.Length);
            if (count == 0) break;

            totalSamples += count;
            int newSeconds = (int)(totalSamples / reader.Channels / reader.SampleRate);
            if (newSeconds > seconds)
            {
                seconds = newSeconds;
                float peak = 0f;
                for (int i = 0; i < count; i++)
                    if (Math.Abs(buffer[i]) > peak) peak = Math.Abs(buffer[i]);
                Console.WriteLine($"[{seconds,4}s] samples: {totalSamples,10} | peak: {peak:F4}");
            }
        }

        await fillTask;
        Console.WriteLine($"\nStopped. Total samples: {totalSamples}");
    }
    catch (OperationCanceledException) { Console.WriteLine("\nCancelled."); }
    catch (Exception ex) { Console.WriteLine($"\nError ({ex.GetType().Name}): {ex.Message}\n{ex.StackTrace}"); }
}

// ── HLS / fMP4 / AAC test ─────────────────────────────────────────────────────

async Task TestHls(string m3u8Url, CancellationToken token)
{
    Console.WriteLine($"Fetching HLS playlist: {m3u8Url}\n");

    try
    {
        using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };

        string playlist = await client.GetStringAsync(m3u8Url, token);

        // Resolve master playlist to media playlist
        if (playlist.Contains("#EXT-X-STREAM-INF"))
        {
            var lines = playlist.Split('\n');
            for (int i = 0; i < lines.Length - 1; i++)
            {
                if (lines[i].TrimStart().StartsWith("#EXT-X-STREAM-INF"))
                {
                    string variantUrl = lines[i + 1].Trim();
                    if (!variantUrl.StartsWith("http"))
                        variantUrl = new Uri(new Uri(m3u8Url), variantUrl).ToString();
                    Console.WriteLine($"Variant: {variantUrl}");
                    playlist = await client.GetStringAsync(variantUrl, token);
                    m3u8Url = variantUrl;
                    break;
                }
            }
        }

        // Parse media playlist for init segment and first media segment
        string initUrl = null;
        string segUrl = null;
        foreach (var raw in playlist.Split('\n'))
        {
            var line = raw.Trim();
            if (line.StartsWith("#EXT-X-MAP:URI=\""))
                initUrl = line.Substring(16).TrimEnd('"');
            else if (!line.StartsWith("#") && line.Length > 0 && segUrl == null)
                segUrl = line;
        }

        var baseUri = new Uri(m3u8Url);
        if (initUrl != null && !initUrl.StartsWith("http"))
            initUrl = new Uri(baseUri, initUrl).ToString();
        if (segUrl != null && !segUrl.StartsWith("http"))
            segUrl = new Uri(baseUri, segUrl).ToString();

        Console.WriteLine($"Init segment : {initUrl ?? "(none)"}");
        Console.WriteLine($"First segment: {segUrl}\n");

        byte[] initBytes = initUrl != null ? await client.GetByteArrayAsync(initUrl, token) : Array.Empty<byte>();
        byte[] segBytes  = await client.GetByteArrayAsync(segUrl, token);
        Console.WriteLine($"Init: {initBytes.Length} bytes | Segment: {segBytes.Length} bytes");

        // Combine init + segment so SharpJaad sees a complete MP4 structure
        byte[] combined = new byte[initBytes.Length + segBytes.Length];
        Buffer.BlockCopy(initBytes, 0, combined, 0, initBytes.Length);
        Buffer.BlockCopy(segBytes, 0, combined, initBytes.Length, segBytes.Length);

        Console.WriteLine("Parsing with SharpJaad...\n");

        using var ms = new MemoryStream(combined);
        var container = new MP4Container(ms);
        var movie = container.GetMovie();
        var tracks = movie.GetTracks(AudioTrack.AudioCodec.AAC);

        Console.WriteLine($"AAC tracks found: {tracks.Count}");
        if (tracks.Count == 0) { Console.WriteLine("No AAC tracks!"); return; }

        var track = (AudioTrack)tracks[0];
        int sampleRate = (int)track.GetSampleRate();
        int channels   = track.GetChannelCount();
        Console.WriteLine($"Sample rate: {sampleRate} Hz");
        Console.WriteLine($"Channels   : {channels}\n");

        var decoder = new Decoder(track.GetDecoderSpecificInfo());
        var buf = new SampleBuffer();
        buf.SetBigEndian(false);

        long totalSamples = 0;
        float peak = 0f;

        while (track.HasMoreFrames() && !token.IsCancellationRequested)
        {
            var frame = track.ReadNextFrame();
            decoder.DecodeFrame(frame.GetData(), buf);

            int shortCount = buf.Data.Length / 2;
            for (int i = 0; i < shortCount; i++)
            {
                float s = BitConverter.ToInt16(buf.Data, i * 2) / 32768f;
                if (Math.Abs(s) > peak) peak = Math.Abs(s);
            }
            totalSamples += shortCount;
        }

        // SharpJaad reads the moov/track metadata but can't enumerate frames from fMP4 fragments.
        // Parse moof > traf > trun ourselves to extract sample sizes, then pull frames from mdat.
        var aacFrames = ExtractFMp4Frames(segBytes);
        Console.WriteLine($"fMP4 AAC frames found: {aacFrames.Count}");

        if (aacFrames.Count == 0) { Console.WriteLine("No frames extracted from fMP4 segment."); return; }

        var decoder2 = new Decoder(track.GetDecoderSpecificInfo());
        var buf2 = new SampleBuffer();
        buf2.SetBigEndian(false);

        long totalSamples2 = 0;
        float peak2 = 0f;

        foreach (var frame in aacFrames)
        {
            if (token.IsCancellationRequested) break;
            decoder2.DecodeFrame(frame, buf2);
            int shortCount = buf2.Data.Length / 2;
            for (int i = 0; i < shortCount; i++)
            {
                float s = BitConverter.ToInt16(buf2.Data, i * 2) / 32768f;
                if (Math.Abs(s) > peak2) peak2 = Math.Abs(s);
            }
            totalSamples2 += shortCount;
        }

        Console.WriteLine($"Total samples decoded: {totalSamples2}");
        Console.WriteLine($"Peak amplitude       : {peak2:F4}");
        Console.WriteLine(peak2 > 0 ? "\nSharpJaad decoded fMP4 successfully." : "\nWARNING: peak is 0 — silent output.");
    }
    catch (OperationCanceledException) { Console.WriteLine("\nCancelled."); }
    catch (Exception ex) { Console.WriteLine($"\nError ({ex.GetType().Name}): {ex.Message}\n{ex.StackTrace}"); }
}

// Parses moof > traf > trun to get sample sizes, then slices mdat into individual AAC frames.
static List<byte[]> ExtractFMp4Frames(byte[] seg)
{
    var frames = new List<byte[]>();
    int pos = 0;

    int moofStart = -1;
    int[] sampleSizes = null;
    int trunDataOffset = 0;

    while (pos + 8 <= seg.Length)
    {
        int boxSize = ReadInt32BE(seg, pos);
        string boxType = System.Text.Encoding.ASCII.GetString(seg, pos + 4, 4);

        if (boxType == "moof")
        {
            moofStart = pos;
            sampleSizes = ParseTrun(seg, pos + 8, pos + boxSize, out trunDataOffset);
        }
        else if (boxType == "mdat" && moofStart >= 0 && sampleSizes != null)
        {
            // trun data_offset is relative to the start of moof
            int dataStart = moofStart + trunDataOffset;
            int offset = dataStart;
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

static int[] ParseTrun(byte[] data, int start, int end, out int dataOffset)
{
    dataOffset = 0;
    int pos = start;

    while (pos + 8 <= end)
    {
        int boxSize = ReadInt32BE(data, pos);
        string boxType = System.Text.Encoding.ASCII.GetString(data, pos + 4, 4);

        if (boxType == "traf")
            return ParseTrun(data, pos + 8, pos + boxSize, out dataOffset);

        if (boxType == "trun")
        {
            int p = pos + 8;
            int version = data[p];
            int flags = (data[p + 1] << 16) | (data[p + 2] << 8) | data[p + 3];
            p += 4;

            int sampleCount = ReadInt32BE(data, p); p += 4;

            if ((flags & 0x001) != 0) { dataOffset = ReadInt32BE(data, p); p += 4; }
            if ((flags & 0x004) != 0) p += 4; // first sample flags

            bool hasDuration = (flags & 0x100) != 0;
            bool hasSize     = (flags & 0x200) != 0;
            bool hasFlags    = (flags & 0x400) != 0;
            bool hasCts      = (flags & 0x800) != 0;

            var sizes = new int[sampleCount];
            for (int i = 0; i < sampleCount; i++)
            {
                if (hasDuration) p += 4;
                if (hasSize) { sizes[i] = ReadInt32BE(data, p); p += 4; }
                if (hasFlags)  p += 4;
                if (hasCts)    p += 4;
            }
            return sizes;
        }

        if (boxSize < 8) break;
        pos += boxSize;
    }

    return Array.Empty<int>();
}

static int ReadInt32BE(byte[] d, int i) =>
    (d[i] << 24) | (d[i + 1] << 16) | (d[i + 2] << 8) | d[i + 3];

// NLayer reads stream.Position even on non-seekable streams, so we track it manually.
class PositionTrackingStream(Stream inner) : Stream
{
    private long _position;

    public override bool CanRead => true;
    public override bool CanSeek => false;
    public override bool CanWrite => false;
    public override long Length => throw new NotSupportedException();
    public override long Position { get => _position; set => throw new NotSupportedException(); }

    public override int Read(byte[] buffer, int offset, int count)
    {
        int read = inner.Read(buffer, offset, count);
        _position += read;
        return read;
    }

    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    public override void Flush() { }
}
