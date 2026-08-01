import { createContext, useContext } from 'react';
import { AuthStore } from './AuthStore';
import { ConnectionStore } from './ConnectionStore';
import { ControllerStore } from './ControllerStore';
import { HistoryStore } from './HistoryStore';
import { PlanStore } from './PlanStore';
import { PwaStore } from './PwaStore';
import { ScheduleStore } from './ScheduleStore';
import { UiStore } from './UiStore';
import { WeatherStore } from './WeatherStore';
import { ZoneStore } from './ZoneStore';

/**
 * Owns every store and the one place they talk to each other.
 *
 * Selecting a controller has to reload zones, programs, history and weather
 * together; putting that fan-out here keeps each store unaware of the others'
 * loading concerns.
 */
export class RootStore {
  // First, and deliberately so: its constructor adopts any stored token, and field
  // initialisers run in declaration order. Nothing below fetches while constructing,
  // but putting it last would leave that a matter of luck rather than of design.
  readonly auth = new AuthStore();

  readonly ui = new UiStore();
  readonly controllers = new ControllerStore(this);
  readonly zones = new ZoneStore(this);
  readonly schedules = new ScheduleStore(this);
  readonly history = new HistoryStore(this);
  readonly weather = new WeatherStore(this);
  readonly plans = new PlanStore(this);

  // Independent of the others: these talk to the browser and to the transport
  // rather than to a controller. PwaStore registers the service worker as soon as
  // the app is constructed.
  readonly pwa = new PwaStore();
  readonly connection = new ConnectionStore();

  async start() {
    await this.weather.loadSettings();
    await this.controllers.load();
    this.controllers.startTicking();
  }

  stop() {
    this.controllers.stopTicking();
  }

  /** Called by ControllerStore once a different controller is selected. */
  async onControllerChanged(controllerId: number) {
    await Promise.all([
      this.zones.load(controllerId),
      this.history.load(controllerId),
      this.weather.load(controllerId),
      this.schedules.load(controllerId),
      this.plans.load(controllerId),
    ]);
  }
}

export const StoreContext = createContext<RootStore | null>(null);

export function useStore(): RootStore {
  const store = useContext(StoreContext);
  if (!store) throw new Error('useStore must be used inside a StoreContext provider.');
  return store;
}
