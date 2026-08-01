import { MotionConfig } from 'framer-motion';
import { observer } from 'mobx-react-lite';
import { useEffect } from 'react';
import { Modals } from './components/Modals';
import { Shell } from './components/Shell';
import { Toasts } from './components/Toasts';
import { EventsScreen } from './screens/EventsScreen';
import { SchedulesScreen } from './screens/SchedulesScreen';
import { SettingsScreen } from './screens/SettingsScreen';
import { SignInScreen } from './screens/SignInScreen';
import { ZonesScreen } from './screens/ZonesScreen';
import { useStore } from './stores/RootStore';

export const App = observer(function App() {
  const store = useStore();
  const { auth } = store;

  useEffect(() => {
    void auth.start();
  }, [auth]);

  // Nothing loads until there is somebody to load it for. Polling before sign-in
  // would just be a stream of 401s, and each one would trip the session-ended
  // handler and fight the screen the user is trying to type into.
  useEffect(() => {
    if (!auth.signedIn) return;

    void store.start();
    return () => store.stop();
  }, [store, auth.signedIn]);

  return (
    // The CSS reduced-motion rule cannot reach Framer Motion, which animates in
    // JavaScript. "user" makes it follow the same system preference, so honouring
    // the setting does not depend on which layer happens to drive an animation.
    <MotionConfig reducedMotion="user">
      {auth.gate === 'checking' ? (
        // Deliberately blank. Deciding between "sign in" and "welcome back" takes
        // one request, and a spinner for that long only ever reads as a flicker.
        <div className="boot" />
      ) : auth.signedIn ? (
        <>
          <Shell>
            {store.ui.tab === 'events' && <EventsScreen />}
            {store.ui.tab === 'zones' && <ZonesScreen />}
            {store.ui.tab === 'schedules' && <SchedulesScreen />}
            {store.ui.tab === 'settings' && <SettingsScreen />}
          </Shell>
          <Modals />
        </>
      ) : (
        <>
          <SignInScreen />
          {/* Toasts live inside Shell once signed in; the sign-in screen has no
              shell, so it carries its own copy for the same messages. */}
          <Toasts />
        </>
      )}
    </MotionConfig>
  );
});
