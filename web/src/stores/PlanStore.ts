import { makeAutoObservable, runInAction } from 'mobx';
import { ApiError, api } from '../api/client';
import type { ActivePlan, ArmedState, Plan, PlanPreset, PlanRun, SavePlan } from '../api/types';
import type { RootStore } from './RootStore';

/**
 * Watering plans: the schedules this app runs itself.
 *
 * Distinct from ScheduleStore, which reads the *controller's* own programs. On
 * firmware that will not expose those at all, this is the only scheduling there is.
 */
export class PlanStore {
  plans: Plan[] = [];
  presets: PlanPreset[] = [];
  runs: PlanRun[] = [];
  active: ActivePlan | null = null;
  armed: ArmedState | null = null;

  loading = false;
  saving = false;
  disarming = false;

  /**
   * True once a load has finished, whether or not it worked.
   *
   * Distinct from `loading`, and the distinction is the point: an empty list means
   * "no plans" only after this is true. Before it the screen knows nothing, and
   * announcing that nothing is scheduled would not be a slower answer but a wrong
   * one.
   *
   * It counts a failed attempt too, on purpose. Requiring success would leave a
   * screen showing a skeleton for as long as the server stayed unreachable, which
   * reads as broken; the connection banner is the right place to explain that.
   */
  loaded = false;

  private readonly root: RootStore;

  constructor(root: RootStore) {
    this.root = root;
    makeAutoObservable<PlanStore, 'root'>(this, { root: false }, { autoBind: true });
  }

  get enabled(): Plan[] {
    return this.plans.filter((plan) => plan.enabled);
  }

  /** True when the controller has a schedule of its own that would compete with ours. */
  get hasCompetingSchedule(): boolean {
    return this.armed?.canDisarm === true && !this.armed.controllerScheduleCleared;
  }

  byId(planId: number): Plan | undefined {
    return this.plans.find((plan) => plan.id === planId);
  }

  async load(controllerId: number) {
    this.loading = true;
    try {
      const [plans, presets, runs, active] = await Promise.all([
        api.plans.list(controllerId),
        api.plans.presets(controllerId),
        api.plans.runs(controllerId),
        api.plans.active(controllerId),
      ]);

      runInAction(() => {
        this.plans = plans;
        this.presets = presets;
        this.runs = runs;
        this.active = active;
      });
    } catch {
      // Deliberately keeps whatever was already there. A refresh failing because the
      // phone changed networks is not evidence that the plans were deleted, and
      // blanking the screen on a blip is worse than showing something a minute old —
      // the connection banner is what says it might be stale.
    } finally {
      runInAction(() => {
        this.loading = false;
        this.loaded = true;
      });
    }

    // Reading the controller's own schedule is several round trips, so it is not
    // part of the first paint.
    void this.loadArmedState(controllerId);
  }

  async loadArmedState(controllerId: number) {
    try {
      const armed = await api.arming.state(controllerId);
      runInAction(() => {
        this.armed = armed;
      });
    } catch {
      runInAction(() => {
        this.armed = null;
      });
    }
  }

  /** Polls the running plan, so the UI follows it zone by zone. */
  async refreshActive() {
    const controllerId = this.root.controllers.selectedId;
    if (controllerId === null) return;

    try {
      const active = await api.plans.active(controllerId);
      runInAction(() => {
        this.active = active;
      });
    } catch {
      /* A failed poll is not worth surfacing; the next one will do. */
    }
  }

  // ------------------------------------------------------------------ writes

  async createFromPreset(presetKey: string) {
    const controllerId = this.root.controllers.selectedId;
    if (controllerId === null) return null;

    try {
      const plan = await api.plans.fromPreset(controllerId, presetKey);
      runInAction(() => {
        this.plans.push(plan);
      });
      this.root.ui.notify('good', `${plan.name} added`, 'Check the run times, then switch it on.');
      return plan;
    } catch (error) {
      this.report(error, 'Could not create the plan');
      return null;
    }
  }

  async save(planId: number | null, body: SavePlan) {
    const controllerId = this.root.controllers.selectedId;
    if (controllerId === null) return null;

    this.saving = true;
    try {
      const saved = planId === null
        ? await api.plans.create(controllerId, body)
        : await api.plans.update(controllerId, planId, body);

      runInAction(() => {
        const index = this.plans.findIndex((plan) => plan.id === saved.id);
        if (index >= 0) this.plans[index] = saved;
        else this.plans.push(saved);
      });

      this.root.ui.notify('good', `${saved.name} saved`);
      return saved;
    } catch (error) {
      this.report(error, 'Plan not saved');
      return null;
    } finally {
      runInAction(() => {
        this.saving = false;
      });
    }
  }

  /** Switches a plan on or off without opening the editor. */
  async setEnabled(plan: Plan, enabled: boolean) {
    const previous = plan.enabled;
    plan.enabled = enabled;

    const saved = await this.save(plan.id, { ...toSavePlan(plan), enabled });
    if (!saved) {
      runInAction(() => {
        plan.enabled = previous;
      });
    }
  }

