import { useEffect, useState } from "react";
import { Button, Field, inputClass, Modal } from "./components/ui";
import { formatBytes } from "./lib/utils";
import { DanishKeyboard } from "./components/Keyboard";
import { OnlimeCode } from "./OnlimeCode";

type Tab = "overview" | "library" | "sources" | "playback" | "backups" | "updates" | "netværk" | "onlime";

type Overview = {
  cpuPercent: number;
  memoryUsedBytes: number;
  memoryTotalBytes: number;
  diskUsedBytes: number;
  diskTotalBytes: number;
  databaseBytes: number;
  mediaCount: number;
  hiddenCount: number;
  errorCount: number;
  lastScanAt: string | null;
  lastBackupAt: string | null;
  version: string;
  uptime: string;
  playbackError: string | null;
};

type AdminMedia = {
  id: string;
  title: string;
  artist: string;
  album: string;
  genre: string;
  year: number | null;
  mediaType: string;
  isHidden: boolean;
  isReady: boolean;
  indexState: string;
  error: string | null;
  userPlayCount: number;
  autoPlayCount: number;
  relativePath: string;
  thumbnailUrl: string | null;
};

type Settings = {
  clubName: string;
  welcomeMessage: string;
  instructions: string;
  publicBaseUrl: string;
  cooldownMinutes: number;
  idleTimeoutSeconds: number;
  idleStrategy: string;
  audioDevice: string;
  uiOutput: string;
  playbackOutput: string;
  imageSeconds: number;
  timezone: string;
  backupHour: number;
};

async function admin<T>(url: string, init?: RequestInit): Promise<T> {
  const response = await fetch(url, { ...init, headers: { "Content-Type": "application/json", ...(init?.headers ?? {}) } });
  if (response.status === 401) throw new Error("pin");
  if (!response.ok) throw new Error(await response.text());
  return response.json() as Promise<T>;
}

export function Admin() {
  const [authed, setAuthed] = useState<boolean | null>(null);
  const [pin, setPin] = useState("");
  const [pinError, setPinError] = useState("");
  const [tab, setTab] = useState<Tab>("overview");

  useEffect(() => {
    fetch("/api/admin/session").then((response) => response.json()).then((body: { authenticated: boolean }) => setAuthed(body.authenticated)).catch(() => setAuthed(false));
  }, []);

  async function login() {
    const response = await fetch("/api/admin/login", { method: "POST", headers: { "Content-Type": "application/json" }, body: JSON.stringify({ pin }) });
    if (!response.ok) {
      setPinError("That PIN was not accepted.");
      setPin("");
      return;
    }
    setAuthed(true);
  }

  if (authed === null) return <main className="admin"><p>Checking the club PIN…</p></main>;
  if (!authed) {
    return (
      <main className="pin-gate">
        <div className="pin-card">
          <p className="eyebrow">Administration</p>
          <h1 className="font-serif text-5xl">Club PIN</h1>
          <p className="muted">The first PIN is 2580. Change it after installation. Guests never see this page from the shelf.</p>
          <p className="text-3xl tracking-[0.4em]">{pin.padEnd(4, "·")}</p>
          {pinError && <p>{pinError}</p>}
          <div className="pad">
            {["1", "2", "3", "4", "5", "6", "7", "8", "9", "C", "0", "OK"].map((key) => (
              <button
                key={key}
                className="pad-key"
                type="button"
                onClick={() => {
                  if (key === "C") setPin("");
                  else if (key === "OK") void login();
                  else setPin((current) => (current + key).slice(0, 8));
                }}
              >
                {key}
              </button>
            ))}
          </div>
        </div>
      </main>
    );
  }

  return (
    <main className="admin">
      <header className="flex items-center justify-between gap-3">
        <div>
          <p className="eyebrow">Administration</p>
          <h1 className="font-serif text-4xl">House controls</h1>
        </div>
        <Button variant="ghost" onClick={() => { void fetch("/api/admin/logout", { method: "POST" }); setAuthed(false); }}>Sign out</Button>
      </header>
      <nav className="admin-nav">
        {(["overview", "library", "sources", "playback", "backups", "updates", "netværk", "onlime"] as Tab[]).map((item) => (
          <button key={item} className={tab === item ? "active" : ""} type="button" onClick={() => setTab(item)}>{item}</button>
        ))}
      </nav>
      {tab === "overview" && <OverviewPanel />}
      {tab === "library" && <LibraryPanel />}
      {tab === "sources" && <SourcesPanel />}
      {tab === "playback" && <PlaybackPanel />}
      {tab === "backups" && <BackupsPanel />}
      {tab === "updates" && <UpdatesPanel />}
      {tab === "netværk" && <NetworkPanel />}
      {tab === "onlime" && <OnlimePanel />}
    </main>
  );
}

