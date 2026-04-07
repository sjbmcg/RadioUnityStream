using System;
using System.IO;
using System.IO.Pipelines;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using NLayer;

const string DefaultUrl = "http://ice3.somafm.com/groovesalad-128-mp3";
string url = args.Length > 0 ? args[0] : DefaultUrl;

Console.WriteLine($"Connecting to: {url}");
Console.WriteLine("Press Ctrl+C to stop.\n");

using var cts = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };

try
{
    using var client = new HttpClient();
    client.Timeout = Timeout.InfiniteTimeSpan;

    using var response = await client.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cts.Token);
    response.EnsureSuccessStatusCode();

    Console.WriteLine($"Connected. Status: {(int)response.StatusCode}");
    Console.WriteLine($"Content-Type: {response.Content.Headers.ContentType}\n");

    await using var httpStream = await response.Content.ReadAsStreamAsync(cts.Token);

    // Bridge async HTTP reads to NLayer's synchronous reader via a pipe
    var pipe = new Pipe();
    var fillTask = Task.Run(async () =>
    {
        try
        {
            var buffer = new byte[65536];
            int bytesRead;
            while ((bytesRead = await httpStream.ReadAsync(buffer, 0, buffer.Length, cts.Token)) > 0)
                await pipe.Writer.WriteAsync(buffer.AsMemory(0, bytesRead), cts.Token);
        }
        catch (OperationCanceledException) { }
        finally { await pipe.Writer.CompleteAsync(); }
    });

    using var reader = new MpegFile(new PositionTrackingStream(pipe.Reader.AsStream()));

    Console.WriteLine($"Sample rate : {reader.SampleRate} Hz");
    Console.WriteLine($"Channels    : {reader.Channels}");
    Console.WriteLine();

    var buffer2 = new float[reader.SampleRate * reader.Channels]; // 1 second worth
    long totalSamples = 0;
    int seconds = 0;

    while (!cts.Token.IsCancellationRequested)
    {
        int count = reader.ReadSamples(buffer2, 0, buffer2.Length);
        if (count == 0) break;

        totalSamples += count;
        int newSeconds = (int)(totalSamples / reader.Channels / reader.SampleRate);

        if (newSeconds > seconds)
        {
            seconds = newSeconds;
            float peak = 0f;
            for (int i = 0; i < count; i++)
                if (Math.Abs(buffer2[i]) > peak) peak = Math.Abs(buffer2[i]);

            Console.WriteLine($"[{seconds,4}s] samples decoded: {totalSamples,10} | peak amplitude: {peak:F4}");
        }
    }

    await fillTask;
    Console.WriteLine($"\nStopped. Total samples decoded: {totalSamples}");
}
catch (OperationCanceledException)
{
    Console.WriteLine("\nCancelled by user.");
}
catch (Exception ex)
{
    Console.WriteLine($"\nError ({ex.GetType().Name}): {ex.Message}");
    Console.WriteLine(ex.StackTrace);
}

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
