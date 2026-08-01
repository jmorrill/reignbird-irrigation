import { observer } from 'mobx-react-lite';
import { useEffect, useState } from 'react';
import { api } from '../api/client';
import type { SipExchange } from '../api/types';
import { InstallIcon, PlusIcon, SensorIcon } from '../components/Icons';
import {
  Button,
  Card,
  Field,
  Pill,
  SectionHead,
  Segmented,
  Select,
  Stepper,
  TextInput,
  Toggle,
} from '../components/ui';
import { useStore } from '../stores/RootStore';
import type { Theme } from '../stores/UiStore';

export const SettingsScreen = observer(function SettingsScreen() {
  const { controllers, ui } = useStore();

  return (
    <div className="stack">
      <SectionHead
        eyebrow="Configuration"
        title="Settings"
        action={
          <Button size="sm" icon={<PlusIcon size={16} />} onClick={() => ui.setAddControllerOpen(true)}>
            Add controller
          </Button>
        }
      />

      {controllers.selected && (
        <>
          <ControllerPanel />
          <WateringPanel />
        </>
      )}

      <SkipPanel />
      <AppearancePanel />
      <InstallPanel />

      {controllers.selected && <DiagnosticsPanel />}
    </div>
  );
});

/* -------------------------------------------------------------- controller */

const ControllerPanel = observer(function ControllerPanel() {
  const { controllers } = useStore();
  const controller = controllers.selected!;
  const capabilities = controllers.capabilities;
  const [busy, setBusy] = useState(false);

  return (
    <Card>
      <SectionHead eyebrow="Controller" title={controller.name} />

      <dl className="spec">
        <SpecRow label="Model" value={`${controller.modelSeries} (${controller.modelId})`} />
        <SpecRow label="Firmware" value={controller.firmwareVersion || 'Unknown'} />
        <SpecRow label="Serial" value={controller.serialNumber || 'Unknown'} />
        <SpecRow label="Address" value={controller.host} />
        <SpecRow
          label="Status"
          value={controllers.online ? 'Responding' : controller.lastError ?? 'Not responding'}
        />
        <SpecRow label="Zones" value={String(capabilities?.stations.length ?? 0)} />
        <SpecRow
          label="Controller clock"
          value={
            controllers.state
              ? `${controllers.state.controllerDate} ${controllers.state.controllerTime}`
              : 'Unknown'
          }
        />
      </dl>

      {capabilities && (
        <div className="caps">
          <span className="eyebrow">Supports</span>
          <div className="caps__list">
            <Pill tone={capabilities.supportsSchedulePages ? 'turf' : 'neutral'}>
              {capabilities.supportsSchedulePages
                ? `${capabilities.maxPrograms} controller programs`
                : 'App-side scheduling only'}
            </Pill>
            {capabilities.supportsUniversalTransport && <Pill tone="turf">Universal transport</Pill>}
            {capabilities.supportsZoneSeasonalAdjust && <Pill tone="turf">Per-zone adjust</Pill>}
            {capabilities.supportsIrrigationStatistics && <Pill tone="turf">Statistics</Pill>}
            {capabilities.supportsFlowMonitoring && <Pill tone="turf">Flow monitoring</Pill>}
            {capabilities.supportsStationErrors && <Pill tone="turf">Station faults</Pill>}
          </div>
        </div>
      )}

      <div className="row-actions">
        <Button
          size="sm"
          disabled={busy || !controllers.online}
          onClick={async () => {
            setBusy(true);
            await controllers.syncClock();
            setBusy(false);
          }}
        >
          Sync clock
        </Button>
        <Button
          size="sm"
          tone="ghost"
          disabled={busy}
          onClick={async () => {
            setBusy(true);
            try {
              await api.controllers.refresh(controller.id);
              await controllers.load();
            } finally {
              setBusy(false);
            }
          }}
        >
          Re-detect hardware
        </Button>
        <Button
          size="sm"
          tone="danger"
          onClick={() => {
            if (confirm(`Remove ${controller.name}? Its zone names, photos and history are deleted too.`)) {
              void controllers.removeController(controller.id);
            }
          }}
        >
          Remove
        </Button>
      </div>
    </Card>
  );
});

