import { AnimatePresence, motion } from 'framer-motion';
import type { ReactNode } from 'react';
import { useEffect, useId, useRef, useState } from 'react';
import { CheckIcon, CloseIcon } from './Icons';

/* ------------------------------------------------------------------ button */

type ButtonTone = 'primary' | 'quiet' | 'ghost' | 'danger';

export function Button({
  children,
  onClick,
  tone = 'quiet',
  size = 'md',
  disabled,
  type = 'button',
  full,
  icon,
  title,
}: {
  children?: ReactNode;
  onClick?: () => void;
  tone?: ButtonTone;
  size?: 'sm' | 'md' | 'lg';
  disabled?: boolean;
  type?: 'button' | 'submit';
  full?: boolean;
  icon?: ReactNode;
  title?: string;
}) {
  return (
    <motion.button
      type={type}
      className={`btn btn--${tone} btn--${size}${full ? ' btn--full' : ''}`}
      onClick={onClick}
      disabled={disabled}
      title={title}
      whileTap={disabled ? undefined : { scale: 0.97 }}
      transition={{ type: 'spring', stiffness: 500, damping: 30 }}
    >
      {icon}
      {children}
    </motion.button>
  );
}

/* -------------------------------------------------------------------- card */

export function Card({
  children,
  className = '',
  padded = true,
}: {
  children: ReactNode;
  className?: string;
  padded?: boolean;
}) {
  return <div className={`card${padded ? ' card--padded' : ''} ${className}`.trim()}>{children}</div>;
}

export function SectionHead({
  title,
  eyebrow,
  action,
}: {
  title: string;
  eyebrow?: string;
  action?: ReactNode;
}) {
  return (
    <div className="section-head">
      <div>
        {eyebrow && <div className="eyebrow">{eyebrow}</div>}
        <h2 className="section-head__title">{title}</h2>
      </div>
      {action}
    </div>
  );
}

/* ------------------------------------------------------------------- pills */

export function Pill({
  children,
  tone = 'neutral',
}: {
  children: ReactNode;
  tone?: 'neutral' | 'water' | 'turf' | 'dawn' | 'clay';
}) {
  return <span className={`pill pill--${tone}`}>{children}</span>;
}

/* -------------------------------------------------------------- segmented */

export function Segmented<T extends string>({
  options,
  value,
  onChange,
  label,
}: {
  options: { value: T; label: string }[];
  value: T;
  onChange: (value: T) => void;
  label?: string;
}) {
  const groupId = useId();

  return (
    <div className="segmented" role="tablist" aria-label={label}>
      {options.map((option) => {
        const selected = option.value === value;
        return (
          <button
            key={option.value}
            role="tab"
            aria-selected={selected}
            className={`segmented__item${selected ? ' is-selected' : ''}`}
            onClick={() => onChange(option.value)}
          >
            {selected && (
              <motion.span
                layoutId={`segmented-${groupId}`}
                className="segmented__thumb"
                transition={{ type: 'spring', stiffness: 420, damping: 34 }}
              />
            )}
            <span className="segmented__label">{option.label}</span>
          </button>
        );
      })}
    </div>
  );
}

/* ------------------------------------------------------------------ toggle */

export function Toggle({
  checked,
  onChange,
  label,
  hint,
  disabled,
  ariaLabel,
}: {
  checked: boolean;
  onChange: (checked: boolean) => void;
  label: string;
  hint?: string;
  disabled?: boolean;
  ariaLabel?: string;
}) {
  // A label-less toggle sits inline next to a heading that already names it, so the
  // text block is omitted rather than left as an empty gap.
  const compact = label.trim().length === 0;

  return (
    <label className={`toggle${disabled ? ' is-disabled' : ''}${compact ? ' toggle--compact' : ''}`}>
      {!compact && (
        <span className="toggle__text">
          <span className="toggle__label">{label}</span>
          {hint && <span className="toggle__hint">{hint}</span>}
        </span>
      )}
      <input
        type="checkbox"
        className="visually-hidden"
        checked={checked}
        disabled={disabled}
        aria-label={compact ? ariaLabel ?? 'Enabled' : undefined}
        onChange={(event) => onChange(event.target.checked)}
      />
      <span className={`toggle__track${checked ? ' is-on' : ''}`} aria-hidden>
        <motion.span
          className="toggle__knob"
          layout
          transition={{ type: 'spring', stiffness: 600, damping: 34 }}
        />
      </span>
    </label>
  );
}

/* ----------------------------------------------------------------- stepper */

