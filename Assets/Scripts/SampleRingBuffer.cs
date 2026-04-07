using System;

/// <summary>
/// Thread-safe circular buffer for float audio samples.
/// Written by the background stream thread, read by Unity's audio callback thread.
/// </summary>
public class SampleRingBuffer
{
    private readonly float[] _buffer;
    private int _writePos;
    private int _readPos;
    private int _count;
    private readonly object _lock = new object();

    public SampleRingBuffer(int capacity)
    {
        _buffer = new float[capacity];
    }

    public int Available { get { lock (_lock) { return _count; } } }

    public void Write(float[] samples, int offset, int count)
    {
        lock (_lock)
        {
            for (int i = 0; i < count; i++)
            {
                _buffer[_writePos] = samples[offset + i];
                _writePos = (_writePos + 1) % _buffer.Length;

                if (_count < _buffer.Length)
                    _count++;
                else
                    _readPos = (_readPos + 1) % _buffer.Length; // overwrite oldest on overflow
            }
        }
    }

    public int Read(float[] dest, int offset, int count)
    {
        lock (_lock)
        {
            int toRead = Math.Min(count, _count);
            for (int i = 0; i < toRead; i++)
            {
                dest[offset + i] = _buffer[_readPos];
                _readPos = (_readPos + 1) % _buffer.Length;
                _count--;
            }
            return toRead;
        }
    }
}
