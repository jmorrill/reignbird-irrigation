import { makeAutoObservable, runInAction } from 'mobx';
import { ApiError, api } from '../api/client';
import type { Zone } from '../api/types';
import type { RootStore } from './RootStore';

export class ZoneStore {
  zones: Zone[] = [];
  loading = false;

  /**
   * True once a load has actually succeeded. Distinct from `loading`: an empty
   * list means "there are none" only after this is true, and before it the screen
   * knows nothing and should say so rather than showing an empty state.
   */
  loaded = false;

  /**
   * Stations with a photo upload in flight. Replaced rather than mutated on each
   * change, so reactivity does not depend on how the set itself is observed.
   */
  uploading: ReadonlySet<number> = new Set();

  private readonly root: RootStore;

  constructor(root: RootStore) {
    this.root = root;
    makeAutoObservable<ZoneStore, 'root'>(this, { root: false }, { autoBind: true });
  }

  get ordered(): Zone[] {
    return [...this.zones].sort((a, b) => a.sortOrder - b.sortOrder || a.stationNumber - b.stationNumber);
  }

  get visible(): Zone[] {
    return this.ordered.filter((zone) => zone.enabled);
  }

  get disabled(): Zone[] {
    return this.ordered.filter((zone) => !zone.enabled);
  }

  byStation(station: number): Zone | undefined {
    return this.zones.find((zone) => zone.stationNumber === station);
  }

  async load(controllerId: number) {
    this.loading = true;
    try {
      const zones = await api.zones.list(controllerId);
      runInAction(() => {
        this.zones = zones;
      });
    } catch {
      // Keeps the last known zones rather than emptying the screen on a blip.
    } finally {
      runInAction(() => {
        this.loading = false;
        this.loaded = true;
      });
    }
  }

  async run(station: number, minutes: number) {
    const controllerId = this.root.controllers.selectedId;
    if (controllerId === null) return;

    const zone = this.byStation(station);
    try {
      await api.zones.run(controllerId, station, minutes);
      this.root.ui.notify('info', `${zone?.name ?? `Zone ${station}`} started`, `${minutes} min`);
      await this.root.controllers.refreshState();
      await this.load(controllerId);
    } catch (error) {
      const message = error instanceof ApiError ? error.message : 'Could not start the zone.';
      this.root.ui.notify('bad', 'Could not start watering', message);
    }
  }

  async queue(station: number, minutes: number) {
    const controllerId = this.root.controllers.selectedId;
    if (controllerId === null) return;

    const zone = this.byStation(station);
    try {
      await api.zones.queue(controllerId, station, minutes);
      this.root.ui.notify('info', `${zone?.name ?? `Zone ${station}`} queued`, `${minutes} min`);
    } catch (error) {
      const message = error instanceof ApiError ? error.message : 'Could not queue the zone.';
      this.root.ui.notify('bad', 'Could not queue the zone', message);
    }
  }

  async update(station: number, changes: Partial<Zone>) {
    const controllerId = this.root.controllers.selectedId;
    if (controllerId === null) return;

    // Apply locally first: editing a zone name should feel immediate, and the
    // server is the source of truth only if it disagrees.
    const index = this.zones.findIndex((zone) => zone.stationNumber === station);
    const previous = index >= 0 ? { ...this.zones[index] } : null;
    if (index >= 0) this.zones[index] = { ...this.zones[index], ...changes };

    try {
      const saved = await api.zones.update(controllerId, station, changes);
      runInAction(() => {
        const current = this.zones.findIndex((zone) => zone.stationNumber === station);
        if (current >= 0) this.zones[current] = saved;
      });
    } catch (error) {
      runInAction(() => {
        if (previous && index >= 0) this.zones[index] = previous;
      });
      const message = error instanceof ApiError ? error.message : 'Could not save the zone.';
      this.root.ui.notify('bad', 'Changes not saved', message);
    }
  }

  /**
   * Uploads a zone photo.
   *
   * Keyed by station rather than a single flag, because the sheet can be closed and
   * another zone opened while the first is still going up — a lone flag would show
   * the second zone as busy and leave the first looking idle.
   *
   * A photo off a phone camera is several megabytes, so this is the slowest thing
   * anybody does in the app, and until now it reported nothing at all until the
   * toast arrived. It read as a button that did nothing.
   */
  async uploadPhoto(station: number, file: File) {
    const controllerId = this.root.controllers.selectedId;
    if (controllerId === null) return;

    runInAction(() => {
      this.uploading = new Set(this.uploading).add(station);
    });

    try {
      const { photoUrl } = await api.zones.uploadPhoto(controllerId, station, file);
      runInAction(() => {
        const index = this.zones.findIndex((zone) => zone.stationNumber === station);
        if (index >= 0) this.zones[index] = { ...this.zones[index], photoUrl };
      });
      this.root.ui.notify('good', 'Photo saved');
    } catch (error) {
      const message = error instanceof ApiError ? error.message : 'Could not save the photo.';
      this.root.ui.notify('bad', 'Photo not saved', message);
    } finally {
      runInAction(() => {
        const next = new Set(this.uploading);
        next.delete(station);
        this.uploading = next;
      });
    }
  }

  isUploadingPhoto(station: number) {
    return this.uploading.has(station);
  }
}
