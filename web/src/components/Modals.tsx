import { observer } from 'mobx-react-lite';
import { useEffect, useRef, useState } from 'react';
import type { FrequencyType, PlantType, SoilType, SprinklerType, SunExposure } from '../api/types';
import { AddControllerSheetContent, EditControllerSheetContent } from '../screens/SettingsScreen';
import { HEAD_LABELS, PLANT_LABELS } from '../screens/ZonesScreen';
import { useStore } from '../stores/RootStore';
import { FREQUENCY_OPTIONS, UNSET_START_TIME, formatStartTime } from '../stores/ScheduleStore';
import { CameraIcon, CloseIcon, PlusIcon } from './Icons';
import { PlanModals } from './PlanModals';
import { useMediaUrl } from './useMediaUrl';
import {
  Button,
  DayPicker,
  DraftTextInput,
  Field,
  Select,
  Sheet,
  Stepper,
  Toggle,
} from './ui';

/** Every modal in the app, driven by UiStore so nothing else has to own open state. */
export const Modals = observer(function Modals() {
  return (
    <>
      <QuickRunSheet />
      <ZoneSheet />
      <ProgramEditorSheet />
      <PlanModals />
      <AddControllerSheet />
      <EditControllerSheet />
    </>
  );
});

/* --------------------------------------------------------------- quick run */

const QuickRunSheet = observer(function QuickRunSheet() {
  const { ui, zones, controllers } = useStore();
  const [minutes, setMinutes] = useState(10);
  const [selected, setSelected] = useState<number[]>([]);

  useEffect(() => {
    if (ui.quickRunOpen) setSelected([]);
  }, [ui.quickRunOpen]);

  const start = async () => {
    if (selected.length === 0) return;

    // The first zone starts immediately; the rest queue behind it, which is what
    // the controller's own stacking command is for.
    const [first, ...rest] = selected;
    await zones.run(first, minutes);
    for (const station of rest) await zones.queue(station, minutes);

    ui.setQuickRunOpen(false);
  };

  const toggle = (station: number) =>
    setSelected((current) =>
      current.includes(station) ? current.filter((s) => s !== station) : [...current, station],
    );

  return (
    <Sheet
      open={ui.quickRunOpen}
      onClose={() => ui.setQuickRunOpen(false)}
      title="Quick run"
      footer={
        <>
          <Button tone="ghost" onClick={() => ui.setQuickRunOpen(false)}>
            Cancel
          </Button>
          <Button
            tone="primary"
            full
            onClick={start}
            disabled={selected.length === 0 || !controllers.online}
          >
            {selected.length === 0
              ? 'Pick a zone'
              : `Water ${selected.length} ${selected.length === 1 ? 'zone' : 'zones'} · ${minutes} min`}
          </Button>
        </>
      }
    >
      <div className="form-stack">
        <Field label="Run each zone for">
          <Stepper value={minutes} onChange={setMinutes} min={1} max={60} suffix=" min" label="run time" />
        </Field>

        <Field label="Zones" hint="They water one after another, in the order you pick them.">
          <div className="picklist">
            {zones.visible.map((zone) => {
              const index = selected.indexOf(zone.stationNumber);
              const on = index >= 0;
              return (
                <button
                  key={zone.stationNumber}
                  className={`picklist__item${on ? ' is-on' : ''}`}
                  onClick={() => toggle(zone.stationNumber)}
                  aria-pressed={on}
                >
                  <span className="picklist__station data">
                    {on ? index + 1 : String(zone.stationNumber).padStart(2, '0')}
                  </span>
                  <span className="picklist__name">{zone.name}</span>
                </button>
              );
            })}
          </div>
        </Field>
      </div>
    </Sheet>
  );
});

/* --------------------------------------------------------------- zone edit */

