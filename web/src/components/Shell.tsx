import { AnimatePresence, motion } from 'framer-motion';
import { observer } from 'mobx-react-lite';
import type { ReactNode } from 'react';
import { useEffect, useState } from 'react';
import { useStore } from '../stores/RootStore';
import type { Tab } from '../stores/UiStore';
import { AppNotices } from './AppNotices';
import { CalendarIcon, CheckIcon, ChevronIcon, EventsIcon, PlayIcon, SettingsIcon, ZonesIcon } from './Icons';
import { NowWateringBar } from './NowWatering';
import { Toasts } from './Toasts';

const TABS: { id: Tab; label: string; icon: typeof EventsIcon }[] = [
  { id: 'events', label: 'Events', icon: EventsIcon },
  { id: 'zones', label: 'Zones', icon: ZonesIcon },
  { id: 'schedules', label: 'Schedules', icon: CalendarIcon },
  { id: 'settings', label: 'Settings', icon: SettingsIcon },
];

export const Shell = observer(function Shell({ children }: { children: ReactNode }) {
  const { ui } = useStore();

  return (
    <div className="shell">
      <header className="shell__head">
        <div className="shell__head-inner">
          <ControllerSwitcher />

          <div className="shell__head-actions">
            <ThemeToggle />
          </div>
        </div>
      </header>

      <AppNotices />

      <main className="shell__main">
        <div className="shell__content">
          <AnimatePresence mode="wait">
            <motion.div
              key={ui.tab}
              initial={{ opacity: 0, y: 8 }}
              animate={{ opacity: 1, y: 0 }}
              exit={{ opacity: 0, y: -6 }}
              transition={{ duration: 0.2, ease: [0.22, 1, 0.36, 1] }}
            >
              {children}
            </motion.div>
          </AnimatePresence>
        </div>
      </main>

      {ui.tab !== 'events' && <NowWateringBar />}

      <QuickRunButton />

      <nav className="shell__nav" aria-label="Sections">
        {TABS.map((tab) => {
          const Icon = tab.icon;
          const active = ui.tab === tab.id;
          return (
            <button
              key={tab.id}
              className={`shell__tab${active ? ' is-active' : ''}`}
              onClick={() => ui.setTab(tab.id)}
              aria-current={active ? 'page' : undefined}
            >
              {active && (
                <motion.span
                  layoutId="tab-marker"
                  className="shell__tab-marker"
                  transition={{ type: 'spring', stiffness: 420, damping: 34 }}
                />
              )}
              <span className="shell__tab-inner">
                <Icon size={22} strokeWidth={active ? 1.9 : 1.6} />
                <span className="shell__tab-label">{tab.label}</span>
              </span>
            </button>
          );
        })}
      </nav>

      <Toasts />
    </div>
  );
});

/**
 * Names the current controller, and switches between them when there is more than
 * one. With a single controller it is just a shortcut to its settings, so no menu
 * appears for a choice that does not exist.
 */
const ControllerSwitcher = observer(function ControllerSwitcher() {
  const { ui, controllers } = useStore();
  const [open, setOpen] = useState(false);

  const controller = controllers.selected;
  const several = controllers.controllers.length > 1;

  useEffect(() => {
    if (!open) return;
    const close = () => setOpen(false);
    document.addEventListener('click', close);
    return () => document.removeEventListener('click', close);
  }, [open]);

  return (
    <div className="switcher">
      <button
        className="shell__controller"
        aria-haspopup={several ? 'menu' : undefined}
        aria-expanded={several ? open : undefined}
        title={several ? 'Switch controller' : 'Controller settings'}
        onClick={(event) => {
          if (!several) {
            ui.setTab('settings');
            return;
          }
          event.stopPropagation();
          setOpen(!open);
        }}
      >
        <span className={`shell__dot${controllers.online ? ' is-online' : ''}`} aria-hidden />
        <span className="shell__controller-text">
          <span className="shell__controller-name">{controller?.name ?? 'Reignbird'}</span>
          <span className="shell__controller-meta data">
            {controller ? `${controller.modelSeries} · ${controller.host}` : 'No controller'}
          </span>
        </span>
        {several && <ChevronIcon size={16} className="switcher__caret" />}
      </button>

      <AnimatePresence>
        {open && several && (
          <motion.div
            className="switcher__menu"
            role="menu"
            initial={{ opacity: 0, y: -6, scale: 0.98 }}
            animate={{ opacity: 1, y: 0, scale: 1 }}
            exit={{ opacity: 0, y: -4, scale: 0.99 }}
            transition={{ duration: 0.15 }}
            onClick={(event) => event.stopPropagation()}
          >
            {controllers.controllers.map((option) => (
              <button
                key={option.id}
                role="menuitem"
                className={`switcher__item${option.id === controllers.selectedId ? ' is-current' : ''}`}
                onClick={() => {
                  setOpen(false);
                  void controllers.selectController(option.id);
                }}
              >
                <span className={`shell__dot${option.online ? ' is-online' : ''}`} aria-hidden />
                <span className="switcher__text">
                  <span className="switcher__name">{option.name}</span>
                  <span className="switcher__meta data">
                    {option.modelSeries} · {option.host}
                  </span>
                </span>
                {option.id === controllers.selectedId && <CheckIcon size={15} />}
              </button>
            ))}
          </motion.div>
        )}
      </AnimatePresence>
    </div>
  );
});

const QuickRunButton = observer(function QuickRunButton() {
  const { ui, controllers } = useStore();

  // Nothing to run without a controller, and while watering the stop control in
  // the status bar is the action that matters.
  if (!controllers.selected || controllers.isWatering) return null;

  return (
    <motion.button
      className="quickrun-fab"
      onClick={() => ui.setQuickRunOpen(true)}
      whileTap={{ scale: 0.94 }}
      initial={{ scale: 0, opacity: 0 }}
      animate={{ scale: 1, opacity: 1 }}
      transition={{ type: 'spring', stiffness: 400, damping: 26 }}
      aria-label="Quick run"
    >
      <PlayIcon size={22} />
      <span className="quickrun-fab__label">Quick run</span>
    </motion.button>
  );
});

const ThemeToggle = observer(function ThemeToggle() {
  const { ui } = useStore();
  const next = ui.theme === 'dark' ? 'light' : 'dark';

  return (
    <button
      className="shell__icon-btn"
      onClick={() => ui.setTheme(next)}
      title={`Switch to ${next} theme`}
      aria-label={`Switch to ${next} theme`}
    >
      <svg width="20" height="20" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="1.6" strokeLinecap="round" aria-hidden>
        {ui.theme === 'dark' ? (
          <>
            <circle cx="12" cy="12" r="4" />
            <path d="M12 2.6v2.1M12 19.3v2.1M21.4 12h-2.1M4.7 12H2.6M18.6 5.4l-1.5 1.5M6.9 17.1l-1.5 1.5M18.6 18.6l-1.5-1.5M6.9 6.9 5.4 5.4" />
          </>
        ) : (
          <path d="M20 14.5A8.4 8.4 0 0 1 9.5 4 8.6 8.6 0 1 0 20 14.5Z" />
        )}
      </svg>
    </button>
  );
});
