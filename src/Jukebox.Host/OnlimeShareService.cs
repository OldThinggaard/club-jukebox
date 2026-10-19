using Jukebox.Core;
using Jukebox.Core.Data;
using Jukebox.Core.Domain;
using Jukebox.Core.Indexing;
using Jukebox.Core.Onlime;

namespace Jukebox.Host;

public sealed class OnlimeShareService : IDisposable
{
    private readonly SettingsStore _settings;
    private readonly JukeboxPaths _paths;
    private readonly MediaIndexer _indexer;
    private readonly OnlimeShareClient _client;
    private readonly HttpMessageHandler _handler;
    private readonly object _gate = new();
    private bool _busy;
    private int _done;
    private int _total;
    private string _message;

    public OnlimeShareService(SettingsStore settings, JukeboxPaths paths, MediaIndexer indexer)
    {
        _settings = settings;
        _paths = paths;
        _indexer = indexer;
        _handler = new SocketsHttpHandler { AllowAutoRedirect = false, PooledConnectionLifetime = TimeSpan.FromMinutes(10) };
        _client = new OnlimeShareClient(_handler);
        _message = settings.GetValue(SettingKeys.OnlimeShareNote) ?? "";
    }

    public object Status()
    {
        lock (_gate)
        {
            return new
            {
                url = _settings.GetValue(SettingKeys.OnlimeShareUrl) ?? "",
                busy = _busy,
                done = _done,
                total = _total,
                message = _busy ? BusyText() : _message
            };
        }
    }

    public (bool Ok, int Status, object Body) Save(string? url)
    {
        var text = (url ?? "").Trim();
        if (text.Length == 0)
        {
            _settings.Set(SettingKeys.OnlimeShareUrl, "");
            _settings.Audit("onlime-share", "cleared");
            lock (_gate)
                _message = "Linket er fjernet.";
            return (true, StatusCodes.Status200OK, new { ok = true, url = "", message = "Linket er fjernet." });
        }

        if (!OnlimeShareLink.TryParse(text, out var link))
        {
            return (false, StatusCodes.Status400BadRequest, new
            {
                ok = false,
                reason = "Indsæt et offentligt Onlime-link. Det starter med https://cloud.onlime.dk/s/"
            });
        }

        _settings.Set(SettingKeys.OnlimeShareUrl, link.Url);
        _settings.Audit("onlime-share", "saved");
        return (true, StatusCodes.Status200OK, new { ok = true, url = link.Url, message = "Linket er gemt." });
    }

    public (bool Ok, int Status, object Body) Begin()
    {
        lock (_gate)
        {
            if (_busy)
                return (true, StatusCodes.Status200OK, new { ok = true, message = BusyText() });
            var saved = _settings.GetValue(SettingKeys.OnlimeShareUrl);
            if (string.IsNullOrWhiteSpace(saved) || !OnlimeShareLink.TryParse(saved, out _))
                return (false, StatusCodes.Status400BadRequest, new { ok = false, reason = "Indsæt linket til mappen." });
            _busy = true;
            _done = 0;
            _total = 0;
            _message = "Henter sange.";
        }

        _ = Task.Run(() => RunAsync(CancellationToken.None));
        return (true, StatusCodes.Status200OK, new { ok = true, message = "Henter sange." });
    }

    public void Dispose()
    {
        _client.Dispose();
        _handler.Dispose();
    }

    private async Task RunAsync(CancellationToken cancellationToken)
    {
        var message = "Sangene kunne ikke hentes. Prøv igen.";
        try
        {
            var saved = _settings.GetValue(SettingKeys.OnlimeShareUrl) ?? "";
            if (!OnlimeShareLink.TryParse(saved, out var link))
            {
                message = "Indsæt linket til mappen.";
            }
            else
            {
                var known = OnlimeShareClient.NamesUnder(_paths.Library, _paths.Smb, _paths.OnlimeMedia);
                var report = await _client.FetchAsync(link, _paths.OnlimeMedia, known, cancellationToken, (done, total) =>
                {
                    lock (_gate)
                    {
                        _done = done;
                        _total = total;
                    }
                });
                message = report.Message;
                if (report.Added > 0)
                {
                    try
                    {
                        await _indexer.ReconcileAsync(CancellationToken.None);
                    }
                    catch (Exception)
                    {
                        // The regular scan still picks the files up.
                    }
                }
            }
        }
        catch (OperationCanceledException)
        {
            message = "Hentningen stoppede.";
        }
        catch (Exception)
        {
            message = "Maskinen kunne ikke nå Onlime. Sæt kablet i, og prøv igen.";
        }

        _settings.Set(SettingKeys.OnlimeShareNote, message);
        _settings.Set(SettingKeys.OnlimeShareAt, ClubCulture.Iso(DateTime.UtcNow));
        lock (_gate)
        {
            _busy = false;
            _message = message;
        }
    }

    private string BusyText() => _total > 0 ? $"Henter sange. {_done} af {_total}." : "Henter sange.";
}

public sealed class OnlimeShareWorker(OnlimeShareService shares) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await Task.Delay(TimeSpan.FromSeconds(45), stoppingToken);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        while (!stoppingToken.IsCancellationRequested)
        {
            shares.Begin();
            try
            {
                await Task.Delay(TimeSpan.FromHours(6), stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }
}
