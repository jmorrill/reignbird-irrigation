import { makeAutoObservable, runInAction } from 'mobx';
import { ApiError, api } from '../api/client';
import type { FrequencyType, Program } from '../api/types';
import type { RootStore } from './RootStore';

/** The controller's sentinel for an unused start-time slot. */
export const UNSET_START_TIME = 65535;

export class ScheduleStore {
  programs: Program[] = [];
  loading = false;

  /**
   * True once a load has actually succeeded. Distinct from `loading`: an empty
   * list means "there are none" only after this is true, and before it the screen
   * knows nothing and should say so rather than showing an empty state.
   */
  loaded = false;
  saving = false;

  private readonly root: RootStore;

  constructor(root: RootStore) {
    this.root = root;
    makeAutoObservable<ScheduleStore, 'root'>(this, { root: false }, { autoBind: true });
  }

  get active(): Program[] {
    return this.programs.filter((program) => program.enabled);
  }

  byNumber(index: number): Program | undefined {
    return this.programs.find((program) => program.programNumber === index);
  }

  async load(controllerId: number) {
    this.loading = true;
    try {
      const programs = await api.programs.list(controllerId);
      runInAction(() => {
        this.programs = programs;
      });
    } catch {
      // Keeps the last known programs. A failed refresh is not evidence they were
      // deleted, and the connection banner is what says they may be stale.
    } finally {
      runInAction(() => {
        this.loading = false;
        this.loaded = true;
      });
    }
  }

  async save(program: Program) {
    const controllerId = this.root.controllers.selectedId;
    if (controllerId === null) return;

    this.saving = true;
    try {
      const saved = await api.programs.save(controllerId, program.programNumber, {
        frequency: program.frequency,
        customDays: program.customDays,
        cyclicDays: program.cyclicDays,
        seasonalAdjustPercent: program.seasonalAdjustPercent,
        startTimes: program.startTimes,
        stationRunTimes: program.stationRunTimes,
      });

      runInAction(() => {
        const index = this.programs.findIndex((p) => p.programNumber === saved.programNumber);
        if (index >= 0) this.programs[index] = saved;
      });

      this.root.ui.notify('good', `Program ${program.label} saved`);
    } catch (error) {
      const message = error instanceof ApiError ? error.message : 'Could not save the program.';
      const title = error instanceof ApiError ? error.title : undefined;
      this.root.ui.notify('bad', title ?? 'Program not saved', message);
    } finally {
      runInAction(() => {
        this.saving = false;
      });
    }
  }

  async run(programNumber: number) {
    const controllerId = this.root.controllers.selectedId;
    if (controllerId === null) return;

    const program = this.byNumber(programNumber);
    try {
      await api.programs.run(controllerId, programNumber);
      this.root.ui.notify('info', `Program ${program?.label ?? programNumber} started`);
      await this.root.controllers.refreshState();
    } catch (error) {
      const message = error instanceof ApiError ? error.message : 'Could not start the program.';
      this.root.ui.notify('bad', 'Could not start the program', message);
    }
  }
}

/** Human phrasing for a watering frequency. */
export function describeFrequency(program: Program): string {
  switch (program.frequency) {
    case 'Cyclic':
      return program.cyclicDays === 1
        ? 'Every day'
        : `Every ${program.cyclicDays} days`;
    case 'OddDays':
      return 'Odd days of the month';
    case 'EvenDays':
      return 'Even days of the month';
    case 'CustomDays': {
      const names = ['Sun', 'Mon', 'Tue', 'Wed', 'Thu', 'Fri', 'Sat'];
      const chosen = program.customDays
        .map((on, index) => (on ? names[index] : null))
        .filter((name): name is string => name !== null);

      if (chosen.length === 0) return 'No days selected';
      if (chosen.length === 7) return 'Every day';
      return chosen.join(' · ');
    }
    default:
      return 'Unknown';
  }
}

export const FREQUENCY_OPTIONS: { value: FrequencyType; label: string }[] = [
  { value: 'CustomDays', label: 'Days of the week' },
  { value: 'Cyclic', label: 'Every N days' },
  { value: 'OddDays', label: 'Odd days' },
  { value: 'EvenDays', label: 'Even days' },
];

/** Minutes from midnight to a display clock. */
export function formatStartTime(minutes: number): string {
  if (minutes >= 1440 || minutes < 0) return '—';
  const hours = Math.floor(minutes / 60);
  const mins = minutes % 60;
  const suffix = hours < 12 ? 'am' : 'pm';
  const display = hours % 12 === 0 ? 12 : hours % 12;
  return `${display}:${String(mins).padStart(2, '0')}${suffix}`;
}
