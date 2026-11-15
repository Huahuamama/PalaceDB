// The command table: one name per thing the user can ask for, whatever asked for it.
//
// The menu bar, the toolbar, the context menu and the keyboard all end up here, so a
// shortcut and a menu entry cannot drift apart.

import { send } from './bridge.js';
import { confirmDeleteColumn, currentColumn, moveColumn, promptAddColumn, promptEditColumn } from './schema.js';
import { currentSheetData, flags, grid, state } from './session.js';
import { openSettings } from './settingsdlg.js';
import { flushPendingEdits } from './subtables.js';
import { confirmDeleteTable, promptAddTable, promptDisplayColumn, promptSeparatorTitle } from './tables.js';
import { promptTableSettings, promptTemplates } from './templates.js';
import { toggleSidebar } from './view.js';
import { toggleTheme } from './viewoptions.js';

export function runRowAction(action) {
  if (!state.currentSheet || !state.document) return;

  const rows = grid.selectedRowIndices();
  const columns = grid.selectedColumnNames();

  switch (action) {
    case 'addRow':
      // The toolbar button appends: a new row belongs at the end of the table, not above
      // whatever the cursor happens to be on. The row-number menu has "Insert Row Here"
      // for the other case.
      send('insertRow', { sheet: state.currentSheet, row: grid.rowCount });
      break;
    case 'duplicateRows':
      if (rows.length) send('duplicateRows', { sheet: state.currentSheet, rows });
      break;
    case 'deleteRows':
      if (rows.length) send('deleteRows', { sheet: state.currentSheet, rows });
      break;
    case 'copy':
      if (rows.length) send('copyCells', { sheet: state.currentSheet, rows, columns });
      break;
    case 'cut':
      if (rows.length) send('cutCells', { sheet: state.currentSheet, rows, columns });
      break;
    case 'paste':
      send('pasteCells', {
        sheet: state.currentSheet,
        row: grid.cursor.row,
        column: grid.cursor.col,
        mode: 'overwrite',
      });
      break;
    case 'pasteInsert':
      send('pasteCells', { sheet: state.currentSheet, row: grid.cursor.row, mode: 'insert' });
      break;
    case 'pasteInsertAt':
      if (state.currentSheet) send('pasteCells', { sheet: state.currentSheet, row: arg, mode: 'insert' });
      break;
    default:
      break;
  }
}

