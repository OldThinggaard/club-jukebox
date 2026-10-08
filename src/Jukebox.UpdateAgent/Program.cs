using Jukebox.Core.Updates;

var data = Arg(args, "--data") ?? Environment.GetEnvironmentVariable("JUKEBOX_DATA_DIR") ?? "/var/lib/jukebox";
var app = Arg(args, "--app") ?? "/opt/jukebox/current";
var extra = Arg(args, "--sources");
var roots = new List<string>
{
    Path.Combine(data, "onlime-sync", "updates"),
    Path.Combine(data, "updates"),
    Path.Combine(data, "smb", "updates"),
    "/media",
    "/run/media"
};
if (!string.IsNullOrWhiteSpace(extra))
    roots.AddRange(extra.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));

return UpdateApplicator.ApplyIfAvailable(app, data, roots);

static string? Arg(string[] args, string name)
{
    var index = Array.IndexOf(args, name);
    return index >= 0 && index + 1 < args.Length ? args[index + 1] : null;
}
