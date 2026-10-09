import { useEffect, useState } from "react";
import { DanishKeyboard } from "./components/Keyboard";
import { OnlimeCode } from "./OnlimeCode";

type Disk = { name: string; path: string; size: string; model: string; transport: string; removable: boolean };
type Wifi = { ssid: string; signal: number; secure: boolean };
type Step = "intro" | "wifi" | "onlime" | "disk" | "working";

export function Install() {
  const [step, setStep] = useState<Step>("intro");
  const [installerMode, setInstallerMode] = useState(false);
  const [disks, setDisks] = useState<Disk[]>([]);
  const [disk, setDisk] = useState<Disk | null>(null);
  const [networks, setNetworks] = useState<Wifi[]>([]);
  const [ssid, setSsid] = useState("");
  const [psk, setPsk] = useState("");
  const [shift, setShift] = useState(false);
  const [message, setMessage] = useState("");
  const [bad, setBad] = useState(false);
  const [lines, setLines] = useState<string[]>([]);
  const [busy, setBusy] = useState(false);

  useEffect(() => {
    fetch("/api/install/state").then((response) => response.json()).then((state) => setInstallerMode(Boolean(state.installerMode))).catch(() => undefined);
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

  async function loadWifi() {
    setBusy(true);
    setMessage("");
    try {
      const response = await fetch("/api/install/wifi");
      setNetworks(await response.json());
    } catch {
      setNetworks([]);
    } finally {
      setBusy(false);
    }
  }

  async function loadDisks() {
    const response = await fetch("/api/install/disks");
    const list = (await response.json()) as Disk[];
    setDisks(list);
    setDisk(list.find((item) => !item.removable) ?? list[0] ?? null);
  }

  async function connectWifi(skip: boolean) {
    if (skip) {
      setStep("onlime");
      return;
    }
    setBusy(true);
    setBad(false);
    setMessage("");
    try {
      const response = await fetch("/api/install/wifi", {
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
      setStep("onlime");
    } catch {
      setBad(true);
      setMessage("Kunne ikke nå netværksprogrammet.");
    } finally {
      setBusy(false);
    }
  }

  async function loginOnlime(token: string) {
    setBusy(true);
    setBad(false);
    setMessage("");
    try {
      const response = await fetch("/api/install/onlime", {
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
      setMessage(body.message ?? "Onlime er klar.");
      await loadDisks();
      setStep("disk");
    } catch {
      setBad(true);
      setMessage("Kunne ikke logge på Onlime.");
    } finally {
      setBusy(false);
    }
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

  function typeInto(value: string, setValue: (next: string) => void) {
    return (key: string) => setValue(value + key);
  }

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
            <p>Bagefter vælger du det trådløse netværk og henter filmene ned fra Onlime Sync. Læg film i mappen <strong>media</strong> og plakater i mappen <strong>events</strong>.</p>
            {!installerMode && <p className="warn">Denne skærm er en forhåndsvisning. Selve sletningen kører kun når pc'en er startet fra USB-nøglen.</p>}
            <button className="icon-button" type="button" onClick={() => { setStep("wifi"); void loadWifi(); }}>Start installationen</button>
          </section>
        )}
        {step === "wifi" && (
          <section className="install-card">
            <h2>Vælg trådløst netværk</h2>
            <div className="choice-list">
              {networks.length === 0 && <p>{busy ? "Leder efter netværk…" : "Ingen trådløse netværk fundet. Et netværkskabel kan bruges i stedet."}</p>}
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
                <DanishKeyboard uppercase={shift} onKey={typeInto(psk, setPsk)} onBackspace={() => setPsk((value) => value.slice(0, -1))} onClear={() => setPsk("")} onSpace={() => setPsk((value) => value + " ")} onShift={() => setShift((value) => !value)} />
              </>
            )}
            {message && <p className={bad ? "warn" : "ok"}>{message}</p>}
            <div className="install-actions">
              <button className="icon-button" type="button" disabled={busy || !ssid} onClick={() => void connectWifi(false)}>Forbind</button>
              <button className="quiet-button" type="button" onClick={() => void connectWifi(true)}>Brug netværkskabel</button>
              <button className="quiet-button" type="button" onClick={() => void loadWifi()}>Søg igen</button>
            </div>
          </section>
        )}
        {step === "onlime" && (
          <section className="install-card">
            <h2>Hent koden fra Onlime</h2>
            <p>Log ind på Onlime og kopiér den lange kode. Sync henter hele mappen bagefter. Læg film i <strong>media</strong> og plakater i <strong>events</strong>.</p>
            <OnlimeCode busy={busy} message={message} bad={bad} openUrl="/api/install/open-onlime" submitLabel="Start sync" onSubmit={(token) => void loginOnlime(token)} />
            <button className="quiet-button" type="button" onClick={() => { void loadDisks().then(() => setStep("disk")); }}>Spring over</button>
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
