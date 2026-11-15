// The right-click menu, its sub-menus and the items it is built from.
//
// An item carries an action name and the menu calls back into the application when one is
// picked; the handler is installed at start-up so that this module does not have to know
// what the actions do.

import { send } from './bridge.js';
import { panelId } from './grid.js';
import { currentSheetData, grid, state } from './session.js';
import { closeAllPanels, closePanel, flushPendingEdits } from './subtables.js';

/**
 * What a picked item does. Installed at start-up by the bootstrap, which is the only
 * module that knows the command table: the menu itself only carries the name.
 */
let actionHandler = () => {};

export function setActionHandler(handler) {
  actionHandler = handler;
}

const contextMenu = document.getElementById('contextMenu');
const subMenu = document.getElementById('subMenu');

export function hideContextMenu() {
  contextMenu.hidden = true;
  subMenu.hidden = true;
  contextMenu.innerHTML = '';
  subMenu.innerHTML = '';
}

/**
 * Fills a menu element. Items are either 'sep' or
 * { label, action, arg, shortcut, disabled, submenu }.
 *
 * `nested` is set when the target *is* the submenu: hovering one of its entries must not
 * hide the menu the pointer is inside, which is what made "Convert" impossible to use.
 */
function buildMenuInto(root, items, onPick, nested = false) {
  root.innerHTML = '';

  // A menu that has checkable entries reserves the tick column for all of them, so the
  // labels stay aligned.
  const hasChecks = items.some((item) => item !== 'sep' && 'checked' in item);

  for (const item of items) {
    if (item === 'sep') {
      const sep = document.createElement('div');
      sep.className = 'context-sep';
      root.appendChild(sep);
      continue;
    }

    const button = document.createElement('button');
    button.className = 'context-item';
    button.type = 'button';
    button.disabled = Boolean(item.disabled);

    if (hasChecks) {
      const check = document.createElement('span');
      check.className = 'context-check';
      check.textContent = item.checked ? '\u2713' : '';
      button.appendChild(check);
    }

    const label = document.createElement('span');
    label.className = 'context-label';
    label.textContent = item.label;
    button.appendChild(label);

    if (item.submenu) {
      button.classList.add('has-submenu');

      const arrow = document.createElement('span');
      arrow.className = 'context-arrow';
      arrow.textContent = '\u25B8';
      button.appendChild(arrow);

      // Both hover and click open the submenu so the menu stays usable with a
      // trackpad, where hovering a narrow item is easy to miss.
      const open = () => openSubMenu(button, item.submenu, onPick);
      button.addEventListener('mouseenter', open);
      button.addEventListener('click', (event) => {
        event.stopPropagation();
        open();
      });
      root.appendChild(button);
      continue;
    }

    if (item.shortcut) {
      const kbd = document.createElement('kbd');
      kbd.textContent = item.shortcut;
      button.appendChild(kbd);
    }

    button.addEventListener('mouseenter', () => {
      if (!nested) subMenu.hidden = true;
    });
    button.addEventListener('click', () => {
      hideContextMenu();
      onPick(item);
    });

    root.appendChild(button);
  }
}

/** Opens the nested menu to the right of `anchor`, flipping when it would overflow. */
function openSubMenu(anchor, items, onPick) {
  buildMenuInto(subMenu, items, onPick, true);
  subMenu.hidden = false;

  const rect = anchor.getBoundingClientRect();
  const box = subMenu.getBoundingClientRect();

  let left = rect.right - 4;
  if (left + box.width > window.innerWidth - 4) left = Math.max(4, rect.left - box.width + 4);

  let top = rect.top - 6;
  if (top + box.height > window.innerHeight - 4) {
    top = Math.max(4, window.innerHeight - box.height - 4);
  }

  subMenu.style.left = left + 'px';
  subMenu.style.top = top + 'px';
}

/** Shows a menu at the pointer. `onPick` defaults to the application's own actions. */
export function showContextMenu(x, y, items, onPick) {
  hideContextMenu();
  buildMenuInto(contextMenu, items, onPick || ((item) => actionHandler(item.action, item.arg)));

  contextMenu.hidden = false;

  // Keep the menu inside the window.
  const rect = contextMenu.getBoundingClientRect();
  contextMenu.style.left = Math.max(4, Math.min(x, window.innerWidth - rect.width - 8)) + 'px';
  contextMenu.style.top = Math.max(4, Math.min(y, window.innerHeight - rect.height - 8)) + 'px';
}

/**
 * Conversions offered for a column, or an empty list when its type has none.
 * Text-like columns get case rules, numbers get scaling and shifting.
 */
function convertSubmenu(column) {
  if (!column) return [];

  const text = [
    ['lowercase', 'lowercase'],
    ['UPPERCASE', 'uppercase'],
    ['UpperIdent', 'upperIdent'],
    ['lowerIdent', 'lowerIdent'],
  ];
  const number = [
    ['* 10', 'mul10'],
    ['/ 10', 'div10'],
    ['+ 1', 'plus1'],
    ['- 1', 'minus1'],
  ];

  let pairs;
  switch (column.type) {
    case 'Id':
    case 'Text':
    case 'Enum':
    case 'Flags':
      pairs = text;
      break;
    case 'Int':
    case 'Float':
      pairs = number;
      break;
    default:
      return [];
  }

  return pairs.map(([label, conversion]) => ({
    label,
    action: 'convertColumn',
    arg: { sheet: state.currentSheet, column: column.name, conversion },
  }));
}

