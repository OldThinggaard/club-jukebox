export type Media = {
  id: string;
  title: string;
  artist: string;
  album: string;
  genre: string;
  year: number | null;
  decade: string | null;
  mediaType: string;
  durationMs: number | null;
  thumbnailUrl: string | null;
  canQueue: boolean;
  blockReason: string | null;
};

export type QueueCard = {
  id: string;
  mediaId: string;
  title: string;
  artist: string;
  mediaType: string;
  thumbnailUrl: string | null;
  origin: string;
};

export type NowPlaying = {
  mediaId: string;
  title: string;
  artist: string;
  album: string;
  mediaType: string;
  thumbnailUrl: string | null;
  contentUrl: string;
  positionMs: number;
  durationMs: number;
  origin: string;
};

export type Screen = {
  clubName: string;
  welcomeMessage: string;
  instructions: string;
  publicUrl: string;
  clock: string;
  dateLabel: string;
  serverTime: string;
  queue: QueueCard[];
  nowPlaying: NowPlaying | null;
  events: { id: string; title: string; imageUrl: string | null }[];
  logoUrl: string | null;
  engine: string;
  phase: string;
  libraryCount: number;
  indexing: boolean;
  version: string;
  cooldownMinutes: number;
};

export type ArtistGroup = {
  letter: string;
  artists: { name: string; count: number }[];
};

export type Category = { id: string; name: string; kind: string; count: number; isHidden?: boolean; isAuto?: boolean; slug?: string };

async function read<T>(response: Response): Promise<T> {
  if (!response.ok) {
    const text = await response.text();
    throw new Error(text || `Request failed (${response.status})`);
  }
  return response.json() as Promise<T>;
}

export const api = {
  screen: () => fetch("/api/screen").then((response) => read<Screen>(response)),
  media: (params: Record<string, string>) => {
    const query = new URLSearchParams(params);
    return fetch(`/api/media?${query}`).then((response) => read<Media[]>(response));
  },
  categories: () => fetch("/api/categories").then((response) => read<Category[]>(response)),
  artists: () => fetch("/api/artists").then((response) => read<{ alphabet: string; groups: ArtistGroup[] }>(response)),
  enqueue: (mediaId: string, origin: "user" | "mobile") =>
    fetch("/api/queue", {
      method: "POST",
      headers: { "Content-Type": "application/json" },
      body: JSON.stringify({ mediaId, origin, clientLabel: origin === "mobile" ? "phone" : "touchscreen" }),
    }).then((response) => read<{ ok: boolean; reason?: string }>(response)),
  activity: () => fetch("/api/activity", { method: "POST" }),
  heartbeat: () => fetch("/api/hall/heartbeat", { method: "POST" }),
};
