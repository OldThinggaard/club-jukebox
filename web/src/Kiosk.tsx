import { useEffect, useMemo, useRef, useState } from "react";
import { DanishKeyboard } from "./components/Keyboard";
import { api, type Category, type Media, type Screen } from "./lib/api";
import { formatDuration } from "./lib/utils";

const mobile = () => window.matchMedia("(max-width: 800px)").matches;

export function Kiosk() {
  const [screen, setScreen] = useState<Screen | null>(null);
  const [media, setMedia] = useState<Media[]>([]);
  const [shelfReady, setShelfReady] = useState(false);
  const [categories, setCategories] = useState<Category[]>([]);
  const [category, setCategory] = useState("");
  const [query, setQuery] = useState("");
  const [artist, setArtist] = useState("");
  const [letter, setLetter] = useState("");
  const [artists, setArtists] = useState<{ letter: string; artists: { name: string; count: number }[] }[]>([]);
  const [alphabet, setAlphabet] = useState("ABCDEFGHIJKLMNOPQRSTUVWXYZÆØÅ");
  const [showArtists, setShowArtists] = useState(false);
  const [searchOpen, setSearchOpen] = useState(false);
  const [queueOpen, setQueueOpen] = useState(false);
  const [shift, setShift] = useState(false);
  const [toast, setToast] = useState<{ text: string; bad?: boolean } | null>(null);
  const [staffPin, setStaffPin] = useState(false);
  const [level, setLevel] = useState(80);
  const hold = useRef<number | null>(null);
  const levelSend = useRef<{ pending: number | null; busy: boolean }>({ pending: null, busy: false });

  useEffect(() => {
    void api.volume().then((body) => setLevel(body.percent)).catch(() => undefined);
  }, []);

  function chooseLevel(next: number) {
    const value = Math.max(0, Math.min(100, Math.round(next)));
    setLevel(value);
    const state = levelSend.current;
    state.pending = value;
    if (state.busy) return;
    state.busy = true;
    void (async () => {
      while (state.pending !== null) {
        const percent = state.pending;
        state.pending = null;
        try {
          await api.setVolume(percent);
        } catch {
          // The slider stays where the finger left it. The next move tries again.
        }
      }
      state.busy = false;
      if (state.pending !== null) chooseLevel(state.pending);
    })();
  }

  useEffect(() => {
    let stop = false;
    const pull = () => {
      api.screen().then((next) => {
        if (!stop) setScreen(next);
      }).catch(() => undefined);
      api.categories().then((next) => {
        if (stop) return;
        setCategories(next);
        setCategory((current) => current && !next.some((item) => item.id === current) ? "" : current);
      }).catch(() => undefined);
    };
    pull();
    const timer = window.setInterval(pull, 2000);
    return () => {
      stop = true;
      window.clearInterval(timer);
    };
  }, []);

  useEffect(() => {
    api.categories().then(setCategories).catch(() => undefined);
    api.artists().then((result) => {
      setArtists(result.groups);
      setAlphabet(result.alphabet);
    }).catch(() => undefined);
  }, [screen?.libraryCount]);

  const idleAt = useRef(Date.now());
  const idleSeconds = useRef(0);
  const view = useRef({ query: "", searchOpen: false, category: "", showArtists: false, artist: "", letter: "", queueOpen: false, staffPin: false });
  idleSeconds.current = screen?.idleTimeoutSeconds ?? 0;
  view.current = { query, searchOpen, category, showArtists, artist, letter, queueOpen, staffPin };

  useEffect(() => {
    const mark = () => { idleAt.current = Date.now(); };
    window.addEventListener("pointerdown", mark, true);
    window.addEventListener("keydown", mark, true);
    const timer = window.setInterval(() => {
      const seconds = idleSeconds.current;
      if (seconds <= 0) return;
      if (Date.now() - idleAt.current < seconds * 1000) return;
      const state = view.current;
      if (state.query.trim()) return;
      const away = state.searchOpen || state.category !== "" || state.showArtists || state.artist !== "" || state.letter !== "" || state.queueOpen || state.staffPin;
      if (!away) return;
      idleAt.current = Date.now();
      setQuery("");
      setSearchOpen(false);
      setCategory("");
      setShowArtists(false);
      setArtist("");
      setLetter("");
      setQueueOpen(false);
      setStaffPin(false);
      setShift(false);
    }, 1000);
    return () => {
      window.removeEventListener("pointerdown", mark, true);
      window.removeEventListener("keydown", mark, true);
      window.clearInterval(timer);
    };
  }, []);

  const playingId = screen?.nowPlaying?.mediaId ?? "";
  const queuedIds = new Set(screen?.queue.map((item) => item.mediaId) ?? []);
  const shelfKey = `${playingId}|${screen?.queue.map((item) => item.mediaId).join(",") ?? ""}`;

  useEffect(() => {
    let current = true;
    const handle = window.setTimeout(() => {
      const params: Record<string, string> = {};
      if (query.trim()) params.q = query.trim();
      if (category) params.category = category;
      if (artist) params.artist = artist;
      if (letter) params.letter = letter;
      void everySong(params, () => current).then((items) => {
        if (!current || items === null) return;
        const random = !params.q;
        setMedia((shown) => placeSongs(shown, items, random));
        setShelfReady(true);
      }).catch(() => {
        if (!current) return;
        setMedia([]);
        setShelfReady(true);
      });
    }, 180);
    return () => {
      current = false;
      window.clearTimeout(handle);
    };
  }, [query, category, artist, letter, screen?.libraryCount, screen?.nowPlaying?.queueId, shelfKey]);

  useEffect(() => {
    const onKey = (event: KeyboardEvent) => {
      if (event.key === "Escape") {
        setStaffPin(false);
        return;
      }
      if (event.key !== "Tab") return;
      event.preventDefault();
      setStaffPin(true);
    };
    window.addEventListener("keydown", onKey, true);
    return () => window.removeEventListener("keydown", onKey, true);
  }, []);

  function beginStaffHold() {
    if (hold.current !== null) window.clearTimeout(hold.current);
    hold.current = window.setTimeout(() => setStaffPin(true), 1500);
  }

  function endStaffHold() {
    if (hold.current === null) return;
    window.clearTimeout(hold.current);
    hold.current = null;
  }

  useEffect(() => {
    if (!toast) return;
    const timer = window.setTimeout(() => setToast(null), 2400);
    return () => window.clearTimeout(timer);
  }, [toast]);

  const grouped = useMemo(() => artists.filter((group) => !letter || group.letter === letter), [artists, letter]);

  async function choose(item: Media) {
    const note = noteOnPicture(item, playingId, queuedIds);
    if (!item.canQueue || note) {
      setToast({ text: note ?? item.blockReason ?? "This film is not available.", bad: true });
      return;
    }
    const result = await api.enqueue(item.id, mobile() ? "mobile" : "user");
    setToast(result.ok ? { text: `${item.title} added to the queue` } : { text: result.reason ?? "Could not add that film", bad: true });
    const next = await api.screen().catch(() => null);
    if (next) setScreen(next);
  }

  function typeKey(key: string) {
    setQuery((current) => current + key);
    setShift(false);
    setArtist("");
    setLetter("");
    setShowArtists(false);
  }

  return (
    <div
      className="kiosk"
      onPointerDown={() => {
        if (document.visibilityState === "visible") void api.activity();
      }}
    >
      <aside className="volume-rail">
        <strong>{level}</strong>
        <input
          className="volume-slider"
          type="range"
          min={0}
          max={100}
          step={1}
          value={level}
          aria-label="Lyd"
          onInput={(event) => chooseLevel(Number(event.currentTarget.value))}
        />
      </aside>
      {toast && <div className={toast.bad ? "toast bad" : "toast"}>{toast.text}</div>}
      <header className="topbar">
        <div
          className="brand"
          onPointerDown={beginStaffHold}
          onPointerUp={endStaffHold}
          onPointerLeave={endStaffHold}
          onPointerCancel={endStaffHold}
        >
          {screen?.logoUrl && <img src={screen.logoUrl} alt="" />}
          <div>
            <p className="eyebrow">Video jukebox</p>
            <h1>{screen?.clubName ?? "Clubhouse"}</h1>
          </div>
        </div>
        <div className="clock-block">
          <strong>{screen?.clock}</strong>
          <span>{screen?.dateLabel}</span>
        </div>
        <div className="top-actions">
          <button className="icon-button" type="button" onClick={() => setSearchOpen(true)}>
            Search
          </button>
        </div>
      </header>
      <div className="chips">
        <button className={category === "" && !showArtists ? "chip active" : "chip"} type="button" onClick={() => { setCategory(""); setShowArtists(false); setArtist(""); }}>All</button>
        <button className={showArtists ? "chip active" : "chip"} type="button" onClick={() => { setShowArtists(true); setCategory(""); setQuery(""); }}>Artists</button>
        {categories.map((item) => (
          <button key={item.id} className={category === item.id ? "chip active" : "chip"} type="button" onClick={() => { setCategory(item.id); setShowArtists(false); setArtist(""); }}>
            {item.name}
          </button>
        ))}
      </div>
      <main className="stage">
        {showArtists ? (
          <div className="artists">
            <div className="letters">
              <button className={letter === "" ? "letter active" : "letter"} type="button" onClick={() => setLetter("")}>All</button>
              {alphabet.split("").map((item) => (
                <button key={item} className={letter === item ? "letter active" : "letter"} type="button" onClick={() => setLetter(item)}>{item}</button>
              ))}
            </div>
            <div className="artist-list">
              {grouped.length === 0 && <p className="muted">No artists in this letter yet.</p>}
              {grouped.map((group) => (
                <section key={group.letter}>
                  <p className="eyebrow">{group.letter}</p>
                  {group.artists.map((person) => (
                    <button key={person.name} className="artist-name" type="button" onClick={() => { setArtist(person.name); setShowArtists(false); }}>
                      <span>{person.name}</span>
                      <span className="muted">{person.count}</span>
                    </button>
                  ))}
                </section>
              ))}
            </div>
          </div>
        ) : !shelfReady ? (
          <div className="empty">
            <h2>Loading the shelf</h2>
          </div>
        ) : media.length === 0 ? (
          <div className="empty">
            <h2>{screen?.indexing ? "Preparing the library" : query ? "Nothing matched" : "The shelf is empty"}</h2>
            <p className="muted">
              {screen?.indexing
                ? "New films appear here only after the copy has finished, the file has been checked, and a thumbnail is ready."
                : query
                  ? `No title, artist, or genre matched “${query}”.`
                  : "Put films in the Onlime sync folder. They show up on their own."}
            </p>
          </div>
        ) : (
          <div className="grid">
            {media.map((item) => {
              const onPicture = noteOnPicture(item, playingId, queuedIds);
              return (
                <button key={item.id} className={onPicture ? "card noted" : item.canQueue ? "card" : "card blocked"} type="button" onClick={() => void choose(item)}>
                  <figure>
                    {item.thumbnailUrl ? <img src={item.thumbnailUrl} alt="" loading="lazy" decoding="async" /> : <span className="fallback">{item.title.slice(0, 1)}</span>}
                    {!onPicture && formatDuration(item.durationMs) && <span className="badge">{formatDuration(item.durationMs)}</span>}
                    {onPicture && <span className="picture-note">{onPicture}</span>}
                  </figure>
                  <h2>{item.title}</h2>
                  <p>{item.artist || "Unknown artist"}</p>
                  {!item.canQueue && !onPicture && <small>{item.blockReason}</small>}
                </button>
              );
            })}
          </div>
        )}
      </main>
      <footer className="nowbar">
        {screen?.nowPlaying?.thumbnailUrl ? <img src={screen.nowPlaying.thumbnailUrl} alt="" /> : <div className="now-fallback" />}
        <div>
          <h2>{screen?.nowPlaying?.title ?? "Intet spiller"}</h2>
          <p>{screen?.nowPlaying ? screen.nowPlaying.artist : "Fjernsynet venter på en titel."}</p>
        </div>
        <button className="queue-button" type="button" onClick={() => setQueueOpen(true)}>
          Queue {screen?.queue.length ?? 0}
        </button>
      </footer>
      {searchOpen && (
        <div className="search-panel">
          <div className="search-head">
            <input
              autoFocus
              inputMode="none"
              aria-label="Search"
              placeholder="Artist, title, album, genre"
              value={query}
              onChange={(event) => setQuery(event.target.value)}
              onKeyDown={(event) => {
                if (event.key === "Enter") setSearchOpen(false);
              }}
            />
            <button className="icon-button" type="button" onClick={() => setSearchOpen(false)}>Done</button>
          </div>
          <DanishKeyboard
            uppercase={shift}
            onKey={typeKey}
            onBackspace={() => setQuery((current) => current.slice(0, -1))}
            onClear={() => setQuery("")}
            onSpace={() => typeKey(" ")}
            onShift={() => setShift((current) => !current)}
          />
        </div>
      )}
      {staffPin && <StaffPin onClose={() => setStaffPin(false)} />}
      {queueOpen && (
        <div className="queue-sheet">
          <div className="sheet-head">
            <div>
              <p className="eyebrow">In order</p>
              <strong>The queue cannot be skipped or rearranged.</strong>
            </div>
            <button className="icon-button" type="button" onClick={() => setQueueOpen(false)}>Close</button>
          </div>
          {screen?.nowPlaying && <p>Now: {screen.nowPlaying.artist} — {screen.nowPlaying.title}</p>}
          <ol>
            {screen?.queue.length === 0 && <li><span className="muted">Nobody is waiting. Choose something from the shelf.</span></li>}
            {screen?.queue.map((item, index) => (
              <li key={item.id}>
                <strong>{index + 1}</strong>
                {item.thumbnailUrl && <img src={item.thumbnailUrl} alt="" />}
                <span>{item.artist ? `${item.artist} — ` : ""}{item.title}</span>
              </li>
            ))}
          </ol>
        </div>
      )}
    </div>
  );
}

