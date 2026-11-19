using NAudio.CoreAudioApi;

namespace NotchWin.Services;

/// <summary>Reads and sets the system volume exactly (0–100), on whatever speakers/headphones are in use.</summary>
public static class Volume
{
    private static MMDevice Speakers() =>
        new MMDeviceEnumerator().GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);

    public static double Get()
    {
        try { return Speakers().AudioEndpointVolume.MasterVolumeLevelScalar * 100.0; }
        catch (Exception ex) { Log.Write("Volume: " + ex.Message); return 0; }
    }

    public static void Set(double percent)
    {
        try
        {
            var v = Speakers().AudioEndpointVolume;
            v.MasterVolumeLevelScalar = (float)(Math.Clamp(percent, 0, 100) / 100.0);
            if (percent > 0 && v.Mute) v.Mute = false; // "set volume to 40" should also unmute
        }
        catch (Exception ex) { Log.Write("Volume: " + ex.Message); }
    }
}
