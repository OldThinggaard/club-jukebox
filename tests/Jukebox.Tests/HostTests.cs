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

        var open = await client.PostAsync("/api/install/open-onlime", null);
        Assert.Equal(HttpStatusCode.OK, open.StatusCode);
        var opened = await open.Content.ReadFromJsonAsync<OpenOnlimeDto>();
        Assert.False(opened!.Opened);

        var adminOnlime = await client.PostAsJsonAsync("/api/admin/onlime", new { token = "copied-from-onlime", device = "videojukebox" });
        Assert.Equal(HttpStatusCode.Unauthorized, adminOnlime.StatusCode);
        var adminWifi = await client.GetAsync("/api/admin/wifi");
        Assert.Equal(HttpStatusCode.Unauthorized, adminWifi.StatusCode);
    }

    [Fact]
    public async Task Usb_package_install_is_refused_away_from_the_club_machine()
    {
        var client = _factory.CreateClient();
        var anon = await client.GetAsync("/api/admin/package");
        Assert.Equal(HttpStatusCode.Unauthorized, anon.StatusCode);

        var login = await client.PostAsJsonAsync("/api/admin/login", new { pin = "2580" });
        Assert.Equal(HttpStatusCode.OK, login.StatusCode);
        var status = await client.GetFromJsonAsync<PackageDto>("/api/admin/package");
        Assert.NotNull(status);
        Assert.False(status!.CanInstall);
        Assert.False(status.Found);
        Assert.Contains("forhåndsvisning", status.Message);

        var post = await client.PostAsync("/api/admin/package", null);
        Assert.Equal(HttpStatusCode.Forbidden, post.StatusCode);
    }

    [Fact]
    public void Usb_find_lines_say_whether_the_stick_was_seen()
    {
        var ready = UsbPackage.ReadFindResult(0, "ready /mnt/jukebox-usb/sdb1/videojukebox.deb");
        Assert.True(ready.Found);
        Assert.Contains("klar", ready.Message);

        var seen = UsbPackage.ReadFindResult(0, "seen");
        Assert.False(seen.Found);
        Assert.Contains("sat i", seen.Message);
        Assert.Contains("ligger ikke", seen.Message);

        var unread = UsbPackage.ReadFindResult(0, "unread");
        Assert.False(unread.Found);
        Assert.Contains("kunne ikke læses", unread.Message);

        var none = UsbPackage.ReadFindResult(0, "none");
        Assert.False(none.Found);
        Assert.Contains("Ingen USB-nøgle", none.Message);
    }

    [Fact]
    public async Task Onlime_sync_install_stays_off_the_preview()
    {
        var client = _factory.CreateClient();
        var anon = await client.GetAsync("/api/admin/onlime/status");
        Assert.Equal(HttpStatusCode.Unauthorized, anon.StatusCode);

        var login = await client.PostAsJsonAsync("/api/admin/login", new { pin = "2580" });
        Assert.Equal(HttpStatusCode.OK, login.StatusCode);
        var status = await client.GetFromJsonAsync<OnlimeStatusDto>("/api/admin/onlime/status");
        Assert.NotNull(status);
        if (!File.Exists("/usr/bin/jotta-cli"))
        {
            Assert.False(status!.Installed);
            Assert.False(status.CanInstall);
            Assert.Contains("forhåndsvisning", status.Message);
        }

        if (!File.Exists("/opt/jukebox/lib/onlime-install.sh"))
        {
            var post = await client.PostAsync("/api/admin/onlime/install", null);
            Assert.Equal(HttpStatusCode.Forbidden, post.StatusCode);
        }
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
    private sealed record OpenOnlimeDto(bool Opened);
    private sealed record QueueDto(bool Ok, string? Reason);
    private sealed record ScreenDto(string ClubName);
    private sealed record PackageDto(bool Found, bool CanInstall, string Message);
    private sealed record OnlimeStatusDto(bool Installed, bool CanInstall, string Message);
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
