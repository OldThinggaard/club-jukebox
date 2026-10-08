namespace Jukebox.Host;

public sealed class JukeboxConfig
{
    public string DataDirectory { get; set; } = "data";
    public string PublicBaseUrl { get; set; } = "http://jukebox.local:47123";
    public bool SeedSampleMedia { get; set; } = true;
    public bool WatchUsb { get; set; } = true;
    public string PlaybackMode { get; set; } = "auto";
    public int IdleTimeoutSeconds { get; set; } = 90;
    public string DefaultPin { get; set; } = "2580";
    public bool EnableBackground { get; set; } = true;
    public bool InstallerMode { get; set; }
}
