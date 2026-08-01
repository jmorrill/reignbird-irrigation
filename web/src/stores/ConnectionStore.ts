import { makeAutoObservable, runInAction } from 'mobx';
import { onServerReachabilityChange } from '../api/client';

/**
 * Whether the app can currently reach its own server.
 *
 * Deliberately not `navigator.onLine`. That reports whether the machine has a
 * network interface up, which says nothing about whether this server is
 * answering — and over a tailnet or a home LAN, being "online" while the server
 * is unreachable is the normal failure, not the exotic one.
 *
 * The signal instead comes from the requests the app is already making, so it
 * costs no extra polling and cannot disagree with what the screens are showing.
 */
export class ConnectionStore {
  reachable = true;

  constructor() {
    makeAutoObservable(this);
    onServerReachabilityChange((reachable) => runInAction(() => (this.reachable = reachable)));
  }
}