export function runAction(action, arg) {
  switch (action) {
    case 'newWindow': send('newWindow'); break;
    case 'newDatabase': send('newDatabase'); break;
    case 'open': send('openFile'); break;
    case 'openRecent': if (arg) send('openRecent', { path: arg }); break;
    case 'clearRecent': send('clearRecent'); break;
    case 'about':
      // The build details come from the host; the dialog opens when they arrive.
      flags.pendingAbout = true;
      send('about');
      break;
    // A sub-table edit is written on a short delay, so flush it before the file is
    // written; otherwise the last keystroke in an open panel misses the save.
    case 'save': flushPendingEdits(); send('save'); break;
    case 'saveAs': flushPendingEdits(); send('saveAs'); break;
    case 'closeDocument': send('closeDocument'); break;
    case 'exit': send('exit'); break;
    // A half typed list edit belongs to the step being undone, so it is written first.
    case 'undo': flushPendingEdits(); send('undo'); break;
    case 'redo': flushPendingEdits(); send('redo'); break;
    case 'toggleTheme': toggleTheme(); break;
    case 'toggleSidebar': toggleSidebar(); break;
    case 'settings': openSettings(); break;
    case 'addRow':
    case 'duplicateRows':
    case 'deleteRows':
    case 'copy':
    case 'cut':
    case 'paste':
    case 'pasteInsert':
      runRowAction(action);
      break;
    case 'insertRowAbove':
      if (state.currentSheet) send('insertRow', { sheet: state.currentSheet, row: grid.cursor.row });
      break;
    case 'insertRowBelow':
      if (state.currentSheet) send('insertRow', { sheet: state.currentSheet, row: grid.cursor.row + 1 });
      break;
    // The row-number menu acts on the row it was opened on, which is not always the row
    // the cursor sits on (a right-click on another row selects it, a block selection not).
    case 'insertRowAt':
      if (state.currentSheet) send('insertRow', { sheet: state.currentSheet, row: arg });
      break;
    case 'insertRowBelowAt':
      if (state.currentSheet) send('insertRow', { sheet: state.currentSheet, row: arg + 1 });
      break;
    case 'appendRow':
      if (state.currentSheet) send('insertRow', { sheet: state.currentSheet, row: grid.rowCount });
      break;
    case 'pasteAt':
      if (state.currentSheet) {
        send('pasteCells', { sheet: state.currentSheet, row: arg, column: 0, mode: 'overwrite' });
      }
      break;
    case 'clearCells':
      if (grid.onClearRequest) grid.onClearRequest();
      break;
    case 'fillSelection': {
      // Without an open editor, fill the selection with the cursor cell's own value.
      if (!state.currentSheet) break;
      const data = grid.cellValue(grid.cursor.row, grid.cursor.col);
      // Nothing to copy from (the row is not on screen): filling with a blank would wipe
      // the block instead of doing what was asked.
      if (!data) break;
      send('fillCells', {
        sheet: state.currentSheet,
        rows: grid.selectedRowIndices(),
        columns: grid.selectedColumnNames(),
        text: data.t,
      });
      break;
    }
    case 'selectAll':
      grid.selectAll();
      break;
    case 'addColumn':
      promptAddColumn();
      break;
    case 'editColumn':
      if (state.currentSheet) promptEditColumn();
      break;
    case 'moveColumnLeft':
      moveColumn(-1);
      break;
    case 'moveColumnRight':
      moveColumn(1);
      break;
    case 'deleteColumn':
      if (state.currentSheet) confirmDeleteColumn();
      break;
    case 'hideColumn': {
      if (!state.currentSheet) break;
      const column = grid.columns[grid.cursor.col];
      if (!column) break;
      send('setColumnHidden', { sheet: state.currentSheet, column: column.name, hidden: true });
      break;
    }
    case 'showAllColumns':
      if (state.currentSheet) send('setColumnHidden', { sheet: state.currentSheet, all: true });
      break;
    case 'convertColumn':
      if (arg) send('convertColumn', arg);
      break;
    case 'clearColumnFill':
      if (state.currentSheet) {
        send('setStyle', {
          sheet: state.currentSheet,
          kind: 'column',
          color: '',
          rows: [],
          columns: grid.selectedColumnNames(),
        });
      }
      break;
    case 'addTable':
      promptAddTable();
      break;
    case 'deleteTable':
      confirmDeleteTable();
      break;
    case 'tableSettings':
      promptTableSettings();
      break;
    case 'templates':
      promptTemplates();
      break;
    case 'moveTableUp':
      if (state.currentSheet) send('moveSheet', { sheet: state.currentSheet, direction: -1 });
      break;
    case 'moveTableDown':
      if (state.currentSheet) send('moveSheet', { sheet: state.currentSheet, direction: 1 });
      break;
    case 'toggleTableIndex':
    case 'toggleTableGroup': {
      const sheet = currentSheetData();
      if (!sheet) break;
      const index = action === 'toggleTableIndex';
      send(index ? 'toggleSheetIndex' : 'toggleSheetGroup', {
        sheet: sheet.name,
        enabled: !(index ? sheet.hasIndex : sheet.hasGroup),
      });
      break;
    }
    case 'toggleSeparator':
      if (state.currentSheet) send('toggleSeparator', { sheet: state.currentSheet, row: grid.cursor.row });
      break;
    case 'toggleSeparatorAt':
      if (state.currentSheet) send('toggleSeparator', { sheet: state.currentSheet, row: arg });
      break;
    case 'nameSeparator':
      promptSeparatorTitle();
      break;
    case 'nameSeparatorAt':
      promptSeparatorTitle(arg);
      break;
    case 'displayColumn':
      promptDisplayColumn();
      break;
    case 'toggleDisplayIcon': {
      const sheet = currentSheetData();
      const column = currentColumn();
      if (!sheet || !column) break;
      send('setDisplayIcon', {
        sheet: sheet.name,
        // Clicking a column that already is the icon clears the property.
        column: sheet.displayIcon === column.name ? '' : column.name,
      });
      break;
    }
    case 'toggleLocalizable': {
      const column = currentColumn();
      if (!column || !state.currentSheet) break;
      send('setColumnKind', {
        sheet: state.currentSheet,
        column: column.name,
        localizable: !column.localizable,
      });
      break;
    }
    case 'exportLocalized':
      // The destination normally comes from a file dialog; a test may pass one in.
      send('exportLocalized', arg && arg.path ? { path: arg.path } : {});
      break;
    case 'applyFill':
      applyFill(document.getElementById('fillColor').value);
      break;
    case 'clearFill':
      applyFill('');
      break;
    default: break;
  }
}

/** Applies (or clears) a background colour for the current selection. */
function applyFill(color) {
  if (!state.currentSheet) return;
  send('setStyle', {
    sheet: state.currentSheet,
    kind: document.getElementById('fillScope').value,
    color,
    rows: grid.selectedRowIndices(),
    columns: grid.selectedColumnNames(),
  });
}
