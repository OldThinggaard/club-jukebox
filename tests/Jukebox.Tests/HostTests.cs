using System.Net;
using System.Net.Http.Json;
using Jukebox.Core.Data;
using Jukebox.Host;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;

namespace Jukebox.Tests;

public class HostTests : IClassFixture<JukeboxFactory>
{
    private readonly JukeboxFactory _factory;

    public HostTests(JukeboxFactory factory) => _factory = factory;

    [Fact]
    public async Task Health_and_pin_gate_work()
    {
        var client = _factory.CreateClient();
        var health = await client.GetFromJsonAsync<HealthDto>("/api/health");
        Assert.Equal("ok", health!.Status);

        var denied = await client.GetAsync("/api/admin/overview");
        Assert.Equal(HttpStatusCode.Unauthorized, denied.StatusCode);

        var bad = await client.PostAsJsonAsync("/api/admin/login", new { pin = "0000" });
        Assert.Equal(HttpStatusCode.Unauthorized, bad.StatusCode);

        var good = await client.PostAsJsonAsync("/api/admin/login", new { pin = "2580" });
        Assert.Equal(HttpStatusCode.OK, good.StatusCode);
        var overview = await client.GetAsync("/api/admin/overview");
        Assert.Equal(HttpStatusCode.OK, overview.StatusCode);
    }

    [Fact]
    public async Task Install_wizard_refuses_to_wipe_a_disk_until_booted_from_the_usb()
    {
        var client = _factory.CreateClient();
        var state = await client.GetFromJsonAsync<InstallStateDto>("/api/install/state");
        Assert.False(state!.InstallerMode);

        var wipe = await client.PostAsJsonAsync("/api/install/run", new { path = "/dev/sda", confirm = "sda" });
        Assert.Equal(HttpStatusCode.Forbidden, wipe.StatusCode);
    }

    [Fact]
    public async Task Queue_accepts_a_ready_film_once()
    {
        var id = _factory.AddFilm("Coldplay", "Yellow");
        var client = _factory.CreateClient();
        var first = await client.PostAsJsonAsync("/api/queue", new { mediaId = id, origin = "user" });
        var firstBody = await first.Content.ReadFromJsonAsync<QueueDto>();
        Assert.True(firstBody!.Ok);
        var second = await client.PostAsJsonAsync("/api/queue", new { mediaId = id, origin = "mobile" });
        var secondBody = await second.Content.ReadFromJsonAsync<QueueDto>();
        Assert.False(secondBody!.Ok);

        var screen = await client.GetFromJsonAsync<ScreenDto>("/api/screen");
        Assert.Contains("Oak", screen!.ClubName);
    }

    private sealed record HealthDto(string Status);
    private sealed record InstallStateDto(bool InstallerMode);
    private sealed record QueueDto(bool Ok, string? Reason);
    private sealed record ScreenDto(string ClubName);
}

public sealed class JukeboxFactory : WebApplicationFactory<Program>
{
    private readonly string _directory = Directory.CreateTempSubdirectory("jukebox-host-").FullName;

    public JukeboxFactory()
    {
        Environment.SetEnvironmentVariable("Jukebox__DataDirectory", _directory);
        Environment.SetEnvironmentVariable("Jukebox__SeedSampleMedia", "false");
        Environment.SetEnvironmentVariable("Jukebox__WatchUsb", "false");
        Environment.SetEnvironmentVariable("Jukebox__IdleTimeoutSeconds", "3600");
        Environment.SetEnvironmentVariable("Jukebox__PlaybackMode", "timer");
        Environment.SetEnvironmentVariable("Jukebox__EnableBackground", "false");
    }

    public string AddFilm(string artist, string title)
    {
        using var scope = Services.CreateScope();
        var media = scope.ServiceProvider.GetRequiredService<MediaStore>();
        var file = $"{artist} - {title}.mp4";
        var folder = Path.Combine(_directory, "library");
        Directory.CreateDirectory(folder);
        File.WriteAllBytes(Path.Combine(folder, file), [0, 1, 2, 3]);
        return media.SaveIndexed(new IndexedSave
        {
            SourceId = "local-media",
            RelativePath = file,
            FileName = file,
            MediaType = "video",
            LibraryRole = "catalog",
            FileSize = 4,
            ModifiedUnix = 1,
            Resolved = new Jukebox.Core.Metadata.ResolvedMetadata { Title = title, Artist = artist, ArtistLetter = "C" },
            FileNameParts = new Jukebox.Core.Metadata.FileNameParts(artist, title, null),
            Ready = true
        }, []);
    }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        try { Directory.Delete(_directory, true); } catch { /* temp cleanup */ }
    }
}
