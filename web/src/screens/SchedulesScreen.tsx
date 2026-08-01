import { AnimatePresence, motion } from 'framer-motion';
import { observer } from 'mobx-react-lite';
import { useState } from 'react';
import type { Plan, Program } from '../api/types';
import { AlertIcon, CalendarIcon, ChevronIcon, DropIcon, PlusIcon, StopIcon } from '../components/Icons';
import {
  Button,
  Card,
  EmptyState,
  Pill,
  SectionHead,
  Segmented,
  Skeleton,
  Toggle,
} from '../components/ui';
import { useStore } from '../stores/RootStore';
import { describeFrequency, formatStartTime } from '../stores/ScheduleStore';
import { describeNextRun, describePlanFrequency, describeStartTimes } from '../stores/PlanStore';

type View = 'plans' | 'programs' | 'calendar';

export const SchedulesScreen = observer(function SchedulesScreen() {
  const { controllers } = useStore();
  const [view, setView] = useState<View>('plans');

  // The controller's own programs are only worth a tab when it will actually show
  // them to us. On current firmware it will not.
  const showPrograms = controllers.capabilities?.supportsSchedulePages ?? false;

  const options: { value: View; label: string }[] = [
    { value: 'plans', label: 'Plans' },
    ...(showPrograms ? [{ value: 'programs' as const, label: 'Controller' }] : []),
    { value: 'calendar', label: 'Calendar' },
  ];

  return (
    <div className="stack">
      <SectionHead
        eyebrow="Watering"
        title="Schedules"
        action={<Segmented label="Schedule view" value={view} onChange={setView} options={options} />}
      />

      {view === 'plans' && <PlansView />}
      {view === 'programs' && <ProgramList />}
      {view === 'calendar' && <MonthCalendar />}
    </div>
  );
});

/* ------------------------------------------------------------------- plans */

const PlansView = observer(function PlansView() {
  const { plans, ui, controllers } = useStore();

  if (plans.loading && plans.plans.length === 0) {
    return <Skeleton height={140} count={2} />;
  }

  return (
    <div className="stack">
      <CompetingScheduleBanner />
      <ActivePlanCard />

      {plans.plans.length === 0 ? (
        <Card>
          <EmptyState
            icon={<CalendarIcon size={40} strokeWidth={1.2} />}
            title="No plans yet"
            detail="A plan is a watering schedule this app runs itself — several passes a day, cycle and soak, whatever the job needs. Start from a preset and adjust it."
            action={
              <Button tone="primary" icon={<PlusIcon size={16} />} onClick={() => ui.setPlanPickerOpen(true)}>
                Add a plan
              </Button>
            }
          />
        </Card>
      ) : (
        <>
          <div className="stack-sm">
            {plans.plans.map((plan, index) => (
              <motion.div
                key={plan.id}
                initial={{ opacity: 0, y: 8 }}
                animate={{ opacity: 1, y: 0 }}
                transition={{ delay: Math.min(index * 0.04, 0.2), duration: 0.28 }}
              >
                <PlanCard plan={plan} />
              </motion.div>
            ))}
          </div>

          <Button icon={<PlusIcon size={16} />} onClick={() => ui.setPlanPickerOpen(true)} full>
            Add another plan
          </Button>
        </>
      )}

      {!controllers.online && plans.plans.length > 0 && (
        <p className="muted">
          The controller is not responding, so nothing will run until it is back.
        </p>
      )}
    </div>
  );
});

/**
 * Warns when the controller still has run times of its own.
 *
 * Two schedules watering the same yard is the failure mode this whole feature has
 * to avoid, and nothing on the controller would tell you it was happening.
 */
