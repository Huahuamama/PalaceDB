// Lifecycle of the sub-tables open under List / Properties cells.
//
// The registry is kept here rather than with the editor that builds them, because the
// document view has to close and refresh them whenever the table changes shape.

import { send } from './bridge.js';
import { grid, subPanels } from './session.js';

/** Closes one sub-table, writing anything it still holds back. */
export function closePanel(id) {
  const panel = subPanels.get(id);
  if (!panel) return;
  subPanels.delete(id);
  panel.destroy();
  grid.removePanel(id);
}

/** Closes every sub-table (switching table, closing the document). */
export function closeAllPanels() {
  for (const id of [...subPanels.keys()]) closePanel(id);
  grid.clearPanels();
}

/** Writes any edit the panels are still holding back. */
export function flushPendingEdits() {
  for (const panel of subPanels.values()) panel.flush();
}

/** Refreshes the open sub-tables after the document changed shape (new fields, rows). */
export function refreshPanels(rowCount) {
  for (const panel of [...subPanels.values()]) {
    // A deleted row takes its panel with it.
    if (typeof rowCount === 'number' && panel.row >= rowCount) {
      closePanel(panel.id);
      continue;
    }
    send('openSubValue', { sheet: panel.sheet, row: panel.row, column: panel.column });
  }
}
