export const SONG_TEXT_MS = 15_000;

export function songTextVisible(positionMs: number, durationMs: number): boolean {
  const position = Math.max(0, positionMs);
  if (position < SONG_TEXT_MS)
    return true;
  return durationMs > 0 && durationMs - position <= SONG_TEXT_MS;
}
