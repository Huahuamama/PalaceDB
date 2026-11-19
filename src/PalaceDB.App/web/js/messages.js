// What the host sends back, and which module handles it.
//
// The page never polls: every reply arrives here and is handed to the view or the dialog
// it belongs to.

import { onMessage } from './bridge.js';
import { onSearchResults, resetSearch } from './findbar.js';
import { applyImages } from './images.js';
import { renderRecentFiles, showAbout } from './menus.js';
import { editor, flags, grid, state, subPanels } from './session.js';
import { showMergeReport } from './settingsdlg.js';
import { showSubValueEditor } from './subvalues.js';
import { showColumnImpact, showTemplateReport } from './templates.js';
import { toast } from './toast.js';
import { applyState, refreshIssue, renderTableList, showDocument, showWelcome } from './view.js';
import { applyFileScheme } from './viewoptions.js';

onMessage((message) => {
  if (!message || typeof message !== 'object') return;

  switch (message.type) {
    case 'document':
      showDocument(message.payload);
      break;
    case 'columnImpact':
      showColumnImpact(message.payload);
      break;
    case 'templateReport':
      showTemplateReport(message.payload);
      break;
    case 'recentFiles':
      renderRecentFiles(message.payload.files);
      break;
    case 'appInfo':
      state.appInfo = message.payload;
      if (flags.pendingAbout) {
        flags.pendingAbout = false;
        showAbout(message.payload);
      }
      break;
    case 'documentClosed':
      resetSearch();
      showWelcome();
      // Without a file there is no per-file palette any more: back to the preference.
      applyFileScheme(null);
      break;
    case 'fileScheme':
      // The host echoed the palette it stored for this file; nothing to do unless it
      // normalised the value away.
      if (message.payload && message.payload.scheme !== state.settings.scheme) {
        applyFileScheme(message.payload.scheme);
      }
      break;
    case 'rows':
      grid.applyRows(message.payload);
      refreshIssue();
      break;
    case 'searchResults':
      onSearchResults(message.payload);
      break;
    case 'firstIssue': {
      const payload = message.payload || {};
      state.issue = typeof payload.row === 'number'
        ? {
            sheet: payload.sheet || state.currentSheet || '',
            row: payload.row,
            column: payload.column || '',
            detail: payload.detail || 'invalid value',
          }
        : null;
      refreshIssue();
      break;
    }
    case 'rowUpdated':
      if (message.payload.sheet === state.currentSheet) grid.replaceRow(message.payload.row);
      break;
    case 'invalidate':
      grid.invalidate();
      if (message.payload.cursor && message.payload.cursor.sheet === state.currentSheet) {
        // Undo and redo put the cursor back on the change, but the viewport stays where
        // the user left it: being pulled to another row is what made the table jump.
        grid.moveCursor(
          message.payload.cursor.row,
          message.payload.cursor.column,
          false,
          !message.payload.keepView,
        );
      }
      break;
    case 'cellRejected':
      toast(message.payload.message, 'error');
      break;
    case 'state':
      applyState(message.payload);
      break;
    case 'refOptions':
      editor.applyRefOptions(message.payload);
      break;
    case 'images':
      applyImages(message.payload.entries);
      break;
    case 'mergeReport':
      showMergeReport(message.payload);
      break;
    case 'subValue':
      showSubValueEditor(message.payload);
      break;
    case 'browseResult': {
      // Whichever editor asked for the file gets it: a list cell has its own picker.
      const waiting = [...subPanels.values()].find((panel) => panel.pendingBrowse);
      if (waiting) waiting.applyBrowseResult(message.payload.path);
      else editor.applyBrowseResult(message.payload.path);
      break;
    }
    case 'clipboardWritten':
      toast(
        `${message.payload.cut ? 'Cut' : 'Copied'} ${message.payload.rows} x ${message.payload.columns} cells.`,
        'info',
      );
      break;
    case 'columnWidth': {
      const column = grid.columns.find((c) => c.name === message.payload.column);
      if (column) column.width = Math.max(40, Math.round(message.payload.width));
      // A wider or narrower column wraps differently, so every measured row is stale.
      grid.clearRowHeights();
      grid.renderHead();
      grid.scheduleRender();
      break;
    }
    case 'sheetSelected':
      state.currentSheet = message.payload.sheet;
      renderTableList();
      break;
    case 'toast':
      toast(message.payload.message, message.payload.level);
      break;
    case 'error':
      toast(message.payload.message, 'error');
      break;
    default:
      break;
  }
});
