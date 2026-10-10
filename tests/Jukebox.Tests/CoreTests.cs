using Jukebox.Core;
using Jukebox.Core.Backup;
using Jukebox.Core.Data;
using Jukebox.Core.Domain;
using Jukebox.Core.Metadata;
using Jukebox.Core.Playback;
using Jukebox.Core.Queue;
using Jukebox.Core.Thumbnails;
using Jukebox.Core.Updates;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;

namespace Jukebox.Tests;

public class FilenameParserTests
{
    [Fact]
    public void Splits_artist_and_title()
    {
        var parts = FilenameParser.Parse("Queen - Bohemian Rhapsody.mp4");
        Assert.Equal("Queen", parts.Artist);
        Assert.Equal("Bohemian Rhapsody", parts.Title);
    }

    [Fact]
    public void Reads_an_en_dash_and_a_year()
    {
        var parts = FilenameParser.Parse("Queen – Radio Ga Ga (1984).mkv");
        Assert.Equal("Queen", parts.Artist);
        Assert.Equal("Radio Ga Ga", parts.Title);
        Assert.Equal(1984, parts.Year);
    }

    [Fact]
    public void Keeps_danish_letters()
    {
        var parts = FilenameParser.Parse("Kim Larsen - Papirsklip.mp3");
        Assert.Equal("Kim Larsen", parts.Artist);
        Assert.Equal("Papirsklip", parts.Title);
        Assert.Equal("K", ClubCulture.Letter(parts.Artist));
        Assert.Equal("Å", ClubCulture.Letter("Åse"));
    }
}

public class MetadataAndCategoryTests
{
    [Fact]
    public void Embedded_metadata_wins_over_the_filename()
    {
        var resolved = MetadataMerger.Resolve(
            new ProbeTags { Title = "Bohemian Rhapsody", Artist = "Queen", Genre = "Rock", Year = 1975 },
            FilenameParser.Parse("something-else.mp4"),
            null,
            "something-else.mp4");
        Assert.Equal("Queen", resolved.Artist);
        Assert.Equal("Bohemian Rhapsody", resolved.Title);
        Assert.Equal("70s", resolved.Decade);
    }

    [Fact]
    public void Administrator_override_wins_over_embedded_tags()
    {
        var resolved = MetadataMerger.Resolve(
            new ProbeTags { Title = "Track 01", Artist = "Unknown" },
            new FileNameParts("Queen", "Bohemian Rhapsody", 1975),
            new MetadataOverride { Title = "Bohemian Rhapsody", Artist = "Queen", Year = 1975 },
            "track.mp4");
        Assert.Equal("Queen", resolved.Artist);
        Assert.Equal("Bohemian Rhapsody", resolved.Title);
    }

    [Fact]
    public void Builds_genre_decade_and_live_categories()
    {
        var categories = Categorizer.Build("video", "Rock, Dansk", 1986, "Wembley Live", "Queen", "Live");
        Assert.Contains(categories, item => item.Name == "Music Videos");
        Assert.Contains(categories, item => item.Name == "80s");
        Assert.Contains(categories, item => item.Name == "Rock");
        Assert.Contains(categories, item => item.Name == "Live Concerts");
        Assert.Contains(categories, item => item.Name == "Danish");
    }
}

public class QueuePolicyTests
{
    [Fact]
    public void Blocks_the_current_film_the_queue_and_a_recent_play()
    {
        Assert.Equal(QueueBlock.NowPlaying, QueuePolicy.Evaluate(true, false, true, true, false, false, false, true));
        Assert.Equal(QueueBlock.AlreadyQueued, QueuePolicy.Evaluate(true, false, true, false, true, false, false, true));
        Assert.Equal(QueueBlock.Cooldown, QueuePolicy.Evaluate(true, false, true, false, false, true, false, true));
        Assert.Equal(QueueBlock.None, QueuePolicy.Evaluate(true, false, true, false, false, true, false, false));
    }
}

