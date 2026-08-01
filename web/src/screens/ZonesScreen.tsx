import { motion } from 'framer-motion';
import { observer } from 'mobx-react-lite';
import { useState } from 'react';
import type { Zone } from '../api/types';
import { CameraIcon, PlayIcon, StopIcon, ZonesIcon } from '../components/Icons';
import { formatCountdown } from '../components/NowWatering';
import { Button, EmptyState, SectionHead, Skeleton } from '../components/ui';
import { useMediaUrl } from '../components/useMediaUrl';
import { useStore } from '../stores/RootStore';

const PLANT_LABELS: Record<string, string> = {
  CoolSeasonGrass: 'Cool-season grass',
  WarmSeasonGrass: 'Warm-season grass',
  Shrubs: 'Shrubs',
  Trees: 'Trees',
  Flowers: 'Flowers',
  GroundCover: 'Ground cover',
  Garden: 'Garden',
  Xeriscape: 'Xeriscape',
};

const HEAD_LABELS: Record<string, string> = {
  FixedSpray: 'Fixed spray',
  Rotor: 'Rotor',
  RotaryNozzle: 'Rotary nozzle',
  Drip: 'Drip',
  Bubbler: 'Bubbler',
  Emitter: 'Emitter',
};

export const ZonesScreen = observer(function ZonesScreen() {
  const { zones, controllers, ui } = useStore();
  const [showDisabled, setShowDisabled] = useState(false);

  // Not `loading`: that is false before the first request even starts, which is
  // exactly the moment an empty list would be mistaken for "there are no zones".
  if (!zones.loaded) {
    return <Skeleton height={92} count={6} />;
  }

  if (zones.zones.length === 0) {
    return (
      <EmptyState
        icon={<ZonesIcon size={40} strokeWidth={1.2} />}
        title="No zones found"
        detail="The controller did not report any stations. Refresh it from Settings once it is wired up."
      />
    );
  }

  const visible = showDisabled ? zones.ordered : zones.visible;

  return (
    <div className="stack">
      <SectionHead
        eyebrow={`${zones.visible.length} zones`}
        title="Your yard"
        action={
          zones.disabled.length > 0 ? (
            <Button size="sm" tone="ghost" onClick={() => setShowDisabled(!showDisabled)}>
              {showDisabled ? 'Hide disabled' : `Show ${zones.disabled.length} disabled`}
            </Button>
          ) : undefined
        }
      />

      {/*
        The manifold. Zones are physically laterals branching off one supply line
        and they run in sequence, so the list is drawn as that line — with flow
        travelling down it to whichever zone is open.
      */}
      <div className="manifold">
        <div className="manifold__rail" aria-hidden>
          <div className="manifold__rail-line" />
        </div>

        <ul className="manifold__zones">
          {visible.map((zone, index) => (
            <ZoneRow
              key={zone.stationNumber}
              zone={zone}
              index={index}
              watering={controllers.activeStation === zone.stationNumber && controllers.isWatering}
              remaining={controllers.remainingSeconds}
              onOpen={() => ui.openZoneSheet(zone.stationNumber)}
            />
          ))}
        </ul>
      </div>
    </div>
  );
});

const ZoneRow = observer(function ZoneRow({
  zone,
  index,
  watering,
  remaining,
  onOpen,
}: {
  zone: Zone;
  index: number;
  watering: boolean;
  remaining: number;
  onOpen: () => void;
}) {
  const { zones, controllers } = useStore();
  const photo = useMediaUrl(zone.photoUrl);

  return (
    <motion.li
      className={`zrow${watering ? ' is-watering' : ''}${zone.enabled ? '' : ' is-disabled'}`}
      initial={{ opacity: 0, x: -8 }}
      animate={{ opacity: 1, x: 0 }}
      transition={{ delay: Math.min(index * 0.035, 0.28), duration: 0.3, ease: [0.22, 1, 0.36, 1] }}
    >
      <span className="zrow__tap" aria-hidden>
        <span className="zrow__tap-line" />
        <span className="zrow__tap-node" />
      </span>

      <motion.button
        className="zrow__card"
        onClick={onOpen}
        layoutId={`zone-${zone.stationNumber}`}
        whileTap={{ scale: 0.995 }}
      >
        {photo ? (
          <span className="zrow__photo" style={{ backgroundImage: `url(${photo})` }} />
        ) : (
          <span className="zrow__photo zrow__photo--empty">
            <CameraIcon size={18} />
          </span>
        )}

        <span className="zrow__text">
          <span className="zrow__head">
            <span className="zrow__station data">{String(zone.stationNumber).padStart(2, '0')}</span>
            <span className="zrow__name">{zone.name}</span>
            {!zone.enabled && <span className="zrow__off">Disabled</span>}
          </span>
          <span className="zrow__meta data">
            {HEAD_LABELS[zone.sprinklerType] ?? zone.sprinklerType} · {zone.nozzleFlowGpm} gpm
            {zone.lastRunUtc && ` · watered ${relativeDay(zone.lastRunUtc)}`}
          </span>
        </span>

        {watering && (
          <span className="zrow__live data">
            <span className="zrow__live-dot" aria-hidden />
            {formatCountdown(remaining)}
          </span>
        )}
      </motion.button>

      <div className="zrow__actions">
        {watering ? (
          <button
            className="zrow__act zrow__act--stop"
            onClick={() => controllers.stop()}
            aria-label={`Stop ${zone.name}`}
            title="Stop"
          >
            <StopIcon size={15} />
          </button>
        ) : (
          <button
            className="zrow__act"
            onClick={() => zones.run(zone.stationNumber, 10)}
            disabled={!controllers.online || !zone.enabled}
            aria-label={`Water ${zone.name} for 10 minutes`}
            title="Run 10 min"
          >
            <PlayIcon size={15} />
          </button>
        )}
      </div>
    </motion.li>
  );
});

/** "today", "yesterday", or a short date. */
function relativeDay(iso: string): string {
  const date = new Date(iso);
  const days = Math.floor((Date.now() - date.getTime()) / 86_400_000);

  if (days <= 0) return 'today';
  if (days === 1) return 'yesterday';
  if (days < 7) return `${days} days ago`;
  return date.toLocaleDateString(undefined, { month: 'short', day: 'numeric' });
}

export { PLANT_LABELS, HEAD_LABELS, relativeDay };
