import { makeAutoObservable, runInAction } from 'mobx';
import { api, onSessionEnded, setAuthToken } from '../api/client';
import { ApiError } from '../api/client';
import type { Account } from '../api/types';

const TOKEN_KEY = 'reignbird.token';

/** What the app should be showing before it will show anything else. */
export type Gate = 'checking' | 'setup' | 'login' | 'ready';

/**
 * Who is signed in.
 *
 * The token lives in localStorage, which is the usual trade for bearer tokens: it
 * survives a reload, and script running on the page could read it. That is an
 * acceptable position for an app served same-origin with no third-party scripts and
 * no ad tags, and it is why the token carries a security stamp — a password change
 * revokes every outstanding token straight away rather than leaving a stolen one
 * valid for a month.
 */
export class AuthStore {
  gate: Gate = 'checking';
  user: Account | null = null;

  /** Set while a sign-in or setup request is in flight. */
  busy = false;

  /** The last failure, shown on the sign-in form. */
  error: string | null = null;

  /** Other accounts, loaded on demand by the settings screen. */
  accounts: Account[] = [];

  constructor() {
    makeAutoObservable(this);

    // Adopt any stored token before the first request goes out, so a reload does
    // not flash the sign-in screen at someone who is already signed in.
    const stored = localStorage.getItem(TOKEN_KEY);
    if (stored) setAuthToken(stored);

    // The server gets the final say: if it ever rejects the token, the session is
    // over no matter what localStorage still holds.
    onSessionEnded(() => this.endSession());
  }

  get signedIn(): boolean {
    return this.gate === 'ready';
  }

  /**
   * Works out what to show first.
   *
   * Order matters. A stored token is tried before anything else so the common case
   * costs one request; only if there is no token, or it is no longer any good, does
   * the app ask whether it needs a sign-in screen or a first-run setup screen.
   */
  async start() {
    if (localStorage.getItem(TOKEN_KEY)) {
      try {
        const user = await api.auth.me();
        runInAction(() => {
          this.user = user;
          this.gate = 'ready';
        });
        return;
      } catch {
        // Expired, revoked, or the account is gone. Fall through and ask.
        this.clearToken();
      }
    }

    await this.refreshGate();
  }

  private async refreshGate() {
    try {
      const { setupRequired } = await api.auth.status();
      runInAction(() => (this.gate = setupRequired ? 'setup' : 'login'));
    } catch {
      // The server is unreachable. Showing the sign-in screen is the honest
      // outcome — the offline banner explains the rest.
      runInAction(() => (this.gate = 'login'));
    }
  }

  async signIn(username: string, password: string) {
    await this.attempt(() => api.auth.login(username, password));
  }

  async createFirstAccount(username: string, password: string) {
    await this.attempt(() => api.auth.setup(username, password));
  }

  private async attempt(request: () => Promise<{ token: string; user: Account }>) {
    runInAction(() => {
      this.busy = true;
      this.error = null;
    });

    try {
      const session = await request();
      this.adopt(session.token, session.user);
    } catch (error) {
      runInAction(() => {
        this.error =
          error instanceof ApiError ? error.message : 'Something went wrong. Try again.';
      });
    } finally {
      runInAction(() => (this.busy = false));
    }
  }

  private adopt(token: string, user: Account) {
    localStorage.setItem(TOKEN_KEY, token);
    setAuthToken(token);

    runInAction(() => {
      this.user = user;
      this.error = null;
      this.gate = 'ready';
    });
  }

  signOut() {
    this.endSession();
  }

  /** Drops the session and returns to the sign-in screen. */
  private endSession() {
    this.clearToken();
    runInAction(() => {
      this.user = null;
      this.accounts = [];
      this.gate = 'login';
    });
  }

  private clearToken() {
    localStorage.removeItem(TOKEN_KEY);
    setAuthToken(null);
  }

  /**
   * Changing a password revokes every token issued under the old one — including
   * the one making this request — so the server hands back a fresh session and it
   * has to be adopted, or the user would be signed out by their own password change.
   */
  async changePassword(currentPassword: string, newPassword: string) {
    const session = await api.auth.changePassword(currentPassword, newPassword);
    this.adopt(session.token, session.user);
  }

  async loadAccounts() {
    const accounts = await api.users.list();
    runInAction(() => (this.accounts = accounts));
  }

  async addAccount(username: string, password: string) {
    await api.users.create(username, password);
    await this.loadAccounts();
  }

  async removeAccount(id: number) {
    await api.users.remove(id);
    await this.loadAccounts();
  }
}
