import { observer } from 'mobx-react-lite';
import { useState } from 'react';
import { api } from '../api/client';
import type { Place } from '../api/types';
import { Button, Field, Spinner, TextInput } from './ui';

/**
 * Picks a location by postcode or place name.
 *
 * Coordinates are only needed for the forecast and the weather skips, and asking for
 * decimal degrees to get them is a good way to have nobody fill the field in. A
 * postcode or a town name is something people actually know.
 *
 * The chosen place carries its IANA time zone too, which the app needs regardless and
 * would otherwise be a four-hundred-entry dropdown. Getting it from the same choice
 * also means the two cannot disagree — a controller in Denver with the server's idea
 * of the time zone would water at the wrong hour.
 */
export const LocationField = observer(function LocationField({
  latitude,
  longitude,
  label,
  onPicked,
  onCleared,
}: {
  latitude: number | null;
  longitude: number | null;
  /** What the current coordinates are known as, when that is known. */
  label?: string | null;
  onPicked: (place: Place) => void;
  onCleared?: () => void;
}) {
  const [query, setQuery] = useState('');
  // null means "not searched yet", which is different from an empty list.
  const [results, setResults] = useState<Place[] | null>(null);
  const [searching, setSearching] = useState(false);

  const hasLocation = latitude !== null && longitude !== null;

  async function search() {
    const trimmed = query.trim();
    if (trimmed.length < 2) return;

    setSearching(true);
    try {
      setResults(await api.places(trimmed));
    } catch {
      setResults([]);
    } finally {
      setSearching(false);
    }
  }

  return (
    <Field
      label="Location"
      hint="Postcode or town — used for the forecast and weather skips, and to set the time zone."
    >
      {hasLocation && (
        <div className="location__current">
          <span className="location__label">
            {label || `${latitude!.toFixed(4)}, ${longitude!.toFixed(4)}`}
          </span>
          {onCleared && (
            <button type="button" className="location__clear" onClick={onCleared}>
              Clear
            </button>
          )}
        </div>
      )}

      <div className="location__search">
        <TextInput
          value={query}
          onChange={setQuery}
          placeholder={hasLocation ? 'Change location…' : 'e.g. 80202 or Denver'}
        />
        <Button size="sm" onClick={search} disabled={searching || query.trim().length < 2}>
          {searching ? <Spinner size={14} /> : 'Search'}
        </Button>
      </div>

      {results !== null && results.length === 0 && !searching && (
        <p className="location__none">No matches. Try a town name, or a postcode with its country.</p>
      )}

      {results !== null && results.length > 0 && (
        <ul className="location__results">
          {results.map((place) => (
            <li key={`${place.latitude},${place.longitude},${place.label}`}>
              <button
                type="button"
                className="location__result"
                onClick={() => {
                  onPicked(place);
                  setResults(null);
                  setQuery('');
                }}
              >
                <span className="location__result-name">{place.label}</span>
                <span className="location__result-meta data">
                  {place.latitude.toFixed(3)}, {place.longitude.toFixed(3)}
                  {place.timeZoneId && ` · ${place.timeZoneId}`}
                </span>
              </button>
            </li>
          ))}
        </ul>
      )}
    </Field>
  );
});
