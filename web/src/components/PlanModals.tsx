import { observer } from 'mobx-react-lite';
import { useEffect, useState } from 'react';
import type { PlanFrequency, PlanZone, SavePlan } from '../api/types';
import { useStore } from '../stores/RootStore';
import { formatMinuteOfDay, toSavePlan } from '../stores/PlanStore';
import { CloseIcon, DropIcon, PlusIcon } from './Icons';
import { Button, DayPicker, Field, Select, Sheet, Stepper, TextInput, Toggle } from './ui';

const PLAN_FREQUENCY_OPTIONS: { value: PlanFrequency; label: string }[] = [
  { value: 'DaysOfWeek', label: 'Days of the week' },
  { value: 'EveryDay', label: 'Every day' },
  { value: 'EveryNDays', label: 'Every N days' },
  { value: 'OddDays', label: 'Odd days' },
  { value: 'EvenDays', label: 'Even days' },
];

/** Every modal belonging to watering plans. */
export const PlanModals = observer(function PlanModals() {
  return (
    <>
      <PlanPresetSheet />
      <PlanEditorSheet />
    </>
  );
});

/* -------------------------------------------------------- plan preset picker */

/**
 * Presets, each shown with the reasoning behind it.
 *
 * The horticultural "why" is the useful part. Knowing that seed wants little and
 * often, and that an established lawn wants the opposite, is what lets someone
 * choose well — and choose to stop, once the grass has taken.
 */
const PlanPresetSheet = observer(function PlanPresetSheet() {
  const { ui, plans } = useStore();
  const [busy, setBusy] = useState<string | null>(null);

  return (
    <Sheet open={ui.planPickerOpen} onClose={() => ui.setPlanPickerOpen(false)} title="Add a plan">
      <div className="form-stack">
        <p className="muted">
          Start from one of these and adjust it. A new plan arrives switched off, so nothing waters
          until you have looked at the run times.
        </p>

        <div className="preset-list">
          {plans.presets.map((preset) => (
            <button
              key={preset.key}
              className="preset"
              disabled={busy !== null}
              onClick={async () => {
                setBusy(preset.key);
                const created = await plans.createFromPreset(preset.key);
                setBusy(null);
                if (created) ui.openPlanEditor(created.id);
              }}
            >
              <span className="preset__head">
                <span className="preset__name">{preset.name}</span>
                <span className="preset__summary">{preset.summary}</span>
              </span>
              <span className="preset__why">{preset.rationale}</span>
            </button>
          ))}
        </div>

        <Button tone="ghost" full onClick={() => ui.openPlanEditor('new')}>
          Start from scratch instead
        </Button>
      </div>
    </Sheet>
  );
});

/* --------------------------------------------------------------- plan editor */

/** A blank plan, for "start from scratch". */
function emptyPlan(stations: number[]): SavePlan {
  return {
    name: 'New plan',
    description: '',
    enabled: false,
    frequency: 'DaysOfWeek',
    daysOfWeek: [false, true, false, true, false, true, false],
    intervalDays: 2,
    startTimes: [360],
    latestStartMinute: null,
    seasonalAdjustPercent: 100,
    cycleSoakEnabled: false,
    cycles: 2,
    soakMinutes: 15,
    weatherSkipEnabled: true,
    zones: stations.map((station, index) => ({
      stationNumber: station,
      minutes: 10,
      sortOrder: index,
    })),
  };
}

function minutesToTimeValue(minutes: number): string {
  const safe = Math.max(0, Math.min(1439, minutes));
  return `${String(Math.floor(safe / 60)).padStart(2, '0')}:${String(safe % 60).padStart(2, '0')}`;
}