const ZoneSheet = observer(function ZoneSheet() {
  const { ui, zones, controllers } = useStore();
  const station = ui.openZone;
  const zone = station === null ? undefined : zones.byStation(station);
  const fileInput = useRef<HTMLInputElement>(null);
  const photo = useMediaUrl(zone?.photoUrl);

  const [minutes, setMinutes] = useState(10);

  if (!zone) return <Sheet open={false} onClose={() => ui.closeZoneSheet()} title="Zone">{null}</Sheet>;

  return (
    <Sheet
      open={station !== null}
      onClose={() => ui.closeZoneSheet()}
      title={zone.name}
      footer={
        <Button
          tone="primary"
          full
          size="lg"
          disabled={!controllers.online || !zone.enabled}
          onClick={async () => {
            await zones.run(zone.stationNumber, minutes);
            ui.closeZoneSheet();
          }}
        >
          Water for {minutes} min
        </Button>
      }
    >
      <div className="form-stack">
        <div className="zone-photo">
          {photo ? (
            <img src={photo} alt="" className="zone-photo__img" />
          ) : (
            <div className="zone-photo__empty">
              <CameraIcon size={26} />
              <span>No photo yet</span>
            </div>
          )}
          <button className="zone-photo__btn" onClick={() => fileInput.current?.click()}>
            {zone.photoUrl ? 'Replace photo' : 'Add photo'}
          </button>
          <input
            ref={fileInput}
            type="file"
            accept="image/jpeg,image/png,image/webp"
            className="visually-hidden"
            onChange={(event) => {
              const file = event.target.files?.[0];
              if (file) void zones.uploadPhoto(zone.stationNumber, file);
              event.target.value = '';
            }}
          />
        </div>

        <Field label="Run time">
          <Stepper value={minutes} onChange={setMinutes} min={1} max={60} suffix=" min" label="run time" />
        </Field>

        <Field label="Name">
          <DraftTextInput
            value={zone.name}
            normalize={(raw) => raw.trim() || zone.name}
            onCommit={(name) => zones.update(zone.stationNumber, { name })}
          />
        </Field>

        <div className="grid-2">
          <Field label="Planting">
            <Select<PlantType>
              value={zone.plantType}
              onChange={(value) => zones.update(zone.stationNumber, { plantType: value })}
              options={Object.entries(PLANT_LABELS).map(([value, label]) => ({
                value: value as PlantType,
                label,
              }))}
            />
          </Field>

          <Field label="Sprinkler">
            <Select<SprinklerType>
              value={zone.sprinklerType}
              onChange={(value) => zones.update(zone.stationNumber, { sprinklerType: value })}
              options={Object.entries(HEAD_LABELS).map(([value, label]) => ({
                value: value as SprinklerType,
                label,
              }))}
            />
          </Field>

          <Field label="Soil">
            <Select<SoilType>
              value={zone.soilType}
              onChange={(value) => zones.update(zone.stationNumber, { soilType: value })}
              options={[
                { value: 'Clay', label: 'Clay' },
                { value: 'ClayLoam', label: 'Clay loam' },
                { value: 'Loam', label: 'Loam' },
                { value: 'SandyLoam', label: 'Sandy loam' },
                { value: 'Sand', label: 'Sand' },
                { value: 'Silt', label: 'Silt' },
              ]}
            />
          </Field>

          <Field label="Sun">
            <Select<SunExposure>
              value={zone.sunExposure}
              onChange={(value) => zones.update(zone.stationNumber, { sunExposure: value })}
              options={[
                { value: 'FullSun', label: 'Full sun' },
                { value: 'PartialShade', label: 'Partial shade' },
                { value: 'FullShade', label: 'Full shade' },
              ]}
            />
          </Field>
        </div>

        <Field
          label="Nozzle flow rate"
          hint="Gallons per minute for this zone's heads. Used to estimate water use — the controller cannot measure it."
        >
          <DraftTextInput
            value={String(zone.nozzleFlowGpm)}
            inputMode="decimal"
            // Rejecting unparseable text here rather than while typing is what lets
            // "1." exist on the way to "1.5", and lets the field be emptied and
            // retyped instead of snapping back on the first keystroke.
            normalize={(raw) => {
              const parsed = Number.parseFloat(raw);
              return Number.isFinite(parsed) && parsed > 0 ? String(parsed) : String(zone.nozzleFlowGpm);
            }}
            onCommit={(text) =>
              zones.update(zone.stationNumber, { nozzleFlowGpm: Number.parseFloat(text) })
            }
          />
        </Field>

        <Toggle
          label="Zone enabled"
          hint="A disabled zone is skipped by every program and cannot be run by hand."
          checked={zone.enabled}
          onChange={(value) => zones.update(zone.stationNumber, { enabled: value })}
        />
      </div>
    </Sheet>
  );
});

/* ----------------------------------------------------------- program edit */

