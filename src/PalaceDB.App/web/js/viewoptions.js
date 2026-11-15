// Appearance: the size of the interface text, the size of the table text and the palette.
//
// Applying them is not just a CSS variable swap - the row height is read back by the grid
// to place rows, editors and sub-tables - so this module also forces a re-measure.

import { send } from './bridge.js';
import { refreshMetrics } from './grid.js';
import { grid, state, subPanels } from './session.js';
import { SCHEMES, applyAppearance, isDarkScheme, loadSettings, normalize, saveSettings } from './settings.js';

/**
 * Applies a settings object to the whole window.
 *
 * The sizes and the palette are CSS variables, but the row height is also read by the
 * grid to place rows, editors and sub-tables. So the metrics are re-read and the table
 * is laid out again; otherwise the rows would keep their old height and every click
 * would land one row off.
 *
 * The sizes are a preference of the application and go to localStorage. The palette is
 * a property of the open file and is written to its side-car instead, so two databases
 * can be told apart; localStorage keeps it only as the fallback a file without a
 * palette of its own opens with.
 */
export function applySettings(next, { persist = true, syncFileScheme = false } = {}) {
  const before = state.settings;
  state.settings = normalize(next);
  applyAppearance(state.settings);

  if (persist) {
    saveSettings(state.settings);
    // The Settings window previews a palette by applying it with persist:false, so by
    // the time its OK runs `before` already holds the new scheme and the comparison
    // below cannot see the change. syncFileScheme forces the side-car write in that
    // case; without it the file kept the old palette and the next document refresh
    // (insert row, column options, ...) reverted the window to it.
    if (state.document && (syncFileScheme || state.settings.scheme !== before.scheme)) {
      send('setFileScheme', { scheme: state.settings.scheme });
    }
  }

  refreshMetrics();
  // The font size decides the row height, so every measurement taken with the old one is
  // wrong: this is the one place that has to throw them away.
  grid.invalidate({ remeasure: true });
  for (const panel of subPanels.values()) {
    panel.grid.invalidate({ remeasure: true });
    panel.measure();
  }
  grid.layoutPanels();
  syncThemeMenu();
}

/**
 * Adopts the palette stored with a document. A file that never had one chosen keeps the
 * stored preference, which is what makes a brand new database open in the usual colours.
 */
export function applyFileScheme(scheme) {
  const next = SCHEMES.some((item) => item.id === scheme) ? scheme : loadSettings().scheme;
  if (next === state.settings.scheme) return;
  applySettings({ ...state.settings, scheme: next }, { persist: false });
}

/** The View menu names the mode it switches to, not the one that is on. */
function syncThemeMenu() {
  const item = document.querySelector('[data-action="toggleTheme"] span');
  if (item) item.textContent = isDarkScheme(state.settings.scheme) ? 'Light Mode' : 'Dark Mode';
}

/** Ctrl+T flips between the light and the dark family, whatever the exact palette is. */
export function toggleTheme() {
  const dark = isDarkScheme(state.settings.scheme);
  applySettings({ ...state.settings, scheme: dark ? 'light' : 'dark' });
}