function SpecRow({ label, value }: { label: string; value: string }) {
  return (
    <div className="spec__row">
      <dt className="spec__label">{label}</dt>
      <dd className="spec__value data">{value}</dd>
    </div>
  );
}

/* ---------------------------------------------------------------- watering */

const WateringPanel = observer(function WateringPanel() {
  const { controllers } = useStore();
  const state = controllers.state;

  const [delay, setDelay] = useState(state?.rainDelayDays ?? 0);
  const [adjust, setAdjust] = useState(state?.seasonalAdjustPercent ?? 100);

  useEffect(() => {
    if (state) {
      setDelay(state.rainDelayDays);
      setAdjust(state.seasonalAdjustPercent);
    }
  }, [state?.rainDelayDays, state?.seasonalAdjustPercent]);

  return (
    <Card>
      <SectionHead eyebrow="Watering" title="Manual overrides" />

      <div className="setting-row">
        <div className="setting-row__text">
          <span className="setting-row__label">Rain delay</span>
          <span className="setting-row__hint">
            Pause all automatic watering. The controller counts the days down itself.
          </span>
        </div>
        <div className="setting-row__control">
          <Stepper value={delay} onChange={setDelay} min={0} max={14} suffix="d" label="rain delay" />
          <Button
            size="sm"
            disabled={!controllers.online || delay === state?.rainDelayDays}
            onClick={() => controllers.setRainDelay(delay)}
          >
            Apply
          </Button>
        </div>
      </div>

      <div className="setting-row">
        <div className="setting-row__text">
          <span className="setting-row__label">Seasonal adjust</span>
          <span className="setting-row__hint">
            Scales every run time. 100% waters exactly as programmed.
          </span>
        </div>
        <div className="setting-row__control">
          <Stepper
            value={adjust}
            onChange={setAdjust}
            min={0}
            max={200}
            step={5}
            suffix="%"
            label="seasonal adjust"
          />
          <Button
            size="sm"
            disabled={!controllers.online || adjust === state?.seasonalAdjustPercent}
            onClick={() => controllers.setSeasonalAdjust(adjust)}
          >
            Apply
          </Button>
        </div>
      </div>

      <div className="setting-row">
        <div className="setting-row__text">
          <span className="setting-row__label">Rain sensor</span>
          <span className="setting-row__hint">Reported by the controller's sensor input.</span>
        </div>
        <div className="setting-row__control">
          <Pill tone={controllers.sensorWet ? 'water' : 'neutral'}>
            <SensorIcon size={13} />
            {controllers.sensorWet ? 'Wet — watering held' : 'Dry'}
          </Pill>
        </div>
      </div>
    </Card>
  );
});

/* -------------------------------------------------------------------- skip */

const SkipPanel = observer(function SkipPanel() {
  const { weather } = useStore();
  const settings = weather.settings;

  if (!settings) return null;

  return (
    <Card>
      <SectionHead
        eyebrow="Weather"
        title="Skip rules"
        action={
          <Button size="sm" tone="ghost" onClick={() => weather.evaluateNow()}>
            Check today
          </Button>
        }
      />
      <p className="muted">
        Evaluated each morning against the forecast for this controller's location. A skip applies a
        one-day rain delay, which the controller clears on its own.
      </p>

      <Toggle
        label="Skip for rain"
        hint={`When ${settings.rainThresholdMm} mm or more is forecast today.`}
        checked={settings.rainSkipEnabled}
        onChange={(value) => weather.saveSettings({ ...settings, rainSkipEnabled: value })}
      />
      <Toggle
        label="Skip for freeze"
        hint={`When the low reaches ${settings.freezeThresholdC}°C or below. Watering in a freeze damages both plants and pipes.`}
        checked={settings.freezeSkipEnabled}
        onChange={(value) => weather.saveSettings({ ...settings, freezeSkipEnabled: value })}
      />
      <Toggle
        label="Skip for wind"
        hint={`When gusts reach ${settings.windThresholdKph} km/h and most of the water would miss.`}
        checked={settings.windSkipEnabled}
        onChange={(value) => weather.saveSettings({ ...settings, windSkipEnabled: value })}
      />
      <Toggle
        label="Skip when saturated"
        hint={`When the last ${settings.saturationLookbackDays} days already delivered ${settings.saturationThresholdMm} mm.`}
        checked={settings.saturationSkipEnabled}
        onChange={(value) => weather.saveSettings({ ...settings, saturationSkipEnabled: value })}
      />
    </Card>
  );
});

