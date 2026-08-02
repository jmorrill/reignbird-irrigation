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
  const [objectUrl, setObjectUrl] = useState<string | null>(null);

  useEffect(() => {
    if (!path) {
      setObjectUrl(null);
      return;
    }

    // Let go of the previous photo before fetching this one. The cleanup below
    // revokes the URL this state still pointed at, so keeping it meant rendering a
    // revoked object URL — a broken image — until the new blob arrived.
    setObjectUrl(null);

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
        setObjectUrl(created);
      })
      .catch(() => {
        /* Offline, or the photo is gone. The placeholder covers it. */
      });

    return () => {
      cancelled = true;
      if (created) URL.revokeObjectURL(created);
    };
  }, [path]);

  return objectUrl;
}
