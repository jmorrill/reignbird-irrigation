/*
 * One consistent icon set, drawn rather than imported.
 *
 * All are 24×24 on a 1.6 stroke with round caps, so they sit together without
 * any one shouting. The weather glyphs are deliberately simple: at 28px in the
 * forecast strip, detail turns to mud.
 */

interface IconProps {
  size?: number;
  className?: string;
  strokeWidth?: number;
}

const base = (size: number) => ({
  width: size,
  height: size,
  viewBox: '0 0 24 24',
  fill: 'none',
  stroke: 'currentColor',
  strokeLinecap: 'round' as const,
  strokeLinejoin: 'round' as const,
  'aria-hidden': true,
});

export const DropIcon = ({ size = 24, className, strokeWidth = 1.6 }: IconProps) => (
  <svg {...base(size)} strokeWidth={strokeWidth} className={className}>
    <path d="M12 3.2c3.4 3.9 5.6 6.8 5.6 9.4a5.6 5.6 0 1 1-11.2 0c0-2.6 2.2-5.5 5.6-9.4Z" />
  </svg>
);

export const ZonesIcon = ({ size = 24, className, strokeWidth = 1.6 }: IconProps) => (
  <svg {...base(size)} strokeWidth={strokeWidth} className={className}>
    <rect x="3.5" y="3.5" width="7" height="7" rx="1.6" />
    <rect x="13.5" y="3.5" width="7" height="7" rx="1.6" />
    <rect x="3.5" y="13.5" width="7" height="7" rx="1.6" />
    <rect x="13.5" y="13.5" width="7" height="7" rx="1.6" />
  </svg>
);

export const EventsIcon = ({ size = 24, className, strokeWidth = 1.6 }: IconProps) => (
  <svg {...base(size)} strokeWidth={strokeWidth} className={className}>
    <path d="M4 6h16M4 12h16M4 18h10" />
  </svg>
);

export const CalendarIcon = ({ size = 24, className, strokeWidth = 1.6 }: IconProps) => (
  <svg {...base(size)} strokeWidth={strokeWidth} className={className}>
    <rect x="3.5" y="5" width="17" height="15.5" rx="2.2" />
    <path d="M3.5 9.5h17M8 3v4M16 3v4" />
  </svg>
);

export const SettingsIcon = ({ size = 24, className, strokeWidth = 1.6 }: IconProps) => (
  <svg {...base(size)} strokeWidth={strokeWidth} className={className}>
    <circle cx="12" cy="12" r="3.1" />
    <path d="M12 2.8v2.4M12 18.8v2.4M21.2 12h-2.4M5.2 12H2.8M18.5 5.5l-1.7 1.7M7.2 16.8l-1.7 1.7M18.5 18.5l-1.7-1.7M7.2 7.2 5.5 5.5" />
  </svg>
);

export const PlayIcon = ({ size = 24, className, strokeWidth = 1.6 }: IconProps) => (
  <svg {...base(size)} strokeWidth={strokeWidth} className={className}>
    <path d="M8 5.6 18.4 12 8 18.4V5.6Z" fill="currentColor" stroke="none" />
  </svg>
);

export const StopIcon = ({ size = 24, className, strokeWidth = 1.6 }: IconProps) => (
  <svg {...base(size)} strokeWidth={strokeWidth} className={className}>
    <rect x="6.5" y="6.5" width="11" height="11" rx="2" fill="currentColor" stroke="none" />
  </svg>
);

export const SkipIcon = ({ size = 24, className, strokeWidth = 1.6 }: IconProps) => (
  <svg {...base(size)} strokeWidth={strokeWidth} className={className}>
    <path d="M6 5.6 14.5 12 6 18.4V5.6Z" fill="currentColor" stroke="none" />
    <path d="M18 5.6v12.8" />
  </svg>
);

export const PlusIcon = ({ size = 24, className, strokeWidth = 1.7 }: IconProps) => (
  <svg {...base(size)} strokeWidth={strokeWidth} className={className}>
    <path d="M12 5.5v13M5.5 12h13" />
  </svg>
);

