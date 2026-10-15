using Jukebox.Core;
using Jukebox.Core.Backup;
using Jukebox.Core.Data;
using Jukebox.Core.Domain;
using Jukebox.Core.Indexing;
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
    public void Clean_marker_and_the_text_after_it_leave_the_title()
    {
        Assert.Equal("Song Name", TitleCleaner.Strip("Song Name (Clean) Official Video"));
        Assert.Equal("Song Name", TitleCleaner.Strip("Song Name ( clean )"));
        Assert.Equal("Song Name", TitleCleaner.Strip("Song Name (Dirty) Official Video"));
        Assert.Equal("Song Name", TitleCleaner.Strip("Song Name ( dirty )"));
        Assert.Equal("Song Name", TitleCleaner.Strip("Song Name (XXX) Live"));
        Assert.Equal("Song Name", TitleCleaner.Strip("Song Name ( xxx )"));
        Assert.Equal("Song Name (Clean Version)", TitleCleaner.Strip("Song Name (Clean Version)"));
        Assert.Equal("Song Name (Dirty Version)", TitleCleaner.Strip("Song Name (Dirty Version)"));
        Assert.Equal("Bohemian Rhapsody", TitleCleaner.Strip("Bohemian Rhapsody"));
        var resolved = MetadataMerger.Resolve(
            new ProbeTags { Title = "Song Name (Clean) Official Video", Artist = "Queen" },
            FilenameParser.Parse("Queen - Song Name (Clean) Official Video.mp4"),
            null,
            "Queen - Song Name (Clean) Official Video.mp4");
        Assert.Equal("Song Name", resolved.Title);
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
        Assert.Equal(1, categories.Count(item => item.Name.Equals("Danish", StringComparison.OrdinalIgnoreCase)));

        var tagged = Categorizer.Build("video", "Danish", 1986, "Dansevise", "Grethe", "");
        var fromTitle = Categorizer.Build("video", "Pop", 1986, "Danish song", "Grethe", "");
        Assert.Equal(1, tagged.Count(item => item.Name.Equals("Danish", StringComparison.OrdinalIgnoreCase)));
        Assert.Equal(1, fromTitle.Count(item => item.Name.Equals("Danish", StringComparison.OrdinalIgnoreCase)));
        Assert.Equal("genre", tagged.Single(item => item.Name == "Danish").Kind);
        Assert.Equal(tagged.Single(item => item.Name == "Danish").Kind, fromTitle.Single(item => item.Name == "Danish").Kind);
    }

    [Fact]
    public void Existing_danish_chips_fold_into_one()
    {
        using var fixture = new TempLibrary();
        var song = fixture.Add("Grethe", "Dansevise", "Danish", 1986);
        var now = ClubCulture.Iso(DateTime.UtcNow);
        fixture.Database.InWrite((connection, tx) =>
        {
            JukeboxDatabase.Execute(connection, """
                INSERT INTO categories(id, name, slug, kind, is_auto, is_hidden, created_at)
                VALUES ('rule-danish', 'Danish', 'rule-danish', 'rule', 1, 0, $now);
                """, tx, ("$now", now));
            JukeboxDatabase.Execute(connection, """
                INSERT INTO media_categories(media_id, category_id, origin) VALUES ($song, 'rule-danish', 'auto');
                """, tx, ("$song", song));
        });

        fixture.Media.FoldDanishTags();
        fixture.Media.FoldDanishTags();

        var chips = fixture.Media.Categories(true).Where(item => item.Name.Equals("Danish", StringComparison.OrdinalIgnoreCase)).ToList();
        Assert.Single(chips);
        Assert.Equal("genre-danish", chips[0].Slug);
        Assert.Contains(chips[0].Id, fixture.Media.CategoryIds(song));

        using var again = new TempLibrary();
        var first = again.Add("Kim Larsen", "Papirsklip", "Pop", 1983);
        again.Database.InWrite((connection, tx) =>
        {
            JukeboxDatabase.Execute(connection, """
                INSERT INTO categories(id, name, slug, kind, is_auto, is_hidden, created_at)
                VALUES ('only-rule', 'Danish', 'rule-danish', 'rule', 1, 0, $now);
                """, tx, ("$now", now));
            JukeboxDatabase.Execute(connection, """
                INSERT OR IGNORE INTO media_categories(media_id, category_id, origin) VALUES ($song, 'only-rule', 'auto');
                """, tx, ("$song", first));
        });
        again.Media.FoldDanishTags();
        var second = again.Add("Grethe", "Danish night", "Rock", 1986);
        var folded = again.Media.Categories(true).Where(item => item.Name.Equals("Danish", StringComparison.OrdinalIgnoreCase)).ToList();
        Assert.Single(folded);
        Assert.Equal("genre-danish", folded[0].Slug);
        Assert.Contains(folded[0].Id, again.Media.CategoryIds(first));
        Assert.Contains(folded[0].Id, again.Media.CategoryIds(second));
    }

    [Fact]
    public void Decade_spellings_share_one_chip()
    {
        Assert.Equal("80s", ClubCulture.CanonicalDecade("80s"));
        Assert.Equal("80s", ClubCulture.CanonicalDecade("80's"));
        Assert.Equal("80s", ClubCulture.CanonicalDecade("80’s"));
        Assert.Equal("80s", ClubCulture.CanonicalDecade("80´s"));
        Assert.Equal("80s", ClubCulture.CanonicalDecade("1980's"));
        Assert.Equal("90s", ClubCulture.CanonicalDecade("90´s"));
        Assert.Equal("2000s", ClubCulture.CanonicalDecade("2000's"));
        Assert.Equal("2010s", ClubCulture.CanonicalDecade("2010s"));
        Assert.Null(ClubCulture.CanonicalDecade("Rock"));
        Assert.Null(ClubCulture.CanonicalDecade("80s Rock"));

        var categories = Categorizer.Build("video", "80's, Rock", 1986, "Radio Ga Ga", "Queen", "");
        Assert.Equal(1, categories.Count(item => item.Name == "80s" && item.Kind == "decade"));
        Assert.DoesNotContain(categories, item => item.Name.Contains('\''));
        Assert.Contains(categories, item => item.Name == "Rock" && item.Kind == "genre");

        var nineties = Categorizer.Build("video", "90´s", 1991, "Enjoy the Silence", "Depeche Mode", "");
        Assert.Equal(1, nineties.Count(item => item.Name == "90s"));
    }

    [Fact]
    public void Existing_decade_spellings_fold_into_one_chip()
    {
        using var fixture = new TempLibrary();
        var eighties = fixture.Add("Queen", "Radio Ga Ga", "Rock", 1984);
        var nineties = fixture.Add("Depeche Mode", "Enjoy the Silence", "Pop", 1990);
        var thousands = fixture.Add("Daft Punk", "One More Time", "Dance", 0);
        var now = ClubCulture.Iso(DateTime.UtcNow);
        fixture.Database.InWrite((connection, tx) =>
        {
            JukeboxDatabase.Execute(connection, """
                INSERT INTO categories(id, name, slug, kind, is_auto, is_hidden, created_at)
                VALUES ('alias-80', '80''s', 'genre-80-s', 'genre', 1, 0, $now),
                       ('alias-90a', '90´s', 'genre-90-acute', 'genre', 1, 0, $now),
                       ('alias-90b', '90’s', 'genre-90-curly', 'genre', 1, 0, $now),
                       ('alias-2000', '2000''s', 'genre-2000-s', 'genre', 1, 0, $now);
                """, tx, ("$now", now));
            JukeboxDatabase.Execute(connection, """
                INSERT INTO media_categories(media_id, category_id, origin) VALUES
                    ($a, 'alias-80', 'auto'),
                    ($b, 'alias-90a', 'auto'),
                    ($b, 'alias-90b', 'auto'),
                    ($c, 'alias-2000', 'auto');
                """, tx, ("$a", eighties), ("$b", nineties), ("$c", thousands));
        });

        fixture.Media.FoldDecadeTags();
        fixture.Media.FoldDecadeTags();

        var names = fixture.Media.Categories(true).Select(item => item.Name).ToList();
        Assert.Equal(1, names.Count(name => name == "80s"));
        Assert.Equal(1, names.Count(name => name == "90s"));
        Assert.DoesNotContain(names, name => name is "80's" or "90´s" or "90’s");
        Assert.Contains(names, name => name == "Rock");
        Assert.Contains(names, name => name == "Pop");

        var eightiesChip = fixture.Media.Categories(true).Single(item => item.Name == "80s").Id;
        var ninetiesChip = fixture.Media.Categories(true).Single(item => item.Name == "90s").Id;
        var thousandsChip = fixture.Media.Categories(true).Single(item => item.Name == "2000s");
        Assert.Equal("decade", thousandsChip.Kind);
        Assert.Contains(eightiesChip, fixture.Media.CategoryIds(eighties));
        Assert.Contains(ninetiesChip, fixture.Media.CategoryIds(nineties));
        Assert.Contains(thousandsChip.Id, fixture.Media.CategoryIds(thousands));
        Assert.Contains(names, name => name == "Dance");
    }
}

