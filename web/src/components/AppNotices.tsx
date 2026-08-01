import { AnimatePresence, motion } from 'framer-motion';
import { observer } from 'mobx-react-lite';
import type { ReactNode } from 'react';
import { useEffect } from 'react';
import { useStore } from '../stores/RootStore';
import { AlertIcon, CloseIcon, RefreshIcon } from './Icons';

/**
 * Status bars about the app itself, under the header.
 *
 * That placement is a deliberate split from the toasts at the bottom of the
 * screen, which report what the sprinklers did. These two are about whether the
 * app can be trusted right now, and both have to persist — a toast that timed out
 * would take the only route to the new version with it.
 */
export const AppNotices = observer(function AppNotices() {
  const { pwa, connection, controllers, ui } = useStore();

  useEffect(() => {
    if (!pwa.offlineReady) return;
    // Transient and purely reassuring, so it belongs with the other toasts.
    ui.notify('good', 'Ready to work offline', 'The app will open without a network from now on.');
    pwa.dismissOfflineReady();
  }, [pwa.offlineReady, pwa, ui]);

  return (
    <AnimatePresence initial={false}>
      {!connection.reachable && (
        <Bar key="offline" tone="warn" icon={<AlertIcon size={16} />}>
          <span className="notice__text">
            <span className="notice__title">Can't reach the Reignbird server</span>
            <span className="notice__detail">
              This is the last view that loaded, not live state. Nothing can be started or stopped
              until the server answers again.
            </span>
          </span>

          <button className="btn btn--sm btn--quiet" onClick={() => void controllers.load()}>
            Retry
          </button>
        </Bar>
      )}

      {pwa.updateReady && (
        <Bar key="update" tone="water" icon={<RefreshIcon size={16} />}>
          <span className="notice__text">
            <span className="notice__title">A new version is ready</span>
            <span className="notice__detail">Reload to use it. Nothing watering will be interrupted.</span>
          </span>

          <button className="btn btn--sm btn--primary" onClick={() => void pwa.update()}>
            Reload
          </button>

          <button className="notice__close" onClick={() => pwa.dismissUpdate()} aria-label="Not now">
            <CloseIcon size={15} />
          </button>
        </Bar>
      )}
    </AnimatePresence>
  );
});

function Bar({
  tone,
  icon,
  children,
}: {
  tone: 'water' | 'warn';
  icon: ReactNode;
  children: ReactNode;
}) {
  return (
    <motion.div
      className={`notice notice--${tone}`}
      role="status"
      initial={{ height: 0, opacity: 0 }}
      animate={{ height: 'auto', opacity: 1 }}
      exit={{ height: 0, opacity: 0 }}
      transition={{ type: 'spring', stiffness: 380, damping: 34 }}
    >
      <div className="notice__inner">
        <span className="notice__icon">{icon}</span>
        {children}
      </div>
    </motion.div>
  );
}
