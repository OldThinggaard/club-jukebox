import { useEffect, useState } from "react";

type Disk = { name: string; path: string; size: string; model: string; transport: string; removable: boolean };
type Step = "intro" | "disk" | "working";

export function Install() {
  const [step, setStep] = useState<Step>("intro");
  const [installerMode, setInstallerMode] = useState(false);
  const [disks, setDisks] = useState<Disk[]>([]);
  const [disk, setDisk] = useState<Disk | null>(null);
  const [message, setMessage] = useState("");
  const [bad, setBad] = useState(false);
  const [lines, setLines] = useState<string[]>([]);
  const [busy, setBusy] = useState(false);
  const [gate, setGate] = useState<"wait" | "install">("wait");

  useEffect(() => {
    fetch("/api/install/state").then((response) => response.json()).then((state) => {
      if (state.installerMode) {
        setInstallerMode(true);
        setGate("install");
        return;
      }
      window.location.replace("/");
    }).catch(() => window.location.replace("/"));
  }, []);

  useEffect(() => {
    if (step !== "working") return;
    const timer = window.setInterval(() => {
      fetch("/api/install/status").then((response) => response.json()).then((status) => {
        setLines(status.lines ?? []);
        if (status.error) {
          setBad(true);
          setMessage(status.error);
        }
      }).catch(() => undefined);
    }, 1000);
    return () => window.clearInterval(timer);
  }, [step]);

  async function loadDisks() {
    const response = await fetch("/api/install/disks");
    const list = (await response.json()) as Disk[];
    setDisks(list);
    setDisk(list.find((item) => !item.removable) ?? list[0] ?? null);
    setStep("disk");
  }

  async function install() {
    if (!disk) return;
    setBusy(true);
    setBad(false);
    setStep("working");
    setLines(["Sletter disken og installerer Videojukebox."]);
    try {
      const response = await fetch("/api/install/run", {
        method: "POST",
        headers: { "Content-Type": "application/json" },
        body: JSON.stringify({ path: disk.path, confirm: disk.name }),
      });
      const body = await response.json();
      if (!response.ok || body.ok === false) {
        setBad(true);
        setMessage(body.reason ?? "Installationen blev ikke startet.");
      }
    } catch {
      setBad(true);
      setMessage("Installationen kunne ikke starte.");
    } finally {
      setBusy(false);
    }
  }

  if (gate !== "install") return <main className="kiosk"><p>Hylden åbnes.</p></main>;

  return (
    <div className="kiosk install">
      <header className="topbar">
        <div>
          <p className="eyebrow">Installation</p>
          <h1>Videojukebox</h1>
        </div>
      </header>
      <main className="stage install-stage">
        {step === "intro" && (
          <section className="install-card">
            <h2>Nøglen sletter harddisken og lægger jukeboksen på den.</h2>
            <p>Computerens egen disk bliver tømt. Film, Windows og andre programmer på den disk forsvinder. USB-nøglen selv bliver ikke slettet.</p>
            <p>Installationen kræver ikke netværk. Onlime sættes op bagefter under Administration, når maskinen er startet.</p>
            {!installerMode && <p className="warn">Denne skærm er en forhåndsvisning. Selve sletningen kører kun når pc'en er startet fra USB-nøglen.</p>}
            <button className="icon-button" type="button" onClick={() => void loadDisks()}>Start installationen</button>
          </section>
        )}
        {step === "disk" && (
          <section className="install-card">
            <h2>Vælg harddisken der skal slettes</h2>
            <div className="choice-list">
              {disks.length === 0 && <p>Der blev ikke fundet en harddisk. Genstart maskinen og prøv igen.</p>}
              {disks.map((item) => (
                <button key={item.path} className={disk?.path === item.path ? "choice active" : "choice"} type="button" onClick={() => setDisk(item)}>
                  <span>{item.model}</span>
                  <span className="muted">{item.size} · {item.name}</span>
                </button>
              ))}
            </div>
            {disk && <p className="warn">Hele {disk.model} ({disk.size}) bliver slettet, og Videojukebox bliver lagt på den.</p>}
            {message && <p className={bad ? "warn" : "ok"}>{message}</p>}
            <button className="icon-button danger-button" type="button" disabled={busy || !disk} onClick={() => void install()}>Slet disken og installér</button>
          </section>
        )}
        {step === "working" && (
          <section className="install-card">
            <h2>{bad ? "Installationen stoppede" : "Installerer"}</h2>
            <ul className="status-lines">
              {lines.map((line, index) => <li key={`${index}-${line}`}>{line}</li>)}
            </ul>
            {message && <p className={bad ? "warn" : "ok"}>{message}</p>}
            {!bad && <p>Tag ikke nøglen ud. Maskinen genstarter selv, når disken er klar. Tag nøglen ud når skærmen bliver sort, så den ikke starter USB-nøglen igen.</p>}
          </section>
        )}
      </main>
    </div>
  );
}
