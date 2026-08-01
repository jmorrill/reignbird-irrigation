import { MotionConfig } from 'framer-motion';
import { observer } from 'mobx-react-lite';
import { useEffect } from 'react';
import { Modals } from './components/Modals';
import { Shell } from './components/Shell';
import { EventsScreen } from './screens/EventsScreen';
import { SchedulesScreen } from './screens/SchedulesScreen';
import { SettingsScreen } from './screens/SettingsScreen';
import { ZonesScreen } from './screens/ZonesScreen';
import { useStore } from './stores/RootStore';

export const App = observer(function App() {
  const store = useStore();

  useEffect(() => {
    void store.start();
    return () => store.stop();
  }, [store]);

  return (
    // The CSS reduced-motion rule cannot reach Framer Motion, which animates in
    // JavaScript. "user" makes it follow the same system preference, so honouring
    // the setting does not depend on which layer happens to drive an animation.
    <MotionConfig reducedMotion="user">
      <Shell>
        {store.ui.tab === 'events' && <EventsScreen />}
        {store.ui.tab === 'zones' && <ZonesScreen />}
        {store.ui.tab === 'schedules' && <SchedulesScreen />}
        {store.ui.tab === 'settings' && <SettingsScreen />}
      </Shell>
      <Modals />
    </MotionConfig>
  );
});