function OverviewPanel() {
  const [data, setData] = useState<Overview | null>(null);
  const [error, setError] = useState("");
  useEffect(() => {
    const pull = () => admin<Overview>("/api/admin/overview").then(setData).catch((reason: Error) => setError(reason.message));
    pull();
    const timer = window.setInterval(pull, 3000);
    return () => window.clearInterval(timer);
  }, []);
  if (!data) return <p>{error || "Reading the machine…"}</p>;
  const cards = [
    ["CPU", `${data.cpuPercent}%`],
    ["Memory", `${formatBytes(data.memoryUsedBytes)} / ${formatBytes(data.memoryTotalBytes)}`],
    ["Disk", `${formatBytes(data.diskUsedBytes)} / ${formatBytes(data.diskTotalBytes)}`],
    ["Database", formatBytes(data.databaseBytes)],
    ["Media", String(data.mediaCount)],
    ["Hidden", String(data.hiddenCount)],
    ["Index errors", String(data.errorCount)],
    ["Last scan", data.lastScanAt ? new Date(data.lastScanAt).toLocaleString() : "Not yet"],
    ["Last backup", data.lastBackupAt ? new Date(data.lastBackupAt).toLocaleString() : "Not yet"],
    ["Version", data.version],
    ["Uptime", data.uptime],
  ];
  return (
    <section>
      {data.playbackError && <p className="banner">Playback: {data.playbackError}</p>}
      <div className="stat-grid">
        {cards.map(([label, value]) => (
          <article key={label} className="stat"><span>{label}</span><strong>{value}</strong></article>
        ))}
      </div>
    </section>
  );
}

function LibraryPanel() {
  const [items, setItems] = useState<AdminMedia[]>([]);
  const [q, setQ] = useState("");
  const [editing, setEditing] = useState<AdminMedia | null>(null);
  const load = (text = q) => admin<AdminMedia[]>(`/api/admin/media?limit=100${text ? `&q=${encodeURIComponent(text)}` : ""}`).then(setItems);
  useEffect(() => { void load(""); }, []);
  return (
    <section className="panel">
      <div className="actions">
        <input className={inputClass} value={q} placeholder="Find in the whole library" onChange={(event) => setQ(event.target.value)} />
        <Button type="button" onClick={() => void load()}>Search</Button>
        <Button variant="ghost" type="button" onClick={() => void admin("/api/admin/library/reindex", { method: "POST" })}>Re-index</Button>
        <Button variant="danger" type="button" onClick={() => { if (confirm("Rebuild removes the catalogue and reads every folder again. Play counts are cleared.")) void admin("/api/admin/library/rebuild", { method: "POST" }).then(() => load()); }}>Rebuild</Button>
      </div>
      <table className="table">
        <thead><tr><th>Title</th><th>State</th><th>Plays</th><th></th></tr></thead>
        <tbody>
          {items.map((item) => (
            <tr key={item.id}>
              <td><strong>{item.title}</strong><div className="muted">{item.artist} · {item.relativePath}</div></td>
              <td>{item.isReady ? (item.isHidden ? "Hidden" : "On the shelf") : item.indexState}{item.error ? ` — ${item.error}` : ""}</td>
              <td>{item.userPlayCount} guest / {item.autoPlayCount} house</td>
              <td><Button variant="ghost" type="button" onClick={() => setEditing(item)}>Edit</Button></td>
            </tr>
          ))}
        </tbody>
      </table>
      {editing && <MediaEditor item={editing} onClose={() => { setEditing(null); void load(); }} />}
    </section>
  );
}

