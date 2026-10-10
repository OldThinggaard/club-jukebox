namespace Jukebox.Core;

public sealed class JukeboxPaths
{
    public required string DataDirectory { get; init; }

    public string DatabasePath => Path.Combine(DataDirectory, "jukebox.db");
    public string Thumbnails => Path.Combine(DataDirectory, "thumbnails");
    public string Backups => Path.Combine(DataDirectory, "backups");
    public string Logs => Path.Combine(DataDirectory, "logs");
    public string Branding => Path.Combine(DataDirectory, "branding");
    public string Library => Path.Combine(DataDirectory, "library");
    public string OnlimeMedia => Path.Combine(DataDirectory, "onlime-sync", "media");
    public string OnlimeEvents => Path.Combine(DataDirectory, "onlime-sync", "events");
    public string OnlimeReklamer => Path.Combine(DataDirectory, "onlime-sync", "reklamer");
    public string OnlimeUpdates => Path.Combine(DataDirectory, "onlime-sync", "updates");
    public string Smb => Path.Combine(DataDirectory, "smb");
    public string LocalUpdates => Path.Combine(DataDirectory, "updates");
    public string Cache => Path.Combine(DataDirectory, "cache");
    public string WelcomeCard => Path.Combine(Cache, "welcome.jpg");
    public string PidFile => Path.Combine(DataDirectory, "host.pid");
    public string DisplaysFile => Path.Combine(DataDirectory, "displays.env");
    public string PlayRequest => Path.Combine(DataDirectory, "play.request");
    public string PlayDone => Path.Combine(DataDirectory, "play.done");
    public string PlayerStamp => Path.Combine(DataDirectory, "player.stamp");
    public string AudioEnv => Path.Combine(DataDirectory, "audio.env");

    public string LogoPath => Path.Combine(Branding, "logo.png");

    public void Ensure()
    {
        foreach (var directory in new[]
                 {
                     DataDirectory, Thumbnails, Backups, Logs, Branding, Library,
                     OnlimeMedia, OnlimeEvents, OnlimeReklamer, OnlimeUpdates, Smb, LocalUpdates, Cache
                 })
        {
            Directory.CreateDirectory(directory);
        }
    }

    public static string ResolveDataDirectory(string configured, string contentRoot)
    {
        if (Path.IsPathRooted(configured))
            return configured;
        return Path.GetFullPath(Path.Combine(contentRoot, configured));
    }
}