public class ThumbnailScorerTests
{
    [Fact]
    public void Prefers_a_detailed_frame_over_black()
    {
        using var black = new Image<Rgba32>(80, 80, Color.Black);
        using var noisy = new Image<Rgba32>(80, 80);
        noisy.ProcessPixelRows(accessor =>
        {
            for (var y = 0; y < accessor.Height; y++)
            {
                var row = accessor.GetRowSpan(y);
                for (var x = 0; x < row.Length; x++)
                    row[x] = new Rgba32((byte)(x * 3), (byte)(y * 2), (byte)(255 - x), 255);
            }
        });
        Assert.True(ThumbnailScorer.Score(noisy).Total > ThumbnailScorer.Score(black).Total);
    }
}

public class RetentionTests
{
    [Fact]
    public void Keeps_the_newest_daily_backups()
    {
        var records = Enumerable.Range(0, 9).Select(index => new BackupRecord
        {
            Id = index.ToString(),
            Kind = "daily",
            Path = index.ToString(),
            CreatedAt = $"2026-01-{index + 1:00}T00:00:00.0000000Z"
        }).ToList();
        var expired = RetentionPolicy.Expired(records);
        Assert.Equal(2, expired.Count);
        Assert.Contains(expired, item => item.Id == "0");
    }
}

public class DisplayParserTests
{
    [Fact]
    public void Assigns_the_touch_panel_and_the_television()
    {
        const string text = """
            eDP-1 connected primary 1280x800+0+0 (normal) 340mm x 190mm
            HDMI-1 connected 1920x1080+1280+0 (normal) 1600mm x 900mm
            """;
        var displays = DisplayAudioProbe.ParseXrandr(text);
        var (ui, playback) = DisplayAudioProbe.AutoAssign(displays);
        Assert.Equal("eDP-1", ui);
        Assert.Equal("HDMI-1", playback);
        Assert.Equal(1, DisplayAudioProbe.ScreenIndex(displays, "HDMI-1"));
    }

    [Fact]
    public void Names_the_speaker_and_the_headphone_jack()
    {
        const string text =
            "Sink #0\n" +
            "\tName: alsa_output.pci-0000_00_1f.3.analog-stereo\n" +
            "\tPorts:\n" +
            "\t\tanalog-output-speaker: Speakers (type: Speaker, priority: 10000, not available)\n" +
            "\t\tanalog-output-headphones: Headphones (type: Headphones, priority: 9900, availability unknown)\n" +
            "\tActive Port: analog-output-speaker\n" +
            "Sink #1\n" +
            "    Name: alsa_output.pci-0000_00_1f.3.hdmi-stereo\n" +
            "    Ports:\n" +
            "        hdmi-output-0: HDMI / DisplayPort (type: HDMI, priority: 5900, available)\n" +
            "    Active Port: hdmi-output-0\n";
        var devices = DisplayAudioProbe.ParseSinks(text);
        Assert.Equal(["Indbyggede højttalere", "Hovedtelefonudgang", "Lyd på HDMI"], devices.Select(item => item.Label).ToArray());
        var route = DisplayAudioProbe.Route(devices[1].Id);
        Assert.Equal("pulse/alsa_output.pci-0000_00_1f.3.analog-stereo", route.MpvDevice);
        Assert.Equal("analog-output-headphones", route.Port);
        Assert.Equal((null, null, null), DisplayAudioProbe.Route(""));
        Assert.Equal(devices[0].Id, DisplayAudioProbe.ResolveOutput("", devices));
        Assert.Equal(devices[0].Id, DisplayAudioProbe.ResolveOutput("speakers", devices));
        Assert.Equal(devices[1].Id, DisplayAudioProbe.ResolveOutput("headphones", devices));
        Assert.Equal(devices[1].Id, DisplayAudioProbe.ResolveOutput(devices[1].Id, devices));
        var onlyAnalog = DisplayAudioProbe.WithHeadphoneChoice([
            new AudioDevice("pulse/alsa_output.pci-0000_00_1f.3.analog-stereo", "Analog udgang", "pulse")
        ]);
        Assert.Contains(onlyAnalog, item => item.Label == "Hovedtelefonudgang");
        Assert.Equal("analog-output-headphones", DisplayAudioProbe.Route(DisplayAudioProbe.ResolveOutput("headphones", onlyAnalog)).Port);
    }
}