function MediaEditor({ item, onClose }: { item: AdminMedia; onClose: () => void }) {
  const [title, setTitle] = useState(item.title);
  const [artist, setArtist] = useState(item.artist);
  const [album, setAlbum] = useState(item.album);
  const [genre, setGenre] = useState(item.genre);
  const [year, setYear] = useState(item.year?.toString() ?? "");
  const [hidden, setHidden] = useState(item.isHidden);
  async function save() {
    await admin(`/api/admin/media/${item.id}`, {
      method: "PUT",
      body: JSON.stringify({ title, artist, album, genre, year: year ? Number(year) : null, hidden }),
    });
    onClose();
  }
  return (
    <Modal open title={item.title} onClose={onClose}>
      <div className="form-grid">
        <Field label="Title"><input value={title} onChange={(event) => setTitle(event.target.value)} /></Field>
        <Field label="Artist"><input value={artist} onChange={(event) => setArtist(event.target.value)} /></Field>
        <Field label="Album"><input value={album} onChange={(event) => setAlbum(event.target.value)} /></Field>
        <Field label="Genre"><input value={genre} onChange={(event) => setGenre(event.target.value)} /></Field>
        <Field label="Year"><input value={year} onChange={(event) => setYear(event.target.value)} /></Field>
      </div>
      <label className="mt-3 flex items-center gap-2"><input type="checkbox" checked={hidden} onChange={(event) => setHidden(event.target.checked)} /> Hide from the shelf</label>
      <div className="actions">
        <Button type="button" onClick={() => void save()}>Save correction</Button>
        <Button variant="ghost" type="button" onClick={() => void admin(`/api/admin/media/${item.id}/override`, { method: "DELETE" }).then(onClose)}>Use file metadata again</Button>
      </div>
    </Modal>
  );
}

function SourcesPanel() {
  const [sources, setSources] = useState<{ id: string; name: string; kind: string; path: string; priority: number; enabled: boolean; role: string }[]>([]);
  const [name, setName] = useState("Extra folder");
  const [path, setPath] = useState("/media/library");
  const load = () => admin<typeof sources>("/api/admin/sources").then(setSources);
  useEffect(() => { void load(); }, []);
  return (
    <section className="panel">
      <p className="muted">Onlime is just a folder. The jukebox keeps working when that folder stops changing.</p>
      {sources.map((source) => (
        <article key={source.id} className="stat mt-3">
          <strong>{source.name}</strong>
          <span>{source.kind} · priority {source.priority} · {source.role} · {source.enabled ? "watching" : "paused"}</span>
          <div className="muted">{source.path}</div>
          <div className="actions">
            <Button variant="ghost" type="button" onClick={() => void admin(`/api/admin/sources/${source.id}`, { method: "PUT", body: JSON.stringify({ ...source, enabled: !source.enabled }) }).then(load)}>{source.enabled ? "Pause" : "Watch"}</Button>
            <Button variant="danger" type="button" onClick={() => void admin(`/api/admin/sources/${source.id}`, { method: "DELETE" }).then(load)}>Remove</Button>
          </div>
        </article>
      ))}
      <div className="form-grid mt-4">
        <Field label="Name"><input value={name} onChange={(event) => setName(event.target.value)} /></Field>
        <Field label="Folder"><input value={path} onChange={(event) => setPath(event.target.value)} /></Field>
      </div>
      <Button className="mt-3" type="button" onClick={() => void admin("/api/admin/sources", { method: "POST", body: JSON.stringify({ name, kind: "local", path, priority: 6, enabled: true, role: "catalog" }) }).then(load)}>Add folder</Button>
    </section>
  );
}

