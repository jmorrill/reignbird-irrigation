import { useEffect, useState } from 'react';

/**
 * A clock that re-renders on an interval, so relative times age instead of
 * freezing at whatever they read when the screen was opened.
 *
 * Pick the interval to match the coarsest unit on display: a value shown in
 * minutes gains nothing from ticking every second, and a countdown reading in
 * seconds is wrong for up to a second at any slower rate. Callers showing both
 * are expected to vary it as the deadline approaches rather than pay for the
 * fast rate all the time.
 */
export function useNow(intervalMs: number): number {
  const [now, setNow] = useState(() => Date.now());

  useEffect(() => {
    const id = window.setInterval(() => setNow(Date.now()), intervalMs);
    return () => window.clearInterval(id);
  }, [intervalMs]);

  return now;
}
