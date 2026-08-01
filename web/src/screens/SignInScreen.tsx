import { motion } from 'framer-motion';
import { observer } from 'mobx-react-lite';
import { useState } from 'react';
import { DropIcon } from '../components/Icons';
import { Button, Field, TextInput } from '../components/ui';
import { useStore } from '../stores/RootStore';

/**
 * Sign in, or create the first account.
 *
 * Both live in one component because they are the same form with different stakes,
 * and the difference is worth showing rather than hiding: on first run the fields
 * are choosing a password rather than proving one, and whoever gets here first
 * claims the system.
 */
export const SignInScreen = observer(function SignInScreen() {
  const { auth } = useStore();
  const firstRun = auth.gate === 'setup';

  const [username, setUsername] = useState('');
  const [password, setPassword] = useState('');
  const [confirm, setConfirm] = useState('');
  const [mismatch, setMismatch] = useState<string | null>(null);

  const canSubmit =
    username.trim().length > 0 && password.length > 0 && !auth.busy && (!firstRun || confirm.length > 0);

  async function submit(event: React.FormEvent) {
    event.preventDefault();
    setMismatch(null);

    if (firstRun && password !== confirm) {
      setMismatch('Those passwords do not match.');
      return;
    }

    if (firstRun) await auth.createFirstAccount(username.trim(), password);
    else await auth.signIn(username.trim(), password);
  }

  return (
    <div className="signin">
      <motion.div
        className="signin__card"
        initial={{ opacity: 0, y: 12 }}
        animate={{ opacity: 1, y: 0 }}
        transition={{ type: 'spring', stiffness: 320, damping: 30 }}
      >
        <div className="signin__mark">
          <DropIcon size={30} strokeWidth={1.5} />
        </div>

        <h1 className="signin__title">{firstRun ? 'Set up Reignbird' : 'Reignbird'}</h1>
        <p className="signin__sub">
          {firstRun
            ? 'Choose the first account. Anyone who reaches this screen can claim it, so do this before opening the port to your network.'
            : 'Sign in to control your irrigation.'}
        </p>

        <form className="signin__form" onSubmit={submit}>
          <Field label="Username">
            <TextInput
              value={username}
              onChange={setUsername}
              placeholder={firstRun ? 'Pick a username' : 'Your username'}
              autoComplete="username"
            />
          </Field>

          <Field label="Password" hint={firstRun ? 'At least 8 characters.' : undefined}>
            <TextInput
              value={password}
              onChange={setPassword}
              type="password"
              autoComplete={firstRun ? 'new-password' : 'current-password'}
            />
          </Field>

          {firstRun && (
            <Field label="Confirm password">
              <TextInput value={confirm} onChange={setConfirm} type="password" autoComplete="new-password" />
            </Field>
          )}

          {(auth.error || mismatch) && (
            <p className="signin__error" role="alert">
              {mismatch ?? auth.error}
            </p>
          )}

          <Button type="submit" tone="primary" size="lg" full disabled={!canSubmit}>
            {auth.busy ? 'Working…' : firstRun ? 'Create account' : 'Sign in'}
          </Button>
        </form>
      </motion.div>
    </div>
  );
});