function PlaybackPanel() {
  const [settings, setSettings] = useState<Settings | null>(null);
  const [audio, setAudio] = useState<{ id: string; label: string }[]>([]);
  const [displays, setDisplays] = useState<{ name: string; connected: boolean; width: number }[]>([]);
  useEffect(() => {
    void admin<Settings>("/api/admin/settings").then(setSettings);
    void admin<{ id: string; label: string }[]>("/api/admin/audio-devices").then(setAudio);
    void admin<{ displays: { name: string; connected: boolean; width: number }[] }>("/api/admin/displays").then((body) => setDisplays(body.displays));
  }, []);
  if (!settings) return <p>Loading settings…</p>;
  const set = (patch: Partial<Settings>) => setSettings({ ...settings, ...patch });
  return (
    <section className="panel">
      <div className="form-grid">
        <Field label="Club name"><input value={settings.clubName} onChange={(event) => set({ clubName: event.target.value })} /></Field>
        <Field label="Public address"><input value={settings.publicBaseUrl} onChange={(event) => set({ publicBaseUrl: event.target.value })} /></Field>
        <Field label="Cooldown minutes"><input type="number" value={settings.cooldownMinutes} onChange={(event) => set({ cooldownMinutes: Number(event.target.value) })} /></Field>
        <Field label="Idle seconds"><input type="number" value={settings.idleTimeoutSeconds} onChange={(event) => set({ idleTimeoutSeconds: Number(event.target.value) })} /></Field>
        <Field label="Idle strategy">
          <select value={settings.idleStrategy} onChange={(event) => set({ idleStrategy: event.target.value })}>
            <option value="hybrid">Hybrid, 80% least played</option>
            <option value="least">Least played</option>
            <option value="random">Random</option>
          </select>
        </Field>
        <Field label="Audio output">
          <select value={settings.audioDevice} onChange={(event) => set({ audioDevice: event.target.value })}>
            <option value="">System default</option>
            {audio.map((device) => <option key={device.id} value={device.id}>{device.label}</option>)}
          </select>
        </Field>
        <Field label="Touchscreen output">
          <select value={settings.uiOutput} onChange={(event) => set({ uiOutput: event.target.value })}>
            <option value="">Automatic</option>
            {displays.filter((item) => item.connected).map((item) => <option key={item.name} value={item.name}>{item.name}</option>)}
          </select>
        </Field>
        <Field label="Television output">
          <select value={settings.playbackOutput} onChange={(event) => set({ playbackOutput: event.target.value })}>
            <option value="">Automatic</option>
            {displays.filter((item) => item.connected).map((item) => <option key={item.name} value={item.name}>{item.name}</option>)}
          </select>
        </Field>
        <Field label="Photo seconds"><input type="number" value={settings.imageSeconds} onChange={(event) => set({ imageSeconds: Number(event.target.value) })} /></Field>
        <Field label="Backup hour"><input type="number" value={settings.backupHour} onChange={(event) => set({ backupHour: Number(event.target.value) })} /></Field>
      </div>
      <Field label="Welcome"><textarea value={settings.welcomeMessage} onChange={(event) => set({ welcomeMessage: event.target.value })} /></Field>
      <Field label="Instructions"><textarea value={settings.instructions} onChange={(event) => set({ instructions: event.target.value })} /></Field>
      <div className="actions">
        <Button type="button" onClick={() => void admin("/api/admin/settings", { method: "PUT", body: JSON.stringify(settings) })}>Save</Button>
      </div>
      <PinChange />
    </section>
  );
}

function PinChange() {
  const [current, setCurrent] = useState("");
  const [next, setNext] = useState("");
  const [message, setMessage] = useState("");
  return (
    <div className="mt-6">
      <h2 className="font-serif text-2xl">Change PIN</h2>
      <div className="form-grid">
        <Field label="Current"><input value={current} onChange={(event) => setCurrent(event.target.value)} /></Field>
        <Field label="New"><input value={next} onChange={(event) => setNext(event.target.value)} /></Field>
      </div>
      <Button className="mt-3" type="button" onClick={() => void admin("/api/admin/pin", { method: "POST", body: JSON.stringify({ current, next }) }).then(() => setMessage("PIN updated")).catch(() => setMessage("The PIN was not changed"))}>Update PIN</Button>
      {message && <p>{message}</p>}
    </div>
  );
}

function BackupsPanel() {
  const [rows, setRows] = useState<{ id: string; kind: string; createdAt: string; sizeBytes: number; note: string | null }[]>([]);
  const load = () => admin<typeof rows>("/api/admin/backups").then(setRows);
  useEffect(() => { void load(); }, []);
  return (
    <section className="panel">
      <Button type="button" onClick={() => void admin("/api/admin/backups", { method: "POST" }).then(load)}>Back up now</Button>
      <table className="table">
        <tbody>
          {rows.map((row) => (
            <tr key={row.id}>
              <td>{row.kind}<div className="muted">{row.createdAt}</div></td>
              <td>{formatBytes(row.sizeBytes)}</td>
              <td>
                <Button variant="ghost" type="button" onClick={() => { if (confirm("Restore this backup and restart the jukebox?")) void admin(`/api/admin/backups/${row.id}/restore`, { method: "POST" }); }}>Restore</Button>
              </td>
            </tr>
          ))}
        </tbody>
      </table>
      {rows.length === 0 && <p className="muted">No backups yet. A daily copy is taken at the configured hour.</p>}
    </section>
  );
}

