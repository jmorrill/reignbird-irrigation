import { makeAutoObservable, runInAction } from 'mobx';
import * as signalR from '@microsoft/signalr';
import { API_BASE, ApiError, api, getAuthToken } from '../api/client';
import type { Capabilities, Controller, ControllerState } from '../api/types';
import type { RootStore } from './RootStore';

/**
 * Controllers, the selected one, its live state, and the SignalR connection that
 * keeps that state fresh.
 *
 * The countdown is ticked locally between server pushes. The controller only
 * reports remaining seconds when polled, and a timer that visibly jumps in
 * five-second steps looks broken even though the data is correct.
 */
/** Remembers which controller was last in view, across reloads. */
const SELECTED_KEY = 'rainbird.selectedController';

export class ControllerStore {
  controllers: Controller[] = [];
  selectedId: number | null = null;
  capabilities: Capabilities | null = null;
  state: ControllerState | null = null;

  loading = true;
  connected = false;
  error: string | null = null;

  private hub: signalR.HubConnection | null = null;
  private ticker: number | null = null;
  private subscribedTo: number | null = null;
  private readonly root: RootStore;

  constructor(root: RootStore) {
    this.root = root;
    // The connection handle, timer id and subscription bookkeeping are plumbing;
    // observing them would only cause needless reactions.
    makeAutoObservable<ControllerStore, 'root' | 'hub' | 'ticker' | 'subscribedTo'>(
      this,
      { root: false, hub: false, ticker: false, subscribedTo: false },
      { autoBind: true },
    );
  }

  get selected(): Controller | null {
    return this.controllers.find((c) => c.id === this.selectedId) ?? null;
  }

  get isWatering(): boolean {
    return this.state?.isWatering ?? false;
  }

  get activeStation(): number {
    return this.state?.activeStation ?? 0;
  }

  get remainingSeconds(): number {
    return this.state?.remainingRuntimeSeconds ?? 0;
  }

  get online(): boolean {
    return this.selected?.online ?? false;
  }

  get rainDelayDays(): number {
    return this.state?.rainDelayDays ?? 0;
  }

  get sensorWet(): boolean {
    return this.state?.sensorState === 'Wet';
  }

  async load() {
    this.loading = true;
    try {
      const controllers = await api.controllers.list();
      runInAction(() => {
        this.controllers = controllers;
        this.error = null;

        if (this.selectedId === null && controllers.length > 0) {
          // Reopen on whichever controller was last in view. Landing on the first one
          // every time means anyone with more than one starts on the wrong yard.
          const remembered = Number(localStorage.getItem(SELECTED_KEY));
          const known = controllers.some((c) => c.id === remembered);
          this.selectedId = known ? remembered : controllers[0].id;
        }
        // Done loading as soon as we know *which* controllers exist. Waiting for the
        // live state as well means an unreachable controller leaves the whole screen
        // on skeletons, when the app already knows enough to show it as offline.
        this.loading = false;
      });

      if (this.selectedId !== null) await this.selectController(this.selectedId);
    } catch (error) {
      runInAction(() => {
        this.error = error instanceof ApiError ? error.message : 'Could not load controllers.';
        this.loading = false;
      });
    }
  }

  async selectController(id: number) {
    this.selectedId = id;
    localStorage.setItem(SELECTED_KEY, String(id));
    this.state = this.controllers.find((c) => c.id === id)?.state ?? null;

    await Promise.all([this.loadCapabilities(id), this.refreshState()]);
    await this.subscribe(id);
    await this.root.onControllerChanged(id);
  }

  private async loadCapabilities(id: number) {
    try {
      const capabilities = await api.controllers.capabilities(id);
      runInAction(() => {
        this.capabilities = capabilities;
      });
    } catch {
      runInAction(() => {
        this.capabilities = null;
      });
    }
  }

