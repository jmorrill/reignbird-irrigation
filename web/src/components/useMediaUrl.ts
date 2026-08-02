import { useEffect, useState } from 'react';
import { API_BASE, getAuthToken } from '../api/client';

/**
 * Turns a `/media/...` path into something an `<img>` or a CSS background can use.
 *
 * Zone photos sit behind authentication, and a bearer token cannot ride along on an
 * image request — the browser writes those, and it will not attach an Authorization
 * header to them. So the photo is fetched like any other API call and handed to the
 * DOM as an object URL instead.
 *
 * Returns null until the photo has loaded, and on failure, which is why the callers
 * keep their existing "no photo" placeholder.
 */
export function useMediaUrl(path: string | null | undefined): string | null {
  // The path is remembered alongside its object URL so the two can never be
  // reported out of step. Holding the URL alone meant that for the one render
  // between the path changing and the effect running, this returned the previous
  // photo's URL — which the cleanup was about to revoke — as though it were the
  // answer for the new one.
  const [resolved, setResolved] = useState<{ path: string; url: string } | null>(null);

  useEffect(() => {
    if (!path) {
      setResolved(null);
      return;
    }

    // A request that outlives the component — the user swiped to another zone —
    // must neither set state nor leak the blob it just created.
    let cancelled = false;
    let created: string | null = null;

    const token = getAuthToken();

    void fetch(`${API_BASE}${path}`, {
      headers: token ? { Authorization: `Bearer ${token}` } : {},
    })
      .then((response) => (response.ok ? response.blob() : null))
      .then((blob) => {
        if (cancelled || !blob) return;
        created = URL.createObjectURL(blob);
        setResolved({ path, url: created });
      })
      .catch(() => {
        /* Offline, or the photo is gone. The placeholder covers it. */
      });

    return () => {
      cancelled = true;
      if (created) URL.revokeObjectURL(created);
    };
  }, [path]);

  // Only ever the URL for the path being asked about. Anything else is a photo of
  // something the caller is no longer showing.
  return resolved && resolved.path === path ? resolved.url : null;
}
