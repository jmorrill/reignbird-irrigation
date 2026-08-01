import { makeAutoObservable, runInAction } from 'mobx';
import { ApiError, api } from '../api/client';
import type { SkipEvent, SkipSettings, UnitPreferences, WeatherDay } from '../api/types';
import type { RootStore } from './RootStore';

export class WeatherStore {
  forecast: WeatherDay[] = [];
  skips: SkipEvent[] = [];
  settings: SkipSettings | null = null;
  units: UnitPreferences = { useMetric: false, showVolume: true };
  loading = false;

  /**
   * True once a load has actually succeeded. Distinct from `loading`: an empty
   * list means "there are none" only after this is true, and before it the screen
   * knows nothing and should say so rather than showing an empty state.
   */
  loaded = false;

  private readonly root: RootStore;

  constructor(root: RootStore) {
    this.root = root;
    makeAutoObservable<WeatherStore, 'root'>(this, { root: false }, { autoBind: true });
  }

  /** Five days centred on today — what the strip shows. */
  get strip(): WeatherDay[] {
    const today = new Date().toLocaleDateString('en-CA');
    const todayIndex = this.forecast.findIndex((day) => day.date >= today);
    if (todayIndex < 0) return this.forecast.slice(-5);

    const start = Math.max(0, todayIndex - 2);
    return this.forecast.slice(start, start + 5);
  }

  get today(): WeatherDay | undefined {
    const today = new Date().toLocaleDateString('en-CA');
    return this.forecast.find((day) => day.date === today);
  }

  get recentSkip(): SkipEvent | undefined {
    return this.skips[0];
  }

  async load(controllerId: number) {
    this.loading = true;
    try {
      const [forecast, skips] = await Promise.all([
        api.weather.forecast(controllerId),
        api.weather.skips(controllerId),
      ]);
      runInAction(() => {
        this.forecast = forecast;
        this.skips = skips;
      });
    } catch {
      // Keeps yesterday's forecast rather than blanking the strip.
    } finally {
      runInAction(() => {
        this.loading = false;
        this.loaded = true;
      });
    }
  }

  async loadSettings() {
    try {
      const [settings, units] = await Promise.all([api.settings.getSkip(), api.settings.getUnits()]);
      runInAction(() => {
        this.settings = settings;
        this.units = units;
      });
    } catch {
      /* Defaults are fine; the settings screen will show them. */
    }
  }

  async saveSettings(settings: SkipSettings) {
    const previous = this.settings;
    this.settings = settings;
    try {
      const saved = await api.settings.setSkip(settings);
      runInAction(() => {
        this.settings = saved;
      });
    } catch (error) {
      runInAction(() => {
        this.settings = previous;
      });
      const message = error instanceof ApiError ? error.message : 'Could not save the settings.';
      this.root.ui.notify('bad', 'Settings not saved', message);
    }
  }

  async saveUnits(units: UnitPreferences) {
    const previous = this.units;
    this.units = units;
    try {
      const saved = await api.settings.setUnits(units);
      runInAction(() => {
        this.units = saved;
      });
    } catch {
      runInAction(() => {
        this.units = previous;
      });
    }
  }

  /** Runs the skip rules now, so the user can see what they would decide today. */
  async evaluateNow() {
    const controllerId = this.root.controllers.selectedId;
    if (controllerId === null) return;

    try {
      const decision = await api.weather.evaluate(controllerId);
      if (decision.shouldSkip) {
        this.root.ui.notify('warn', `Watering skipped — ${decision.reason?.toLowerCase()}`, decision.details);
      } else {
        this.root.ui.notify('good', 'No skip needed', decision.details);
      }
      await this.load(controllerId);
      await this.root.controllers.refreshState();
    } catch (error) {
      const message = error instanceof ApiError ? error.message : 'Could not evaluate the forecast.';
      this.root.ui.notify('bad', 'Evaluation failed', message);
    }
  }

  /** Temperature in the user's chosen unit. */
  temp(celsius: number): string {
    return this.units.useMetric
      ? `${Math.round(celsius)}°`
      : `${Math.round((celsius * 9) / 5 + 32)}°`;
  }
}