export const CloseIcon = ({ size = 24, className, strokeWidth = 1.7 }: IconProps) => (
  <svg {...base(size)} strokeWidth={strokeWidth} className={className}>
    <path d="M6.5 6.5l11 11M17.5 6.5l-11 11" />
  </svg>
);

export const ChevronIcon = ({ size = 24, className, strokeWidth = 1.7 }: IconProps) => (
  <svg {...base(size)} strokeWidth={strokeWidth} className={className}>
    <path d="M9 5.5 15.5 12 9 18.5" />
  </svg>
);

export const CheckIcon = ({ size = 24, className, strokeWidth = 2 }: IconProps) => (
  <svg {...base(size)} strokeWidth={strokeWidth} className={className}>
    <path d="M5 12.5 10 17.5 19 7" />
  </svg>
);

export const PauseIcon = ({ size = 24, className, strokeWidth = 1.6 }: IconProps) => (
  <svg {...base(size)} strokeWidth={strokeWidth} className={className}>
    <path d="M9.5 6v12M14.5 6v12" strokeWidth="2.4" />
  </svg>
);

export const RefreshIcon = ({ size = 24, className, strokeWidth = 1.6 }: IconProps) => (
  <svg {...base(size)} strokeWidth={strokeWidth} className={className}>
    <path d="M20 12a8 8 0 1 1-2.6-5.9" />
    <path d="M20 4v4.6h-4.6" />
  </svg>
);

export const InstallIcon = ({ size = 24, className, strokeWidth = 1.6 }: IconProps) => (
  <svg {...base(size)} strokeWidth={strokeWidth} className={className}>
    <path d="M12 3.5v10.5" />
    <path d="M8.2 10.4 12 14.2l3.8-3.8" />
    <path d="M4.5 16.5v2.2a1.8 1.8 0 0 0 1.8 1.8h11.4a1.8 1.8 0 0 0 1.8-1.8v-2.2" />
  </svg>
);

export const SensorIcon = ({ size = 24, className, strokeWidth = 1.6 }: IconProps) => (
  <svg {...base(size)} strokeWidth={strokeWidth} className={className}>
    <path d="M12 3v3M12 18v3M3 12h3M18 12h3" />
    <circle cx="12" cy="12" r="4.2" />
  </svg>
);

export const CameraIcon = ({ size = 24, className, strokeWidth = 1.6 }: IconProps) => (
  <svg {...base(size)} strokeWidth={strokeWidth} className={className}>
    <path d="M3.5 8.5A1.8 1.8 0 0 1 5.3 6.7h2.2L9 4.5h6l1.5 2.2h2.2a1.8 1.8 0 0 1 1.8 1.8v9a1.8 1.8 0 0 1-1.8 1.8H5.3a1.8 1.8 0 0 1-1.8-1.8Z" />
    <circle cx="12" cy="12.8" r="3.3" />
  </svg>
);

export const TerminalIcon = ({ size = 24, className, strokeWidth = 1.6 }: IconProps) => (
  <svg {...base(size)} strokeWidth={strokeWidth} className={className}>
    <rect x="3" y="4.5" width="18" height="15" rx="2.2" />
    <path d="M7.5 10 10 12.5 7.5 15M13 15.5h4" />
  </svg>
);

export const AlertIcon = ({ size = 24, className, strokeWidth = 1.6 }: IconProps) => (
  <svg {...base(size)} strokeWidth={strokeWidth} className={className}>
    <path d="M12 4.5 21 19.5H3L12 4.5Z" />
    <path d="M12 10v4M12 16.8v.2" strokeWidth="1.9" />
  </svg>
);

// --------------------------------------------------------------- weather

export const SunIcon = ({ size = 24, className, strokeWidth = 1.6 }: IconProps) => (
  <svg {...base(size)} strokeWidth={strokeWidth} className={className}>
    <circle cx="12" cy="12" r="4" />
    <path d="M12 2.6v2.1M12 19.3v2.1M21.4 12h-2.1M4.7 12H2.6M18.6 5.4l-1.5 1.5M6.9 17.1l-1.5 1.5M18.6 18.6l-1.5-1.5M6.9 6.9 5.4 5.4" />
  </svg>
);