export function Stepper({
  value,
  onChange,
  min = 1,
  max = 60,
  step = 1,
  suffix,
  label,
}: {
  value: number;
  onChange: (value: number) => void;
  min?: number;
  max?: number;
  step?: number;
  suffix?: string;
  label?: string;
}) {
  const clamp = (next: number) => Math.min(max, Math.max(min, next));

  return (
    <div className="stepper">
      <button
        className="stepper__btn"
        onClick={() => onChange(clamp(value - step))}
        disabled={value <= min}
        aria-label={`Decrease ${label ?? 'value'}`}
      >
        −
      </button>
      <div className="stepper__value data">
        {value}
        {suffix && <span className="stepper__suffix">{suffix}</span>}
      </div>
      <button
        className="stepper__btn"
        onClick={() => onChange(clamp(value + step))}
        disabled={value >= max}
        aria-label={`Increase ${label ?? 'value'}`}
      >
        +
      </button>
    </div>
  );
}

/* ------------------------------------------------------------------- field */

export function Field({
  label,
  hint,
  children,
}: {
  label: string;
  hint?: string;
  children: ReactNode;
}) {
  return (
    <label className="field">
      <span className="field__label">{label}</span>
      {children}
      {hint && <span className="field__hint">{hint}</span>}
    </label>
  );
}

export function TextInput({
  value,
  onChange,
  placeholder,
  type = 'text',
  inputMode,
  autoComplete,
}: {
  value: string;
  onChange: (value: string) => void;
  placeholder?: string;
  type?: string;
  inputMode?: 'text' | 'numeric' | 'decimal';
  /** Set on the sign-in fields so password managers offer to fill and to save. */
  autoComplete?: string;
}) {
  return (
    <input
      className="input"
      type={type}
      inputMode={inputMode}
      autoComplete={autoComplete}
      value={value}
      placeholder={placeholder}
      onChange={(event) => onChange(event.target.value)}
    />
  );
}

/**
 * A text field for a value that lives on the server.
 *
 * Binding an input straight to server state and saving every keystroke looks fine
 * until you type quickly, and then it fights you. Two things go wrong at once. The
 * replies come back out of order, so an older one lands last and snaps the field
 * back to a shorter string. And the server normalises what it stores — it trims
 * names — so the moment you press space the reply arrives without it and the space
 * you just typed disappears. Between them, a zone could not be called "Front Yard".
 *
 * So while the field has focus it belongs to whoever is typing: the draft is local,
 * nothing is sent, and updates arriving from elsewhere are ignored rather than
 * allowed to overwrite a half-typed word. On blur or Enter it normalises once,
 * saves once, and goes back to following the server. Escape abandons the edit.
 */
export function DraftTextInput({
  value,
  onCommit,
  normalize,
  placeholder,
  type = 'text',
  inputMode,
}: {
  value: string;
  /** Called on blur or Enter, and only when the value actually changed. */
  onCommit: (value: string) => void;
  /**
   * Settles the text once editing finishes — trimming, or rejecting something
   * unparseable by returning the current value. Applied to what is displayed as well
   * as to what is saved, so the field never shows something that was not stored.
   */
  normalize?: (raw: string) => string;
  placeholder?: string;
  type?: string;
  inputMode?: 'text' | 'numeric' | 'decimal';
}) {
  const [draft, setDraft] = useState(value);

  // A ref, not state: this must not be a dependency of the effect below, or blurring
  // would re-run it and briefly restore the old value before the save lands.
  const editing = useRef(false);

  useEffect(() => {
    if (!editing.current) setDraft(value);
  }, [value]);

  function commit() {
    editing.current = false;

    const settled = normalize ? normalize(draft) : draft;
    setDraft(settled);

    if (settled !== value) onCommit(settled);
  }

  return (
    <input
      className="input"
      type={type}
      inputMode={inputMode}
      value={draft}
      placeholder={placeholder}
      onFocus={() => (editing.current = true)}
      onChange={(event) => setDraft(event.target.value)}
      onBlur={commit}
      onKeyDown={(event) => {
        if (event.key === 'Enter') event.currentTarget.blur();
        if (event.key === 'Escape') {
          setDraft(value);
          editing.current = false;
          event.currentTarget.blur();
        }
      }}
    />
  );
}

export function Select<T extends string>({
  value,
  onChange,
  options,
}: {
  value: T;
  onChange: (value: T) => void;
  options: { value: T; label: string }[];
}) {
  return (
    <div className="select">
      <select value={value} onChange={(event) => onChange(event.target.value as T)}>
        {options.map((option) => (
          <option key={option.value} value={option.value}>
            {option.label}
          </option>
        ))}
      </select>
    </div>
  );
}

