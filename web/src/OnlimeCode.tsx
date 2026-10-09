import { useState } from "react";

const OnlimeUrl = "https://cloud.onlime.dk/web/secure";

type Props = {
  busy: boolean;
  message: string;
  bad: boolean;
  openUrl: string;
  submitLabel: string;
  onSubmit: (token: string) => void;
};

export function OnlimeCode({ busy, message, bad, openUrl, submitLabel, onSubmit }: Props) {
  const [token, setToken] = useState("");
  const [hint, setHint] = useState("");

  async function openOnlime() {
    setHint("");
    try {
      const response = await fetch(openUrl, { method: "POST" });
      const body = await response.json();
      if (body.opened) {
        setHint("Onlime er åbnet på skærmen. Log ind, kopiér koden, luk vinduet, og tryk Indsæt kode.");
        return;
      }
    } catch {
      /* The page can still open in this browser. */
    }
    window.open(OnlimeUrl, "onlime", "popup,width=1100,height=800");
    setHint("Log ind på Onlime. Kopiér koden, kom tilbage hertil, og tryk Indsæt kode.");
  }

  async function paste() {
    try {
      const text = (await navigator.clipboard.readText()).replace(/\s+/g, "");
      if (text.length < 8) {
        setHint("Udklipsholderen har ikke koden endnu. Kopiér den på Onlime-siden først.");
        return;
      }
      setToken(text);
      setHint("Koden er sat ind. Sync kan starte.");
    } catch {
      setHint("Tryk længe i feltet, og vælg Sæt ind.");
    }
  }

  return (
    <div className="onlime-code">
      <ol>
        <li>Tryk Åbn Onlime, og log ind med din normale bruger.</li>
        <li>Tryk Generate under Personal login token.</li>
        <li>Kopiér koden fra siden. Du skal ikke taste den.</li>
        <li>Luk Onlime, tryk Indsæt kode, og start sync.</li>
      </ol>
      <p className="muted">{OnlimeUrl}</p>
      <textarea aria-label="Onlime login-kode" value={token} onChange={(event) => setToken(event.target.value.replace(/\s+/g, ""))} placeholder="Den kopierede kode sættes ind her" />
      {(hint || message) && <p className={bad ? "warn" : "ok"}>{bad ? message : hint || message}</p>}
      <div className="install-actions">
        <button className="icon-button" type="button" onClick={() => void openOnlime()}>Åbn Onlime</button>
        <button className="quiet-button" type="button" onClick={() => void paste()}>Indsæt kode</button>
        <button className="icon-button" type="button" disabled={busy || token.length < 8} onClick={() => onSubmit(token)}>
          {submitLabel}
        </button>
      </div>
    </div>
  );
}