public class HallPlaybackTests
{
    [Fact]
    public void Writes_a_route_the_player_can_read()
    {
        var text = HallPlayback.AudioEnv("alsa_output.pci-0000_00_1f.3.analog-stereo", "analog-output-headphones", 140);
        Assert.Contains("SINK=alsa_output.pci-0000_00_1f.3.analog-stereo", text);
        Assert.Contains("PORT=analog-output-headphones", text);
        Assert.Contains("VOLUME=100", text);
        Assert.Equal("SINK=\nPORT=\nVOLUME=80\n", HallPlayback.AudioEnv("sink name", "port;rm", 80));
    }

    [Fact]
    public void Moves_on_a_few_seconds_after_a_real_song()
    {
        Assert.Equal(TimeSpan.FromMinutes(12) + TimeSpan.FromSeconds(8), HallPlayback.PlaybackCap(12 * 60 * 1000));
        Assert.Equal(TimeSpan.FromHours(4) + TimeSpan.FromSeconds(8), HallPlayback.PlaybackCap(4L * 60 * 60 * 1000, "video"));
        Assert.Equal(TimeSpan.FromHours(3), HallPlayback.PlaybackCap(10_000));
        Assert.Equal(TimeSpan.FromSeconds(14), HallPlayback.PlaybackCap(12_000, "image"));
    }

    [Fact]
    public void Done_file_matches_only_this_play()
    {
        var root = Directory.CreateTempSubdirectory("jukebox-play-");
        try
        {
            var done = Path.Combine(root.FullName, "play.done");
            HallPlayback.AtomicWrite(done, "other\n");
            Assert.False(HallPlayback.DoneIs(done, "this"));
            HallPlayback.AtomicWrite(done, "this\n");
            Assert.True(HallPlayback.DoneIs(done, "this"));
            var request = HallPlayback.Request("this", "/media/song.mp4", "video", 180_000, null);
            Assert.StartsWith("this\n/media/song.mp4\nvideo\n180000\n", request);
        }
        finally
        {
            root.Delete(true);
        }
    }
}

public class SearchSyntaxTests
{
    [Fact]
    public void Builds_a_prefix_query()
    {
        Assert.Equal("\"que\"*", SearchSyntax.ToFts("que"));
        Assert.Equal("\"queen\"* AND \"live\"*", SearchSyntax.ToFts("queen live"));
    }
}

public class VersionTests
{
    [Fact]
    public void Orders_semantic_versions()
    {
        Assert.True(VersionCompare.Compare("1.2.0", "1.1.9") > 0);
        Assert.Equal(0, VersionCompare.Compare("1.0.0", "1.0.0"));
    }
}

public class LibrarySearchTests
{
    [Fact]
    public void Finds_queen_from_a_partial_name()
    {
        using var fixture = new TempLibrary();
        fixture.Add("Queen", "Bohemian Rhapsody", "Rock", 1975);
        fixture.Add("Queen", "Radio Ga Ga", "Rock", 1984);
        fixture.Add("ABBA", "Dancing Queen", "Pop", 1976);
        var hits = fixture.Media.Query(new CatalogFilter { Text = "que", Limit = 10 });
        Assert.Equal(3, hits.Count);
        Assert.Contains(hits, item => item.Title == "Bohemian Rhapsody");
        Assert.Contains(hits, item => item.Artist == "ABBA");
        var artists = fixture.Media.Artists();
        Assert.Contains(artists, item => item.Name == "ABBA" && item.Letter == "A");
    }

