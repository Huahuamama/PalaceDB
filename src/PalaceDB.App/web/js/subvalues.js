// The sub-table editor of a List / Properties cell.
//
// It is a real grid - the same class as the table above - rather than a dialog or a form
// of inputs: it has a header, a gutter, typed cells, keyboard navigation and its own
// editors, and it writes back while you type.

import { send } from './bridge.js';
import { showContextMenu } from './contextmenu.js';
import { openModal } from './dialogs.js';
import { panelId } from './grid.js';
import { promptEditColumn, promptEditSubField } from './schema.js';
import { grid, subPanels } from './session.js';
import { SubPanel } from './subpanel.js';
import { closePanel } from './subtables.js';

/**
 * The sub-table of a List / Properties cell, opened underneath its row.
 *
 * It is a real grid — the same class as the table above — rather than a dialog or a form
 * of inputs: it has a header, a gutter, typed cells, keyboard navigation and its own
 * editors, and it writes back while you type. Each cell keeps its own panel, so several
 * lists can be open at once, including two lists of the same row.
 */
export function showSubValueEditor(payload) {
  const col = grid.columns.findIndex((column) => column.name === payload.column);
  if (col < 0) return;

  const id = panelId(payload.row, col);
  const existing = subPanels.get(id);

  if (existing) {
    // A re-sent document (after an edit elsewhere) refreshes the panel in place so it
    // keeps its position and height.
    existing.setData(payload);
    return;
  }

  const panel = new SubPanel({
    grid,
    send,
    closePanel,
    // The panel's right-click menus are built where the actions live: it asks for a menu
    // at a point, and every entry carries its own callback.
    menu: (x, y, items) => showContextMenu(x, y, items, (item) => {
      if (item.onSelect) item.onSelect();
    }),
    confirm: ({ title, message, okLabel, onOk }) => openModal({
      title,
      okLabel,
      build: () => {
        const text = document.createElement('p');
        text.style.margin = '0';
        text.textContent = message;
        return text;
      },
      onOk: () => {
        onOk();
        return true;
      },
    }),
    promptSubField: (options) => promptEditSubField(options),
    promptFields: (sheet, columnName) => {
      const column = grid.columns.find((item) => item.name === columnName);
      if (column) promptEditColumn(column, sheet);
    },
  }, payload, col);

  subPanels.set(id, panel);
  grid.mountPanel(id, payload.row, col, panel.node, 80);
  panel.measure();
  panel.focus();
}
