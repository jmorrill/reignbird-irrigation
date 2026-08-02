import { makeAutoObservable, runInAction } from 'mobx';
import { ApiError, PHOTO_EXTENSIONS, api } from '../api/client';
import type { Zone } from '../api/types';
import type { RootStore } from './RootStore';

/** Marks a photo URL as freshly written, so anything caching it fetches again. */
function stamped(photoUrl: string): string {
  return `${photoUrl}${photoUrl.includes('?') ? '&' : '?'}v=${Date.now()}`;
}

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

  /**
   * Object URLs for the pictures currently going up, keyed the same way.
   *
   * The picked file can be shown straight away — it is already on the device — so
   * the wait is spent looking at the photo rather than at a placeholder wondering
   * whether the right one was chosen.
   */
  previews: ReadonlyMap<number, string> = new Map();

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

    // Caught here rather than after several megabytes have gone up the wire. Only
    // when the browser actually reported a type: some pickers report none at all,
    // and refusing those would block files that are perfectly fine.
    if (file.type && !(file.type in PHOTO_EXTENSIONS)) {
      this.root.ui.notify('bad', 'Photo not saved', 'Photos must be JPEG, PNG or WebP.');
      return;
    }

    const preview = URL.createObjectURL(file);

    runInAction(() => {
      this.uploading = new Set(this.uploading).add(station);
      this.previews = new Map(this.previews).set(station, preview);
    });

    try {
      const { photoUrl } = await api.zones.uploadPhoto(controllerId, station, file);
      runInAction(() => {
        const index = this.zones.findIndex((zone) => zone.stationNumber === station);
        // Replacing a photo of the same format writes the same filename, so the URL
        // comes back identical and nothing downstream can tell the picture changed:
        // the old one stays on screen and the replacement looks like it failed. The
        // stamp is what makes it a different URL to everything that watches one.
        if (index >= 0) this.zones[index] = { ...this.zones[index], photoUrl: stamped(photoUrl) };
      });
      this.root.ui.notify('good', 'Photo saved');
    } catch (error) {
      const message = error instanceof ApiError ? error.message : 'Could not save the photo.';
      this.root.ui.notify('bad', 'Photo not saved', message);
    } finally {
      runInAction(() => {
        const stillUploading = new Set(this.uploading);
        stillUploading.delete(station);
        this.uploading = stillUploading;

        const remainingPreviews = new Map(this.previews);
        remainingPreviews.delete(station);
        this.previews = remainingPreviews;
      });

      URL.revokeObjectURL(preview);
    }
  }

  isUploadingPhoto(station: number) {
    return this.uploading.has(station);
  }

  previewFor(station: number) {
    return this.previews.get(station) ?? null;
  }
}