/* -------------------------------------------------------------- appearance */

const AppearancePanel = observer(function AppearancePanel() {
  const { ui, weather } = useStore();

  return (
    <Card>
      <SectionHead eyebrow="Display" title="Appearance" />

      <div className="setting-row">
        <div className="setting-row__text">
          <span className="setting-row__label">Theme</span>
          <span className="setting-row__hint">System follows your device setting.</span>
        </div>
        <div className="setting-row__control">
          <Segmented<Theme>
            label="Theme"
            value={ui.theme}
            onChange={(theme) => ui.setTheme(theme)}
            options={[
              { value: 'light', label: 'Light' },
              { value: 'dark', label: 'Dark' },
              { value: 'system', label: 'Auto' },
            ]}
          />
        </div>
      </div>

      <div className="setting-row">
        <div className="setting-row__text">
          <span className="setting-row__label">Units</span>
          <span className="setting-row__hint">Applies to temperature and water use.</span>
        </div>
        <div className="setting-row__control">
          <Segmented
            label="Units"
            value={weather.units.useMetric ? 'metric' : 'us'}
            onChange={(value) => weather.saveUnits({ ...weather.units, useMetric: value === 'metric' })}
            options={[
              { value: 'us', label: '°F · gal' },
              { value: 'metric', label: '°C · L' },
            ]}
          />
        </div>
      </div>
    </Card>
  );
});

/* ----------------------------------------------------------------- install */

/**
 * Offers installation, or explains why the browser will not.
 *
 * The explaining half matters more than the offering half. Only Chromium fires
 * `beforeinstallprompt`, and it fires nothing at all over plain HTTP, so a panel
 * that just hid itself would leave "why is there no install button" indistinguishable
 * from "this browser cannot". Reaching the app at a LAN or tailnet address over HTTP
 * is by far the most likely reason, and it is fixable, so it gets said out loud.
 */
const InstallPanel = observer(function InstallPanel() {
  const { pwa, ui } = useStore();

  const state = describeInstallState(pwa.installed, pwa.canInstall, pwa.blockedByInsecureOrigin);

  return (
    <Card>
      <SectionHead eyebrow="App" title="Install" />

      <div className="setting-row">
        <div className="setting-row__text">
          <span className="setting-row__label">{state.label}</span>
          <span className="setting-row__hint">{state.hint}</span>
        </div>

        {pwa.canInstall && (
          <div className="setting-row__control">
            <Button
              size="sm"
              icon={<InstallIcon size={16} />}
              onClick={async () => {
                const outcome = await pwa.install();
                if (outcome === 'accepted') ui.notify('good', 'Installing Reignbird');
              }}
            >
              Install
            </Button>
          </div>
        )}
      </div>
    </Card>
  );
});

function describeInstallState(installed: boolean, canInstall: boolean, insecure: boolean) {
  if (installed) {
    return {
      label: 'Installed',
      hint: 'Reignbird is running as an app rather than a browser tab.',
    };
  }

  if (canInstall) {
    return {
      label: 'Install Reignbird',
      hint: 'Adds it to your home screen or dock and opens it in its own window.',
    };
  }

  if (insecure) {
    return {
      label: 'Needs a secure connection',
      hint:
        `Browsers only install apps served over HTTPS, or from localhost. This page is ${window.location.origin}, `
        + 'so installing and offline support are switched off here.',
    };
  }

  return {
    label: 'Install from your browser',
    hint: 'This browser has no install button to trigger. On iOS use Share, then Add to Home Screen.',
  };
}

/* ------------------------------------------------------------- diagnostics */

