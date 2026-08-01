import { motion } from 'framer-motion';
import { observer } from 'mobx-react-lite';
import { useState } from 'react';
import type { RunTrigger, WeatherDay } from '../api/types';
import { AlertIcon, DropIcon, WeatherIcon, WindIcon } from '../components/Icons';
import { StatusHero } from '../components/NowWatering';
import { Button, Card, EmptyState, Pill, SectionHead, Segmented, Skeleton } from '../components/ui';
import { useStore } from '../stores/RootStore';
import { describeFrequency, formatStartTime } from '../stores/ScheduleStore';

export const EventsScreen = observer(function EventsScreen() {
  const { controllers } = useStore();

  if (controllers.loading) {
    return <Skeleton height={200} count={3} />;
  }

  if (!controllers.selected) {
    return (
      <EmptyState
        icon={<DropIcon size={40} strokeWidth={1.2} />}
        title="No controller yet"
        detail="Add your Rain Bird controller to start watering from here."
      />
    );
  }

  return (
    <div className="stack">
      <StatusHero />
      <WeatherStrip />
      <RunTimeline />
      <UsagePanel />
    </div>
  );
});

/* ---------------------------------------------------------------- forecast */

const WeatherStrip = observer(function WeatherStrip() {
  const { weather } = useStore();
  const days = weather.strip;

  if (days.length === 0) {
    return (
      <Card>
        <SectionHead eyebrow="Forecast" title="No location set" />
        <p className="muted">
          Add coordinates in Settings and the forecast will drive rain, freeze and wind skips.
        </p>
      </Card>
    );
  }

  const today = new Date().toLocaleDateString('en-CA');

  return (
    <section>
      <SectionHead eyebrow="Forecast" title="This week" />
      <div className="wx-strip">
        {days.map((day) => (
          <WeatherCell key={day.date} day={day} isToday={day.date === today} />
        ))}
      </div>
    </section>
  );
});

const WeatherCell = observer(function WeatherCell({ day, isToday }: { day: WeatherDay; isToday: boolean }) {
  const { weather } = useStore();
  const label = isToday
    ? 'Today'
    : new Date(`${day.date}T12:00:00`).toLocaleDateString(undefined, { weekday: 'short' });

  return (
    <div className={`wx-cell${isToday ? ' is-today' : ''}`}>
      <span className="wx-cell__day">{label}</span>
      <span className="wx-cell__icon">
        <WeatherIcon condition={day.condition} size={28} />
      </span>
      <span className="wx-cell__temps data">
        <span className="wx-cell__high">{weather.temp(day.tempHighC)}</span>
        <span className="wx-cell__low">{weather.temp(day.tempLowC)}</span>
      </span>
      {day.precipitationProbability >= 20 && (
        <span className="wx-cell__precip data">{day.precipitationProbability}%</span>
      )}
      {day.skipReason && (
        <span className="wx-cell__skip" title={`Skipped — ${day.skipReason.toLowerCase()}`}>
          {day.skipReason === 'Wind' ? <WindIcon size={13} /> : <AlertIcon size={13} />}
        </span>
      )}
      {day.hasScheduledRun && !day.skipReason && (
        <span className="wx-cell__ran" aria-label="Watered">
          <DropIcon size={13} />
        </span>
      )}
    </div>
  );
});

/* --------------------------------------------------------------- timeline */

const RunTimeline = observer(function RunTimeline() {
  const { history, schedules, controllers } = useStore();
  const [view, setView] = useState<'upcoming' | 'past'>('past');

  return (
    <section>
      <SectionHead
        eyebrow="Activity"
        title="Watering"
        action={
          <Segmented
            label="Watering history"
            value={view}
            onChange={setView}
            options={[
              { value: 'upcoming', label: 'Upcoming' },
              { value: 'past', label: 'Past' },
            ]}
          />
        }
      />

      {view === 'upcoming' ? (
        schedules.active.length === 0 ? (
          <Card>
            <EmptyState
              title="Nothing scheduled"
              detail="Programs with a start time and at least one zone run time will appear here."
            />
          </Card>
        ) : (
          <div className="stack-sm">
            {schedules.active.map((program) => {
              const starts = program.startTimes.filter((time) => time >= 0 && time < 1440);
              return (
                <Card key={program.programNumber} className="run-card">
                  <div className="run-card__lead">
                    <span className="run-card__badge data">{program.label}</span>
                  </div>
                  <div className="run-card__body">
                    <p className="run-card__title">
                      {starts.map(formatStartTime).join(' · ') || 'No start time'}
                    </p>
                    <p className="run-card__meta">
                      {describeFrequency(program)} · {program.totalMinutes} min
                    </p>
                  </div>
                  <Button
                    size="sm"
                    onClick={() => schedules.run(program.programNumber)}
                    disabled={!controllers.online}
                  >
                    Run now
                  </Button>
                </Card>
              );
            })}
          </div>
        )
      ) : history.runsByDay.length === 0 ? (
        <Card>
          <EmptyState
            title="No watering recorded yet"
            detail="Runs are logged as they happen, including manual ones started from this app."
          />
        </Card>
      ) : (
        <div className="stack-sm">
          {history.runsByDay.slice(0, 5).map((group, index) => (
            <motion.div
              key={group.day}
              initial={{ opacity: 0, y: 6 }}
              animate={{ opacity: 1, y: 0 }}
              transition={{ delay: Math.min(index * 0.03, 0.2), duration: 0.25 }}
            >
              <div className="day-group">
                <div className="day-group__head">
                  <span className="day-group__label">{group.label}</span>
                  <span className="day-group__stat data">
                    {group.minutes} min · {group.gallons} gal
                  </span>
                </div>
                <Card padded={false}>
                  {group.runs.map((run) => (
                    <div key={run.id} className="run-row">
                      <span className="run-row__station data">
                        {String(run.stationNumber).padStart(2, '0')}
                      </span>
                      <span className="run-row__name">{run.zoneName}</span>
                      <span className="run-row__trigger">
                        <RunTriggerPill trigger={run.trigger} />
                      </span>
                      <span className="run-row__time data">
                        {new Date(run.startedUtc).toLocaleTimeString(undefined, {
                          hour: 'numeric',
                          minute: '2-digit',
                        })}
                      </span>
                      <span className="run-row__duration data">
                        {formatRunDuration(run.durationSeconds)}
                      </span>
                    </div>
                  ))}
                </Card>
              </div>
            </motion.div>
          ))}
        </div>
      )}
    </section>
  );
});