const PlanEditorSheet = observer(function PlanEditorSheet() {
  const { ui, plans, zones } = useStore();

  const open = ui.openPlan !== null;
  const existing = typeof ui.openPlan === 'number' ? plans.byId(ui.openPlan) : undefined;

  const [draft, setDraft] = useState<SavePlan | null>(null);

  // Reload the form whenever a different plan is opened.
  useEffect(() => {
    if (ui.openPlan === null) return;

    if (ui.openPlan === 'new') {
      setDraft(emptyPlan(zones.visible.map((zone) => zone.stationNumber)));
    } else if (existing) {
      setDraft(toSavePlan(existing));
    }
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [ui.openPlan, existing?.id]);

  if (!open || !draft) {
    return (
      <Sheet open={false} onClose={() => ui.closePlanEditor()} title="Plan">
        {null}
      </Sheet>
    );
  }

  const patch = (changes: Partial<SavePlan>) => setDraft({ ...draft, ...changes });

  const minutesFor = (station: number) =>
    draft.zones.find((zone) => zone.stationNumber === station)?.minutes ?? 0;

  /** Rebuilds the zone list so ordering follows the yard, not edit order. */
  const setZoneMinutes = (station: number, minutes: number) => {
    const rebuilt: PlanZone[] = zones.visible.map((zone, index) => ({
      stationNumber: zone.stationNumber,
      minutes: zone.stationNumber === station ? minutes : minutesFor(zone.stationNumber),
      sortOrder: index,
    }));

    patch({ zones: rebuilt.filter((zone) => zone.minutes > 0) });
  };

  const minutesPerPass = draft.zones.reduce(
    (total, zone) => total + Math.round((zone.minutes * draft.seasonalAdjustPercent) / 100),
    0,
  );

  return (
    <Sheet
      open={open}
      onClose={() => ui.closePlanEditor()}
      title={existing ? existing.name : 'New plan'}
      footer={
        <>
          <Button tone="ghost" onClick={() => ui.closePlanEditor()}>
            Cancel
          </Button>
          <Button
            tone="primary"
            full
            disabled={plans.saving || draft.zones.length === 0}
            onClick={async () => {
              const saved = await plans.save(existing ? existing.id : null, draft);
              if (saved) ui.closePlanEditor();
            }}
          >
            {plans.saving
              ? 'Saving…'
              : draft.zones.length === 0
                ? 'Give a zone a run time'
                : `Save · ${minutesPerPass} min a pass`}
          </Button>
        </>
      }
    >
      <div className="form-stack">
        <Field label="Name">
          <TextInput value={draft.name} onChange={(name) => patch({ name })} />
        </Field>

        <Field label="Notes" hint="Optional. What this plan is for.">
          <TextInput
            value={draft.description}
            onChange={(description) => patch({ description })}
            placeholder="e.g. while the new seed establishes"
          />
        </Field>

        <Toggle
          label="Plan is on"
          hint="A plan that is off keeps its settings but never runs."
          checked={draft.enabled}
          onChange={(enabled) => patch({ enabled })}
        />

        <Field label="Water on">
          <Select<PlanFrequency>
            value={draft.frequency}
            onChange={(frequency) => patch({ frequency })}
            options={PLAN_FREQUENCY_OPTIONS}
          />
        </Field>

        {draft.frequency === 'DaysOfWeek' && (
          <Field label="Days">
            <DayPicker days={draft.daysOfWeek} onChange={(daysOfWeek) => patch({ daysOfWeek })} />
          </Field>
        )}

        {draft.frequency === 'EveryNDays' && (
          <Field label="Interval">
            <Stepper
              value={draft.intervalDays}
              onChange={(intervalDays) => patch({ intervalDays })}
              min={1}
              max={31}
              suffix=" days"
            />
          </Field>
        )}

        <Field
          label="Start times"
          hint="Each one runs the whole plan. Several a day is how a seed bed is kept damp — and is the thing a controller program cannot do."
        >
          <div className="starts">
            {draft.startTimes.map((minutes, position) => (
              <div key={position} className="starts__item">
                <input
                  className="input starts__input"
                  type="time"
                  value={minutesToTimeValue(minutes)}
                  onChange={(event) => {
                    const [hours, mins] = event.target.value.split(':').map(Number);
                    const next = [...draft.startTimes];
                    next[position] = (hours || 0) * 60 + (mins || 0);
                    patch({ startTimes: next });
                  }}
                />
                <button
                  className="starts__remove"
                  disabled={draft.startTimes.length === 1}
                  aria-label={`Remove the ${formatMinuteOfDay(minutes)} start`}
                  onClick={() =>
                    patch({ startTimes: draft.startTimes.filter((_, i) => i !== position) })
                  }
                >
                  <CloseIcon size={15} />
                </button>
              </div>
            ))}

            <button
              className="starts__add"
              onClick={() => {
                const last = draft.startTimes[draft.startTimes.length - 1] ?? 360;
                patch({ startTimes: [...draft.startTimes, Math.min(1380, last + 240)] });
              }}
            >
              <PlusIcon size={15} />
              Add a start time
            </button>
          </div>
        </Field>

        <Toggle
          label="Break each zone into shorter passes"
          hint="Long runs on clay or a slope run off before they soak in. Splitting them lets the water go into the soil instead of down the drive."
          checked={draft.cycleSoakEnabled}
          onChange={(cycleSoakEnabled) => patch({ cycleSoakEnabled })}
        />

        {draft.cycleSoakEnabled && (
          <div className="grid-2">
            <Field label="Passes per zone">
              <Stepper
                value={draft.cycles}
                onChange={(cycles) => patch({ cycles })}
                min={2}
                max={10}
                suffix="×"
              />
            </Field>
            <Field label="Rest between">
              <Stepper
                value={draft.soakMinutes}
                onChange={(soakMinutes) => patch({ soakMinutes })}
                min={0}
                max={120}
                step={5}
                suffix=" min"
              />
            </Field>
          </div>
        )}

        <Field label="Seasonal adjust" hint="Scales every zone in this plan.">
          <Stepper
            value={draft.seasonalAdjustPercent}
            onChange={(seasonalAdjustPercent) => patch({ seasonalAdjustPercent })}
            min={0}
            max={200}
            step={5}
            suffix="%"
          />
        </Field>

        <Toggle
          label="Let the weather skip this plan"
          hint="Turn this off for seed or new sod, where letting the bed dry out costs more than the water saved."
          checked={draft.weatherSkipEnabled}
          onChange={(weatherSkipEnabled) => patch({ weatherSkipEnabled })}
        />

        <Field label="Zone run times" hint="Set a zone to zero to leave it out of this plan.">
          <div className="runtimes">
            {zones.visible.map((zone) => {
              const minutes = minutesFor(zone.stationNumber);
              return (
                <div
                  key={zone.stationNumber}
                  className={`runtimes__row${minutes === 0 ? ' is-off' : ''}`}
                >
                  <span className="runtimes__station data">
                    {String(zone.stationNumber).padStart(2, '0')}
                  </span>
                  <span className="runtimes__name">{zone.name}</span>
                  <Stepper
                    value={minutes}
                    onChange={(next) => setZoneMinutes(zone.stationNumber, next)}
                    min={0}
                    max={240}
                    suffix="m"
                    label={`${zone.name} run time`}
                  />
                </div>
              );
            })}
          </div>
        </Field>

        {existing && existing.timeline.length > 0 && (
          <Field label="What one pass looks like" hint="As last saved.">
            <ol className="timeline">
              {existing.timeline.map((step, index) => (
                <li key={index} className={`timeline__step${step.isSoak ? ' is-soak' : ''}`}>
                  <span className="timeline__marker" aria-hidden>
                    {step.isSoak ? null : <DropIcon size={12} />}
                  </span>
                  <span className="timeline__label">{step.isSoak ? 'Soak' : step.zoneName}</span>
                  <span className="timeline__time data">{step.minutes}m</span>
                </li>
              ))}
            </ol>
          </Field>
        )}
      </div>
    </Sheet>
  );
});