const CompetingScheduleBanner = observer(function CompetingScheduleBanner() {
  const { plans } = useStore();

  if (!plans.hasCompetingSchedule) return null;

  return (
    <Card className="banner banner--warn">
      <div className="banner__body">
        <span className="banner__icon">
          <AlertIcon size={20} />
        </span>
        <div>
          <p className="banner__title">The controller has its own schedule too</p>
          <p className="banner__detail">{plans.armed?.explanation}</p>
        </div>
      </div>
      <Button
        tone="quiet"
        size="sm"
        disabled={plans.disarming}
        onClick={() => plans.disarm()}
      >
        {plans.disarming ? 'Clearing…' : 'Clear it'}
      </Button>
    </Card>
  );
});

const ActivePlanCard = observer(function ActivePlanCard() {
  const { plans } = useStore();
  const active = plans.active;

  return (
    <AnimatePresence>
      {active && (
        <motion.div
          initial={{ opacity: 0, y: -8 }}
          animate={{ opacity: 1, y: 0 }}
          exit={{ opacity: 0, height: 0 }}
        >
          <Card className="active-plan">
            <div className="active-plan__head">
              <span className="hero__pulse" aria-hidden />
              <span className="eyebrow">Running now</span>
            </div>

            <p className="active-plan__name">{active.planName}</p>
            <p className="active-plan__step data">
              {active.soaking
                ? `Soaking · ${active.stepMinutes} min`
                : `${active.currentZoneName ?? `Zone ${active.currentStation}`} · ${active.stepMinutes} min`}
              {'  ·  '}
              step {active.stepIndex} of {active.stepCount}
            </p>

            <div className="active-plan__track" aria-hidden>
              <motion.span
                className="active-plan__fill"
                initial={false}
                animate={{ width: `${(active.stepIndex / Math.max(1, active.stepCount)) * 100}%` }}
                transition={{ duration: 0.4, ease: [0.22, 1, 0.36, 1] }}
              />
            </div>

            <div className="active-plan__actions">
              <Button tone="primary" size="sm" icon={<StopIcon size={15} />} onClick={() => plans.cancel()}>
                Stop the plan
              </Button>
            </div>
          </Card>
        </motion.div>
      )}
    </AnimatePresence>
  );
});

const PlanCard = observer(function PlanCard({ plan }: { plan: Plan }) {
  const { plans, ui, controllers, zones } = useStore();
  const running = plans.active?.planId === plan.id;

  return (
    <Card className={`plan${plan.enabled ? '' : ' is-off'}${running ? ' is-running' : ''}`}>
      <div className="plan__head">
        <div className="plan__title-group">
          <h3 className="plan__title">{plan.name}</h3>
          <p className="plan__sub">
            {describePlanFrequency(plan)} · {describeStartTimes(plan)}
          </p>
        </div>
        <Toggle
          label=""
          ariaLabel={`${plan.name} is ${plan.enabled ? 'on' : 'off'}`}
          checked={plan.enabled}
          onChange={(enabled) => plans.setEnabled(plan, enabled)}
        />
      </div>

      {plan.description && <p className="plan__description">{plan.description}</p>}

      <div className="plan__stats">
        <span className="prog__stat">
          <span className="prog__stat-value data">{plan.passesPerDay}</span>
          <span className="prog__stat-label">{plan.passesPerDay === 1 ? 'pass a day' : 'passes a day'}</span>
        </span>
        <span className="prog__stat">
          <span className="prog__stat-value data">{plan.wateringMinutesPerDay}</span>
          <span className="prog__stat-label">minutes a day</span>
        </span>
        <span className="prog__stat">
          <span className="prog__stat-value data">{plan.zones.length}</span>
          <span className="prog__stat-label">zones</span>
        </span>
        {plan.cycleSoakEnabled && (
          <span className="prog__stat">
            <span className="prog__stat-value data">{plan.cycles}×</span>
            <span className="prog__stat-label">cycle &amp; soak</span>
          </span>
        )}
      </div>

      <div className="plan__meta">
        {plan.enabled ? (
          <Pill tone="water">Next {describeNextRun(plan.nextRunUtc)}</Pill>
        ) : (
          <Pill tone="neutral">Off</Pill>
        )}
        {plan.seasonalAdjustPercent !== 100 && (
          <Pill tone="dawn">{plan.seasonalAdjustPercent}% seasonal</Pill>
        )}
        {!plan.weatherSkipEnabled && <Pill tone="neutral">Ignores weather</Pill>}
      </div>

      {plan.zones.length > 0 && (
        <div className="prog__zones">
          {plan.zones.slice(0, 8).map((zone) => (
            <span key={zone.stationNumber} className="prog__zone">
              <span className="prog__zone-name">
                {zones.byStation(zone.stationNumber)?.name ?? `Zone ${zone.stationNumber}`}
              </span>
              <span className="prog__zone-time data">{zone.minutes}m</span>
            </span>
          ))}
        </div>
      )}

      <div className="prog__actions">
        <Button size="sm" onClick={() => ui.openPlanEditor(plan.id)}>
          Edit
        </Button>
        <Button
          size="sm"
          tone="ghost"
          disabled={!controllers.online || running || plan.zones.length === 0}
          onClick={() => plans.run(plan)}
        >
          {running ? 'Running' : 'Run now'}
        </Button>
        <Button
          size="sm"
          tone="ghost"
          onClick={() => {
            if (confirm(`Delete "${plan.name}"?`)) void plans.remove(plan);
          }}
        >
          Delete
        </Button>
      </div>
    </Card>
  );
});

