// User preferences: the size of the interface text, the size of the table text and the
// colour scheme.
//
// The sizes are written to localStorage rather than to the document, so a choice applies
// to every database that is opened and never shows up as an unsaved change. The palette
// is per file: it is stored in the document's side-car (`X.cdb.meta.json`) so two open
// databases can be told apart, and localStorage only remembers the fallback that a file
// without a palette of its own opens with.
//
// The colours themselves live in css/themes.css. This module only knows the ids, the
// display names and a few swatch colours for the settings window.

const KEY = 'palacedb.settings';

/** Palettes offered in the settings window, in the order they are shown. */
export const SCHEMES = [
  { id: 'light', name: 'Daylight', hint: 'Light', dark: false, swatch: ['#eef1f6', '#ffffff', '#3b6ef5'] },
  { id: 'dark', name: 'Midnight', hint: 'Dark', dark: true, swatch: ['#1e1f22', '#2a2c30', '#5b9dff'] },
  { id: 'graphite', name: 'Graphite', hint: 'Dark', dark: true, swatch: ['#17181a', '#242629', '#8b93a1'] },
  { id: 'ocean', name: 'Deep Sea', hint: 'Dark', dark: true, swatch: ['#0d1526', '#17233c', '#4d8dff'] },
  { id: 'sand', name: 'Sandstone', hint: 'Light', dark: false, swatch: ['#f4ede0', '#fffdf8', '#b0743a'] },
  { id: 'rose', name: 'Blush', hint: 'Light', dark: false, swatch: ['#fdf0f4', '#fffbfc', '#e2557f'] },
  { id: 'lilac', name: 'Lilac', hint: 'Light', dark: false, swatch: ['#f4f1fc', '#fdfcff', '#8b5cf6'] },
  { id: 'olive', name: 'Olive', hint: 'Light', dark: false, swatch: ['#eef1e4', '#fbfdf5', '#6b8e23'] },
  { id: 'mint', name: 'Mint', hint: 'Light', dark: false, swatch: ['#eaf6f2', '#fbfefd', '#14a37f'] },
];

export const DEFAULTS = Object.freeze({ uiFontSize: 15, gridFontSize: 14, scheme: 'light' });

export const UI_SIZE = Object.freeze({ min: 12, max: 20, step: 1 });
export const GRID_SIZE = Object.freeze({ min: 10, max: 20, step: 1 });

function clampTo(value, min, max, fallback) {
  const number = Math.round(Number(value));
  if (!Number.isFinite(number)) return fallback;
  return Math.min(max, Math.max(min, number));
}

export function isDarkScheme(id) {
  const scheme = SCHEMES.find((item) => item.id === id);
  return scheme ? scheme.dark : false;
}

export function schemeName(id) {
  const scheme = SCHEMES.find((item) => item.id === id);
  return scheme ? scheme.name : DEFAULTS.scheme;
}

/** Reads anything (a stored blob, a half-filled draft) into a usable settings object. */
export function normalize(raw) {
  const source = raw && typeof raw === 'object' ? raw : {};
  return {
    uiFontSize: clampTo(source.uiFontSize, UI_SIZE.min, UI_SIZE.max, DEFAULTS.uiFontSize),
    gridFontSize: clampTo(source.gridFontSize, GRID_SIZE.min, GRID_SIZE.max, DEFAULTS.gridFontSize),
    scheme: SCHEMES.some((item) => item.id === source.scheme) ? source.scheme : DEFAULTS.scheme,
  };
}

export function loadSettings() {
  try {
    return normalize(JSON.parse(localStorage.getItem(KEY) || '{}'));
  } catch {
    // A corrupt value must not stop the window from opening.
    return { ...DEFAULTS };
  }
}

export function saveSettings(settings) {
  try {
    localStorage.setItem(KEY, JSON.stringify(normalize(settings)));
  } catch {
    /* storage may be unavailable; the choice still applies to this session */
  }
}

export function isDefault(settings) {
  const current = normalize(settings);
  return current.uiFontSize === DEFAULTS.uiFontSize
    && current.gridFontSize === DEFAULTS.gridFontSize
    && current.scheme === DEFAULTS.scheme;
}

/**
 * Writes the chosen sizes and palette onto the document.
 *
 * Row and header heights are derived from the table font instead of being fixed: a row
 * has to stay tall enough for two lines of text, so a bigger font would otherwise clip
 * the second line and put the selection box in the wrong place.
 */
export function applyAppearance(settings) {
  const root = document.documentElement;
  const ui = settings.uiFontSize;
  const table = settings.gridFontSize;

  root.style.setProperty('--font-size', `${ui}px`);
  root.style.setProperty('--font-size-small', `${Math.max(10, Math.round(ui * 0.87))}px`);
  root.style.setProperty('--grid-font-size', `${table}px`);
  root.style.setProperty('--row-height', `${Math.round(table * 2.72)}px`);
  root.style.setProperty('--head-height', `${Math.round(table * 2.58)}px`);
  // The row number only needs three digits, so the gutter is narrow and clips anything
  // longer instead of taking space away from the table.
  root.style.setProperty('--gutter-width', `${Math.max(24, Math.round(table * 2))}px`);

  document.body.dataset.scheme = settings.scheme;
  // `theme-dark` still marks the dark family. The dark rules, the menu wording and the
  // native form controls are shared; only the palette above tells the schemes apart.
  document.body.classList.toggle('theme-dark', isDarkScheme(settings.scheme));
}