const ProgramEditorSheet = observer(function ProgramEditorSheet() {
  const { ui, schedules, zones, controllers } = useStore();
  const index = ui.openProgram;
  const source = index === null ? undefined : schedules.byNumber(index);

  const [frequency, setFrequency] = useState<FrequencyType>('CustomDays');
  const [days, setDays] = useState<boolean[]>([false, false, false, false, false, false, false]);
  const [cyclicDays, setCyclicDays] = useState(2);
  const [adjust, setAdjust] = useState(100);
  const [startTimes, setStartTimes] = useState<number[]>([]);
  const [runTimes, setRunTimes] = useState<Record<string, number>>({});

  // Reload the form whenever a different program is opened.
  useEffect(() => {
    if (!source) return;
    setFrequency(source.frequency);
    setDays([...source.customDays]);
    setCyclicDays(source.cyclicDays || 2);
    setAdjust(source.seasonalAdjustPercent);
    setStartTimes(source.startTimes.filter((time) => time >= 0 && time < 1440));
    setRunTimes({ ...source.stationRunTimes });
  }, [source?.programNumber]);

  if (!source) {
    return (
      <Sheet open={false} onClose={() => ui.closeProgramEditor()} title="Program">
        {null}
      </Sheet>
    );
  }

  const maxStarts = controllers.capabilities?.maxStartTimes ?? 4;

  const save = async () => {
    const padded = [
      ...startTimes,
      ...Array.from({ length: maxStarts }, () => UNSET_START_TIME),
    ].slice(0, maxStarts);

    await schedules.save({
      ...source,
      frequency,
      customDays: days,
      cyclicDays,
      seasonalAdjustPercent: adjust,
      startTimes: padded,
      stationRunTimes: runTimes,
    });
    ui.closeProgramEditor();
  };

  const totalMinutes = Object.values(runTimes).reduce((sum, value) => sum + value, 0);

  return (
    <Sheet
      open={index !== null}
      onClose={() => ui.closeProgramEditor()}
      title={`Program ${source.label}`}
      footer={
        <>
          <Button tone="ghost" onClick={() => ui.closeProgramEditor()}>
            Cancel
          </Button>
          <Button tone="primary" full onClick={save} disabled={schedules.saving}>
            {schedules.saving ? 'Writing to controller…' : `Save · ${totalMinutes} min`}
          </Button>
        </>
      }
    >
      <div className="form-stack">
        <Field label="Water on">
          <Select<FrequencyType> value={frequency} onChange={setFrequency} options={FREQUENCY_OPTIONS} />
        </Field>

        {frequency === 'CustomDays' && (
          <Field label="Days">
            <DayPicker days={days} onChange={setDays} />
          </Field>
        )}

        {frequency === 'Cyclic' && (
          <Field label="Interval" hint="The controller counts down and waters when it reaches zero.">
            <Stepper value={cyclicDays} onChange={setCyclicDays} min={1} max={31} suffix=" days" />
          </Field>
        )}

        <Field label="Start times" hint={`Up to ${maxStarts} on this model. The whole program runs from each.`}>
          <div className="starts">
            {startTimes.map((time, position) => (
              <div key={position} className="starts__item">
                <input
                  className="input starts__input"
                  type="time"
                  value={minutesToTimeValue(time)}
                  onChange={(event) => {
                    const next = [...startTimes];
                    next[position] = timeValueToMinutes(event.target.value);
                    setStartTimes(next);
                  }}
                />
                <button
                  className="starts__remove"
                  onClick={() => setStartTimes(startTimes.filter((_, i) => i !== position))}
                  aria-label={`Remove start time ${formatStartTime(time)}`}
                >
                  <CloseIcon size={15} />
                </button>
              </div>
            ))}

            {startTimes.length < maxStarts && (
              <button className="starts__add" onClick={() => setStartTimes([...startTimes, 5 * 60 + 15])}>
                <PlusIcon size={15} />
                Add start time
              </button>
            )}
          </div>
        </Field>

        <Field label="Seasonal adjust" hint="Scales every run time in this program.">
          <Stepper value={adjust} onChange={setAdjust} min={0} max={200} step={5} suffix="%" />
        </Field>

        <Field label="Zone run times" hint="Set a zone to zero minutes to leave it out of this program.">
          <div className="runtimes">
            {zones.ordered.map((zone) => {
              const value = runTimes[String(zone.stationNumber)] ?? 0;
              return (
                <div key={zone.stationNumber} className={`runtimes__row${value === 0 ? ' is-off' : ''}`}>
                  <span className="runtimes__station data">
                    {String(zone.stationNumber).padStart(2, '0')}
                  </span>
                  <span className="runtimes__name">{zone.name}</span>
                  <Stepper
                    value={value}
                    onChange={(next) =>
                      setRunTimes({ ...runTimes, [String(zone.stationNumber)]: next })
                    }
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
      </div>
    </Sheet>
  );
});

function minutesToTimeValue(minutes: number): string {
  const safe = Math.max(0, Math.min(1439, minutes));
  return `${String(Math.floor(safe / 60)).padStart(2, '0')}:${String(safe % 60).padStart(2, '0')}`;
}

function timeValueToMinutes(value: string): number {
  const [hours, minutes] = value.split(':').map(Number);
  return (hours || 0) * 60 + (minutes || 0);
}

/* ------------------------------------------------------- add a controller */

const AddControllerSheet = observer(function AddControllerSheet() {
  const { ui } = useStore();

  return (
    <Sheet
      open={ui.addControllerOpen}
      onClose={() => ui.setAddControllerOpen(false)}
      title="Add a controller"
    >
      <AddControllerSheetContent onDone={() => ui.setAddControllerOpen(false)} />
    </Sheet>
  );
});

const EditControllerSheet = observer(function EditControllerSheet() {
  const { ui, controllers } = useStore();

  return (
    <Sheet
      open={ui.editControllerOpen && controllers.selected !== null}
      onClose={() => ui.setEditControllerOpen(false)}
      title="Edit controller"
    >
      <EditControllerSheetContent onDone={() => ui.setEditControllerOpen(false)} />
    </Sheet>
  );
});
