import { AnimatePresence, motion } from 'framer-motion';
import { observer } from 'mobx-react-lite';
import { useStore } from '../stores/RootStore';
import { DropIcon, SkipIcon, StopIcon } from './Icons';
import { Button, GhostText } from './ui';

/** Seconds to m:ss. */
export function formatCountdown(seconds: number): string {
  const safe = Math.max(0, seconds);
  const minutes = Math.floor(safe / 60);
  const rest = safe % 60;
  return `${minutes}:${String(rest).padStart(2, '0')}`;
}

/**
 * A ring that drains as the run completes.
 *
 * The controller reports seconds remaining but never the original duration, so
 * the total is remembered from the highest value seen for this run. That is the
 * only way to draw a truthful progress arc from this protocol.
 */
export const CountdownRing = observer(function CountdownRing({
  remaining,
  total,
  size = 132,
}: {
  remaining: number;
  total: number;
  size?: number;
}) {
  const stroke = 7;
  const radius = (size - stroke) / 2;
  const circumference = 2 * Math.PI * radius;
  const progress = total > 0 ? Math.min(1, Math.max(0, remaining / total)) : 0;

  return (
    <div className="ring" style={{ width: size, height: size }}>
      <svg width={size} height={size} aria-hidden>
        <circle
          cx={size / 2}
          cy={size / 2}
          r={radius}
          fill="none"
          stroke="var(--water-line)"
          strokeWidth={stroke}
          opacity={0.4}
        />
        <motion.circle
          cx={size / 2}
          cy={size / 2}
          r={radius}
          fill="none"
          stroke="var(--water)"
          strokeWidth={stroke}
          strokeLinecap="round"
          strokeDasharray={circumference}
          transform={`rotate(-90 ${size / 2} ${size / 2})`}
          animate={{ strokeDashoffset: circumference * (1 - progress) }}
          transition={{ duration: 0.9, ease: 'linear' }}
        />
      </svg>
      <div className="ring__center">
        <span className="ring__value data">{formatCountdown(remaining)}</span>
        <span className="ring__unit">remaining</span>
      </div>
    </div>
  );
});

/**
 * The Events hero. It answers one question — what is the system doing — and
 * changes character completely depending on the answer, rather than being a
 * fixed panel with variable text.
 */
export const StatusHero = observer(function StatusHero() {
  const { controllers, zones } = useStore();
  const state = controllers.state;

  if (!controllers.selected) return null;

  if (!controllers.online) {
    return (
      <div className="hero hero--offline">
        <div className="hero__body">
          <div className="eyebrow">Controller</div>
          <p className="hero__headline">Not responding</p>
          <p className="hero__detail">
            {controllers.selected.lastError ??
              `Nothing answered at ${controllers.selected.host}. Check the controller is powered on and on the same network.`}
          </p>
        </div>
      </div>
    );
  }

  if (state?.isWatering) {
    const zone = zones.byStation(state.activeStation);
    return <WateringHero zoneName={zone?.name ?? `Zone ${state.activeStation}`} station={state.activeStation} />;
  }

  if ((state?.rainDelayDays ?? 0) > 0) {
    return <DelayHero days={state!.rainDelayDays} />;
  }

  return <IdleHero />;
});

const WateringHero = observer(function WateringHero({
  zoneName,
  station,
}: {
  zoneName: string;
  station: number;
}) {
  const { controllers } = useStore();
  const remaining = controllers.remainingSeconds;
  const total = useRunTotal(station, remaining);

  return (
    <motion.div
      className="hero hero--watering"
      initial={{ opacity: 0, scale: 0.99 }}
      animate={{ opacity: 1, scale: 1 }}
      transition={{ duration: 0.25 }}
    >
      <div className="hero__flow" aria-hidden />
      <div className="hero__body">
        <div className="hero__eyebrow">
          <span className="hero__pulse" aria-hidden />
          <span className="eyebrow">Watering now</span>
        </div>
        <p className="hero__headline">{zoneName}</p>
        <p className="hero__detail data">Station {String(station).padStart(2, '0')}</p>

        <div className="hero__actions">
          <Button tone="primary" icon={<StopIcon size={17} />} onClick={() => controllers.stop()}>
            Stop
          </Button>
          <Button tone="quiet" icon={<SkipIcon size={17} />} onClick={() => controllers.advance()}>
            Next zone
          </Button>
        </div>
      </div>

      <CountdownRing remaining={remaining} total={total} />
    </motion.div>
  );
});