/* --------------------------------------------------------------- programs */

const ProgramList = observer(function ProgramList() {
  const { schedules, ui } = useStore();

  if (schedules.loading && schedules.programs.length === 0) {
    return <Skeleton height={120} count={3} />;
  }

  if (schedules.programs.length === 0) {
    return (
      <Card>
        <EmptyState title="No programs read yet" detail="The controller did not return any schedule pages." />
      </Card>
    );
  }

  return (
    <div className="stack-sm">
      <p className="muted">
        These are the controller's own programs, stored on the device. Leave them empty if you want
        the plans above to be the only thing watering.
      </p>
      {schedules.programs.map((program, index) => (
        <motion.div
          key={program.programNumber}
          initial={{ opacity: 0, y: 8 }}
          animate={{ opacity: 1, y: 0 }}
          transition={{ delay: index * 0.04, duration: 0.28 }}
        >
          <ProgramCard program={program} onEdit={() => ui.openProgramEditor(program.programNumber)} />
        </motion.div>
      ))}
    </div>
  );
});

const ProgramCard = observer(function ProgramCard({
  program,
  onEdit,
}: {
  program: Program;
  onEdit: () => void;
}) {
  const { schedules, controllers, zones } = useStore();
  const starts = program.startTimes.filter((time) => time >= 0 && time < 1440);
  const activeZones = Object.entries(program.stationRunTimes).filter(([, minutes]) => minutes > 0);

  return (
    <Card className={`prog${program.enabled ? '' : ' is-off'}`}>
      <div className="prog__head">
        <span className="prog__badge data">{program.label}</span>
        <div className="prog__title-group">
          <h3 className="prog__title">{describeFrequency(program)}</h3>
          <p className="prog__sub data">
            {starts.length > 0 ? starts.map(formatStartTime).join('  ·  ') : 'No start time'}
          </p>
        </div>
        {program.enabled ? <Pill tone="turf">On</Pill> : <Pill tone="neutral">Off</Pill>}
      </div>

      <div className="prog__stats">
        <span className="prog__stat">
          <span className="prog__stat-value data">{activeZones.length}</span>
          <span className="prog__stat-label">zones</span>
        </span>
        <span className="prog__stat">
          <span className="prog__stat-value data">{program.totalMinutes}</span>
          <span className="prog__stat-label">minutes total</span>
        </span>
        <span className="prog__stat">
          <span className="prog__stat-value data">{program.seasonalAdjustPercent}%</span>
          <span className="prog__stat-label">seasonal adjust</span>
        </span>
      </div>

      {activeZones.length > 0 && (
        <div className="prog__zones">
          {activeZones.slice(0, 8).map(([station, minutes]) => (
            <span key={station} className="prog__zone">
              <span className="prog__zone-name">
                {zones.byStation(Number(station))?.name ?? `Zone ${station}`}
              </span>
              <span className="prog__zone-time data">{minutes}m</span>
            </span>
          ))}
        </div>
      )}

      <div className="prog__actions">
        <Button size="sm" onClick={onEdit}>
          Edit
        </Button>
        <Button
          size="sm"
          tone="ghost"
          onClick={() => schedules.run(program.programNumber)}
          disabled={!controllers.online || activeZones.length === 0}
        >
          Run now
        </Button>
      </div>
    </Card>
  );
});

