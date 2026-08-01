/** Mirrors the DTOs in RainBird.Server/Api/Contracts.cs. */

export type SensorState = 'Dry' | 'Wet';

export type PlantType =
  | 'CoolSeasonGrass'
  | 'WarmSeasonGrass'
  | 'Shrubs'
  | 'Trees'
  | 'Flowers'
  | 'GroundCover'
  | 'Garden'
  | 'Xeriscape';

export type SoilType = 'Clay' | 'Loam' | 'Sand' | 'Silt' | 'ClayLoam' | 'SandyLoam';
export type SunExposure = 'FullSun' | 'PartialShade' | 'FullShade';
export type SlopeGrade = 'Flat' | 'Slight' | 'Moderate' | 'Steep';
export type SprinklerType = 'FixedSpray' | 'Rotor' | 'RotaryNozzle' | 'Drip' | 'Bubbler' | 'Emitter';
/** 'Unknown' means the run was observed but this app did not start it. */
export type RunTrigger = 'Manual' | 'Program' | 'Test' | 'Unknown';
export type FrequencyType = 'CustomDays' | 'Cyclic' | 'OddDays' | 'EvenDays';
export type SkipReason = 'Rain' | 'Freeze' | 'Wind' | 'Saturation' | 'Manual';

export interface ControllerState {
  controllerTime: string;
  controllerDate: string;
  rainDelayDays: number;
  sensorState: SensorState;
  /** Automatic watering is enabled. Not the same as "a zone is open right now". */
  controllerEnabled: boolean;
  isWatering: boolean;
  activeStation: number;
  remainingRuntimeSeconds: number;
  seasonalAdjustPercent: number;
}

export interface Controller {
  id: number;
  name: string;
  host: string;
  modelId: string;
  modelSeries: string;
  serialNumber: string;
  firmwareVersion: string;
  online: boolean;
  lastError: string | null;
  lastSeenUtc: string | null;
  latitude: number | null;
  longitude: number | null;
  timeZoneId: string;
  state: ControllerState | null;
}

export interface Capabilities {
  modelId: string;
  series: string;
  isProgramBased: boolean;
  maxPrograms: number;
  maxStartTimes: number;
  stations: number[];
  /** The controller exposes its schedule through the legacy SIP page protocol. */
  supportsSchedulePages: boolean;
  supportsCombinedState: boolean;
  supportsControllerToggle: boolean;
  supportsUniversalTransport: boolean;
  /** The controller will not let us read or write its schedule, so this app owns it. */
  requiresSoftwareScheduling: boolean;
  supportsFlowMonitoring: boolean;
  supportsIrrigationStatistics: boolean;
  supportsZoneSeasonalAdjust: boolean;
  supportsStationErrors: boolean;
}

export interface Zone {
  id: number;
  stationNumber: number;
  name: string;
  photoUrl: string | null;
  plantType: PlantType;
  soilType: SoilType;
  sunExposure: SunExposure;
  slope: SlopeGrade;
  sprinklerType: SprinklerType;
  nozzleFlowGpm: number;
  enabled: boolean;
  sortOrder: number;
  lastRunUtc: string | null;
  lastRunSeconds: number | null;
  isWatering: boolean;
  remainingSeconds: number;
}

export interface Program {
  programNumber: number;
  label: string;
  frequency: FrequencyType;
  customDays: boolean[];
  cyclicDays: number;
  seasonalAdjustPercent: number;
  startTimes: number[];
  stationRunTimes: Record<string, number>;
  enabled: boolean;
  totalMinutes: number;
}

export interface Run {
  id: number;
  stationNumber: number;
  zoneName: string;
  startedUtc: string;
  endedUtc: string | null;
  durationSeconds: number;
  trigger: RunTrigger;
  estimatedGallons: number;
}

export interface WeatherDay {
  date: string;
  condition: string;
  conditionCode: number;
  tempHighC: number;
  tempLowC: number;
  precipitationMm: number;
  precipitationProbability: number;
  windKph: number;
  hasScheduledRun: boolean;
  skipReason: SkipReason | null;
}