const DelayHero = observer(function DelayHero({ days }: { days: number }) {
  const { controllers, weather } = useStore();
  const skip = weather.recentSkip;

  return (
    <div className="hero hero--delay">
      <div className="hero__body">
        <div className="eyebrow">Watering paused</div>
        <p className="hero__headline">
          <span className="data hero__number">{days}</span> {days === 1 ? 'day' : 'days'} left
        </p>
        <p className="hero__detail">
          {skip?.details ?? 'Automatic watering resumes when the delay ends.'}
        </p>
        <div className="hero__actions">
          <Button tone="quiet" onClick={() => controllers.setRainDelay(0)}>
            Resume watering
          </Button>
        </div>
      </div>
      <div className="hero__glyph" aria-hidden>
        <DropIcon size={80} strokeWidth={1} />
      </div>
    </div>
  );
});

const IdleHero = observer(function IdleHero() {
  const { controllers, schedules, zones, plans } = useStore();

  // Plans answer this by themselves whenever there is one, and they arrive in a
  // single database read. The controller's own programs cost a SIP exchange per
  // program and are reliably the slowest request on the screen, so waiting for
  // them is only worth it when the plans have nothing to say — which is the only
  // case the program fallback is ever used in. Waiting for both meant the hero sat
  // on a skeleton until the slowest request in the app returned, to display an
  // answer that had been settled since the fastest one did.
  const fromPlans = plans.loaded ? nextRunFromPlans(plans.enabled) : null;
  const known = plans.loaded && (fromPlans !== null || schedules.loaded);

  // Still "not sure yet" until one of them has answered: announcing "No watering
  // scheduled" and correcting it a beat later is not a slower answer, it is a
  // wrong one.
  const next = !known ? null : fromPlans ?? nextRunFromPrograms(schedules.programs);

  return (
    <div className="hero hero--idle">
      <div className="hero__body">
        <div className="eyebrow">Idle</div>

        {next ? (
          <>
            <p className="hero__headline">{next.headline}</p>
            <p className="hero__detail">{next.detail}</p>
          </>
        ) : (
          <>
            <p className="hero__headline">
              <GhostText width="min(72%, 340px)" height={26} />
            </p>
            <p className="hero__detail">
              <GhostText width="min(52%, 240px)" height={13} />
            </p>
          </>
        )}
        <div className="hero__actions">
          <Button
            tone="quiet"
            onClick={() => controllers.setRainDelay(1)}
            disabled={!controllers.online}
          >
            Delay a day
          </Button>
          <Button
            tone="ghost"
            onClick={() => controllers.testAll(2)}
            disabled={!controllers.online || zones.zones.length === 0}
          >
            Test all zones
          </Button>
        </div>
      </div>
      <div className="hero__glyph" aria-hidden>
        <DropIcon size={80} strokeWidth={1} />
      </div>
    </div>
  );
});

/** The compact version, shown on every tab except Events. */
export const NowWateringBar = observer(function NowWateringBar() {
  const { controllers, zones } = useStore();
  const watering = controllers.isWatering;
  const zone = zones.byStation(controllers.activeStation);

  return (
    <AnimatePresence>
      {watering && (
        <motion.div
          className="nowbar"
          initial={{ y: 70, opacity: 0 }}
          animate={{ y: 0, opacity: 1 }}
          exit={{ y: 70, opacity: 0 }}
          transition={{ type: 'spring', stiffness: 380, damping: 34 }}
        >
          <span className="nowbar__pulse" aria-hidden />
          <span className="nowbar__text">
            <span className="nowbar__zone">{zone?.name ?? `Zone ${controllers.activeStation}`}</span>
            <span className="nowbar__time data">{formatCountdown(controllers.remainingSeconds)} left</span>
          </span>
          <button className="nowbar__action" onClick={() => controllers.advance()} aria-label="Next zone">
            <SkipIcon size={17} />
          </button>
          <button className="nowbar__action nowbar__action--stop" onClick={() => controllers.stop()}>
            <StopIcon size={15} />
            Stop
          </button>
        </motion.div>
      )}
    </AnimatePresence>
  );
});