function NetworkPanel() {
  const [networks, setNetworks] = useState<{ ssid: string; signal: number; secure: boolean }[]>([]);
  const [ssid, setSsid] = useState("");
  const [psk, setPsk] = useState("");
  const [shift, setShift] = useState(false);
  const [busy, setBusy] = useState(false);
  const [message, setMessage] = useState("");
  const [bad, setBad] = useState(false);

  async function load() {
    setBusy(true);
    try {
      const response = await fetch("/api/admin/wifi");
      setNetworks(response.ok ? await response.json() : []);
    } catch {
      setNetworks([]);
    } finally {
      setBusy(false);
    }
  }

  useEffect(() => { void load(); }, []);

  async function connect() {
    setBusy(true);
    setBad(false);
    setMessage("");
    try {
      const response = await fetch("/api/admin/wifi", {
        method: "POST",
        headers: { "Content-Type": "application/json" },
        body: JSON.stringify({ ssid, psk }),
      });
      const body = await response.json();
      if (!response.ok || body.ok === false) {
        setBad(true);
        setMessage(body.reason ?? "Kunne ikke forbinde.");
        return;
      }
      setMessage(body.message ?? "Forbundet.");
    } catch {
      setBad(true);
      setMessage("Kunne ikke nå netværksprogrammet.");
    } finally {
      setBusy(false);
    }
  }

  return (
    <section className="panel">
      <h2 className="font-serif text-3xl">Netværk</h2>
      <p>Et kabel virker uden opsætning. Trådløst netværk vælges her, efter installationen.</p>
      <div className="choice-list">
        {networks.length === 0 && <p className="muted">{busy ? "Leder efter netværk…" : "Ingen trådløse netværk fundet."}</p>}
        {networks.map((network) => (
          <button key={network.ssid} className={ssid === network.ssid ? "choice active" : "choice"} type="button" onClick={() => setSsid(network.ssid)}>
            <span>{network.ssid}</span>
            <span className="muted">{network.signal}%</span>
          </button>
        ))}
      </div>
      {ssid && (
        <>
          <p>Adgangskode til {ssid}</p>
          <input aria-label="Wi-Fi-adgangskode" value={psk} onChange={(event) => setPsk(event.target.value)} />
          <DanishKeyboard uppercase={shift} onKey={(key) => setPsk((value) => value + key)} onBackspace={() => setPsk((value) => value.slice(0, -1))} onClear={() => setPsk("")} onSpace={() => setPsk((value) => value + " ")} onShift={() => setShift((value) => !value)} />
        </>
      )}
      {message && <p className={bad ? "warn" : "ok"}>{message}</p>}
      <div className="install-actions">
        <Button type="button" disabled={busy || !ssid} onClick={() => void connect()}>Forbind</Button>
        <Button variant="ghost" type="button" onClick={() => void load()}>Søg igen</Button>
      </div>
    </section>
  );
}

function OnlimePanel() {
  const [busy, setBusy] = useState(false);
  const [message, setMessage] = useState("");
  const [bad, setBad] = useState(false);

  async function start(token: string) {
    setBusy(true);
    setBad(false);
    setMessage("");
    try {
      const response = await fetch("/api/admin/onlime", {
        method: "POST",
        headers: { "Content-Type": "application/json" },
        body: JSON.stringify({ token, device: "videojukebox" }),
      });
      const body = await response.json();
      if (!response.ok || body.ok === false) {
        setBad(true);
        setMessage(body.reason ?? "Onlime afviste koden.");
        return;
      }
      setMessage(body.message ?? "Sync er startet.");
    } catch {
      setBad(true);
      setMessage("Kunne ikke starte sync.");
    } finally {
      setBusy(false);
    }
  }

  return (
    <section className="panel">
      <h2 className="font-serif text-3xl">Onlime</h2>
      <p>Onlime sættes op her, efter maskinen er installeret. Sæt først netværk på fanen netværk, eller sæt et kabel i. Log derefter ind på Onlime og kopiér den lange kode. Sync henter filmene bagefter. Læg film i mappen media og plakater i mappen events.</p>
      <OnlimeCode busy={busy} message={message} bad={bad} openUrl="/api/admin/onlime/open" submitLabel="Start sync" onSubmit={(token) => void start(token)} />
    </section>
  );
}

function UpdatesPanel() {
  const [data, setData] = useState<{ version: string; note: string; packages: { path: string; version: string; valid: boolean; newer: boolean; error: string | null }[] } | null>(null);
  useEffect(() => { void admin<NonNullable<typeof data>>("/api/admin/updates").then(setData); }, []);
  if (!data) return <p>Looking for update packages…</p>;
  return (
    <section className="panel">
      <p>Running {data.version}. {data.note}</p>
      {data.packages.length === 0 && <p className="muted">No package in the Onlime updates folder, the local updates folder, or a USB drive.</p>}
      {data.packages.map((item) => (
        <article key={item.path} className="stat mt-3">
          <strong>{item.version || "Unreadable package"}</strong>
          <div className="muted">{item.path}</div>
          <div>{item.valid ? (item.newer ? "Will install on next reboot" : "Already installed or older") : item.error}</div>
        </article>
      ))}
    </section>
  );
}
