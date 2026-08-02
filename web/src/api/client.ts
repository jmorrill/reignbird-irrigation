import type {
  Account,
  Alert,
  AlertPreferences,
  ActivePlan,
  ArmedState,
  CalendarDay,
  Capabilities,
  Controller,
  ControllerState,
  Place,
  Plan,
  PlanPreset,
  PlanRun,
  Program,
  Run,
  SavePlan,
  Session,
  SipExchange,
  SkipDecision,
  SkipEvent,
  SkipSettings,
  UnitPreferences,
  Usage,
  WeatherDay,
  Zone,
} from './types';

/**
 * Always relative. In production the SPA is served by the ASP.NET host; in
 * development the Vite dev server proxies `/api`, `/media` and `/hubs` to it. Both
 * cases are same-origin, so there is no CORS surface at all.
 */
export const API_BASE = '';

/**
 * A failure the interface can show the user directly.
 *
 * The server distinguishes "the controller said no" (422) from "the controller
 * is unreachable" (503) from "wrong password" (401), and those are genuinely
 * different situations for someone standing in their garden — so the message is
 * carried through rather than flattened into "something went wrong".
 */
export class ApiError extends Error {
  readonly status: number;
  readonly title?: string;

  constructor(message: string, status: number, title?: string) {
    super(message);
    this.name = 'ApiError';
    this.status = status;
    this.title = title;
  }

  /** True when retrying might work: the controller was busy or briefly unreachable. */
  get isTransient(): boolean {
    return this.status === 503 || this.status === 502;
  }
}

/**
 * Notified whenever a request proves the server reachable or not.
 *
 * This exists because the app is installable and precached: the shell now loads
 * perfectly well with the server switched off, and every screen would otherwise
 * render its empty state as though the answer were an empty list. "No zones found —
 * the controller did not report any stations" is a claim about someone's hardware,
 * and it must not be made on the strength of a failed fetch.
 */
type ReachabilityListener = (reachable: boolean) => void;

let notifyReachability: ReachabilityListener | null = null;

export function onServerReachabilityChange(listener: ReachabilityListener) {
  notifyReachability = listener;
}

/**
 * The bearer token every request carries, held here rather than read from storage
 * per call so that signing out takes effect on requests already in flight.
 */
let authToken: string | null = null;
let notifySessionEnded: (() => void) | null = null;

export function setAuthToken(token: string | null) {
  authToken = token;
}

export function getAuthToken(): string | null {
  return authToken;
}

/** Called when the server rejects our token — expired, or the account is gone. */
export function onSessionEnded(listener: () => void) {
  notifySessionEnded = listener;
}

function authHeaders(): Record<string, string> {
  return authToken ? { Authorization: `Bearer ${authToken}` } : {};
}

/**
 * A 401 from the login or setup route means "those credentials are wrong" and is the
 * caller's to report. A 401 from anywhere else means the session itself is over, and
 * the whole app has to return to the sign-in screen.
 */
function handleUnauthorized(path: string) {
  if (path.startsWith('/api/auth/login') || path.startsWith('/api/auth/setup')) return;
  notifySessionEnded?.();
}

async function request<T>(path: string, init?: RequestInit): Promise<T> {
  let response: Response;
  try {
    response = await fetch(`${API_BASE}${path}`, {
      ...init,
      headers: {
        ...(init?.body ? { 'Content-Type': 'application/json' } : {}),
        ...authHeaders(),
        ...init?.headers,
      },
    });
  } catch {
    notifyReachability?.(false);
    throw new ApiError('Cannot reach the Reignbird server.', 0, 'Server offline');
  }

  // Any answer at all, including an error status, means the server is there.
  notifyReachability?.(true);

  if (response.status === 401) handleUnauthorized(path);

  if (!response.ok) {
    let message = `Request failed (${response.status}).`;
    let title: string | undefined;
    try {
      const body = await response.json();
      title = body.title;
      message = body.detail ?? body.message ?? message;
    } catch {
      /* Not every error body is JSON; the status alone will have to do. */
    }
    throw new ApiError(message, response.status, title);
  }

  if (response.status === 204 || response.headers.get('content-length') === '0') {
    return undefined as T;
  }

  const text = await response.text();
  return text ? (JSON.parse(text) as T) : (undefined as T);
}

const get = <T>(path: string) => request<T>(path);
const post = <T>(path: string, body?: unknown) =>
  request<T>(path, { method: 'POST', body: body === undefined ? undefined : JSON.stringify(body) });
const put = <T>(path: string, body: unknown) =>
  request<T>(path, { method: 'PUT', body: JSON.stringify(body) });
const del = <T>(path: string) => request<T>(path, { method: 'DELETE' });

/** The picture formats the server stores, and the extension it expects for each. */
export const PHOTO_EXTENSIONS: Readonly<Record<string, string>> = {
  'image/jpeg': '.jpg',
  'image/png': '.png',
  'image/webp': '.webp',
};

