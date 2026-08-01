import { makeAutoObservable, observableRef, runInAction } from 'mobx';
import { registerSW } from 'virtual:pwa-register';

/**
 * Fired before the browser shows its own install affordance. Still not in the DOM
 * typings, and only Chromium fires it at all — Safari installs through the share
 * sheet with no event to intercept.
 */
interface BeforeInstallPromptEvent extends Event {
  prompt(): Promise<void>;
  userChoice: Promise<{ outcome: 'accepted' | 'dismissed' }>;
}

/** How often a long-lived tab asks whether a newer build has been deployed. */
const UPDATE_CHECK_INTERVAL_MS = 60 * 60 * 1000;

/**
 * Everything about being an installed app: the service worker's lifecycle, and
 * whether this browser will offer to install us.
 *
 * Service workers require a secure context, which means HTTPS or localhost. Over
 * plain HTTP on a LAN or a tailnet address the API simply is not there, so this
 * store reports that state rather than failing silently — it is the difference
 * between "your browser cannot install this" and "you are reaching it by an
 * address the browser does not trust", and only one of those is fixable.
 */
export class PwaStore {
  /** A new build is waiting. Nothing changes until the user accepts it. */
  updateReady = false;

  /** The shell is cached, so the app will now start without a network. */
  offlineReady = false;

  /** Set when the browser has offered us its install prompt to trigger. */
  installEvent: BeforeInstallPromptEvent | null = null;

  /** True once running from the home screen or an app window rather than a tab. */
  installed = false;

  private applyUpdate: ((reload?: boolean) => Promise<void>) | null = null;

  constructor() {
    makeAutoObservable<this, 'applyUpdate'>(this, {
      // A DOM event is not something to make deeply observable.
      installEvent: observableRef,
      applyUpdate: false,
    });

    this.installed = detectInstalled();
    this.listenForInstallPrompt();
    this.register();
  }

  /**
   * Whether a service worker can run here at all.
   *
   * `isSecureContext` covers the localhost exemption, so this is true on
   * http://localhost and false on http://192.168.x.x without special-casing.
   */
  get serviceWorkerAvailable(): boolean {
    return 'serviceWorker' in navigator && window.isSecureContext;
  }

  /**
   * True when the only thing standing between this page and installability is HTTPS.
   *
   * Note this cannot be written as "the API exists but the context is insecure":
   * browsers delete `navigator.serviceWorker` outright on an insecure origin rather
   * than leaving it present and failing, so that reading is never satisfied and the
   * app would blame the browser for what is actually the address bar.
   */
  get blockedByInsecureOrigin(): boolean {
    return !window.isSecureContext;
  }

  get canInstall(): boolean {
    return this.installEvent !== null;
  }

  private register() {
    if (!this.serviceWorkerAvailable) return;

    this.applyUpdate = registerSW({
      onNeedRefresh: () => runInAction(() => (this.updateReady = true)),
      onOfflineReady: () => runInAction(() => (this.offlineReady = true)),
      onRegisteredSW: (_url, registration) => {
        if (!registration) return;
        // An installed app can sit open for days. Without this it would only notice
        // a new build on a cold start, which for a home-screen app may be never.
        setInterval(() => void registration.update(), UPDATE_CHECK_INTERVAL_MS);
      },
    });
  }

  private listenForInstallPrompt() {
    window.addEventListener('beforeinstallprompt', (event) => {
      // Suppress the browser's own banner so the offer appears in Settings, next to
      // the rest of the app's configuration, instead of over the top of the UI.
      event.preventDefault();
      runInAction(() => (this.installEvent = event as BeforeInstallPromptEvent));
    });

    window.addEventListener('appinstalled', () => {
      runInAction(() => {
        this.installed = true;
        this.installEvent = null;
      });
    });
  }

  /** Shows the browser's install dialogue. Resolves once the user has answered. */
  async install(): Promise<'accepted' | 'dismissed' | 'unavailable'> {
    const event = this.installEvent;
    if (!event) return 'unavailable';

    await event.prompt();
    const { outcome } = await event.userChoice;

    // The event is single-use: a dismissed prompt cannot be re-shown until the
    // browser decides to offer another one.
    runInAction(() => (this.installEvent = null));
    return outcome;
  }

  /** Activates the waiting service worker and reloads into the new build. */
  async update() {
    if (!this.applyUpdate) return;
    runInAction(() => (this.updateReady = false));
    await this.applyUpdate(true);
  }

  dismissUpdate() {
    this.updateReady = false;
  }

  dismissOfflineReady() {
    this.offlineReady = false;
  }
}

function detectInstalled(): boolean {
  if (window.matchMedia('(display-mode: standalone)').matches) return true;
  // iOS predates display-mode and reports it here instead.
  return (window.navigator as { standalone?: boolean }).standalone === true;
}
