export function WifiChip({ ssid, signal }: { ssid: string; signal: number }) {
  return (
    <p className="wifi-chip" aria-label={`Trådløst netværk ${ssid}`}>
      <WifiMark signal={signal} />
      <span>{ssid}</span>
    </p>
  );
}

function WifiMark({ signal }: { signal: number }) {
  const level = signal >= 75 ? 3 : signal >= 50 ? 2 : signal >= 25 ? 1 : 0;
  return (
    <svg className="wifi-mark" viewBox="0 0 24 24" aria-hidden="true">
      <circle cx="12" cy="18.2" r="1.5" fill="currentColor" />
      <path d="M7.4 14.4a6.5 6.5 0 0 1 9.2 0" fill="none" stroke="currentColor" strokeWidth="1.8" strokeLinecap="round" opacity={level >= 1 ? 1 : 0.28} />
      <path d="M4.5 11.3a10.4 10.4 0 0 1 15 0" fill="none" stroke="currentColor" strokeWidth="1.8" strokeLinecap="round" opacity={level >= 2 ? 1 : 0.28} />
      <path d="M1.7 8.2a14.2 14.2 0 0 1 20.6 0" fill="none" stroke="currentColor" strokeWidth="1.8" strokeLinecap="round" opacity={level >= 3 ? 1 : 0.28} />
    </svg>
  );
}