export interface ZoneUsage {
  stationNumber: number;
  zoneName: string;
  gallons: number;
  minutes: number;
}

export interface Usage {
  month: string;
  gallonsUsed: number;
  totalMinutes: number;
  runCount: number;
  byZone: ZoneUsage[];
}

export interface CalendarDay {
  date: string;
  runCount: number;
  totalMinutes: number;
  gallons: number;
  skipReason: SkipReason | null;
  scheduled: boolean;
}

export interface SkipEvent {
  date: string;
  reason: SkipReason;
  details: string;
}

export interface SkipSettings {
  rainSkipEnabled: boolean;
  freezeSkipEnabled: boolean;
  windSkipEnabled: boolean;
  saturationSkipEnabled: boolean;
  rainThresholdMm: number;
  freezeThresholdC: number;
  windThresholdKph: number;
  saturationThresholdMm: number;
  saturationLookbackDays: number;
}

export interface UnitPreferences {
  useMetric: boolean;
  showVolume: boolean;
}

export interface SipExchange {
  at: string;
  method: string;
  request: string;
  response: string | null;
  error: string | null;
}

export interface SkipDecision {
  shouldSkip: boolean;
  reason: SkipReason | null;
  details: string;
}

/* ------------------------------------------------------------- watering plans */

export type PlanFrequency = 'DaysOfWeek' | 'EveryNDays' | 'OddDays' | 'EvenDays' | 'EveryDay';

export interface PlanZone {
  stationNumber: number;
  minutes: number;
  sortOrder: number;
}

export interface PlanStep {
  stationNumber: number | null;
  zoneName: string | null;
  minutes: number;
  cycle: number;
  isSoak: boolean;
}

export interface Plan {
  id: number;
  name: string;
  description: string;
  enabled: boolean;
  frequency: PlanFrequency;
  daysOfWeek: boolean[];
  intervalDays: number;
  startTimes: number[];
  latestStartMinute: number | null;
  seasonalAdjustPercent: number;
  cycleSoakEnabled: boolean;
  cycles: number;
  soakMinutes: number;
  weatherSkipEnabled: boolean;
  sortOrder: number;
  zones: PlanZone[];
  wateringMinutesPerPass: number;
  elapsedMinutesPerPass: number;
  passesPerDay: number;
  wateringMinutesPerDay: number;
  nextRunUtc: string | null;
  timeline: PlanStep[];
}

export interface PlanPreset {
  key: string;
  name: string;
  summary: string;
  rationale: string;
}

export interface ActivePlan {
  runId: number;
  planId: number;
  planName: string;
  stepIndex: number;
  stepCount: number;
  currentStation: number | null;
  currentZoneName: string | null;
  soaking: boolean;
  stepMinutes: number;
  remainingSteps: number;
}

export interface PlanRun {
  id: number;
  planName: string;
  startedUtc: string;
  endedUtc: string | null;
  status: 'Running' | 'Completed' | 'Cancelled' | 'Skipped' | 'Failed';
  detail: string | null;
  stepCount: number;
  completedSteps: number;
  wateringMinutes: number;
}

/** Whether the controller still has a schedule of its own competing with ours. */
export interface ArmedState {
  canDisarm: boolean;
  controllerScheduleCleared: boolean;
  explanation: string;
  programRunTimeTotals: number[];
}

export interface SavePlan {
  name: string;
  description: string;
  enabled: boolean;
  frequency: PlanFrequency;
  daysOfWeek: boolean[];
  intervalDays: number;
  startTimes: number[];
  latestStartMinute: number | null;
  seasonalAdjustPercent: number;
  cycleSoakEnabled: boolean;
  cycles: number;
  soakMinutes: number;
  weatherSkipEnabled: boolean;
  zones: PlanZone[];
}

/** Someone who can sign in. Every account is equal — there are no roles. */
export interface Account {
  id: number;
  username: string;
  createdUtc: string;
  lastSignInUtc: string | null;
}

export interface Session {
  token: string;
  expiresUtc: string;
  user: Account;
}

/** A place the geocoder matched, with everything needed to use it. */
export interface Place {
  label: string;
  latitude: number;
  longitude: number;
  timeZoneId: string | null;
}