    [Fact]
    public void Refuses_a_second_copy_in_the_queue()
    {
        using var fixture = new TempLibrary();
        var id = fixture.Add("Queen", "Bohemian Rhapsody", "Rock", 1975);
        var first = fixture.Queue.TryEnqueue(id, Origins.User, "touch");
        var second = fixture.Queue.TryEnqueue(id, Origins.Mobile, "phone");
        Assert.True(first.Ok);
        Assert.False(second.Ok);
        Assert.Equal("This is already in the queue.", second.Reason);
    }

    [Fact]
    public void Plays_an_advertisement_only_by_itself()
    {
        using var fixture = new TempLibrary();
        var id = fixture.AddAdvertisement("Sommerfest.mp4");
        var guest = fixture.Queue.TryEnqueue(id, Origins.User, "touch");
        var house = fixture.Queue.TryEnqueue(id, Origins.Auto, "reklame");
        Assert.False(guest.Ok);
        Assert.True(house.Ok);
    }
}

public sealed class TempLibrary : IDisposable
{
    public TempLibrary()
    {
        Root = Directory.CreateTempSubdirectory("jukebox-test-").FullName;
        var paths = new Jukebox.Core.JukeboxPaths { DataDirectory = Root };
        paths.Ensure();
        Database = new JukeboxDatabase(paths);
        Database.Migrate();
        Settings = new SettingsStore(Database);
        Settings.EnsureDefaults(new ClubSettings { CooldownMinutes = 30 }, "2580");
        Media = new MediaStore(Database);
        Queue = new QueueStore(Database, Media, Settings);
        Media.UpsertSource(new MediaSource
        {
            Id = "local",
            Name = "Local",
            Kind = "local",
            Path = paths.Library,
            Priority = 1,
            Enabled = true,
            Role = LibraryRoles.Catalog
        });
        Media.UpsertSource(new MediaSource
        {
            Id = "reklamer",
            Name = "Reklamer",
            Kind = "onlime",
            Path = Path.Combine(Root, "onlime-sync", "reklamer"),
            Priority = 1,
            Enabled = true,
            Role = LibraryRoles.Event
        });
    }

    public string Root { get; }
    public JukeboxDatabase Database { get; }
    public MediaStore Media { get; }
    public QueueStore Queue { get; }
    public SettingsStore Settings { get; }

    public string Add(string artist, string title, string genre, int year)
    {
        var file = $"{artist} - {title}.mp4";
        var resolved = MetadataMerger.Resolve(
            new ProbeTags { Title = title, Artist = artist, Genre = genre, Year = year, DurationMs = 12_000 },
            FilenameParser.Parse(file),
            null,
            file);
        return Save(file, LibraryRoles.Catalog, "local", artist, title, genre, year, resolved);
    }

    public string AddAdvertisement(string fileName)
    {
        var resolved = MetadataMerger.Resolve(new ProbeTags { Title = Path.GetFileNameWithoutExtension(fileName), DurationMs = 8_000 }, FilenameParser.Parse(fileName), null, fileName);
        return Save(fileName, LibraryRoles.Event, "reklamer", "", resolved.Title, "", null, resolved);
    }

    private string Save(string file, string role, string sourceId, string artist, string title, string genre, int? year, Jukebox.Core.Metadata.ResolvedMetadata resolved)
    {
        return Media.SaveIndexed(new IndexedSave
        {
            SourceId = sourceId,
            RelativePath = file,
            FileName = file,
            MediaType = MediaTypes.Video,
            LibraryRole = role,
            FileSize = 1000,
            ModifiedUnix = 1,
            SampleHash = "abc",
            Probe = new ProbeTags { Title = title, Artist = artist, Genre = genre, Year = year, DurationMs = 12_000 },
            FileNameParts = FilenameParser.Parse(file),
            Resolved = resolved,
            Ready = true
        }, Categorizer.Build(MediaTypes.Video, genre, year, title, artist, ""));
    }

    public void Dispose()
    {
        try { Directory.Delete(Root, true); } catch { /* temp cleanup */ }
    }
}
