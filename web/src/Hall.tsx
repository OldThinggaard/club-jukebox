import { useEffect, useState } from "react";
import { api, type Screen } from "./lib/api";

export function Hall() {
  const [screen, setScreen] = useState<Screen | null>(null);
  const [eventIndex, setEventIndex] = useState(0);

  useEffect(() => {
    let stop = false;
    const pull = () => {
      void api.heartbeat();
      api.screen().then((next) => {
        if (!stop) setScreen(next);
      }).catch(() => undefined);
    };
    pull();
    const timer = window.setInterval(pull, 1000);
    return () => {
      stop = true;
      window.clearInterval(timer);
    };
  }, []);

  useEffect(() => {
    const timer = window.setInterval(() => setEventIndex((current) => current + 1), 8000);
    return () => window.clearInterval(timer);
  }, []);

  const events = screen?.events ?? [];
  const event = events.length ? events[eventIndex % events.length] : null;
  const backdrop = event?.imageUrl || screen?.logoUrl;

  return (
    <main className="hall">
      {backdrop && <img className="hall-bg" src={backdrop} alt="" />}
      <div className="hall-shade" />
      <section className="hall-copy">
        {screen?.showClubName && screen.clubName ? <p className="eyebrow">{screen.clubName}</p> : null}
        <h1>{screen?.clock ?? "—"}</h1>
        <p className="date">{screen?.dateLabel}</p>
        <div className="welcome-row">
          <div>
            <p>{screen?.welcomeMessage}</p>
            {event && <p className="muted">{event.title}</p>}
            <p className="muted">{screen?.instructions}</p>
          </div>
          <div className="qr-card">
            <img src="/api/qr.svg" alt="QR code for the jukebox" />
            <p>Scan to choose a film</p>
          </div>
        </div>
      </section>
    </main>
  );
}