/* ------------------------------------------------------------------ helpers */

import { useEffect, useRef, useState } from 'react';

/**
 * Remembers the largest remaining-time seen for the current run so the ring has
 * something to measure against. Resets when the station changes.
 */
function useRunTotal(station: number, remaining: number): number {
  const [total, setTotal] = useState(remaining);
  const currentStation = useRef(station);

  useEffect(() => {
    if (currentStation.current !== station) {
      currentStation.current = station;
      setTotal(remaining);
      return;
    }
    setTotal((previous) => (remaining > previous ? remaining : previous));
  }, [station, remaining]);

  return Math.max(total, remaining, 1);
}

import type { Plan, Program } from '../api/types';
import { describeNextRun } from '../stores/PlanStore';
import { describeFrequency, formatStartTime } from '../stores/ScheduleStore';

/**
 * What this controller is going to do next, and when.
 *
 * Plans are asked first, and that ordering is the whole point rather than a
 * preference. A plan works by leaving the controller's own programs empty and
 * driving the valves from here — so on any controller using one, the hardware has
 * nothing scheduled by design. Reading only the hardware, as this used to, meant the
 * card announced "No watering scheduled" to someone looking at a plan that was about
 * to run.
 *
 * Falls through to the controller's own programs when there are no plans, which is
 * still the right answer for hardware old enough to schedule for itself.
 */
type NextRun = { headline: string; detail: string };

/**
 * Returns null only when there are no plans at all — the one case where the
 * controller's own programs are worth waiting for.
 */
function nextRunFromPlans(plans: Plan[]): NextRun | null {
  const scheduled = plans
    .filter((plan) => plan.nextRunUtc !== null)
    .sort((a, b) => Date.parse(a.nextRunUtc!) - Date.parse(b.nextRunUtc!));

  if (scheduled.length > 0) {
    const soonest = scheduled[0];
    const passes = soonest.passesPerDay > 1 ? `${soonest.passesPerDay}× a day · ` : '';

    return {
      headline: `Next run ${describeNextRun(soonest.nextRunUtc)}`,
      detail: `${soonest.name} · ${passes}${soonest.wateringMinutesPerDay} min of watering a day`,
    };
  }

  // An enabled plan with no next run is waiting on something — every day switched
  // off, or no start time — and saying so beats implying nothing is set up.
  if (plans.length > 0) {
    return {
      headline: 'No upcoming run',
      detail: `${plans.length === 1 ? 'Your plan is' : 'Your plans are'} enabled but not due: check the days and start times.`,
    };
  }

  return null;
}

function nextRunFromPrograms(programs: Program[]): NextRun {
  const active = programs.filter((program) => program.enabled);

  if (active.length === 0) {
    return {
      headline: 'No watering scheduled',
      detail: 'Set up a plan to water automatically, or run a zone by hand.',
    };
  }

  const soonest = active
    .map((program) => ({
      program,
      start: Math.min(...program.startTimes.filter((time) => time >= 0 && time < 1440)),
    }))
    .filter((entry) => Number.isFinite(entry.start))
    .sort((a, b) => a.start - b.start)[0];

  if (!soonest) {
    return {
      headline: 'No start time set',
      detail: 'Programs are configured but none has a start time yet.',
    };
  }

  return {
    headline: `Next run at ${formatStartTime(soonest.start)}`,
    detail: `Program ${soonest.program.label} · ${describeFrequency(soonest.program)} · ${soonest.program.totalMinutes} min total`,
  };
}
