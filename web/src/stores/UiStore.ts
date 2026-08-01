import { makeAutoObservable } from 'mobx';

export type Tab = 'events' | 'zones' | 'schedules' | 'settings';
export type Theme = 'light' | 'dark' | 'system';

export interface Toast {
  id: number;
  tone: 'info' | 'good' | 'warn' | 'bad';
  title: string;
  detail?: string;
}

const THEME_KEY = 'rainbird.theme';

const TABS: readonly Tab[] = ['events', 'zones', 'schedules', 'settings'];

function isTab(value: string | null): value is Tab {
  return value !== null && (TABS as readonly string[]).includes(value);
}

export class UiStore {
  tab: Tab = 'events';
  theme: Theme = 'system';
  toasts: Toast[] = [];

  /** Station number whose detail sheet is open, or null. */
  openZone: number | null = null;

  /** Program index whose editor is open, or null. */
  openProgram: number | null = null;

  quickRunOpen = false;
  addControllerOpen = false;

  /** Plan being edited: an id, 'new' for a blank one, or null when closed. */
  openPlan: number | 'new' | null = null;
  planPickerOpen = false;

  private nextToastId = 1;

  constructor() {
    makeAutoObservable(this);

    const stored = localStorage.getItem(THEME_KEY) as Theme | null;
    if (stored === 'light' || stored === 'dark' || stored === 'system') this.theme = stored;
    this.applyTheme();

    // The installed app's launcher shortcuts open /?tab=zones and friends. Read on
    // entry only: there is no router here, and the tab is not worth putting in the
    // URL for its own sake — this exists so the shortcuts land somewhere useful.
    const requested = new URLSearchParams(window.location.search).get('tab');
    if (isTab(requested)) this.tab = requested;
  }

  setTab(tab: Tab) {
    this.tab = tab;
  }

  setTheme(theme: Theme) {
    this.theme = theme;
    localStorage.setItem(THEME_KEY, theme);
    this.applyTheme();
  }

  private applyTheme() {
    const root = document.documentElement;
    if (this.theme === 'system') root.removeAttribute('data-theme');
    else root.setAttribute('data-theme', this.theme);
  }

  openZoneSheet(station: number) {
    this.openZone = station;
  }

  closeZoneSheet() {
    this.openZone = null;
  }

  openProgramEditor(index: number) {
    this.openProgram = index;
  }

  closeProgramEditor() {
    this.openProgram = null;
  }

  setQuickRunOpen(open: boolean) {
    this.quickRunOpen = open;
  }

  setAddControllerOpen(open: boolean) {
    this.addControllerOpen = open;
  }

  openPlanEditor(planId: number | 'new') {
    this.planPickerOpen = false;
    this.openPlan = planId;
  }

  closePlanEditor() {
    this.openPlan = null;
  }

  setPlanPickerOpen(open: boolean) {
    this.planPickerOpen = open;
  }

  /**
   * Shows a transient message. Errors stay longer than confirmations, because a
   * confirmation is reassurance and an error is something to read.
   */
  notify(tone: Toast['tone'], title: string, detail?: string) {
    const toast: Toast = { id: this.nextToastId++, tone, title, detail };
    this.toasts.push(toast);

    const lifetime = tone === 'bad' ? 7000 : 3500;
    setTimeout(() => this.dismiss(toast.id), lifetime);
  }

  dismiss(id: number) {
    this.toasts = this.toasts.filter((toast) => toast.id !== id);
  }
}
