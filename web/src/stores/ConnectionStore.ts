import { makeAutoObservable, runInAction } from 'mobx';
import { api, onServerReachabilityChange } from '../api/client';
import type { RootStore } from './RootStore';

/** Waits before probing again, growing but never getting silly about it. */
const BACKOFF_MS = [0, 1_000, 2_000, 5_000, 10_000, 15_000];

/** How many attempts may fail before the quiet "reconnecting" becomes a real warning. */
const ATTEMPTS_BEFORE_ALARM = 3;

export type ConnectionState = 'online' | 'reconnecting' | 'offline';

/**
 * Whether the app can reach its own server, and getting back to it when it cannot.
 *
 * Deliberately not `navigator.onLine`. That reports whether the machine has a
 * network interface up, which says nothing about whether this server is answering —
 * and over a tailnet or a home LAN, being "online" while the server is unreachable
 * is the normal failure, not the exotic one. The signal comes instead from the
 * requests the app is already making.
 *
 * The important part is what happens next. A phone that has had the app in the
 * background for an hour comes back to a failed request every time, and announcing
 * "cannot reach the server" at that moment is both alarming and, a few hundred
 * milliseconds later, wrong. So a lost connection is first just "reconnecting": it
 * retries immediately, then backs off, and only calls itself offline once several
 * attempts in a row have failed. Coming back to the app or regaining a network
 * interface both prompt an immediate attempt rather than waiting out a backoff.
 */
export class ConnectionStore {
  state: ConnectionState = 'online';

  /** Consecutive failed probes. Reset by any success. */
  private attempts = 0;
  private timer: ReturnType<typeof setTimeout> | null = null;
  private probing = false;

  private readonly root: RootStore;

  constructor(root: RootStore) {
    this.root = root;

    makeAutoObservable<this, 'root' | 'attempts' | 'timer' | 'probing'>(this, {
      root: false,
      attempts: false,
      timer: false,
      probing: false,
    });

    onServerReachabilityChange((reachable) => {
      if (reachable) this.markReached();
      else this.markLost();
    });

    // Coming back to a backgrounded tab is the single most likely moment for the
    // first request to fail, so it is also the moment most worth retrying at once.
    document.addEventListener('visibilitychange', () => {
      if (document.visibilityState === 'visible') this.recheckNow();
    });

    window.addEventListener('online', () => this.recheckNow());
  }

  /** True while the app still believes it can talk to the server. */
  get reachable(): boolean {
    return this.state === 'online';
  }

  /** True while trying to get back, before it is worth worrying anybody. */
  get reconnecting(): boolean {
    return this.state === 'reconnecting';
  }

  /** Tries again now, whatever the backoff was going to say. */
  recheckNow() {
    if (this.state === 'online') return;
    this.clearTimer();
    void this.probe();
  }

  private markReached() {
    this.clearTimer();
    this.attempts = 0;

    if (this.state !== 'online') runInAction(() => (this.state = 'online'));
  }

  private markLost() {
    // Already on it. Restarting would reset the backoff and hammer a server that is
    // very likely still down.
    if (this.state !== 'online') return;

    runInAction(() => (this.state = 'reconnecting'));
    this.attempts = 0;
    this.scheduleProbe();
  }

  private scheduleProbe() {
    this.clearTimer();
    const wait = BACKOFF_MS[Math.min(this.attempts, BACKOFF_MS.length - 1)];
    this.timer = setTimeout(() => void this.probe(), wait);
  }

  /**
   * Asks the health endpoint, which is anonymous and cheap, rather than re-running
   * whatever request failed. On success the app's data is reloaded, because whatever
   * failed while the connection was gone left the screen holding something older
   * than it now needs to be.
   */
  private async probe() {
    if (this.probing) return;
    this.probing = true;

    try {
      await api.health();
      this.markReached();
      await this.root.refresh();
    } catch {
      this.attempts += 1;

      if (this.attempts >= ATTEMPTS_BEFORE_ALARM && this.state !== 'offline') {
        runInAction(() => (this.state = 'offline'));
      }

      // Keeps trying even after giving up out loud, so it recovers on its own when
      // the server comes back rather than waiting to be told.
      this.scheduleProbe();
    } finally {
      this.probing = false;
    }
  }

  private clearTimer() {
    if (this.timer === null) return;
    clearTimeout(this.timer);
    this.timer = null;
  }
}