/**
 * How a run started.
 *
 * The protocol has no field for this, so it is only known when this app issued the
 * command. Anything else is labelled "Scheduled" — meaning the controller ran it on
 * its own, whether from its programs or from someone pressing buttons on the panel.
 */
function RunTriggerPill({ trigger }: { trigger: RunTrigger }) {
  switch (trigger) {
    case 'Manual':
      return <Pill tone="water">Manual</Pill>;
    case 'Test':
      return <Pill tone="dawn">Test</Pill>;
    case 'Program':
      return <Pill tone="neutral">Program</Pill>;
    default:
      return <Pill tone="neutral">Scheduled</Pill>;
  }
}

/**
 * Run length for the history list. A run that was stopped after a few seconds is
 * real and worth showing, but rounding it to minutes renders it as "0 min", which
 * reads like a bug rather than a short run.
 */
function formatRunDuration(seconds: number): string {
  if (seconds < 60) return `${seconds}s`;
  return `${Math.round(seconds / 60)} min`;
}

/* ------------------------------------------------------------------ usage */

const UsagePanel = observer(function UsagePanel() {
  const { history, weather } = useStore();
  const usage = history.usage;

  if (!usage || usage.runCount === 0) return null;

  const monthLabel = new Date(`${usage.month}-02T12:00:00`).toLocaleDateString(undefined, {
    month: 'long',
  });

  const max = Math.max(...usage.byZone.map((zone) => zone.gallons), 1);

  const litres = usage.gallonsUsed * 3.785;
  const volume = weather.units.useMetric
    ? { value: formatVolume(litres), unit: 'litres' }
    : { value: formatVolume(usage.gallonsUsed), unit: 'gallons' };

  // Rounding to whole hours reports a real ten-minute run as "0 hours of watering",
  // which reads as a bug rather than as a small number.
  const duration = usage.totalMinutes < 60
    ? { value: String(Math.round(usage.totalMinutes)), unit: plural(Math.round(usage.totalMinutes), 'minute') }
    : { value: String(Math.round(usage.totalMinutes / 60)), unit: plural(Math.round(usage.totalMinutes / 60), 'hour') };

  return (
    <section>
      <SectionHead eyebrow="Water use" title={`${monthLabel} so far`} />
      <Card>
        {/* Three figures of equal standing, so they are set at equal weight. The
            first used to be twice the size of the others, which on a baseline row
            pushed its label out of line with theirs and read as a mistake rather
            than as emphasis. */}
        <div className="usage__figures">
          <div className="usage__figure">
            <span className="usage__value data">{volume.value}</span>
            <span className="usage__unit">{volume.unit}, estimated</span>
          </div>
          <div className="usage__figure">
            <span className="usage__value data">{duration.value}</span>
            <span className="usage__unit">{duration.unit} of watering</span>
          </div>
          <div className="usage__figure">
            <span className="usage__value data">{usage.runCount.toLocaleString()}</span>
            <span className="usage__unit">zone {plural(usage.runCount, 'run')}</span>
          </div>
        </div>

        <p className="usage__caveat">
          Estimated from run time and the nozzle flow rate set for each zone. Rain Bird residential
          controllers have no flow meter, so this is a calculation, not a measurement.
        </p>

        <div className="usage__bars">
          {usage.byZone.slice(0, 6).map((zone) => (
            <div key={zone.stationNumber} className="usage__bar">
              <span className="usage__bar-name">{zone.zoneName}</span>
              <span className="usage__bar-track">
                <motion.span
                  className="usage__bar-fill"
                  initial={{ width: 0 }}
                  animate={{ width: `${(zone.gallons / max) * 100}%` }}
                  transition={{ duration: 0.6, ease: [0.22, 1, 0.36, 1] }}
                />
              </span>
              <span className="usage__bar-value data">{Math.round(zone.gallons)}</span>
            </div>
          ))}
        </div>
      </Card>
    </section>
  );
});

/** "1 run" but "2 runs". Naive on purpose: every word this is used on is regular. */
function plural(count: number, word: string): string {
  return count === 1 ? word : `${word}s`;
}

/**
 * Water use, at a precision that does not overstate what is known.
 *
 * A short run rounds to zero whole gallons, and "0 gallons, estimated" next to a run
 * that definitely happened looks broken. Under ten, one decimal place says small
 * rather than none; above it, the decimal is noise on a number that was a
 * calculation from a nozzle rating in the first place.
 */
function formatVolume(value: number): string {
  if (value > 0 && value < 10) return value.toFixed(1);
  return Math.round(value).toLocaleString();
}