public class UsbSongImportTests
{
    [Fact]
    public void Copies_songs_from_the_stick_and_leaves_other_files_alone()
    {
        var root = Directory.CreateTempSubdirectory("jukebox-usb-").FullName;
        try
        {
            var stick = Path.Combine(root, "sdb1");
            var library = Path.Combine(root, "library");
            var onlime = Path.Combine(root, "onlime", "Queen - Stay.mp4");
            Directory.CreateDirectory(Path.Combine(stick, "Queen"));
            Directory.CreateDirectory(Path.Combine(stick, ".hidden"));
            Directory.CreateDirectory(Path.Combine(stick, "$RECYCLE.BIN"));
            Directory.CreateDirectory(Path.GetDirectoryName(onlime)!);
            File.WriteAllBytes(Path.Combine(stick, "Queen", "Radio Ga Ga.mp4"), [1, 2, 3, 4]);
            File.WriteAllBytes(Path.Combine(stick, "Enjoy the Silence.mp3"), [5, 6, 7, 8]);
            File.WriteAllBytes(Path.Combine(stick, "cover.jpg"), [9, 9, 9]);
            File.WriteAllBytes(Path.Combine(stick, "videojukebox.deb"), [8, 8, 8, 8]);
            File.WriteAllBytes(Path.Combine(stick, ".hidden", "Secret.mp4"), [7, 7, 7, 7]);
            File.WriteAllBytes(Path.Combine(stick, "$RECYCLE.BIN", "Gone.mp4"), [6, 6, 6, 6]);
            File.WriteAllBytes(onlime, [4, 4, 4, 4]);
            Directory.CreateDirectory(library);
            File.WriteAllBytes(Path.Combine(library, "Queen - Old.mp4"), [3, 3, 3, 3]);

            var hub = Path.Combine(root, "hub");
            Directory.CreateDirectory(hub);
            var volumes = UsbSongImport.Volumes([hub, stick], new HashSet<string>([stick], StringComparer.Ordinal));
            Assert.Equal(stick, Assert.Single(volumes));

            var files = UsbSongImport.List(stick);
            Assert.Equal(2, files.Count);
            Assert.Contains(files, file => file.RelativePath == Path.Combine("Queen", "Radio Ga Ga.mp4"));
            Assert.Contains(files, file => file.RelativePath == "Enjoy the Silence.mp3");

            var first = UsbSongImport.Copy(files, library, () => 50_000, 0);
            Assert.Equal(2, first.Added);
            Assert.Equal(0, first.Replaced);
            Assert.Equal([1, 2, 3, 4], File.ReadAllBytes(Path.Combine(library, "Queen", "Radio Ga Ga.mp4")));
            Assert.Equal([4, 4, 4, 4], File.ReadAllBytes(onlime));
            Assert.Equal([3, 3, 3, 3], File.ReadAllBytes(Path.Combine(library, "Queen - Old.mp4")));

            File.WriteAllBytes(Path.Combine(stick, "Queen", "Radio Ga Ga.mp4"), [9, 8, 7, 6, 5]);
            var second = UsbSongImport.Copy(UsbSongImport.List(stick), library, () => 50_000, 0);
            Assert.Equal(0, second.Added);
            Assert.Equal(1, second.Replaced);
            Assert.Equal(1, second.Unchanged);
            Assert.Equal([9, 8, 7, 6, 5], File.ReadAllBytes(Path.Combine(library, "Queen", "Radio Ga Ga.mp4")));

            var third = UsbSongImport.Copy(UsbSongImport.List(stick), library, () => 50_000, 0);
            Assert.Equal(0, third.Added);
            Assert.Equal(0, third.Replaced);
            Assert.Equal(2, third.Unchanged);
            Assert.Equal("Sangene ligger der allerede. Du kan tage nøglen ud.", UsbSongImport.DoneMessage(third));
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    [Fact]
    public void Stops_when_the_disk_is_full_or_the_stick_goes_away()
    {
        var root = Directory.CreateTempSubdirectory("jukebox-usb-full-").FullName;
        try
        {
            var stick = Path.Combine(root, "stick");
            var library = Path.Combine(root, "library");
            Directory.CreateDirectory(stick);
            File.WriteAllBytes(Path.Combine(stick, "One.mp4"), [1, 2, 3, 4]);
            File.WriteAllBytes(Path.Combine(stick, "Two.mp4"), new byte[200]);
            var files = UsbSongImport.List(stick);
            var full = UsbSongImport.Copy(files, library, () => 100, 0);
            Assert.True(full.DiskFull);
            Assert.Equal(1, full.Added);
            Assert.False(File.Exists(Path.Combine(library, "Two.mp4")));
            Assert.Contains("Disken er fuld", UsbSongImport.DoneMessage(full));

            var missing = new UsbSongImport.SongFile(Path.Combine(stick, "Gone.mp4"), "Gone.mp4");
            var pulled = UsbSongImport.Copy([missing], library, () => 50_000, 0);
            Assert.True(pulled.StickRemoved);
            Assert.Contains("Nøglen blev taget ud", UsbSongImport.DoneMessage(pulled));

            var outside = Path.Combine(root, "outside.mp4");
            File.WriteAllBytes(outside, [1, 1, 1, 1]);
            var escaped = UsbSongImport.Copy([new UsbSongImport.SongFile(outside, "../outside.mp4")], library, () => 50_000, 0);
            Assert.Equal(0, escaped.Added);
            Assert.Equal([1, 1, 1, 1], File.ReadAllBytes(outside));
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    [Fact]
    public void Idle_text_tells_whether_the_stick_has_songs()
    {
        Assert.Contains("Ingen USB-nøgle", UsbSongImport.IdleMessage(0, 0, false));
        Assert.Contains("ingen sange", UsbSongImport.IdleMessage(1, 0, false));
        Assert.Contains("1 sang", UsbSongImport.IdleMessage(1, 1, false));
        Assert.Contains("kunne ikke læses", UsbSongImport.IdleMessage(1, 0, true));
        Assert.Contains("1 af 4", UsbSongImport.BusyMessage(1, 4));
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
        Assert.Equal("SINK=\nPORT=analog-output-speaker\nVOLUME=40\n", HallPlayback.RouteFile("speakers", 40));
        Assert.Equal("SINK=\nPORT=analog-output-headphones\nVOLUME=12\n", HallPlayback.RouteFile("headphones", 12));
    }

    [Fact]
    public void Moves_on_a_few_seconds_after_a_real_song()
    {
        Assert.Equal(TimeSpan.FromMinutes(12) + TimeSpan.FromSeconds(8), HallPlayback.PlaybackCap(12 * 60 * 1000));
        Assert.Equal(TimeSpan.FromHours(4) + TimeSpan.FromSeconds(8), HallPlayback.PlaybackCap(4L * 60 * 60 * 1000, "video"));
        Assert.Equal(TimeSpan.FromHours(3), HallPlayback.PlaybackCap(10_000));
        Assert.Equal(TimeSpan.FromSeconds(14), HallPlayback.PlaybackCap(12_000, "image"));
        Assert.False(HallPlayback.EndIsBelievable(0));
        Assert.False(HallPlayback.EndIsBelievable(4_999));
        Assert.True(HallPlayback.EndIsBelievable(5_000));
        Assert.True(HallPlayback.StillInTheMiddle(40_000, 180_000));
        Assert.False(HallPlayback.StillInTheMiddle(175_000, 180_000));
        Assert.False(HallPlayback.StillInTheMiddle(-1, 180_000));
        Assert.False(HallPlayback.StillInTheMiddle(10_000, 15_000));
        Assert.False(HallPlayback.StuckBeforeItPlays(10_000, 0));
        Assert.False(HallPlayback.StuckBeforeItPlays(45_000, -1));
        Assert.False(HallPlayback.StuckBeforeItPlays(45_000, 5_000));
        Assert.True(HallPlayback.StuckBeforeItPlays(45_000, 500));
        Assert.True(HallPlayback.SameSongTooSoon("song", "song", 1_000));
        Assert.False(HallPlayback.SameSongTooSoon("song", "other", 1_000));
        Assert.False(HallPlayback.SameSongTooSoon("song", "song", 180_000));
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
            var clock = Path.Combine(root.FullName, "play.clock");
            HallPlayback.AtomicWrite(clock, "other\n1000\n180000\n1\n");
            Assert.False(HallPlayback.ClockEnded(clock, "this"));
            HallPlayback.AtomicWrite(clock, "this\n180000\n180000\n1\n");
            Assert.True(HallPlayback.ClockEnded(clock, "this"));
            Assert.Equal(180_000, HallPlayback.ClockDurationMs(clock, "this"));
            Assert.Equal(180_000, HallPlayback.ClockPositionMs(clock, "this"));
            Assert.Equal(-1, HallPlayback.ClockPositionMs(clock, "other"));
            Assert.Equal(0, HallPlayback.ClockDurationMs(clock, "other"));
            var request = HallPlayback.Request("this", "/media/song.mp4", "video", 180_000, null);
            Assert.StartsWith("this\n/media/song.mp4\nvideo\n180000\n", request);
        }
        finally
        {
            root.Delete(true);
        }
    }
}

public class LiveVolumeTests
{
    [Fact]
    public void Turns_off_the_idle_jack_and_sets_the_one_in_use()
    {
        var root = Directory.CreateTempSubdirectory("jukebox-vol-");
        var log = Path.Combine(root.FullName, "amixer.log");
        File.WriteAllText(Path.Combine(root.FullName, "cards"), " 0 [PCH]: HDA Intel\n");
        var wpctl = Path.Combine(root.FullName, "wpctl");
        var amixer = Path.Combine(root.FullName, "amixer");
        File.WriteAllText(wpctl, "#!/bin/sh\nprintf '%s\\n' '  * analog-output-headphones: Headphones' '    analog-output-speaker: Speakers'\n");
        File.WriteAllText(amixer, "#!/bin/sh\nprintf '%s\\n' \"$*\" >> \"$AMIXER_LOG\"\n");
        File.SetUnixFileMode(wpctl, UnixFileMode.UserRead | UnixFileMode.UserExecute);
        File.SetUnixFileMode(amixer, UnixFileMode.UserRead | UnixFileMode.UserExecute);
        var previousPath = Environment.GetEnvironmentVariable("PATH");
        var previousScript = Environment.GetEnvironmentVariable("JUKEBOX_VOLUME_SCRIPT");
        var previousCards = Environment.GetEnvironmentVariable("JUKEBOX_CARDS");
        var previousLog = Environment.GetEnvironmentVariable("AMIXER_LOG");
        Environment.SetEnvironmentVariable("PATH", root.FullName + ":" + previousPath);
        Environment.SetEnvironmentVariable("JUKEBOX_VOLUME_SCRIPT", "/workspace/deploy/scripts/mpv-volume.py");
        Environment.SetEnvironmentVariable("JUKEBOX_CARDS", Path.Combine(root.FullName, "cards"));
        Environment.SetEnvironmentVariable("AMIXER_LOG", log);
        try
        {
            var report = LiveVolume.Apply(root.FullName, 40);
            var seen = File.ReadAllText(log);
            Assert.True(report.Alsa);
            Assert.True(report.Pipe);
            Assert.False(report.Socket);
            Assert.Contains("sset Headphone 40% unmute", seen);
            Assert.Contains("sset Speaker 0% mute", seen);
            Assert.DoesNotContain("sset Speaker 40% unmute", seen);
            Assert.DoesNotContain("sset Headphone 0% mute", seen);
        }
        finally
        {
            Environment.SetEnvironmentVariable("PATH", previousPath);
            Environment.SetEnvironmentVariable("JUKEBOX_VOLUME_SCRIPT", previousScript);
            Environment.SetEnvironmentVariable("JUKEBOX_CARDS", previousCards);
            Environment.SetEnvironmentVariable("AMIXER_LOG", previousLog);
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
    public void The_next_page_holds_the_songs_after_the_first()
    {
        using var fixture = new TempLibrary();
        fixture.Add("ABBA", "Dancing Queen", "Pop", 1976);
        fixture.Add("House", "Filler", "Pop", 2000);
        fixture.Add("Queen", "Bohemian Rhapsody", "Rock", 1975);
        var first = fixture.Media.Query(new CatalogFilter { Limit = 2 });
        var rest = fixture.Media.Query(new CatalogFilter { Limit = 2, Offset = 2 });
        Assert.Equal(2, first.Count);
        Assert.Equal(["ABBA", "House"], first.Select(item => item.Artist).ToArray());
        Assert.Single(rest);
        Assert.Equal("Queen", rest[0].Artist);
    }

    [Fact]
    public void Stops_when_the_queue_reaches_the_chosen_number()
    {
        using var fixture = new TempLibrary();
        fixture.Settings.Set(SettingKeys.MaxQueueLength, "2");
        var first = fixture.Add("Queen", "Bohemian Rhapsody", "Rock", 1975);
        var second = fixture.Add("ABBA", "Dancing Queen", "Pop", 1976);
        var third = fixture.Add("House", "Filler", "Pop", 2000);
        Assert.True(fixture.Queue.TryEnqueue(first, Origins.User, "touch").Ok);
        Assert.True(fixture.Queue.TryEnqueue(second, Origins.User, "touch").Ok);
        var blocked = fixture.Queue.TryEnqueue(third, Origins.User, "touch");
        Assert.False(blocked.Ok);
        Assert.Equal("The queue is full. Wait for a film to finish.", blocked.Reason);
        Assert.Equal(2, fixture.Settings.Get().MaxQueueLength);
        Assert.True(fixture.Settings.Get().ShowQrOnHall);
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
    public void A_chosen_song_starts_without_waiting_for_the_pause_picture()
    {
        using var fixture = new TempLibrary();
        fixture.Settings.Set(SettingKeys.PauseSeconds, "60");
        var id = fixture.Add("Queen", "Bohemian Rhapsody", "Rock", 1975);
        File.WriteAllBytes(Path.Combine(fixture.Root, "library", "Queen - Bohemian Rhapsody.mp4"), [1]);
        Assert.True(fixture.Queue.TryEnqueue(id, Origins.User, "touch").Ok);
        var coordinator = new PlaybackCoordinator(fixture.Queue, fixture.Media, fixture.Settings);
        var job = coordinator.ClaimNext(0, null, 8);
        Assert.NotNull(job);
        Assert.Equal(Origins.User, job!.Entry.Origin);
    }

    [Fact]
    public void The_pause_picture_stays_until_its_own_time_has_passed()
    {
        using var fixture = new TempLibrary();
        fixture.Settings.Set(SettingKeys.PauseSeconds, "1");
        fixture.Settings.Set(SettingKeys.IdleStrategy, "least");
        var id = fixture.Add("Queen", "Bohemian Rhapsody", "Rock", 1975);
        File.WriteAllBytes(Path.Combine(fixture.Root, "library", "Queen - Bohemian Rhapsody.mp4"), [1]);
        var coordinator = new PlaybackCoordinator(fixture.Queue, fixture.Media, fixture.Settings);
        Assert.Null(coordinator.ClaimNext(0, null, 8));
        Assert.Null(coordinator.ClaimNext(0, null, 8));
        Thread.Sleep(1200);
        var job = coordinator.ClaimNext(0, null, 8);
        Assert.NotNull(job);
        Assert.Equal(Origins.Auto, job!.Entry.Origin);
        Assert.Equal(id, job.Entry.MediaId);
    }

    [Fact]
    public void Hides_the_song_until_the_player_is_chosen()
    {
        using var fixture = new TempLibrary();
        var id = fixture.Add("Queen", "Bohemian Rhapsody", "Rock", 1975);
        var file = Path.Combine(fixture.Root, "library", "Queen - Bohemian Rhapsody.mp4");
        File.WriteAllBytes(file, [1, 2, 3]);
        Assert.True(fixture.Queue.TryEnqueue(id, Origins.User, "touch").Ok);
        var coordinator = new PlaybackCoordinator(fixture.Queue, fixture.Media, fixture.Settings);
        var job = coordinator.ClaimNext(0, null, 8);
        Assert.NotNull(job);
        Assert.Null(coordinator.Snapshot().Current);
        coordinator.SetEngine("mpv");
        coordinator.Publish();
        Assert.Equal("mpv", coordinator.Snapshot().Engine);
        Assert.Equal(job!.Entry.Id, coordinator.Snapshot().Current!.Entry.Id);
        coordinator.Finish(job, PlayResult.Done());
        Assert.Null(coordinator.Snapshot().Current);
    }

    [Fact]
    public void A_guest_song_replaces_the_house_song_at_once()
    {
        using var fixture = new TempLibrary();
        var houseId = fixture.Add("House", "Filler", "Pop", 2000);
        var guestId = fixture.Add("Queen", "Bohemian Rhapsody", "Rock", 1975);
        File.WriteAllBytes(Path.Combine(fixture.Root, "library", "House - Filler.mp4"), [1]);
        File.WriteAllBytes(Path.Combine(fixture.Root, "library", "Queen - Bohemian Rhapsody.mp4"), [1, 2, 3]);
        Assert.True(fixture.Queue.TryEnqueue(houseId, Origins.Auto, "idle").Ok);
        var coordinator = new PlaybackCoordinator(fixture.Queue, fixture.Media, fixture.Settings);
        var house = coordinator.ClaimNext(0, null, 8);
        Assert.NotNull(house);
        coordinator.SetEngine("mpv");
        coordinator.Publish();
        Assert.True(fixture.Queue.TryEnqueue(guestId, Origins.User, "touch").Ok);

        var guest = coordinator.TakeGuestSong(house!);

        Assert.NotNull(guest);
        Assert.Equal(guestId, guest!.Entry.MediaId);
        Assert.Equal("user", guest.Entry.Origin);
        Assert.Equal(guest.Entry.Id, coordinator.Snapshot().Current!.Entry.Id);
        Assert.Equal("mpv", coordinator.Snapshot().Engine);
        var active = fixture.Queue.Active();
        Assert.Single(active);
        Assert.Equal(guest.Entry.Id, active[0].Id);
        Assert.Equal("playing", active[0].Status);
    }

    [Fact]
    public void A_persons_song_is_not_cut_off()
    {
        using var fixture = new TempLibrary();
        var firstId = fixture.Add("Queen", "Bohemian Rhapsody", "Rock", 1975);
        var secondId = fixture.Add("ABBA", "Dancing Queen", "Pop", 1976);
        File.WriteAllBytes(Path.Combine(fixture.Root, "library", "Queen - Bohemian Rhapsody.mp4"), [1]);
        File.WriteAllBytes(Path.Combine(fixture.Root, "library", "ABBA - Dancing Queen.mp4"), [1, 2]);
        Assert.True(fixture.Queue.TryEnqueue(firstId, Origins.User, "touch").Ok);
        var coordinator = new PlaybackCoordinator(fixture.Queue, fixture.Media, fixture.Settings);
        var first = coordinator.ClaimNext(0, null, 8);
        Assert.NotNull(first);
        coordinator.Publish();
        Assert.True(fixture.Queue.TryEnqueue(secondId, Origins.Mobile, "phone").Ok);

        Assert.Null(coordinator.TakeGuestSong(first!));
        Assert.Equal(first!.Entry.Id, coordinator.Snapshot().Current!.Entry.Id);
        Assert.Equal(2, fixture.Queue.Active().Count);
    }

    [Fact]
    public void A_finished_signal_releases_only_that_song()
    {
        using var fixture = new TempLibrary();
        var coordinator = new PlaybackCoordinator(fixture.Queue, fixture.Media, fixture.Settings);
        coordinator.MarkFinished("song-1");
        Assert.True(coordinator.IsFinished("song-1"));
        Assert.False(coordinator.IsFinished("song-2"));
        coordinator.MarkFinished(null);
        Assert.False(coordinator.IsFinished("song-1"));
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
