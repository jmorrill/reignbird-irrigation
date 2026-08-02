import { makeAutoObservable, runInAction } from 'mobx';
import { ApiError, api } from '../api/client';
import { PHOTO_EXTENSIONS, prepareForUpload } from '../api/photos';
import type { Zone } from '../api/types';
import type { RootStore } from './RootStore';

/** What is currently being done to a zone's photo, if anything. */
export type PhotoTask = 'uploading' | 'removing';

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
   * Stations with a photo operation in flight, and which one.
   *
   * Keyed by station rather than a single flag, because the sheet can be closed and
   * another zone opened while the first is still going — a lone flag would show the
   * second zone as busy and leave the first looking idle. Replaced rather than
   * mutated on each change, so reactivity does not depend on how the map itself is
   * observed.
   */
  photoTasks: ReadonlyMap<number, PhotoTask> = new Map();

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

    // Anything left over from a previous attempt on this zone, before it is
    // overwritten in the map and becomes unreachable.
    this.releasePreview(station);

    const preview = URL.createObjectURL(file);

    this.setPhotoTask(station, 'uploading');
    runInAction(() => {
      this.previews = new Map(this.previews).set(station, preview);
    });

    try {
      // Scaled down after the preview is on screen, not before: the preview comes
      // from the original because it is already on the device and costs nothing,
      // and decoding a twelve-megapixel photo is the one part of this that could
      // hold up the very feedback it is meant to give.
      const prepared = await prepareForUpload(file);

      const { photoUrl, thumbUrl } = await api.zones.uploadPhoto(
        controllerId, station, prepared.photo, prepared.thumb);

      runInAction(() => {
        const index = this.zones.findIndex((zone) => zone.stationNumber === station);
        // Replacing a photo of the same format writes the same filename, so the URL
        // comes back identical and nothing downstream can tell the picture changed:
        // the old one stays on screen and the replacement looks like it failed. The
        // stamp is what makes it a different URL to everything that watches one.
        if (index >= 0) {
          this.zones[index] = {
            ...this.zones[index],
            photoUrl: stamped(photoUrl),
            thumbUrl: stamped(thumbUrl),
          };
        }
      });
      this.root.ui.notify('good', 'Photo saved');

      // The preview deliberately outlives the upload. The stored photo still has to
      // be downloaded before it can be shown, and dropping the preview at the moment
      // the upload finished left the picture to disappear for the length of that
      // download. Whoever is displaying it releases it once the real one has
      // arrived to take its place.
    } catch (error) {
      const message = error instanceof ApiError ? error.message : 'Could not save the photo.';
      this.root.ui.notify('bad', 'Photo not saved', message);

      // Nothing is coming to replace it, so put the previous photo back now.
      this.releasePreview(station);
    } finally {
      this.setPhotoTask(station, null);
    }
  }

  /**
   * Removes a zone's photo, both renditions and the files themselves.
   *
   * Any leftover preview goes with it: while one is held it is what the sheet
   * displays, so a photo removed underneath it would appear not to have gone.
   */
  async clearPhoto(station: number) {
    const controllerId = this.root.controllers.selectedId;
    if (controllerId === null) return;

    this.setPhotoTask(station, 'removing');

    try {
      await api.zones.deletePhoto(controllerId, station);

      this.releasePreview(station);
      runInAction(() => {
        const index = this.zones.findIndex((zone) => zone.stationNumber === station);
        if (index >= 0) this.zones[index] = { ...this.zones[index], photoUrl: null, thumbUrl: null };
      });

      this.root.ui.notify('good', 'Photo removed');
    } catch (error) {
      const message = error instanceof ApiError ? error.message : 'Could not remove the photo.';
      this.root.ui.notify('bad', 'Photo not removed', message);
    } finally {
      this.setPhotoTask(station, null);
    }
  }

  private setPhotoTask(station: number, task: PhotoTask | null) {
    runInAction(() => {
      const next = new Map(this.photoTasks);
      if (task) next.set(station, task);
      else next.delete(station);
      this.photoTasks = next;
    });
  }

  /**
   * Drops the preview for a zone, once something is on screen to replace it.
   *
   * Called when the stored photo has finished downloading, and when the sheet
   * closes — that second one is what stops a preview nobody is looking at from
   * holding on to the original file, which can be several megabytes.
   */
  releasePreview(station: number) {
    const url = this.previews.get(station);
    if (!url) return;

    runInAction(() => {
      const remaining = new Map(this.previews);
      remaining.delete(station);
      this.previews = remaining;
    });

    URL.revokeObjectURL(url);
  }

  photoTask(station: number): PhotoTask | null {
    return this.photoTasks.get(station) ?? null;
  }

  previewFor(station: number) {
    return this.previews.get(station) ?? null;
  }
}
