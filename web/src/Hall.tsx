import { useEffect, useState } from "react";
import { api, type Screen } from "./lib/api";
import { percent } from "./lib/utils";

export function Hall() {
  const [screen, setScreen] = useState<Screen | null>(null);
  const [eventIndex, setEventIndex] = useState(0);
  const [hideVideo, setHideVideo] = useState(false);

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

  useEffect(() => setHideVideo(false), [screen?.nowPlaying?.mediaId]);

  const playing = screen?.nowPlaying;
  const events = screen?.events ?? [];
  const event = events.length ? events[eventIndex % events.length] : null;
  const backdrop = playing?.thumbnailUrl || event?.imageUrl || screen?.logoUrl;
  const showBrowserMedia = screen?.engine !== "mpv" && playing && !hideVideo;

  return (
    <main className="hall">
      {backdrop && <img className="hall-bg" src={backdrop} alt="" />}
      <div className="hall-shade" />
      {showBrowserMedia && playing.mediaType === "video" && (
        <video className="hall-video" src={playing.contentUrl} autoPlay playsInline onError={() => setHideVideo(true)} />
      )}
      {showBrowserMedia && playing.mediaType === "audio" && (
        <audio src={playing.contentUrl} autoPlay onError={() => setHideVideo(true)} />
      )}
      {showBrowserMedia && playing.mediaType === "image" && (
        <img className="hall-audio-art" src={playing.contentUrl} alt="" />
      )}
      <section className="hall-copy">
        <p className="eyebrow">{screen?.clubName ?? "Clubhouse"}</p>
        {playing ? (
          <div className="play-meta">
            <p className="date">{playing.clientLabel === "reklame" ? "Reklame" : playing.origin === "auto" ? "House selection" : "From the queue"}</p>
            <h2>{playing.title}</h2>
            <p>{playing.artist}</p>
            <div className="track" aria-hidden>
              <span style={{ width: `${percent(playing.positionMs, playing.durationMs)}%` }} />
            </div>
          </div>
        ) : (
          <>
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
          </>
        )}
      </section>
    </main>
  );
}
