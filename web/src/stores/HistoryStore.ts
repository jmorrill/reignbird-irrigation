import { makeAutoObservable, runInAction } from 'mobx';
import { api } from '../api/client';
import type { CalendarDay, Run, Usage } from '../api/types';
import type { RootStore } from './RootStore';

export class HistoryStore {
  runs: Run[] = [];
  usage: Usage | null = null;
  calendar: CalendarDay[] = [];
  loading = false;

  /**
   * True once a load has actually succeeded. Distinct from `loading`: an empty
   * list means "there are none" only after this is true, and before it the screen
   * knows nothing and should say so rather than showing an empty state.
   */
  loaded = false;

  /** Month the calendar is showing. */
  calendarYear = new Date().getFullYear();
  calendarMonth = new Date().getMonth() + 1;

  private readonly root: RootStore;

  constructor(root: RootStore) {
    this.root = root;
    makeAutoObservable<HistoryStore, 'root'>(this, { root: false }, { autoBind: true });
  }

  /** Runs grouped by local day, most recent first — the shape the list renders. */
  get runsByDay(): { day: string; label: string; runs: Run[]; minutes: number; gallons: number }[] {
    const groups = new Map<string, Run[]>();

    for (const run of this.runs) {
      const day = new Date(run.startedUtc).toLocaleDateString('en-CA'); // yyyy-mm-dd, sorts correctly
      const bucket = groups.get(day);
      if (bucket) bucket.push(run);
      else groups.set(day, [run]);
    }

    return [...groups.entries()]
      .sort((a, b) => b[0].localeCompare(a[0]))
      .map(([day, runs]) => ({
        day,
        label: labelForDay(day),
        runs,
        minutes: Math.round(runs.reduce((sum, run) => sum + run.durationSeconds, 0) / 60),
        gallons: Math.round(runs.reduce((sum, run) => sum + run.estimatedGallons, 0) * 10) / 10,
      }));
  }

  calendarDay(date: string): CalendarDay | undefined {
    return this.calendar.find((day) => day.date === date);
  }

  async load(controllerId: number) {
    this.loading = true;
    try {
      const [runs, usage, calendar] = await Promise.all([
        api.history.runs(controllerId),
        api.history.usage(controllerId),
        api.history.calendar(controllerId, this.calendarYear, this.calendarMonth),
      ]);
      runInAction(() => {
        this.runs = runs;
        this.usage = usage;
        this.calendar = calendar;
      });
    } catch {
      // History does not vanish because a request did. Keep what we have.
    } finally {
      runInAction(() => {
        this.loading = false;
        this.loaded = true;
      });
    }
  }

  async showMonth(year: number, month: number) {
    this.calendarYear = year;
    this.calendarMonth = month;

    const controllerId = this.root.controllers.selectedId;
    if (controllerId === null) return;

    try {
      const calendar = await api.history.calendar(controllerId, year, month);
      runInAction(() => {
        this.calendar = calendar;
      });
    } catch {
      runInAction(() => {
        this.calendar = [];
      });
    }
  }

  stepMonth(delta: number) {
    const date = new Date(this.calendarYear, this.calendarMonth - 1 + delta, 1);
    return this.showMonth(date.getFullYear(), date.getMonth() + 1);
  }
}

function labelForDay(day: string): string {
  const today = new Date().toLocaleDateString('en-CA');
  const yesterday = new Date(Date.now() - 86_400_000).toLocaleDateString('en-CA');

  if (day === today) return 'Today';
  if (day === yesterday) return 'Yesterday';

  // Parse as local midnight so the label matches the grouping key.
  const [year, month, date] = day.split('-').map(Number);
  return new Date(year, month - 1, date).toLocaleDateString(undefined, {
    weekday: 'long',
    month: 'short',
    day: 'numeric',
  });
}
