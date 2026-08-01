import { makeAutoObservable, runInAction } from 'mobx';
import { api } from '../api/client';
import type { Alert, AlertPreferences } from '../api/types';

/**
 * Notifications: whether this device receives them, which ones, and what has been
 * sent.
 *
 * Push has an unusual number of ways to be quietly switched off — the browser's
 * permission, the operating system's per-app setting, an expired subscription, a
 * service worker that never registered because the page is not on HTTPS. None of
 * them announce themselves, and most look identical from in here: nothing arrives.
 * Which is why the "send a test" button exists, and why the alert list is kept
 * server-side whether or not anything was delivered.
 */
export class AlertStore {
  /** What the browser says: 'default' (never asked), 'granted', or 'denied'. */
  permission: NotificationPermission = 'default';

  /** True when this browser has an active subscription with our server. */
  subscribed = false;

  /** How many devices in total are subscribed, across every browser. */
  deviceCount = 0;

  preferences: AlertPreferences | null = null;
  recent: Alert[] = [];

  busy = false;
  loaded = false;

  constructor() {
    makeAutoObservable(this);
    if (this.supported) this.permission = Notification.permission;
  }

  /**
   * Whether push can work here at all.
   *
   * `PushManager` is missing outright on an insecure origin, the same way the service
   * worker is — so this is false over plain HTTP, and the settings panel says why
   * rather than offering a switch that cannot work.
   */
  get supported(): boolean {
    return 'Notification' in window && 'serviceWorker' in navigator && 'PushManager' in window;
  }

  get blocked(): boolean {
    return this.permission === 'denied';
  }

  async load() {
    try {
      const [{ preferences, subscriptions }, recent] = await Promise.all([
        api.alerts.preferences(),
        api.alerts.recent(),
      ]);

      const endpoint = await this.currentEndpoint();

      runInAction(() => {
        this.preferences = preferences;
        this.deviceCount = subscriptions;
        this.recent = recent;
        this.subscribed = endpoint !== null;
        if (this.supported) this.permission = Notification.permission;
      });
    } catch {
      /* The connection banner covers it. */
    } finally {
      runInAction(() => (this.loaded = true));
    }
  }

  private async currentEndpoint(): Promise<string | null> {
    if (!this.supported) return null;

    const registration = await navigator.serviceWorker.getRegistration();
    const existing = await registration?.pushManager.getSubscription();
    return existing?.endpoint ?? null;
  }

  /**
   * Asks permission, subscribes with the browser's push service, and registers the
   * result with our server.
   */
  async enable(): Promise<string | null> {
    if (!this.supported) return 'This browser cannot receive push notifications.';

    runInAction(() => (this.busy = true));
    try {
      const permission = await Notification.requestPermission();
      runInAction(() => (this.permission = permission));

      if (permission !== 'granted') {
        return permission === 'denied'
          ? 'Notifications are blocked for this site. Allow them in your browser settings first.'
          : null;
      }

      const registration = await navigator.serviceWorker.ready;
      const { publicKey } = await api.alerts.key();

      const subscription = await registration.pushManager.subscribe({
        // Required by every browser: a push must result in something visible.
        userVisibleOnly: true,
        applicationServerKey: decodeKey(publicKey),
      });

      const json = subscription.toJSON();
      const { subscriptions } = await api.alerts.subscribe({
        endpoint: subscription.endpoint,
        p256dh: json.keys?.p256dh ?? '',
        auth: json.keys?.auth ?? '',
        description: describeThisDevice(),
      });

      runInAction(() => {
        this.subscribed = true;
        this.deviceCount = subscriptions;
      });

      return null;
    } catch (error) {
      return error instanceof Error ? error.message : 'Could not turn notifications on.';
    } finally {
      runInAction(() => (this.busy = false));
    }
  }

  async disable() {
    runInAction(() => (this.busy = true));
    try {
      const registration = await navigator.serviceWorker.getRegistration();
      const subscription = await registration?.pushManager.getSubscription();

      if (subscription) {
        // Told to forget it first, then unsubscribed locally: the other order can
        // leave the server pushing to an endpoint nothing is listening on.
        const { subscriptions } = await api.alerts.unsubscribe(subscription.endpoint);
        await subscription.unsubscribe();
        runInAction(() => (this.deviceCount = subscriptions));
      }

      runInAction(() => (this.subscribed = false));
    } finally {
      runInAction(() => (this.busy = false));
    }
  }

  async savePreferences(preferences: AlertPreferences) {
    runInAction(() => (this.preferences = preferences));
    await api.alerts.savePreferences(preferences);
  }

  /** Returns how many devices the test actually reached. */
  async sendTest(): Promise<number> {
    const { delivered } = await api.alerts.test();
    await this.load();
    return delivered;
  }
}

/** "Chrome on Windows", near enough to tell two devices apart in a list. */
function describeThisDevice(): string {
  const agent = navigator.userAgent;

  const browser =
    /Edg\//.test(agent) ? 'Edge'
    : /Chrome\//.test(agent) ? 'Chrome'
    : /Firefox\//.test(agent) ? 'Firefox'
    : /Safari\//.test(agent) ? 'Safari'
    : 'Browser';

  const platform =
    /Android/.test(agent) ? 'Android'
    : /iPhone|iPad/.test(agent) ? 'iOS'
    : /Mac/.test(agent) ? 'macOS'
    : /Windows/.test(agent) ? 'Windows'
    : /Linux/.test(agent) ? 'Linux'
    : 'device';

  return `${browser} on ${platform}`;
}

/**
 * The VAPID public key travels as base64url text and has to reach `subscribe` as
 * bytes.
 */
function decodeKey(base64Url: string): ArrayBuffer {
  const padded = base64Url.padEnd(base64Url.length + ((4 - (base64Url.length % 4)) % 4), '=');
  const binary = atob(padded.replace(/-/g, '+').replace(/_/g, '/'));

  // Allocated rather than built with Uint8Array.from, so its backing store is known
  // to be a plain ArrayBuffer — subscribe() will not take one that might be shared.
  const bytes = new Uint8Array(binary.length);
  for (let index = 0; index < binary.length; index += 1) bytes[index] = binary.charCodeAt(index);

  return bytes.buffer;
}