  async refreshState() {
    if (this.selectedId === null) return;
    try {
      const state = await api.controllers.state(this.selectedId);
      runInAction(() => {
        this.state = state;
        this.error = null;
        this.markOnline(true);
      });
    } catch (error) {
      runInAction(() => {
        this.markOnline(false);
        if (error instanceof ApiError && !error.isTransient) this.error = error.message;
      });
    }
  }

  private markOnline(online: boolean) {
    const controller = this.controllers.find((c) => c.id === this.selectedId);
    if (controller) controller.online = online;
  }

  // ------------------------------------------------------------------- live

  private async subscribe(id: number) {
    await this.ensureHub();
    if (!this.hub || this.hub.state !== signalR.HubConnectionState.Connected) return;

    try {
      if (this.subscribedTo !== null && this.subscribedTo !== id) {
        await this.hub.invoke('Unsubscribe', this.subscribedTo);
      }
      await this.hub.invoke('Subscribe', id);
      this.subscribedTo = id;
    } catch {
      /* The polling fallback keeps the UI current if the hub is unavailable. */
    }
  }

  private async ensureHub() {
    if (this.hub) return;

    const hub = new signalR.HubConnectionBuilder()
      // A WebSocket handshake cannot carry an Authorization header, so SignalR puts
      // the token in the query string instead. Read per attempt rather than captured
      // once, so a reconnect after a password change uses the new token.
      .withUrl(`${API_BASE}/hubs/controller`, { accessTokenFactory: () => getAuthToken() ?? '' })
      .withAutomaticReconnect()
      .configureLogging(signalR.LogLevel.Warning)
      .build();

    hub.on('stateChanged', (payload: { controllerId: number; state: ControllerState | null; online: boolean }) => {
      if (payload.controllerId !== this.selectedId) return;
      runInAction(() => {
        if (payload.state) this.state = payload.state;
        this.markOnline(payload.online);
      });
    });

    hub.on('runStarted', (payload: { controllerId: number; station: number }) => {
      if (payload.controllerId !== this.selectedId) return;
      const zone = this.root.zones.byStation(payload.station);
      this.root.ui.notify('info', `${zone?.name ?? `Zone ${payload.station}`} is watering`);
      void this.root.zones.load(payload.controllerId);
    });

    hub.on('runCompleted', (payload: { controllerId: number; station: number; durationSeconds: number }) => {
      if (payload.controllerId !== this.selectedId) return;
      const zone = this.root.zones.byStation(payload.station);
      const minutes = Math.max(1, Math.round(payload.durationSeconds / 60));
      this.root.ui.notify('good', `${zone?.name ?? `Zone ${payload.station}`} finished`, `${minutes} min`);
      void this.root.zones.load(payload.controllerId);
      void this.root.history.load(payload.controllerId);
    });

    hub.on('planStarted', (payload: { controllerId: number; plan: string }) => {
      if (payload.controllerId !== this.selectedId) return;
      this.root.ui.notify('info', `${payload.plan} started`);
      void this.root.plans.refreshActive();
    });

    hub.on('planStep', (payload: {
      controllerId: number;
      plan: string;
      station: number;
      minutes: number;
      step: number;
      of: number;
    }) => {
      if (payload.controllerId !== this.selectedId) return;
      void this.root.plans.refreshActive();
    });

    hub.on('planFinished', (payload: { controllerId: number; plan: string; status: string; detail?: string }) => {
      if (payload.controllerId !== this.selectedId) return;
      const tone = payload.status === 'Completed' ? 'good' : payload.status === 'Failed' ? 'bad' : 'warn';
      this.root.ui.notify(tone, `${payload.plan} ${payload.status.toLowerCase()}`, payload.detail ?? undefined);
      void this.root.plans.refreshActive();
      void this.root.history.load(payload.controllerId);
    });

    hub.on('planSkipped', (payload: { controllerId: number; plan: string; detail?: string }) => {
      if (payload.controllerId !== this.selectedId) return;
      this.root.ui.notify('warn', `${payload.plan} skipped`, payload.detail ?? undefined);
    });

    hub.onreconnected(() => {
      runInAction(() => {
        this.connected = true;
      });
      if (this.selectedId !== null) void this.subscribe(this.selectedId);
    });

    hub.onclose(() => {
      runInAction(() => {
        this.connected = false;
      });
    });

    try {
      await hub.start();
      runInAction(() => {
        this.hub = hub;
        this.connected = true;
      });
    } catch {
      runInAction(() => {
        this.connected = false;
      });
    }
  }