const DiagnosticsPanel = observer(function DiagnosticsPanel() {
  const { controllers, ui } = useStore();
  const [open, setOpen] = useState(false);
  const [exchanges, setExchanges] = useState<SipExchange[]>([]);
  const [command, setCommand] = useState('4C');
  const [result, setResult] = useState<string | null>(null);

  const controllerId = controllers.selectedId;

  useEffect(() => {
    if (!open || controllerId === null) return;

    const load = () => {
      void api.diagnostics.exchanges(controllerId).then(setExchanges).catch(() => setExchanges([]));
    };
    load();
    const timer = window.setInterval(load, 3000);
    return () => window.clearInterval(timer);
  }, [open, controllerId]);

  return (
    <Card>
      <SectionHead
        eyebrow="Developer"
        title="Protocol console"
        action={
          <Button size="sm" tone="ghost" onClick={() => setOpen(!open)}>
            {open ? 'Hide' : 'Show'}
          </Button>
        }
      />
      <p className="muted">
        Raw SIP traffic to and from the controller. The protocol is a compact binary one with no
        published specification, so being able to see the actual bytes is genuinely useful.
      </p>

      {open && (
        <>
          <div className="console__send">
            <TextInput value={command} onChange={(value) => setCommand(value.toUpperCase())} placeholder="4C" />
            <Button
              onClick={async () => {
                if (controllerId === null) return;
                try {
                  const response = await api.diagnostics.sendRaw(controllerId, command);
                  setResult(`${response.name}  ${response.hex}`);
                } catch (error) {
                  const message = error instanceof Error ? error.message : 'Command failed.';
                  setResult(message);
                  ui.notify('bad', 'Command failed', message);
                }
              }}
            >
              Send
            </Button>
          </div>

          {result && <pre className="console__result data">{result}</pre>}

          <div className="console__log">
            {exchanges.length === 0 ? (
              <p className="muted">No traffic captured yet.</p>
            ) : (
              exchanges.slice(0, 24).map((exchange, index) => (
                <div key={index} className="console__row data">
                  <span className="console__time">
                    {new Date(exchange.at).toLocaleTimeString(undefined, {
                      hour12: false,
                      minute: '2-digit',
                      second: '2-digit',
                    })}
                  </span>
                  <span className="console__req">{exchange.request}</span>
                  <span className="console__arrow">→</span>
                  <span className={`console__res${exchange.error ? ' is-error' : ''}`}>
                    {exchange.error ?? exchange.response ?? '—'}
                  </span>
                </div>
              ))
            )}
          </div>
        </>
      )}
    </Card>
  );
});

/* -------------------------------------------------------- add a controller */

export const AddControllerSheetContent = observer(function AddControllerSheetContent({
  onDone,
}: {
  onDone: () => void;
}) {
  const { controllers, ui } = useStore();
  const [host, setHost] = useState('');
  const [password, setPassword] = useState('');
  const [name, setName] = useState('');
  const [coords, setCoords] = useState('');
  const [busy, setBusy] = useState(false);

  const submit = async () => {
    if (!host.trim()) {
      ui.notify('bad', 'Address required', 'Enter the controller’s IP address on your network.');
      return;
    }

    setBusy(true);
    try {
      const [latitude, longitude] = coords
        .split(',')
        .map((part) => Number.parseFloat(part.trim()));

      await controllers.addController({
        host: host.trim(),
        password,
        name: name.trim() || undefined,
        latitude: Number.isFinite(latitude) ? latitude : null,
        longitude: Number.isFinite(longitude) ? longitude : null,
      });
      onDone();
    } catch (error) {
      const message = error instanceof Error ? error.message : 'Could not reach that controller.';
      ui.notify('bad', 'Controller not added', message);
    } finally {
      setBusy(false);
    }
  };

  return (
    <div className="form-stack">
      <Field label="IP address" hint="Find it in your router's device list, or on the controller's own display.">
        <TextInput value={host} onChange={setHost} placeholder="192.168.1.50" />
      </Field>

      <Field label="Device password" hint="The password set on the LNK WiFi module.">
        <TextInput value={password} onChange={setPassword} type="password" placeholder="••••••••" />
      </Field>

      <Field label="Name" hint="Optional. Shown at the top of every screen.">
        <TextInput value={name} onChange={setName} placeholder="Backyard Controller" />
      </Field>

      <Field
        label="Coordinates"
        hint="Optional, as latitude, longitude. Needed for the forecast and weather skips."
      >
        <TextInput value={coords} onChange={setCoords} placeholder="39.7392, -104.9903" />
      </Field>

      <Button tone="primary" full size="lg" onClick={submit} disabled={busy}>
        {busy ? 'Connecting…' : 'Connect'}
      </Button>
    </div>
  );
});

export { Select };