/* ------------------------------------------------------------------- sheet */

/**
 * A modal that rises from the bottom on a phone and centres on a wide screen.
 * Escape closes it and body scroll is locked while it is open.
 */
export function Sheet({
  open,
  onClose,
  title,
  children,
  footer,
}: {
  open: boolean;
  onClose: () => void;
  title: string;
  children: ReactNode;
  footer?: ReactNode;
}) {
  useEffect(() => {
    if (!open) return;

    const onKey = (event: KeyboardEvent) => {
      if (event.key === 'Escape') onClose();
    };
    document.addEventListener('keydown', onKey);

    const previous = document.body.style.overflow;
    document.body.style.overflow = 'hidden';

    return () => {
      document.removeEventListener('keydown', onKey);
      document.body.style.overflow = previous;
    };
  }, [open, onClose]);

  return (
    <AnimatePresence>
      {open && (
        <motion.div
          className="sheet-layer"
          initial={{ opacity: 0 }}
          animate={{ opacity: 1 }}
          exit={{ opacity: 0 }}
          transition={{ duration: 0.18 }}
        >
          <div className="sheet-scrim" onClick={onClose} />
          <motion.div
            className="sheet"
            role="dialog"
            aria-modal="true"
            aria-label={title}
            initial={{ y: 40, opacity: 0, scale: 0.98 }}
            animate={{ y: 0, opacity: 1, scale: 1 }}
            exit={{ y: 24, opacity: 0, scale: 0.99 }}
            transition={{ type: 'spring', stiffness: 340, damping: 32 }}
          >
            <header className="sheet__head">
              <h2 className="sheet__title">{title}</h2>
              <button className="sheet__close" onClick={onClose} aria-label="Close">
                <CloseIcon size={20} />
              </button>
            </header>
            <div className="sheet__body">{children}</div>
            {footer && <footer className="sheet__foot">{footer}</footer>}
          </motion.div>
        </motion.div>
      )}
    </AnimatePresence>
  );
}

/* -------------------------------------------------------------- empty/skel */

export function EmptyState({
  icon,
  title,
  detail,
  action,
}: {
  icon?: ReactNode;
  title: string;
  detail?: string;
  action?: ReactNode;
}) {
  return (
    <div className="empty">
      {icon && <div className="empty__icon">{icon}</div>}
      <p className="empty__title">{title}</p>
      {detail && <p className="empty__detail">{detail}</p>}
      {action && <div className="empty__action">{action}</div>}
    </div>
  );
}

/** A small indeterminate spinner, for when something is in flight but not yet wrong. */
export function Spinner({ size = 16 }: { size?: number }) {
  return <span className="spinner" style={{ width: size, height: size }} aria-hidden />;
}

/**
 * A placeholder shaped like the text that is coming.
 *
 * For the case where the answer is not known yet and any concrete wording would be a
 * guess. Showing "No watering scheduled" and correcting it a moment later is worse
 * than showing nothing: the first version is not a slower answer, it is a wrong one.
 */
export function GhostText({ width = '60%', height = 14 }: { width?: string | number; height?: number }) {
  return <span className="ghost-text" style={{ width, height }} aria-hidden />;
}

export function Skeleton({ height = 64, count = 1 }: { height?: number; count?: number }) {
  return (
    <div className="skeleton-stack">
      {Array.from({ length: count }, (_, index) => (
        <div key={index} className="skeleton" style={{ height }} />
      ))}
    </div>
  );
}

/* ------------------------------------------------------------------ picker */

/** Day-of-week picker, Sunday first to match the controller's bitmask order. */
export function DayPicker({
  days,
  onChange,
}: {
  days: boolean[];
  onChange: (days: boolean[]) => void;
}) {
  const initials = ['S', 'M', 'T', 'W', 'T', 'F', 'S'];
  const names = ['Sunday', 'Monday', 'Tuesday', 'Wednesday', 'Thursday', 'Friday', 'Saturday'];

  return (
    <div className="daypicker">
      {initials.map((initial, index) => {
        const on = days[index] ?? false;
        return (
          <button
            key={index}
            className={`daypicker__day${on ? ' is-on' : ''}`}
            aria-pressed={on}
            aria-label={names[index]}
            onClick={() => {
              const next = [...days];
              next[index] = !on;
              onChange(next);
            }}
          >
            {on ? <CheckIcon size={14} /> : initial}
          </button>
        );
      })}
    </div>
  );
}