/**
 * A filename the server will accept for an uploaded photo.
 *
 * Falls back to whatever the picker supplied when the type is unrecognised, so the
 * server gets to give its own answer about what it does and does not take rather
 * than this quietly inventing an extension for it.
 */
function uploadNameFor(file: File): string {
  const extension = PHOTO_EXTENSIONS[file.type];
  return extension ? `photo${extension}` : file.name;
}

export const api = {
  /** Postcode or place-name lookup, for people who do not know their latitude. */
  places: (query: string) => get<Place[]>(`/api/settings/places?q=${encodeURIComponent(query)}`),

  health: () => get<{ status: string; simulator: boolean; version: string }>('/api/health'),

  auth: {
    status: () => get<{ setupRequired: boolean }>('/api/auth/status'),
    login: (username: string, password: string) =>
      post<Session>('/api/auth/login', { username, password }),
    setup: (username: string, password: string) =>
      post<Session>('/api/auth/setup', { username, password }),
    me: () => get<Account>('/api/auth/me'),
    changePassword: (currentPassword: string, newPassword: string) =>
      post<Session>('/api/auth/password', { currentPassword, newPassword }),
  },

  alerts: {
    key: () => get<{ publicKey: string }>('/api/alerts/key'),
    recent: () => get<Alert[]>('/api/alerts'),
    preferences: () =>
      get<{ preferences: AlertPreferences; subscriptions: number }>('/api/alerts/preferences'),
    savePreferences: (body: AlertPreferences) => put<AlertPreferences>('/api/alerts/preferences', body),
    subscribe: (body: { endpoint: string; p256dh: string; auth: string; description: string }) =>
      post<{ subscriptions: number }>('/api/alerts/subscribe', body),
    unsubscribe: (endpoint: string) =>
      post<{ subscriptions: number }>('/api/alerts/unsubscribe', { endpoint }),
    test: () => post<{ delivered: number; subscriptions: number }>('/api/alerts/test'),
  },

  users: {
    list: () => get<Account[]>('/api/users'),
    create: (username: string, password: string) =>
      post<Account>('/api/users', { username, password }),
    remove: (id: number) => del<void>(`/api/users/${id}`),
  },

  controllers: {
    list: () => get<Controller[]>('/api/controllers'),
    get: (id: number) => get<Controller>(`/api/controllers/${id}`),

    add: (body: {
      host: string;
      password: string;
      name?: string;
      latitude?: number | null;
      longitude?: number | null;
      timeZoneId?: string;
    }) => post<Controller>('/api/controllers', body),

    update: (
      id: number,
      body: Partial<{
        name: string;
        host: string;
        password: string;
        latitude: number;
        longitude: number;
        timeZoneId: string;
      }>,
    ) => put<Controller>(`/api/controllers/${id}`, body),

    remove: (id: number) => del<void>(`/api/controllers/${id}`),
    state: (id: number) => get<ControllerState>(`/api/controllers/${id}/state`),
    capabilities: (id: number) => get<Capabilities>(`/api/controllers/${id}/capabilities`),
    refresh: (id: number) => post<Capabilities>(`/api/controllers/${id}/refresh`),
  },

  zones: {
    list: (id: number) => get<Zone[]>(`/api/controllers/${id}/zones`),
    update: (id: number, station: number, body: Partial<Zone>) =>
      put<Zone>(`/api/controllers/${id}/zones/${station}`, body),

    run: (id: number, station: number, minutes: number) =>
      post<void>(`/api/controllers/${id}/zones/${station}/run`, { minutes }),

    queue: (id: number, station: number, minutes: number) =>
      post<void>(`/api/controllers/${id}/zones/${station}/queue`, { minutes }),

    uploadPhoto: async (id: number, station: number, file: File) => {
      const form = new FormData();
      // Named from the type the browser reported rather than whatever the picker
      // called it. The server validates by extension, and a capture straight from
      // an Android camera frequently arrives as "image" with no extension at all —
      // a perfectly good JPEG, rejected for how it was named. The server renames
      // the file on arrival, so nothing is lost by not keeping the original.
      form.append('file', file, uploadNameFor(file));
      // Not through request(): the body is FormData, and setting a JSON content type
      // would stop the browser writing the multipart boundary.
      const response = await fetch(`${API_BASE}/api/controllers/${id}/zones/${station}/photo`, {
        method: 'POST',
        body: form,
        headers: authHeaders(),
      });
      if (response.status === 401) handleUnauthorized('/api/photo');
      if (!response.ok) {
        // The server says useful things here — which file types it takes, that the
        // file was empty — and this used to replace all of them with "Could not
        // save the photo", leaving no way to find out what was wrong.
        const message = await response
          .json()
          .then((body: { message?: string }) => body?.message)
          .catch(() => undefined);

        throw new ApiError(message ?? 'Could not save the photo.', response.status);
      }
      return (await response.json()) as { photoUrl: string };
    },
  },

  control: {
    stop: (id: number) => post<void>(`/api/controllers/${id}/stop`),
    advance: (id: number) => post<void>(`/api/controllers/${id}/advance`),
    testAll: (id: number, minutes: number) => post<void>(`/api/controllers/${id}/test`, { minutes }),
    getRainDelay: (id: number) => get<{ days: number }>(`/api/controllers/${id}/rain-delay`),
    setRainDelay: (id: number, days: number) =>
      put<{ days: number }>(`/api/controllers/${id}/rain-delay`, { days }),
    setSeasonalAdjust: (id: number, program: number, percent: number) =>
      put<{ program: number; percent: number }>(`/api/controllers/${id}/seasonal-adjust`, {
        program,
        percent,
      }),
    setEnabled: (id: number, enabled: boolean) =>
      put<{ enabled: boolean }>(`/api/controllers/${id}/enabled`, { enabled }),
    syncClock: (id: number) =>
      put<{ synced: string }>(`/api/controllers/${id}/clock`, { useServerTime: true }),
  },

  programs: {
    list: (id: number) => get<Program[]>(`/api/controllers/${id}/programs`),
    get: (id: number, program: number) => get<Program>(`/api/controllers/${id}/programs/${program}`),
    save: (
      id: number,
      program: number,
      body: {
        frequency: string;
        customDays: boolean[];
        cyclicDays: number;
        seasonalAdjustPercent: number;
        startTimes: number[];
        stationRunTimes: Record<string, number>;
      },
    ) => put<Program>(`/api/controllers/${id}/programs/${program}`, body),
    run: (id: number, program: number) => post<void>(`/api/controllers/${id}/programs/${program}/run`),
  },

  history: {
    runs: (id: number, from?: string, to?: string) => {
      const params = new URLSearchParams();
      if (from) params.set('from', from);
      if (to) params.set('to', to);
      const query = params.toString();
      return get<Run[]>(`/api/controllers/${id}/history${query ? `?${query}` : ''}`);
    },
    calendar: (id: number, year: number, month: number) =>
      get<CalendarDay[]>(`/api/controllers/${id}/calendar?year=${year}&month=${month}`),
    usage: (id: number, year?: number, month?: number) => {
      const params = new URLSearchParams();
      if (year) params.set('year', String(year));
      if (month) params.set('month', String(month));
      const query = params.toString();
      return get<Usage>(`/api/controllers/${id}/usage${query ? `?${query}` : ''}`);
    },
  },

  weather: {
    forecast: (id: number) => get<WeatherDay[]>(`/api/controllers/${id}/weather`),
    skips: (id: number) => get<SkipEvent[]>(`/api/controllers/${id}/skips`),
    evaluate: (id: number) => post<SkipDecision>(`/api/controllers/${id}/evaluate-skip`),
  },

  settings: {
    getSkip: () => get<SkipSettings>('/api/settings/skip'),
    setSkip: (body: SkipSettings) => put<SkipSettings>('/api/settings/skip', body),
    getUnits: () => get<UnitPreferences>('/api/settings/units'),
    setUnits: (body: UnitPreferences) => put<UnitPreferences>('/api/settings/units', body),
    timezones: () => get<{ id: string; name: string }[]>('/api/settings/timezones'),
  },

  plans: {
    list: (id: number) => get<Plan[]>(`/api/controllers/${id}/plans`),
    get: (id: number, planId: number) => get<Plan>(`/api/controllers/${id}/plans/${planId}`),
    create: (id: number, body: SavePlan) => post<Plan>(`/api/controllers/${id}/plans`, body),
    update: (id: number, planId: number, body: SavePlan) =>
      put<Plan>(`/api/controllers/${id}/plans/${planId}`, body),
    remove: (id: number, planId: number) => del<void>(`/api/controllers/${id}/plans/${planId}`),
    run: (id: number, planId: number) => post<void>(`/api/controllers/${id}/plans/${planId}/run`),
    cancel: (id: number) => post<{ cancelled: boolean }>(`/api/controllers/${id}/plans/cancel`),
    active: (id: number) => get<ActivePlan | null>(`/api/controllers/${id}/plans/active`),
    runs: (id: number) => get<PlanRun[]>(`/api/controllers/${id}/plan-runs`),
    presets: (id: number) => get<PlanPreset[]>(`/api/controllers/${id}/plan-presets`),
    fromPreset: (id: number, preset: string) =>
      post<Plan>(`/api/controllers/${id}/plans/from-preset`, { preset }),
  },

  arming: {
    state: (id: number) => get<ArmedState>(`/api/controllers/${id}/armed-state`),
    disarm: (id: number) =>
      post<{ cleared: number; message: string }>(`/api/controllers/${id}/disarm`),
  },

  diagnostics: {
    exchanges: (id: number) => get<SipExchange[]>(`/api/controllers/${id}/diagnostics`),
    sendRaw: (id: number, hex: string) =>
      post<{ name: string; hex: string; fields: Record<string, number> }>(
        `/api/controllers/${id}/diagnostics/raw`,
        { hex },
      ),
  },
};