  /**
   * Ticks the countdown locally and re-polls periodically. The poll is the
   * safety net for when SignalR is unavailable; the tick is what makes the
   * countdown read as a live clock rather than a stepped value.
   */
  startTicking() {
    if (this.ticker !== null) return;

    let sincePoll = 0;
    this.ticker = window.setInterval(() => {
      runInAction(() => {
        if (this.state && this.state.remainingRuntimeSeconds > 0) {
          this.state = {
            ...this.state,
            remainingRuntimeSeconds: Math.max(0, this.state.remainingRuntimeSeconds - 1),
          };
        }
      });

      sincePoll += 1;
      const interval = this.connected ? 15 : 5;
      if (sincePoll >= interval) {
        sincePoll = 0;
        void this.refreshState();
        // A plan advances zone by zone; without this the UI would only move when
        // the hub delivered an event.
        if (this.root.plans.active) void this.root.plans.refreshActive();
      }
    }, 1000);
  }

  stopTicking() {
    if (this.ticker !== null) {
      window.clearInterval(this.ticker);
      this.ticker = null;
    }
  }

  // ---------------------------------------------------------------- control

  async stop() {
    if (this.selectedId === null) return;
    await this.perform(() => api.control.stop(this.selectedId!), 'Watering stopped');
  }

  async advance() {
    if (this.selectedId === null) return;
    await this.perform(() => api.control.advance(this.selectedId!), 'Skipped to the next zone');
  }

  async testAll(minutes: number) {
    if (this.selectedId === null) return;
    await this.perform(
      () => api.control.testAll(this.selectedId!, minutes),
      `Testing every zone for ${minutes} min`,
    );
  }

  async setRainDelay(days: number) {
    if (this.selectedId === null) return;
    await this.perform(
      () => api.control.setRainDelay(this.selectedId!, days),
      days === 0 ? 'Rain delay cleared' : `Watering delayed ${days} ${days === 1 ? 'day' : 'days'}`,
    );
  }

  async setSeasonalAdjust(percent: number) {
    if (this.selectedId === null) return;
    await this.perform(
      () => api.control.setSeasonalAdjust(this.selectedId!, 0, percent),
      `Seasonal adjust set to ${percent}%`,
    );
  }

  async syncClock() {
    if (this.selectedId === null) return;
    await this.perform(() => api.control.syncClock(this.selectedId!), 'Controller clock synced');
  }

  async addController(body: { host: string; password: string; name?: string; latitude?: number | null; longitude?: number | null }) {
    const controller = await api.controllers.add({
      ...body,
      timeZoneId: Intl.DateTimeFormat().resolvedOptions().timeZone,
    });
    runInAction(() => {
      this.controllers.push(controller);
    });
    await this.selectController(controller.id);
    this.root.ui.notify('good', `${controller.name} added`, controller.modelSeries);
    return controller;
  }

  async removeController(id: number) {
    await api.controllers.remove(id);
    runInAction(() => {
      this.controllers = this.controllers.filter((c) => c.id !== id);
      if (this.selectedId === id) this.selectedId = this.controllers[0]?.id ?? null;
    });
    if (this.selectedId !== null) await this.selectController(this.selectedId);
  }

  /** Runs a device command, reports the outcome, and refreshes state. */
  private async perform(action: () => Promise<unknown>, success: string) {
    try {
      await action();
      this.root.ui.notify('good', success);
      await this.refreshState();
    } catch (error) {
      const message = error instanceof ApiError ? error.message : 'The command failed.';
      const title = error instanceof ApiError ? error.title : undefined;
      this.root.ui.notify('bad', title ?? 'Command failed', message);
    }
  }
}