/* --------------------------------------------------------------- calendar */

const MonthCalendar = observer(function MonthCalendar() {
  const { history, controllers } = useStore();

  const year = history.calendarYear;
  const month = history.calendarMonth;
  const monthName = new Date(year, month - 1, 1).toLocaleDateString(undefined, {
    month: 'long',
    year: 'numeric',
  });

  const firstWeekday = new Date(year, month - 1, 1).getDay();
  const daysInMonth = new Date(year, month, 0).getDate();
  const today = new Date().toLocaleDateString('en-CA');

  const cells: (number | null)[] = [
    ...Array.from({ length: firstWeekday }, () => null),
    ...Array.from({ length: daysInMonth }, (_, index) => index + 1),
  ];

  return (
    <Card padded={false}>
      <div className="cal__head">
        <button className="cal__nav" onClick={() => history.stepMonth(-1)} aria-label="Previous month">
          <ChevronIcon size={18} className="cal__nav-prev" />
        </button>
        <h3 className="cal__month">{monthName}</h3>
        <button className="cal__nav" onClick={() => history.stepMonth(1)} aria-label="Next month">
          <ChevronIcon size={18} />
        </button>
      </div>

      <div className="cal__weekdays">
        {['S', 'M', 'T', 'W', 'T', 'F', 'S'].map((day, index) => (
          <span key={index} className="cal__weekday">
            {day}
          </span>
        ))}
      </div>

      <div className="cal__grid">
        {cells.map((day, index) => {
          if (day === null) return <span key={`pad-${index}`} className="cal__cell is-empty" />;

          const date = `${year}-${String(month).padStart(2, '0')}-${String(day).padStart(2, '0')}`;
          const entry = history.calendarDay(date);
          const isToday = date === today;

          return (
            <span key={date} className={`cal__cell${isToday ? ' is-today' : ''}`}>
              <span className="cal__date data">{day}</span>
              {entry?.skipReason ? (
                <span className="cal__mark cal__mark--skip" title={`Skipped — ${entry.skipReason}`}>
                  <AlertIcon size={13} />
                </span>
              ) : entry && entry.runCount > 0 ? (
                <span
                  className="cal__mark cal__mark--ran"
                  title={`${entry.runCount} runs · ${entry.totalMinutes} min · ${entry.gallons} gal`}
                >
                  <DropIcon size={13} />
                  {entry.runCount > 1 && <span className="cal__count data">{entry.runCount}</span>}
                </span>
              ) : null}
            </span>
          );
        })}
      </div>

      <div className="cal__legend">
        <span className="cal__legend-item">
          <DropIcon size={13} className="cal__legend-ran" /> Watered
        </span>
        <span className="cal__legend-item">
          <AlertIcon size={13} className="cal__legend-skip" /> Skipped
        </span>
        <span className="cal__legend-spacer" />
        <Button
          size="sm"
          tone="ghost"
          onClick={() => controllers.setRainDelay(1)}
          disabled={!controllers.online}
        >
          Delay watering
        </Button>
      </div>
    </Card>
  );
});
