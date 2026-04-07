using System;
using UnityEngine;

public interface IRadioStream : IDisposable
{
    void Play(string url);
    void Stop();
    float Volume { get; set; }
    event Action<string> OnError;
}
