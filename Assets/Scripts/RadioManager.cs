using System.Collections;
using NAudio.Wave;
using UnityEngine;
using UnityEngine.UI;
using TMPro;

/// <summary>
/// Manages radio functionality, including volume control and channel selection.
/// </summary>
public class RadioManager : MonoBehaviour
{

     /// <summary>
    /// The UI Slider element for controlling volume.
    /// </summary>
    public Slider volumeSlider;

    /// <summary>
    /// The UI Dropdown element for radio channel selection.
    /// </summary>
    public TMP_Dropdown radioDropdown; 

     /// <summary>
    /// Array of Icecast URLs for SomaFM radio channels.
    /// </summary>
    private string[] icecastUrls = {
        "http://ice3.somafm.com/defcon-128-mp3",  // DEFCON Radio
        "http://ice3.somafm.com/groovesalad-128-mp3",  // Groove Salad
        "http://ice3.somafm.com/dronezone-128-mp3",  // Drone Zone
        "http://ice3.somafm.com/indiepop-128-mp3"  // Indie Pop Rocks!
        // Add more URLs here
    };

    /// <summary>
    /// The MediaFoundationReader for audio processing.
    /// </summary>
    private MediaFoundationReader mediaFoundationReader;

      /// <summary>
    /// The WaveOutEvent for audio output.
    /// </summary>
    private WaveOutEvent waveOut;

    void Start()
    {
     
        if (volumeSlider != null)
        {
            volumeSlider.value = 1f; 
            volumeSlider.onValueChanged.AddListener(SetVolume);
        }

     
        if (radioDropdown != null)
        {
            radioDropdown.onValueChanged.AddListener(ChangeRadioStation);
            radioDropdown.value = 0; 
        }

        
        StartCoroutine(PlayRadio(icecastUrls[0]));
    }

    private IEnumerator PlayRadio(string url)
    {
        yield return null; 
        try
        {
            mediaFoundationReader = new MediaFoundationReader(url);
            waveOut = new WaveOutEvent();
            waveOut.Init(mediaFoundationReader);
            waveOut.Play();
        }
        catch (System.Exception ex)
        {
            Debug.LogError($"Error playing radio: {ex.Message}");
        }
    }

    void OnDestroy()
    {
        if (waveOut != null)
        {
            waveOut.Stop();
            waveOut.Dispose();
        }

        if (mediaFoundationReader != null)
        {
            mediaFoundationReader.Dispose();
        }
    }

    public void StopRadio()
    {
        if (waveOut != null)
        {
            waveOut.Stop();
        }
    }

    public void SetVolume(float volume)
    {
        if (waveOut != null)
        {
            waveOut.Volume = Mathf.Clamp01(volume);
        }
    }

    public float GetVolume()
    {
        return waveOut != null ? waveOut.Volume : 0;
    }


    // Do this shit later
   public void ChangeRadioStation(int dropdownIndex)
{
    if (dropdownIndex < 0 || dropdownIndex >= icecastUrls.Length)
    {
        Debug.LogError("Invalid dropdown index");
        return;
    }

    // Stop the current radio station and then play the new one after a delay
    StartCoroutine(ChangeRadioStationWithDelay(dropdownIndex));
}

private IEnumerator ChangeRadioStationWithDelay(int dropdownIndex)
{
    // Stop the current radio station
    StopRadio();

    yield return new WaitForSeconds(0.1f); // Wait for 100 milliseconds

    // Start playing the new radio station
    StartCoroutine(PlayRadio(icecastUrls[dropdownIndex]));
}
}
