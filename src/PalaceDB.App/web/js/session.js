// What one session holds: the open document, the cursor of the find bar, the sub-tables
// that are open and the two long-lived view objects.
//
// These are the pieces every other module reads. They live apart so that a dialog can
// look at the document without importing the code that draws it - the view imports the
// session, never the other way round.

import { elements } from './dom.js';
import { CellEditor } from './editors.js';
import { Grid } from './grid.js';
import { DEFAULTS, normalize } from './settings.js';

export const state = {
  document: null,
  currentSheet: null,
  appInfo: null,
  issue: null,
  // Which sidebar section is open: 'tables', 'types' or 'columns'. Only one at a time.
  sidebarTab: 'tables',
  // Interface text size, table text size and colour scheme (see settings.js).
  settings: normalize(DEFAULTS),
};

/** Set when About was requested before the host answered with the build details. */
export const flags = { pendingAbout: false };

/** Find / replace state. The host owns the matches; this only tracks the cursor in them. */
export const search = {
  open: false,
  matches: [],
  index: -1,
  truncated: false,
  timer: null,
  // True when the reply to the in-flight query should move the cursor to the match.
  revealPending: false,
};

/** Sub-tables open under List / Properties cells, keyed by "row:col". Several at a time. */
export const subPanels = new Map();

// The cursor callback is installed at start-up rather than passed in: it updates the
// status bar, which belongs to view.js, and that module imports this one.
export const grid = new Grid({
  headScroll: elements.gridHeadScroll,
  head: elements.gridHead,
  body: elements.gridBody,
  spacer: elements.gridSpacer,
  rows: elements.gridRows,
  gutter: elements.gridGutter,
  container: elements.grid,
});

export const editor = new CellEditor(grid);

/** Description of the table currently shown, or null when nothing is open. */
export function currentSheetData() {
  if (!state.document || !state.currentSheet) return null;
  return state.document.sheets.find((s) => s.name === state.currentSheet) || null;
}

/** The public enum / flags types of the open file, as the sidebar shows them. */
export function publicTypesOfDocument() {
  return (state.document && state.document.publicTypes) || [];
}
