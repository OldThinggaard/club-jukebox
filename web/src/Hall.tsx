import { useEffect, useRef, useState, type CSSProperties } from "react";
import { api, type NowPlaying, type Screen } from "./lib/api";
import { songTextVisible } from "./lib/songText";

export function Hall() {
  const [screen, setScreen] = useState<Screen | null>(null);
  const [eventIndex, setEventIndex] = useState(0);
  const [hideVideo, setHideVideo] = useState(false);
  const [tick, setTick] = useState(() => Date.now());
  const videoRef = useRef<HTMLVideoElement | null>(null);
  const audioRef = useRef<HTMLAudioElement | null>(null);
  const anchor = useRef({ queueId: "", at: 0, position: 0, server: -1 });

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

  useEffect(() => {
    const timer = window.setInterval(() => setTick(Date.now()), 250);
    return () => window.clearInterval(timer);
  }, []);

  const playing = screen?.nowPlaying;

  function songEnded(queueId: string | undefined) {
    if (!queueId) return;
    void api.finished(queueId);
  }

  const events = screen?.events ?? [];
  const event = events.length ? events[eventIndex % events.length] : null;
  const backdrop = playing?.thumbnailUrl || event?.imageUrl || screen?.logoUrl;
  const showBrowserMedia = screen?.engine !== "mpv" && playing && !hideVideo;
  const clock = playbackClock(playing, showBrowserMedia ? videoRef.current ?? audioRef.current : null, tick, anchor.current);
  const showSongText = Boolean(playing && screen?.engine !== "mpv" && songTextVisible(clock.positionMs, clock.durationMs));
  const clubName = screen?.clubName?.trim() ?? "";
  const showClubMark = Boolean(!playing && screen?.showClubName && clubName);

  return (
    <main className="hall">
      {backdrop && <img className="hall-bg" src={backdrop} alt="" />}
      {(!playing || showSongText) && <div className="hall-shade" />}
      {showClubMark && (
        <p className="hall-mark" style={clubMark(clubName)} aria-hidden="true">{clubName}</p>
      )}
      {showBrowserMedia && playing.mediaType === "video" && (
        <video ref={videoRef} key={playing.queueId} className="hall-video" src={playing.contentUrl} autoPlay playsInline onEnded={() => songEnded(playing.queueId)} onError={() => setHideVideo(true)} />
      )}
      {showBrowserMedia && playing.mediaType === "audio" && (
        <audio ref={audioRef} key={playing.queueId} src={playing.contentUrl} autoPlay preload="auto" onError={() => setHideVideo(true)} onEnded={() => { setHideVideo(true); songEnded(playing.queueId); }} />
      )}
      {showBrowserMedia && playing.mediaType === "image" && (
        <img className="hall-audio-art" src={playing.contentUrl} alt="" />
      )}
      <section className="hall-copy">
        {showSongText && playing ? (
          <div className="play-meta song-text">
            <h2>{playing.title}</h2>
            <p>{playing.artist}</p>
          </div>
        ) : playing ? null : (
          <>
            <h1>{screen?.clock ?? "—"}</h1>
            <p className="date">{screen?.dateLabel}</p>
            <div className="welcome-row">
              <div>
                <p>{screen?.welcomeMessage}</p>
                {event && <p className="muted">{event.title}</p>}
                <p className="muted">{screen?.instructions}</p>
              </div>
              {screen?.showQr !== false && (
                <div className="qr-card">
                  <img src="/api/qr.svg" alt="QR code for the jukebox" />
                  <p>Scan to choose a song</p>
                </div>
              )}
            </div>
          </>
        )}
      </section>
    </main>
  );
}

function clubMark(name: string): CSSProperties {
  const chars = Math.max(Array.from(name).length, 1);
  const widthFactor = 0.56;
  const fontVw = Math.min(10, 90 / (chars * widthFactor));
  const naturalVw = chars * widthFactor * fontVw;
  const gaps = Math.max(chars - 1, 1);
  const trackingEm = chars > 1 ? Math.min(0.6, Math.max(0, (90 - naturalVw) / fontVw / gaps)) : 0;
  return {
    fontSize: `${fontVw}vw`,
    letterSpacing: `${trackingEm.toFixed(3)}em`,
  };
}

function playbackClock(
  playing: NowPlaying | null | undefined,
  media: HTMLMediaElement | null,
  now: number,
  anchor: { queueId: string; at: number; position: number; server: number },
) {
  if (!playing)
    return { positionMs: 0, durationMs: 0 };
  if (media && playing.mediaType !== "image" && Number.isFinite(media.currentTime)) {
    const duration = Number.isFinite(media.duration) ? media.duration * 1000 : playing.durationMs;
    return { positionMs: media.currentTime * 1000, durationMs: duration || playing.durationMs };
  }
  if (anchor.queueId !== playing.queueId || anchor.server !== playing.positionMs) {
    anchor.queueId = playing.queueId;
    anchor.at = now;
    anchor.position = playing.positionMs;
    anchor.server = playing.positionMs;
  }
  return { positionMs: anchor.position + Math.max(0, now - anchor.at), durationMs: playing.durationMs };
}