// A click on a List / Properties cell asks the host for that cell's rows; the answer
// arrives as a `subValue` message and becomes a table under the row. Each cell keeps its
// own panel, so a row with two lists can show both.
grid.onExpandRequest = (row, col) => {
  const column = grid.columns[col];
  if (!column || !state.currentSheet) return;
  send('openSubValue', { sheet: state.currentSheet, row, column: column.name });
};

grid.onCollapseRequest = (row, col) => closePanel(panelId(row, col));

grid.onContextMenu = ({ header, x, y }) => {
  if (!state.currentSheet) return;

  if (header) {
    const column = grid.columns[grid.cursor.col];
    const items = [
      { label: 'Insert Column...', action: 'addColumn' },
      { label: 'Edit Column...', action: 'editColumn' },
      'sep',
      { label: 'Move Column Left', action: 'moveColumnLeft' },
      { label: 'Move Column Right', action: 'moveColumnRight' },
    ];

    // Bulk value conversions, offered for the types they make sense on (C07).
    const convert = convertSubmenu(column);
    if (convert.length > 0) {
      items.push('sep', { label: 'Convert', submenu: convert });
    }

    // A reference column is labelled by the target table's display column.
    if (column && column.type === 'Ref' && column.refSheet) {
      items.push('sep', { label: `Display Column of '${column.refSheet}'...`, action: 'displayColumn' });
    }

    // Image and text columns can hold the icon drawn next to that label (C06).
    const sheet = currentSheetData();
    if (sheet && column && (column.type === 'Image' || column.type === 'Text')) {
      items.push({
        label: `Icon Column of '${sheet.name}'`,
        action: 'toggleDisplayIcon',
        checked: sheet.displayIcon === column.name,
      });
    }

    // Localizable text is what the export collects (F08).
    if (column && (column.type === 'Text' || column.type === 'Id')) {
      items.push({
        label: 'Localizable',
        action: 'toggleLocalizable',
        checked: Boolean(column.localizable),
      });
    }

    items.push('sep', { label: 'Delete Column', action: 'deleteColumn' }, 'sep',
      { label: 'Clear Column Colour', action: 'clearColumnFill' });

    // Hiding a column is a view choice, not a schema change: the value stays in the file
    // and the sidebar's Columns panel brings it back.
    const sheetData = currentSheetData();
    const hidden = sheetData ? sheetData.columns.filter((c) => c.hidden).length : 0;
    items.push('sep',
      { label: 'Hide Column', action: 'hideColumn', disabled: !column },
      {
        label: hidden > 0 ? `Show All Columns (${hidden} hidden)` : 'Show All Columns',
        action: 'showAllColumns',
        disabled: hidden === 0,
      });

    showContextMenu(x, y, items);
    return;
  }

  // Row operations live on the row number, not here: a cell menu is about the value under
  // the pointer, and having "delete row" next to "copy" made it too easy to hit by mistake.
  showContextMenu(x, y, [
    { label: 'Cut', action: 'cut', shortcut: 'Ctrl+X' },
    { label: 'Copy', action: 'copy', shortcut: 'Ctrl+C' },
    { label: 'Paste', action: 'paste', shortcut: 'Ctrl+V' },
    'sep',
    { label: 'Clear Cells', action: 'clearCells', shortcut: 'Del' },
    { label: 'Select All', action: 'selectAll', shortcut: 'Ctrl+A' },
  ]);
};

// Dragging a row number moves the row; the host owns the order, so it is told where the
// row went and re-sends the table. An open sub-table is anchored to a row number, not to
// a record, so it is closed first — otherwise it would quietly start showing the list of
// whatever row took that place. `below` says which side of the heading in the landing gap
// the row was dropped on, which is what decides the group it joins.
grid.onRowMove = (from, to, below) => {
  if (!state.currentSheet) return;
  flushPendingEdits();
  closeAllPanels();
  send('moveRow', { sheet: state.currentSheet, from, to, below: below !== false });
};

// The row number is also where a row is grabbed, so the whole row menu lives here. Every
// action that targets "this row" passes the row number explicitly: a right-click inside a
// block selection keeps the selection, so the cursor is not necessarily the clicked row.
grid.onGutterMenu = ({ row, x, y }) => {
  if (!state.currentSheet) return;

  const count = grid.selectedRowIndices().length;
  const hasSeparator = grid.separatorTitles.has(row);
  const separatorTitle = grid.separatorTitles.get(row) || '';

  showContextMenu(x, y, [
    { label: 'Insert Row Above', action: 'insertRowAt', arg: row },
    { label: 'Insert Row Below', action: 'insertRowBelowAt', arg: row },
    { label: count > 1 ? `Duplicate ${count} Rows` : 'Duplicate Row', action: 'duplicateRows' },
    { label: count > 1 ? `Delete ${count} Rows` : 'Delete Row', action: 'deleteRows' },
    'sep',
    { label: hasSeparator ? 'Remove Separator' : 'Add Separator', action: 'toggleSeparatorAt', arg: row },
    {
      label: separatorTitle ? `Name Separator... (${separatorTitle})` : 'Name Separator...',
      action: 'nameSeparatorAt',
      arg: row,
      disabled: !hasSeparator,
    },
    'sep',
    { label: 'Insert Paste', action: 'pasteInsertAt', arg: row, shortcut: 'Ctrl+Shift+V' },
  ]);
};

document.addEventListener('mousedown', (event) => {
  if (!event.target.closest('.context-menu')) hideContextMenu();
});
window.addEventListener('blur', hideContextMenu);
window.addEventListener('resize', hideContextMenu);
