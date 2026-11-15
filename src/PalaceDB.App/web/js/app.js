// Application bootstrap: the wiring that ties the modules together and the start-up.
//
// Everything else lives in its own module next to this one. What is left here is the part
// that genuinely belongs to no single domain: the toolbar and the view menu that dispatch
// into the command table, the callbacks the grid calls back on, and the start-up order.

import { connected, send } from './bridge.js';
import { runAction } from './commands.js';
import { setActionHandler, showContextMenu } from './contextmenu.js';
import { elements } from './dom.js';
import { closeFindBar, openFindBar, replaceAllInScope, replaceCurrent, runSearch, stepMatch } from './findbar.js';
import { onImagesLoaded } from './images.js';
import { promptPublicType } from './schema.js';
import { editor, grid, search, state } from './session.js';
import { DEFAULTS, loadSettings } from './settings.js';
import { openSettings } from './settingsdlg.js';
import { promptAddTable } from './tables.js';
import { toast } from './toast.js';
import {
  SIDEBAR_KEY, SIDEBAR_TAB_KEY,
  applySidebar, applySidebarTab, selectSheet, toggleSidebar, updateStatus,
} from './view.js';
import { applySettings, toggleTheme } from './viewoptions.js';
import './menus.js';
import './messages.js';
import './shortcuts.js';

// A picked menu item runs a command, but the menu is loaded before this module is: the
// handler is handed over rather than imported, so the two never import each other.
setActionHandler(runAction);

// The status bar follows the cursor; the grid is built in session.js, which cannot see
// the view that draws it, so the callback is installed here.
grid.onCursorChanged = updateStatus;

// Ctrl+Enter with no editor open: fill the block with the cursor cell's own value. The
// Edit menu lists the same action, so the shortcut has to work from the table too.
grid.onFillRequest = () => runAction('fillSelection');

// A resolved thumbnail only changes pixels, so the rows are redrawn in place; the open
// dropdown is rebuilt too because it may be showing icons.
onImagesLoaded(() => {
  grid.scheduleRender();
  editor.refreshImages();
});

grid.onClearRequest = () => {
  if (!state.currentSheet) return;
  send('clearCells', {
    sheet: state.currentSheet,
    rows: grid.selectedRowIndices(),
    columns: grid.selectedColumnNames(),
  });
};

grid.onColumnResized = (sheet, column, width) => {
  send('setColumnWidth', { sheet, column, width });
};

// Clicking a group heading folds its rows away. The grid has already done it locally, so
// the click feels immediate; the host writes the state to the side-car and re-sends the
// document, which carries the same set back.
grid.onGroupToggled = (title, collapsed) => {
  if (!state.currentSheet) return;
  send('setGroupCollapsed', { sheet: state.currentSheet, title, collapsed });
};


elements.toolbar.addEventListener('click', (event) => {
  const button = event.target.closest('.tool');
  if (!button || button.disabled) return;
  runAction(button.dataset.action);
});

// ---------------------------------------------------------------- startup

elements.welcomeOpen.addEventListener('click', () => send('openFile'));
elements.gridAddTable.addEventListener('click', () => runAction('addTable'));

elements.sidebarTabs.addEventListener('click', (event) => {
  const tab = event.target.closest('.sidebar-tab');
  if (!tab) return;
  applySidebarTab(tab.dataset.tab);
});

elements.sidebarAdd.addEventListener('click', (event) => {
  event.stopPropagation();
  if (state.sidebarTab === 'types') promptPublicType(null);
  else if (state.sidebarTab === 'tables') promptAddTable();
});

// Right-clicking a table selects it and offers the table operations.
elements.tableList.addEventListener('contextmenu', (event) => {
  const item = event.target.closest('.table-item');
  if (!item) return;
  event.preventDefault();

  selectSheet(item.dataset.sheet);

  const sheets = state.document ? state.document.sheets : [];
  const index = sheets.findIndex((s) => s.name === item.dataset.sheet);
  const sheet = index >= 0 ? sheets[index] : null;

  showContextMenu(event.clientX, event.clientY, [
    { label: 'Table Settings...', action: 'tableSettings' },
    { label: 'Delete Table', action: 'deleteTable' },
    'sep',
    { label: 'Move Table Up', action: 'moveTableUp', disabled: index <= 0 },
    {
      label: 'Move Table Down',
      action: 'moveTableDown',
      disabled: index < 0 || index >= sheets.length - 1,
    },
    'sep',
    { label: 'Add Index', action: 'toggleTableIndex', checked: Boolean(sheet && sheet.hasIndex) },
    { label: 'Add Group', action: 'toggleTableGroup', checked: Boolean(sheet && sheet.hasGroup) },
    'sep',
    { label: 'Add Table...', action: 'addTable' },
  ]);
});

elements.sidebarRail.addEventListener('click', () => applySidebar(false));

// The toggle is outside the toolbar, so it needs its own listener rather than the
// toolbar's delegated one. Clicking it is the same as View ▸ Collapse / Ctrl+B / Tab.
elements.sidebarExpand.addEventListener('click', toggleSidebar);

// Restore the view preferences chosen during the previous session.
try {
  applySettings(loadSettings(), { persist: false });
  applySidebar(localStorage.getItem(SIDEBAR_KEY) === '1');
  applySidebarTab(localStorage.getItem(SIDEBAR_TAB_KEY) || 'tables', { persist: false });
} catch {
  applySettings(DEFAULTS, { persist: false });
  applySidebar(false);
}

if (!connected) {
  toast('Running outside the PalaceDB shell: file access is unavailable.', 'warning');
}

// Development hook used by the automated UI tests.
window.__palacedb = {
  grid, editor, state, elements,
  send,
  run: runAction,
  toggleTheme,
  toggleSidebar,
  openSettings,
  applySettings,
  // Find / replace, exposed the same way as the settings window: the UI drives these,
  // and the end-to-end suite needs to step matches without synthesising keystrokes.
  openFindBar, closeFindBar, runSearch, stepMatch, replaceCurrent, replaceAllInScope,
  searchState: search,
  isDark: () => document.body.classList.contains('theme-dark'),
};

send('ready');