  async remove(plan: Plan) {
    const controllerId = this.root.controllers.selectedId;
    if (controllerId === null) return;

    try {
      await api.plans.remove(controllerId, plan.id);
      runInAction(() => {
        this.plans = this.plans.filter((p) => p.id !== plan.id);
      });
      this.root.ui.notify('good', `${plan.name} deleted`);
    } catch (error) {
      this.report(error, 'Could not delete the plan');
    }
  }

  // --------------------------------------------------------------- execution

  async run(plan: Plan) {
    const controllerId = this.root.controllers.selectedId;
    if (controllerId === null) return;

    try {
      await api.plans.run(controllerId, plan.id);
      this.root.ui.notify('info', `${plan.name} started`, `${plan.wateringMinutesPerPass} min of watering`);
      await this.refreshActive();
    } catch (error) {
      this.report(error, 'Could not start the plan');
    }
  }

  async cancel() {
    const controllerId = this.root.controllers.selectedId;
    if (controllerId === null) return;

    try {
      await api.plans.cancel(controllerId);
      runInAction(() => {
        this.active = null;
      });
      this.root.ui.notify('good', 'Plan cancelled');
      await this.root.controllers.refreshState();
    } catch (error) {
      this.report(error, 'Could not cancel the plan');
    }
  }

  /**
   * Clears the controller's own run times so this app is the only thing watering.
   */
  async disarm() {
    const controllerId = this.root.controllers.selectedId;
    if (controllerId === null) return;

    this.disarming = true;
    try {
      const result = await api.arming.disarm(controllerId);
      this.root.ui.notify('good', 'Controller schedule cleared', result.message);
      await this.loadArmedState(controllerId);
    } catch (error) {
      this.report(error, 'Could not clear the controller schedule');
    } finally {
      runInAction(() => {
        this.disarming = false;
      });
    }
  }

  private report(error: unknown, title: string) {
    const message = error instanceof ApiError ? error.message : 'Something went wrong.';
    this.root.ui.notify('bad', title, message);
  }
}

/** Turns a loaded plan back into the shape the save endpoint expects. */
export function toSavePlan(plan: Plan): SavePlan {
  return {
    name: plan.name,
    description: plan.description,
    enabled: plan.enabled,
    frequency: plan.frequency,
    daysOfWeek: plan.daysOfWeek,
    intervalDays: plan.intervalDays,
    startTimes: plan.startTimes,
    latestStartMinute: plan.latestStartMinute,
    seasonalAdjustPercent: plan.seasonalAdjustPercent,
    cycleSoakEnabled: plan.cycleSoakEnabled,
    cycles: plan.cycles,
    soakMinutes: plan.soakMinutes,
    weatherSkipEnabled: plan.weatherSkipEnabled,
    zones: plan.zones,
  };
}

const DAY_NAMES = ['Sun', 'Mon', 'Tue', 'Wed', 'Thu', 'Fri', 'Sat'];

/** How often a plan waters, in plain words. */
export function describePlanFrequency(plan: Plan): string {
  switch (plan.frequency) {
    case 'EveryDay':
      return 'Every day';
    case 'OddDays':
      return 'Odd days of the month';
    case 'EvenDays':
      return 'Even days of the month';
    case 'EveryNDays':
      return plan.intervalDays === 1 ? 'Every day' : `Every ${plan.intervalDays} days`;
    case 'DaysOfWeek': {
      const chosen = plan.daysOfWeek
        .map((on, index) => (on ? DAY_NAMES[index] : null))
        .filter((name): name is string => name !== null);

      if (chosen.length === 0) return 'No days selected';
      if (chosen.length === 7) return 'Every day';
      return chosen.join(' · ');
    }
    default:
      return 'Unknown';
  }
}

/** "6:00am · 11:00am · 3:00pm" */
export function describeStartTimes(plan: Plan): string {
  if (plan.startTimes.length === 0) return 'No start time';
  return plan.startTimes.map(formatMinuteOfDay).join(' · ');
}

export function formatMinuteOfDay(minutes: number): string {
  const hours = Math.floor(minutes / 60);
  const mins = minutes % 60;
  const suffix = hours < 12 ? 'am' : 'pm';
  const display = hours % 12 === 0 ? 12 : hours % 12;
  return `${display}:${String(mins).padStart(2, '0')}${suffix}`;
}

/** A relative "in 4 hours" for the next scheduled pass. */
export function describeNextRun(iso: string | null): string {
  if (!iso) return 'Not scheduled';

  const when = new Date(iso);
  const minutes = Math.round((when.getTime() - Date.now()) / 60000);

  if (minutes < 0) return 'Due now';
  if (minutes < 60) return `in ${minutes} min`;

  const hours = Math.round(minutes / 60);
  if (hours < 24) return `in ${hours} ${hours === 1 ? 'hour' : 'hours'}`;

  return when.toLocaleDateString(undefined, { weekday: 'long' })
    + ` at ${when.toLocaleTimeString(undefined, { hour: 'numeric', minute: '2-digit' })}`;
}
