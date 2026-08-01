import { AnimatePresence, motion } from 'framer-motion';
import { observer } from 'mobx-react-lite';
import { useStore } from '../stores/RootStore';
import { AlertIcon, CheckIcon, CloseIcon, DropIcon } from './Icons';

export const Toasts = observer(function Toasts() {
  const { ui } = useStore();

  return (
    <div className="toasts" role="status" aria-live="polite">
      <AnimatePresence initial={false}>
        {ui.toasts.map((toast) => (
          <motion.div
            key={toast.id}
            className={`toast toast--${toast.tone}`}
            layout
            initial={{ opacity: 0, y: 16, scale: 0.97 }}
            animate={{ opacity: 1, y: 0, scale: 1 }}
            exit={{ opacity: 0, scale: 0.97, transition: { duration: 0.15 } }}
            transition={{ type: 'spring', stiffness: 400, damping: 32 }}
          >
            <span className="toast__icon">
              {toast.tone === 'good' && <CheckIcon size={16} />}
              {toast.tone === 'bad' && <AlertIcon size={16} />}
              {toast.tone === 'warn' && <AlertIcon size={16} />}
              {toast.tone === 'info' && <DropIcon size={16} />}
            </span>
            <span className="toast__text">
              <span className="toast__title">{toast.title}</span>
              {toast.detail && <span className="toast__detail">{toast.detail}</span>}
            </span>
            <button className="toast__close" onClick={() => ui.dismiss(toast.id)} aria-label="Dismiss">
              <CloseIcon size={15} />
            </button>
          </motion.div>
        ))}
      </AnimatePresence>
    </div>
  );
});