export const CloudIcon = ({ size = 24, className, strokeWidth = 1.6 }: IconProps) => (
  <svg {...base(size)} strokeWidth={strokeWidth} className={className}>
    <path d="M7 18.5a4 4 0 0 1-.4-8A5.5 5.5 0 0 1 17.3 9.6a3.9 3.9 0 0 1 .3 8.9Z" />
  </svg>
);

export const PartlyCloudyIcon = ({ size = 24, className, strokeWidth = 1.6 }: IconProps) => (
  <svg {...base(size)} strokeWidth={strokeWidth} className={className}>
    <circle cx="8.6" cy="8.2" r="3" />
    <path d="M8.6 2.9v1.3M3.3 8.2H2M13.9 8.2h1.3M12.4 4.4l-.9.9M5.7 11.1l-.9.9M4.8 4.4l.9.9" />
    <path d="M11 19.8a3.4 3.4 0 0 1-.3-6.8 4.7 4.7 0 0 1 9-.8 3.3 3.3 0 0 1 .3 7.6Z" />
  </svg>
);

export const RainIcon = ({ size = 24, className, strokeWidth = 1.6 }: IconProps) => (
  <svg {...base(size)} strokeWidth={strokeWidth} className={className}>
    <path d="M7 15.5a3.7 3.7 0 0 1-.4-7.4A5.2 5.2 0 0 1 17 7.2a3.6 3.6 0 0 1 .3 8.3Z" />
    <path d="M9 18.4l-.9 2.4M13 18.4l-.9 2.4M17 18.4l-.9 2.4" />
  </svg>
);

export const SnowIcon = ({ size = 24, className, strokeWidth = 1.6 }: IconProps) => (
  <svg {...base(size)} strokeWidth={strokeWidth} className={className}>
    <path d="M7 14.5a3.7 3.7 0 0 1-.4-7.4A5.2 5.2 0 0 1 17 6.2a3.6 3.6 0 0 1 .3 8.3Z" />
    <path d="M8.6 18.2v.2M12 20v.2M15.4 18.2v.2" strokeWidth="2.2" />
  </svg>
);

export const StormIcon = ({ size = 24, className, strokeWidth = 1.6 }: IconProps) => (
  <svg {...base(size)} strokeWidth={strokeWidth} className={className}>
    <path d="M7 14.5a3.7 3.7 0 0 1-.4-7.4A5.2 5.2 0 0 1 17 6.2a3.6 3.6 0 0 1 .3 8.3Z" />
    <path d="M13 16.5 10 20h3l-1 3" />
  </svg>
);

export const FogIcon = ({ size = 24, className, strokeWidth = 1.6 }: IconProps) => (
  <svg {...base(size)} strokeWidth={strokeWidth} className={className}>
    <path d="M4 8.5h16M6 12.5h13M4 16.5h11" />
  </svg>
);

export const WindIcon = ({ size = 24, className, strokeWidth = 1.6 }: IconProps) => (
  <svg {...base(size)} strokeWidth={strokeWidth} className={className}>
    <path d="M3 8.5h11a2.8 2.8 0 1 0-2.8-2.8M3 15.5h14a2.8 2.8 0 1 1-2.8 2.8M3 12h7" />
  </svg>
);

/** Maps our normalised condition names to a glyph. */
export function WeatherIcon({
  condition,
  size = 26,
  className,
}: {
  condition: string;
  size?: number;
  className?: string;
}) {
  switch (condition) {
    case 'clear':
      return <SunIcon size={size} className={className} />;
    case 'partly-cloudy':
      return <PartlyCloudyIcon size={size} className={className} />;
    case 'fog':
      return <FogIcon size={size} className={className} />;
    case 'drizzle':
    case 'rain':
    case 'showers':
      return <RainIcon size={size} className={className} />;
    case 'snow':
    case 'snow-showers':
      return <SnowIcon size={size} className={className} />;
    case 'thunderstorm':
      return <StormIcon size={size} className={className} />;
    default:
      return <CloudIcon size={size} className={className} />;
  }
}