function noteOnPicture(item: Media, playingId: string, queuedIds: ReadonlySet<string>): string | null {
  if (playingId && item.id === playingId) return "This is playing now.";
  if (queuedIds.has(item.id)) return "This is already in the queue.";
  if (item.blockReason?.startsWith("This was played") === true) return item.blockReason;
  if (item.blockReason === "This is playing now." || item.blockReason === "This is already in the queue.") return item.blockReason;
  return null;
}

function placeSongs(shown: Media[], next: Media[], random: boolean): Media[] {
  if (!random) return next;
  if (shown.length === next.length) {
    const incoming = new Map(next.map((item) => [item.id, item]));
    if (shown.every((item) => incoming.has(item.id)))
      return shown.flatMap((item) => {
        const found = incoming.get(item.id);
        return found ? [found] : [];
      });
  }
  return shuffle(next);
}

function shuffle(items: Media[]): Media[] {
  const copy = items.slice();
  for (let index = copy.length - 1; index > 0; index -= 1) {
    const swap = Math.floor(Math.random() * (index + 1));
    const held = copy[index];
    copy[index] = copy[swap];
    copy[swap] = held;
  }
  return copy;
}

async function everySong(params: Record<string, string>, alive: () => boolean): Promise<Media[] | null> {
  const pageSize = 200;
  const seen = new Set<string>();
  const all: Media[] = [];
  let offset = 0;
  while (offset < 20000) {
    if (!alive()) return null;
    const items = await api.media({ ...params, limit: String(pageSize), offset: String(offset) });
    if (!alive()) return null;
    if (items.length === 0) return all;
    let fresh = 0;
    for (const item of items) {
      if (seen.has(item.id)) continue;
      seen.add(item.id);
      all.push(item);
      fresh += 1;
    }
    if (fresh === 0 || items.length < pageSize) return all;
    offset += items.length;
  }
  return all;
}

function StaffPin({ onClose }: { onClose: () => void }) {
  const [pin, setPin] = useState("");
  const [error, setError] = useState("");

  async function login() {
    const response = await fetch("/api/admin/login", {
      method: "POST",
      headers: { "Content-Type": "application/json" },
      body: JSON.stringify({ pin }),
    });
    if (!response.ok) {
      setError("Koden er forkert.");
      setPin("");
      return;
    }
    window.location.replace("/admin");
  }

  return (
    <div className="pin-gate staff-pin" role="dialog" aria-modal="true" aria-label="Administration">
      <div className="pin-card">
        <p className="eyebrow">Administration</p>
        <h1 className="font-serif text-5xl">Tast koden</h1>
        <p className="text-3xl tracking-[0.4em]">{pin.padEnd(4, "·")}</p>
        {error && <p>{error}</p>}
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
        <button className="icon-button mt-3" type="button" onClick={onClose}>Luk</button>
      </div>
    </div>
  );
}
