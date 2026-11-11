// Application bootstrap: menu bar, table list, grid and host messages.

import { send, onMessage, connected } from './bridge.js';
import { Grid, panelId, refreshMetrics } from './grid.js';
import { CellEditor } from './editors.js';
import { SubPanel } from './subpanel.js';
import { applyImages, onImagesLoaded, clearImages } from './images.js';
import {
  SCHEMES, DEFAULTS, UI_SIZE, GRID_SIZE,
  normalize, loadSettings, saveSettings, applyAppearance, isDarkScheme,
} from './settings.js';

const elements = {
  menubar: document.getElementById('menubar'),
  recentMenu: document.getElementById('recentMenu'),
  docTitle: document.getElementById('docTitle'),
  sidebar: document.getElementById('sidebar'),
  sidebarRail: document.getElementById('sidebarRail'),
  sidebarExpand: document.getElementById('sidebarExpand'),
  addTable: document.getElementById('addTable'),
  tableList: document.getElementById('tableList'),
  grid: document.getElementById('grid'),
  gridHeadScroll: document.getElementById('gridHeadScroll'),
  gridHead: document.getElementById('gridHead'),
  gridBody: document.getElementById('gridBody'),
  gridSpacer: document.getElementById('gridSpacer'),
  gridRows: document.getElementById('gridRows'),
  gridGutter: document.getElementById('gridGutter'),
  welcome: document.getElementById('welcome'),
  welcomeOpen: document.getElementById('welcomeOpen'),
  gridEmpty: document.getElementById('gridEmpty'),
  statusIssue: document.getElementById('statusIssue'),
  statusIssueSep: document.getElementById('statusIssueSep'),
  statusTemplate: document.getElementById('statusTemplate'),
  statusTemplateSep: document.getElementById('statusTemplateSep'),
  gridAddTable: document.getElementById('gridAddTable'),
  statusSheet: document.getElementById('statusSheet'),
  statusCursor: document.getElementById('statusCursor'),
  statusRows: document.getElementById('statusRows'),
  statusMode: document.getElementById('statusMode'),
  toasts: document.getElementById('toasts'),
  findBar: document.getElementById('findBar'),
  findInput: document.getElementById('findInput'),
  findCount: document.getElementById('findCount'),
  findPrev: document.getElementById('findPrev'),
  findNext: document.getElementById('findNext'),
  findRegex: document.getElementById('findRegex'),
  findCase: document.getElementById('findCase'),
  findScope: document.getElementById('findScope'),
  findClose: document.getElementById('findClose'),
  findReplaceRow: document.getElementById('findReplaceRow'),
  replaceInput: document.getElementById('replaceInput'),
  replaceOne: document.getElementById('replaceOne'),
  replaceAll: document.getElementById('replaceAll'),
};

const state = {
  document: null,
  currentSheet: null,
  appInfo: null,
  issue: null,
  // Interface text size, table text size and colour scheme (see settings.js).
  settings: normalize(DEFAULTS),
};

/** Set when About was requested before the host answered with the build details. */
let pendingAbout = false;

/** Find / replace state. The host owns the matches; this only tracks the cursor in them. */
const search = {
  open: false,
  matches: [],
  index: -1,
  truncated: false,
  timer: null,
  // True when the reply to the in-flight query should move the cursor to the match.
  revealPending: false,
};

/** Sub-tables open under List / Properties cells, keyed by "row:col". Several at a time. */
const subPanels = new Map();

const grid = new Grid(
  {
    headScroll: elements.gridHeadScroll,
    head: elements.gridHead,
    body: elements.gridBody,
    spacer: elements.gridSpacer,
    rows: elements.gridRows,
    gutter: elements.gridGutter,
    container: elements.grid,
  },
  { onCursorChanged: updateStatus },
);

const editor = new CellEditor(grid);

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

// ---------------------------------------------------------------- toolbar

const toolbar = document.getElementById('toolbar');

toolbar.addEventListener('click', (event) => {
  const button = event.target.closest('.tool');
  if (!button || button.disabled) return;
  runAction(button.dataset.action);
});

function runRowAction(action) {
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

// ---------------------------------------------------------------- view options

const SIDEBAR_KEY = 'palacedb.sidebar.collapsed';

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
function applySettings(next, { persist = true, syncFileScheme = false } = {}) {
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
function applyFileScheme(scheme) {
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
function toggleTheme() {
  const dark = isDarkScheme(state.settings.scheme);
  applySettings({ ...state.settings, scheme: dark ? 'light' : 'dark' });
}

function applySidebar(collapsed) {
  elements.sidebar.classList.toggle('collapsed', collapsed);
  // The list is hidden outright, but the toggle is pinned to the body's top-left
  // corner, so it stays visible (and stays put) in both states.
  document.body.classList.toggle('sidebar-collapsed', collapsed);

  const item = document.querySelector('[data-menu="view"] [data-action="toggleSidebar"] span');
  if (item) item.textContent = collapsed ? 'Expand Table List' : 'Collapse Table List';

  // The pinned button is the single control for this: it keeps its place and only the
  // chevron flips (CSS mirrors it), so the pointer never has to hunt for it.
  if (elements.sidebarExpand) {
    elements.sidebarExpand.title = collapsed
      ? 'Expand the table list (Tab)'
      : 'Collapse the table list (Tab)';
    elements.sidebarExpand.setAttribute('aria-expanded', String(!collapsed));
  }

  try { localStorage.setItem(SIDEBAR_KEY, collapsed ? '1' : '0'); } catch { /* ignore */ }

  // The grid measures its viewport, so it has to be told the width changed.
  grid.scheduleRender();
}

function toggleSidebar() {
  applySidebar(!elements.sidebar.classList.contains('collapsed'));
}

// ---------------------------------------------------------------- menus

function closeMenus() {
  for (const menu of elements.menubar.querySelectorAll('.menu.open')) {
    menu.classList.remove('open');
  }
}

elements.menubar.addEventListener('click', (event) => {
  const title = event.target.closest('.menu-title');
  if (title) {
    const menu = title.parentElement;
    const wasOpen = menu.classList.contains('open');
    closeMenus();
    if (!wasOpen) menu.classList.add('open');
    return;
  }

  const item = event.target.closest('.menu-item');
  if (item) {
    closeMenus();
    if (!item.disabled) runAction(item.dataset.action, item.dataset.path);
  }
});

document.addEventListener('mousedown', (event) => {
  if (!event.target.closest('.menu')) closeMenus();
});

document.addEventListener('keydown', (event) => {
  if (event.key === 'Escape') {
    closeMenus();
    hideContextMenu();
    // Escape leaves find mode, unless the table is being edited (Escape cancels the
    // editor there) or a dialog is up.
    if (search.open && !grid.isEditing() && !modalOpen()) closeFindBar();
  }
});

/**
 * Fills the "Open Recent" submenu. The host owns the list; this only draws it, so the
 * order and the pruning rules live in one place (AppSettings).
 */
function renderRecentFiles(files) {
  const menu = elements.recentMenu;
  if (!menu) return;

  menu.innerHTML = '';
  const entries = Array.isArray(files) ? files : [];

  if (entries.length === 0) {
    const empty = document.createElement('div');
    empty.className = 'menu-item';
    empty.style.color = 'var(--text-faint)';
    empty.textContent = '(no recent files)';
    menu.appendChild(empty);
    return;
  }

  for (const entry of entries) {
    const button = document.createElement('button');
    button.type = 'button';
    button.className = 'menu-item' + (entry.exists ? '' : ' missing');
    button.dataset.action = 'openRecent';
    button.dataset.path = entry.path;
    button.title = entry.path;

    const name = document.createElement('span');
    name.textContent = entry.name;
    const folder = document.createElement('kbd');
    folder.textContent = entry.folder;

    button.appendChild(name);
    button.appendChild(folder);
    menu.appendChild(button);
  }

  const separator = document.createElement('div');
  separator.className = 'menu-sep';
  menu.appendChild(separator);

  const clear = document.createElement('button');
  clear.type = 'button';
  clear.className = 'menu-item';
  clear.dataset.action = 'clearRecent';
  const clearLabel = document.createElement('span');
  clearLabel.textContent = 'Clear Recent Files';
  clear.appendChild(clearLabel);
  menu.appendChild(clear);
}

/** Build details dialog (F09). */
function showAbout(info) {
  openModal({
    title: 'About PalaceDB',
    okLabel: 'Close',
    hideCancel: true,
    build: () => {
      const wrap = document.createElement('div');
      wrap.className = 'about';

      const heading = document.createElement('div');
      heading.className = 'about-title';
      heading.textContent = 'PalaceDB ' + (info.version || '1.0');
      wrap.appendChild(heading);

      const subtitle = document.createElement('div');
      subtitle.className = 'about-subtitle';
      subtitle.textContent = 'Structured static database editor';
      wrap.appendChild(subtitle);

      const rows = [
        ['Runtime', info.runtime],
        ['WebView2', info.webView],
        ['System', info.os],
        ['Format', info.format],
        ['Settings', info.dataFolder],
      ];

      const table = document.createElement('div');
      table.className = 'about-rows';
      for (const [label, value] of rows) {
        if (!value) continue;
        const row = document.createElement('div');
        row.className = 'about-row';

        const key = document.createElement('span');
        key.className = 'about-key';
        key.textContent = label;

        const text = document.createElement('span');
        text.className = 'about-value';
        text.textContent = value;
        text.title = value;

        row.appendChild(key);
        row.appendChild(text);
        table.appendChild(row);
      }
      wrap.appendChild(table);

      return wrap;
    },
    onOk: () => true,
  });
}

// ---------------------------------------------------------------- settings

/**
 * The settings window: interface text size, table text size and colour scheme.
 *
 * Every change is applied as it is made, so the window doubles as the preview — the
 * sample table at the top uses the same variables as the real one. Cancel puts the
 * values that were in force when the window opened back.
 */
function openSettings() {
  const before = { ...state.settings };
  const draft = { ...state.settings };

  openModal({
    title: 'Settings',
    okLabel: 'Done',
    wide: true,
    build: () => buildSettings(draft),
    onOk: () => {
      // Commits through applySettings so the palette also reaches the file's side-car;
      // the sizes are stored globally, the palette travels with the document.
      // The live preview already moved state.settings, hence syncFileScheme: the change
      // is invisible to the "did it differ" check and has to be written explicitly.
      applySettings(draft, { syncFileScheme: true });
      return true;
    },
    // Nothing was committed while the window was open, so cancelling only has to put
    // the values back: no storage, and no side-car write for a palette that was only
    // ever a preview.
    onCancel: () => applySettings(before, { persist: false }),
  });
}

function buildSettings(draft) {
  const wrap = document.createElement('div');
  wrap.className = 'settings';

  // A miniature of the window itself: the bar follows the interface size, the table
  // follows the table size, and both follow the palette.
  const preview = document.createElement('div');
  preview.className = 'settings-preview';
  preview.innerHTML =
    '<div class="sp-bar">'
    + '<span class="sp-brand">PalaceDB</span>'
    + '<span class="sp-tab">items</span>'
    + '<span class="sp-tab sp-tab-active">monsters</span>'
    + '</div>'
    + '<div class="sp-grid">'
    + '<div class="sp-head"><span>id</span><span>name</span><span>price</span></div>'
    + '<div class="sp-row"><span>potion</span><span>Healing Potion</span><span>120</span></div>'
    + '<div class="sp-row sp-cursor"><span>sword</span><span>Iron Sword</span><span>340</span></div>'
    + '</div>';
  wrap.appendChild(preview);

  const sizes = document.createElement('div');
  sizes.className = 'settings-group';
  const uiRow = buildSizeRow({
    label: 'Interface text size',
    hint: 'Menus, toolbar and dialogs',
    range: UI_SIZE,
    get: () => draft.uiFontSize,
    set: (value) => {
      draft.uiFontSize = value;
      applySettings(draft, { persist: false });
    },
  });
  const tableRow = buildSizeRow({
    label: 'Table text size',
    hint: 'Cells, headers and row numbers',
    range: GRID_SIZE,
    get: () => draft.gridFontSize,
    set: (value) => {
      draft.gridFontSize = value;
      applySettings(draft, { persist: false });
    },
  });
  sizes.append(uiRow.node, tableRow.node);
  wrap.appendChild(sizes);

  const schemes = document.createElement('div');
  schemes.className = 'settings-group';
  const title = document.createElement('div');
  title.className = 'settings-group-title';
  title.textContent = 'Colour scheme';
  schemes.appendChild(title);

  const cards = [];
  const grid = document.createElement('div');
  grid.className = 'scheme-grid';
  for (const scheme of SCHEMES) {
    const card = document.createElement('button');
    card.type = 'button';
    card.className = 'scheme-card';
    card.dataset.scheme = scheme.id;
    card.title = `${scheme.name} \u00b7 ${scheme.hint}`;

    const swatches = document.createElement('span');
    swatches.className = 'scheme-swatches';
    for (const color of scheme.swatch) {
      const chip = document.createElement('i');
      chip.style.background = color;
      swatches.appendChild(chip);
    }

    const name = document.createElement('span');
    name.className = 'scheme-name';
    name.textContent = scheme.name;

    card.append(swatches, name);
    card.addEventListener('click', () => {
      draft.scheme = scheme.id;
      applySettings(draft, { persist: false });
      markScheme();
    });

    cards.push(card);
    grid.appendChild(card);
  }
  schemes.appendChild(grid);
  wrap.appendChild(schemes);

  const foot = document.createElement('div');
  foot.className = 'settings-foot';
  const reset = document.createElement('button');
  reset.type = 'button';
  reset.className = 'tool';
  reset.id = 'settingsReset';
  reset.textContent = 'Restore defaults';
  reset.addEventListener('click', () => {
    Object.assign(draft, DEFAULTS);
    applySettings(draft, { persist: false });
    uiRow.sync();
    tableRow.sync();
    markScheme();
  });
  foot.appendChild(reset);
  wrap.appendChild(foot);

  function markScheme() {
    for (const card of cards) {
      const on = card.dataset.scheme === draft.scheme;
      card.classList.toggle('active', on);
      card.setAttribute('aria-pressed', String(on));
    }
  }

  markScheme();
  return wrap;
}

/** One labelled slider with a live pixel readout. */
function buildSizeRow({ label, hint, range, get, set }) {
  const row = document.createElement('div');
  row.className = 'settings-row';

  const text = document.createElement('div');
  text.className = 'settings-label';
  const name = document.createElement('span');
  name.textContent = label;
  const sub = document.createElement('small');
  sub.textContent = hint;
  text.append(name, sub);

  const control = document.createElement('div');
  control.className = 'settings-control';

  const input = document.createElement('input');
  input.type = 'range';
  input.className = 'settings-range';
  input.min = String(range.min);
  input.max = String(range.max);
  input.step = String(range.step);
  input.setAttribute('aria-label', label);

  const value = document.createElement('span');
  value.className = 'settings-value';

  const sync = () => {
    const current = get();
    input.value = String(current);
    value.textContent = `${current} px`;
  };

  input.addEventListener('input', () => {
    set(Number(input.value));
    sync();
  });

  control.append(input, value);
  row.append(text, control);
  sync();
  return { node: row, sync };
}

/**
 * Reports what a three-way merge (F16) did. The merged content is already open and
 * unsaved, so this is a review list rather than a decision: the local value was kept
 * wherever both sides had changed the same thing.
 */
function showMergeReport(report) {
  const conflicts = report.conflicts || [];

  openModal({
    title: conflicts.length ? 'Merged with conflicts' : 'Merge complete',
    okLabel: 'Review',
    hideCancel: true,
    wide: conflicts.length > 0,
    build: () => {
      const wrap = document.createElement('div');
      wrap.className = 'merge-report';

      const lead = document.createElement('p');
      lead.className = 'merge-lead';
      lead.textContent = conflicts.length
        ? `${conflicts.length} value${conflicts.length === 1 ? '' : 's'} changed on both sides. `
          + 'Your local value was kept; the merged file is open and not saved yet.'
        : 'Both versions were combined. The merged file is open and not saved yet.';
      wrap.appendChild(lead);

      if (report.sideFiles && report.sideFiles.length) {
        const note = document.createElement('p');
        note.className = 'merge-note';
        note.textContent = 'Saving removes: ' + report.sideFiles.join(', ');
        wrap.appendChild(note);
      }

      if (!conflicts.length) return wrap;

      const list = document.createElement('div');
      list.className = 'merge-list';
      for (const conflict of conflicts) {
        const row = document.createElement('div');
        row.className = 'merge-row';

        const path = document.createElement('div');
        path.className = 'merge-path';
        path.textContent = conflict.path;
        row.appendChild(path);

        for (const [label, value, cls] of [
          ['Local', conflict.mine, 'mine'],
          ['Incoming', conflict.theirs, 'theirs'],
          ['Base', conflict.base, 'base'],
        ]) {
          const line = document.createElement('div');
          line.className = 'merge-value ' + cls;

          const key = document.createElement('span');
          key.className = 'merge-key';
          key.textContent = label;

          const text = document.createElement('span');
          text.textContent = value;

          line.appendChild(key);
          line.appendChild(text);
          row.appendChild(line);
        }

        list.appendChild(row);
      }

      wrap.appendChild(list);
      return wrap;
    },
    onOk: () => true,
  });
}

function runAction(action, arg) {
  switch (action) {
    case 'newWindow': send('newWindow'); break;
    case 'newDatabase': send('newDatabase'); break;
    case 'open': send('openFile'); break;
    case 'openRecent': if (arg) send('openRecent', { path: arg }); break;
    case 'clearRecent': send('clearRecent'); break;
    case 'about':
      // The build details come from the host; the dialog opens when they arrive.
      pendingAbout = true;
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
    case 'columnOptions':
    case 'renameColumn':
    case 'changeColumnType':
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

// ---------------------------------------------------------------- context menu

const contextMenu = document.getElementById('contextMenu');
const subMenu = document.getElementById('subMenu');

function hideContextMenu() {
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
function showContextMenu(x, y, items, onPick) {
  hideContextMenu();
  buildMenuInto(contextMenu, items, onPick || ((item) => runAction(item.action, item.arg)));

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
// whatever row took that place.
grid.onRowMove = (from, to) => {
  if (!state.currentSheet) return;
  flushPendingEdits();
  closeAllPanels();
  send('moveRow', { sheet: state.currentSheet, from, to });
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

// ---------------------------------------------------------------- modal dialogs

const modal = {
  root: document.getElementById('modal'),
  box: document.querySelector('#modal .modal-box'),
  title: document.getElementById('modalTitle'),
  body: document.getElementById('modalBody'),
  ok: document.getElementById('modalOk'),
  cancel: document.getElementById('modalCancel'),
};

let modalSubmit = null;
let modalCancel = null;

function closeModal() {
  modal.root.hidden = true;
  modalSubmit = null;
  modalCancel = null;
}

/** True while a dialog covers the table. */
function modalOpen() {
  return !modal.root.hidden;
}

/** Closes the dialog through its Cancel path, so it can undo what it applied live. */
function dismissModal() {
  const cancel = modalCancel;
  closeModal();
  if (cancel) cancel();
}

/**
 * Opens a dialog. `build` returns the body node, `onOk` reads it and returns true to
 * close, `onCancel` runs when the dialog is dismissed without OK.
 */
function openModal({ title, build, onOk, onCancel = null, okLabel = 'OK', wide = false, hideCancel = false }) {
  // A dialog takes the keyboard, so an open cell editor is committed first — otherwise it
  // stays behind the modal with a dangling dashed box on the cell.
  if (grid.isEditing()) editor.commit();

  modal.title.textContent = title;
  modal.body.innerHTML = '';
  modal.body.appendChild(build());
  modal.ok.textContent = okLabel;
  modal.cancel.hidden = hideCancel;
  modal.box.classList.toggle('wide', wide);
  modalSubmit = onOk;
  modalCancel = onCancel;
  modal.root.hidden = false;

  // A slider has nothing to select, and selecting a colour input throws in some engines.
  const first = modal.body.querySelector('input:not([type="range"]):not([type="checkbox"]):not([type="color"]), select');
  if (first) {
    first.focus();
    if (typeof first.select === 'function' && first.tagName === 'INPUT') first.select();
  }
}

modal.ok.addEventListener('click', () => {
  // A dialog that only acknowledges something — a report, the About box, the template
  // list — has nothing to submit, and its button still has to close it. Without this,
  // "Close" did nothing at all and the only way out was Cancel, Escape or the backdrop.
  if (!modalSubmit || modalSubmit() !== false) closeModal();
});

modal.cancel.addEventListener('click', dismissModal);

modal.root.addEventListener('mousedown', (event) => {
  if (event.target === modal.root) dismissModal();
});

modal.root.addEventListener('keydown', (event) => {
  if (event.key === 'Escape') {
    event.stopPropagation();
    dismissModal();
  } else if (event.key === 'Enter' && !event.shiftKey) {
    event.preventDefault();
    event.stopPropagation();
    if (modalSubmit && modalSubmit() !== false) closeModal();
  }
});

// ---------------------------------------------------------------- column schema

const COLUMN_TYPES = [
  { value: '1', label: 'Text' },
  { value: '3', label: 'Int' },
  { value: '4', label: 'Float' },
  { value: '2', label: 'Bool' },
  { value: '5', label: 'Enum (fixed list)' },
  { value: '10', label: 'Flags (bit set)' },
  { value: '6', label: 'Reference to a table' },
  { value: '7', label: 'Image path' },
  { value: '13', label: 'File path' },
  { value: '11', label: 'Color' },
  { value: '8', label: 'List of records' },
  { value: '17', label: 'Properties (fixed fields)' },
  { value: '20', label: 'Guid' },
  { value: '22', label: 'Formula' },
  { value: '16', label: 'Dynamic' },
];

// Types a field of a list may have: a list inside a list has no usable editor.
const SUB_TYPES = COLUMN_TYPES.filter((type) => !['8', '17', '21'].includes(type.value));

/** True for the types that are backed by a hidden sub-table. */
function isStructuredType(typeStr) {
  return ['8', '17', '21'].includes(String(typeStr || '').split(':')[0]);
}

/**
 * Splits a stored type string into its numeric id and its payload: "5:A,B" is enum 5 with
 * the values "A,B", "6:Items" a reference to Items. Keeping the two apart is what lets a
 * field keep its enum values while its row is being edited.
 */
function splitTypeStr(typeStr) {
  const text = typeStr === undefined || typeStr === null ? '' : String(typeStr);
  const colon = text.indexOf(':');
  return colon < 0
    ? { id: text, payload: '' }
    : { id: text.slice(0, colon), payload: text.slice(colon + 1) };
}

/** The type string a field of a sub-table is stored as. */
function subTypeString(field) {
  const parts = splitTypeStr(field.type);
  if (parts.id === '5' || parts.id === '10') {
    const values = field.values === undefined ? parts.payload : field.values;
    return parts.id + ':' + values;
  }
  if (parts.id === '6') {
    return '6:' + (field.refSheet === undefined ? parts.payload : field.refSheet);
  }
  return parts.id;
}

/** Builds the small form used by "add column", "rename column" and "change type". */
function buildColumnForm(initial, options = {}) {
  const showName = options.showName !== false;
  const showType = options.showType !== false;
  const showOptions = options.showOptions !== false;
  const typeStr = initial.typeStr || '1';
  const base = typeStr.split(':')[0];
  // What the dialog was opened with, so the enum text can be compared with the original
  // instead of being re-encoded (see the `type` getter below).
  const originalType = base;
  const colon = typeStr.indexOf(':');
  const originalValues = colon < 0 ? '' : typeStr.slice(colon + 1);

  const wrap = document.createElement('div');
  wrap.className = 'form';

  // A column that belongs to a template is not edited on its own, and the dialog has to
  // say so before the user types a new name.
  if (options.note) {
    const note = document.createElement('p');
    note.className = 'form-hint';
    note.textContent = options.note;
    wrap.appendChild(note);
  }

  let nameInput = null;
  if (showName) {
    const row = document.createElement('label');
    row.className = 'form-row';
    const caption = document.createElement('span');
    caption.textContent = 'Name';
    row.appendChild(caption);

    nameInput = document.createElement('input');
    nameInput.type = 'text';
    nameInput.className = 'form-input';
    nameInput.value = initial.name || '';
    row.appendChild(nameInput);
    wrap.appendChild(row);
  }

  let typeSelect = null;
  let valuesRow = null;
  let valuesInput = null;
  let valuesHint = null;
  let refRow = null;
  let refSelect = null;
  let optionalInput = null;
  let defaultInput = null;
  let displaySelect = null;
  let displayRow = null;
  let localizableInput = null;
  let localizableRow = null;
  let subRow = null;
  let subRowsBox = null;

  // A List or Properties column is backed by a hidden sub-table; its fields are edited
  // here rather than in a second dialog. `id` keeps the original name so renaming a
  // field carries its values over instead of looking like remove + add, and `type` keeps
  // the whole stored string so an enum's values survive the round trip — splitting the
  // payload off here is what used to flatten every sub-field enum to "5:".
  const subState = (initial.sub || []).map((field) => {
    const typeStr = String(field.type || field.typeStr || '1');
    return {
      id: field.id || field.name || '',
      name: field.name || '',
      type: typeStr,
    };
  });

  if (showType) {
    const row = document.createElement('label');
    row.className = 'form-row';
    const caption = document.createElement('span');
    caption.textContent = 'Type';
    row.appendChild(caption);

    typeSelect = document.createElement('select');
    typeSelect.className = 'form-input';
    for (const type of COLUMN_TYPES) {
      const option = document.createElement('option');
      option.value = type.value;
      option.textContent = type.label;
      typeSelect.appendChild(option);
    }
    row.appendChild(typeSelect);
    wrap.appendChild(row);

    valuesRow = document.createElement('label');
    valuesRow.className = 'form-row';
    const valuesCaption = document.createElement('span');
    valuesCaption.textContent = 'Values';
    valuesRow.appendChild(valuesCaption);

    valuesInput = document.createElement('input');
    valuesInput.type = 'text';
    valuesInput.className = 'form-input';
    valuesInput.placeholder = 'One, Two, Three';
    valuesInput.value = originalValues;
    valuesRow.appendChild(valuesInput);
    wrap.appendChild(valuesRow);

    // An entry may be left blank on purpose: CastleDB writes "5:,A,B" for an enum whose
    // first value has no label, and the blank has to stay a value of its own.
    valuesHint = document.createElement('p');
    valuesHint.className = 'form-hint values-hint';
    valuesHint.textContent =
      'Leave an entry blank for a value with no label: ",1,2" keeps an unnamed first value.';
    wrap.appendChild(valuesHint);

    refRow = document.createElement('label');
    refRow.className = 'form-row';
    const refCaption = document.createElement('span');
    refCaption.textContent = 'Table';
    refRow.appendChild(refCaption);

    refSelect = document.createElement('select');
    refSelect.className = 'form-input';
    const sheets = state.document ? state.document.sheets : [];
    for (const sheet of sheets) {
      const option = document.createElement('option');
      option.value = sheet.name;
      option.textContent = sheet.name;
      refSelect.appendChild(option);
    }
    if (typeStr.split(':')[1]) refSelect.value = typeStr.split(':')[1];
    refRow.appendChild(refSelect);
    wrap.appendChild(refRow);

    subRow = document.createElement('div');
    subRow.className = 'form-sub';

    const subHead = document.createElement('div');
    subHead.className = 'form-sub-head';
    const subCaption = document.createElement('span');
    subCaption.textContent = 'Fields';
    subHead.appendChild(subCaption);

    const addField = document.createElement('button');
    addField.type = 'button';
    addField.className = 'tool';
    addField.textContent = '+ Add field';
    addField.addEventListener('click', () => {
      subState.push({ name: '', type: '1' });
      renderSubRows();
    });
    subHead.appendChild(addField);
    subRow.appendChild(subHead);

    subRowsBox = document.createElement('div');
    subRowsBox.className = 'form-sub-rows';
    subRow.appendChild(subRowsBox);
    wrap.appendChild(subRow);

    function renderSubRows() {
      subRowsBox.innerHTML = '';

      if (subState.length === 0) {
        const empty = document.createElement('p');
        empty.className = 'form-sub-empty';
        empty.textContent = 'No fields yet — add at least one.';
        subRowsBox.appendChild(empty);
        return;
      }

      subState.forEach((field, index) => {
        const line = document.createElement('div');
        line.className = 'form-sub-row';

        const nameInput = document.createElement('input');
        nameInput.type = 'text';
        nameInput.className = 'form-input';
        nameInput.placeholder = 'field';
        nameInput.value = field.name;
        nameInput.addEventListener('input', () => { field.name = nameInput.value; });
        line.appendChild(nameInput);

        // The stored type is "id" or "id:payload" (an enum's values, a reference's table).
        // Keeping the two apart is what lets an enum field keep its values through a
        // round trip of this dialog instead of being flattened to an empty enum.
        const parts = splitTypeStr(field.type);

        const fieldType = document.createElement('select');
        fieldType.className = 'form-input';
        for (const type of SUB_TYPES) {
          const option = document.createElement('option');
          option.value = type.value;
          option.textContent = type.label;
          fieldType.appendChild(option);
        }
        fieldType.value = SUB_TYPES.some((type) => type.value === parts.id) ? parts.id : '1';
        fieldType.addEventListener('change', () => {
          field.type = fieldType.value;
          renderSubRows();
        });
        line.appendChild(fieldType);

        // The values of an enum or a flag get a line of their own: the row already holds a
        // name, a type and three buttons, and a list of words squeezed between them was
        // unreadable.
        let valuesLine = null;
        if (parts.id === '5' || parts.id === '10') {
          valuesLine = document.createElement('div');
          valuesLine.className = 'form-sub-values';

          const caption = document.createElement('span');
          caption.textContent = 'Values';
          valuesLine.appendChild(caption);

          const values = document.createElement('input');
          values.type = 'text';
          values.className = 'form-input sub-values';
          values.placeholder = 'One, Two, Three';
          values.title = 'The values this field offers, separated by commas';
          values.value = field.values === undefined ? parts.payload : field.values;
          values.addEventListener('input', () => { field.values = values.value; });
          valuesLine.appendChild(values);
        }

        if (parts.id === '6') {
          const targetSelect = document.createElement('select');
          targetSelect.className = 'form-input';
          const sheets = state.document ? state.document.sheets : [];
          for (const sheet of sheets) {
            const option = document.createElement('option');
            option.value = sheet.name;
            option.textContent = sheet.name;
            targetSelect.appendChild(option);
          }
          if (!field.refSheet) field.refSheet = parts.payload || (sheets[0] ? sheets[0].name : '');
          targetSelect.value = field.refSheet;
          targetSelect.addEventListener('change', () => { field.refSheet = targetSelect.value; });
          line.appendChild(targetSelect);
        }

        const remove = document.createElement('button');
        remove.type = 'button';
        remove.className = 'tool sub-remove';
        remove.textContent = '\u00d7';
        remove.title = 'Remove this field';
        remove.addEventListener('click', () => {
          subState.splice(index, 1);
          renderSubRows();
        });

        // Fields are stored in order, and the order is what the sub-table shows: moving
        // one up or down is a real edit, not just a display choice.
        const up = document.createElement('button');
        up.type = 'button';
        up.className = 'tool sub-remove';
        up.textContent = '\u2191';
        up.title = 'Move this field up';
        up.disabled = index === 0;
        up.addEventListener('click', () => moveField(index, -1));
        line.appendChild(up);

        const down = document.createElement('button');
        down.type = 'button';
        down.className = 'tool sub-remove';
        down.textContent = '\u2193';
        down.title = 'Move this field down';
        down.disabled = index === subState.length - 1;
        down.addEventListener('click', () => moveField(index, 1));
        line.appendChild(down);

        line.appendChild(remove);

        subRowsBox.appendChild(line);
        if (valuesLine) subRowsBox.appendChild(valuesLine);
      });
    }

    function moveField(index, delta) {
      const target = index + delta;
      if (target < 0 || target >= subState.length) return;
      const [field] = subState.splice(index, 1);
      subState.splice(target, 0, field);
      renderSubRows();
    }

    renderSubRows();

    typeSelect.value = COLUMN_TYPES.some((t) => t.value === base) ? base : '1';

    const sync = () => {
      const value = typeSelect.value;
      valuesRow.style.display = value === '5' || value === '10' ? '' : 'none';
      valuesHint.style.display = value === '5' || value === '10' ? '' : 'none';
      refRow.style.display = value === '6' ? '' : 'none';
      subRow.hidden = !isStructuredType(value);
      syncOptions(value);
    };
    typeSelect.addEventListener('change', sync);
    sync();
  }

  // ---- optional fields (C01 / C02) ----

  // Display format only applies to numbers, and the localizable flag to text; the rows
  // are shown and hidden with the type rather than rejected after the fact.
  const currentType = () => (typeSelect ? typeSelect.value : base);

  function syncOptions(value) {
    if (displayRow) displayRow.hidden = !(value === '3' || value === '4');
    if (localizableRow) localizableRow.hidden = !(value === '1' || value === '0');
    if (defaultInput) {
      defaultInput.placeholder = defaultPlaceholder(value);
    }
  }

  if (showOptions) {
    const optionalRow = document.createElement('label');
    optionalRow.className = 'form-row form-check';

    optionalInput = document.createElement('input');
    optionalInput.type = 'checkbox';
    optionalInput.checked = Boolean(initial.optional);
    optionalRow.appendChild(optionalInput);

    const optionalText = document.createElement('span');
    optionalText.textContent = 'Optional — a row may leave this column out';
    optionalRow.appendChild(optionalText);
    wrap.appendChild(optionalRow);

    const defaultRow = document.createElement('label');
    defaultRow.className = 'form-row';
    const defaultCaption = document.createElement('span');
    defaultCaption.textContent = 'Default';
    defaultRow.appendChild(defaultCaption);

    defaultInput = document.createElement('input');
    defaultInput.type = 'text';
    defaultInput.className = 'form-input';
    defaultInput.value = initial.defaultValue == null ? '' : String(initial.defaultValue);
    defaultRow.appendChild(defaultInput);
    wrap.appendChild(defaultRow);

    displayRow = document.createElement('label');
    displayRow.className = 'form-row';
    const displayCaption = document.createElement('span');
    displayCaption.textContent = 'Format';
    displayRow.appendChild(displayCaption);

    displaySelect = document.createElement('select');
    displaySelect.className = 'form-input';
    for (const [value, label] of [['default', 'Plain number'], ['percent', 'Percent (0.5 → 50%)']]) {
      const option = document.createElement('option');
      option.value = value;
      option.textContent = label;
      displaySelect.appendChild(option);
    }
    displaySelect.value = initial.display === 'percent' ? 'percent' : 'default';
    displayRow.appendChild(displaySelect);
    wrap.appendChild(displayRow);

    localizableRow = document.createElement('label');
    localizableRow.className = 'form-row form-check';

    localizableInput = document.createElement('input');
    localizableInput.type = 'checkbox';
    localizableInput.checked = Boolean(initial.localizable);
    localizableRow.appendChild(localizableInput);

    const localizableText = document.createElement('span');
    localizableText.textContent = 'Localizable — collected by the text export';
    localizableRow.appendChild(localizableText);
    wrap.appendChild(localizableRow);

    syncOptions(currentType());
  }

  return {
    node: wrap,
    name: () => (nameInput ? nameInput.value.trim() : ''),
    optional: () => (optionalInput ? optionalInput.checked : Boolean(initial.optional)),
    defaultValue: () => (defaultInput ? defaultInput.value.trim() : ''),
    display: () => (displaySelect ? displaySelect.value : 'default'),
    sub: () => subState
      .map((field) => ({
        id: field.id || '',
        name: field.name.trim(),
        type: subTypeString(field),
      }))
      .filter((field) => field.name.length > 0),
    localizable: () => (localizableInput ? localizableInput.checked : false),
    type: () => {
      if (!typeSelect) return typeStr;
      const value = typeSelect.value;
      if (value === '5' || value === '10') {
        // The text is only re-encoded when it was actually edited. Round-tripping an
        // untouched list would trim the spaces and drop the empty first value CastleDB
        // uses for "none", which silently moves every stored index onto another label.
        if (value === originalType && valuesInput.value === originalValues) return typeStr;

        // Empty entries are kept for the same reason: "5:,A,B" is an enum whose first
        // value is empty, not an enum with two values.
        const list = valuesInput.value.split(',').map((s) => s.trim());
        // Turning a column into an enum without typing any value is a mistake, not an
        // enum with a single empty value.
        if (value !== originalType && list.every((item) => item.length === 0)) return null;
        return `${value}:${list.join(',')}`;
      }
      if (value === '6') return refSelect.value ? `${value}:${refSelect.value}` : null;
      return value;
    },
  };
}

/** Hint shown in the default-value field, so the expected text format is obvious. */
function defaultPlaceholder(type) {
  switch (type) {
    case '2': return 'true / false';
    case '3': return '0';
    case '4': return '0.5';
    case '5':
    case '10': return 'One of the values above';
    case '6': return 'Row id';
    case '11': return '#RRGGBB';
    case '8':
    case '17':
    case '21': return '[ ] or { } as JSON';
    default: return 'Text given to new rows';
  }
}

function currentColumn() {
  return grid.columns[grid.cursor.col] || null;
}

/** Warning line for a column dialog, when the table follows a template. */
function templateNote(sheetName) {
  const sheet = state.document && state.document.sheets.find((s) => s.name === sheetName);
  if (!sheet || !sheet.template) return '';

  const others = Math.max(0, (sheet.templateMembers || 1) - 1);
  if (others === 0) return '';

  return `This table follows the template '${sheet.templateName}' (${others + 1} tables): ` +
    'a change to a column is applied to every one of them.';
}

function promptAddColumn() {
  if (!state.currentSheet) return;

  let form = null;
  openModal({
    title: 'Add column',
    okLabel: 'Add',
    build: () => (form = buildColumnForm(
      { name: '', typeStr: '1' },
      { note: templateNote(state.currentSheet) })).node,
    onOk: () => {
      const name = form.name();
      if (!name) {
        toast('The column needs a name.', 'error');
        return false;
      }
      const type = form.type();
      if (!type) {
        toast('Choose the values or the referenced table first.', 'error');
        return false;
      }
      const sub = form.sub();
      if (isStructuredType(type) && sub.length === 0) {
        toast('A list needs at least one field.', 'error');
        return false;
      }
      send('addColumn', {
        sheet: state.currentSheet,
        index: grid.cursor.col + 1,
        name,
        type,
        optional: form.optional(),
        default: form.defaultValue(),
        display: form.display(),
        localizable: form.localizable(),
        sub,
      });
      return true;
    },
  });
}

/**
 * The one dialog for an existing column: name, type, field list and the optional parts
 * (required/optional, default, format, localizable) in a single form that looks like the
 * one used to add a column. The host applies the whole edit as one undo step.
 */
function promptEditColumn(columnOverride, sheetOverride) {
  const column = columnOverride || currentColumn();
  const sheetName = sheetOverride || state.currentSheet;
  if (!column || !sheetName) return;

  // A List or Properties column already has a hidden sub-table; show its fields.
  const subSheetName = `${sheetName}@${column.name}`;
  const subSheets = (state.document && state.document.subSheets) || [];
  const existing = subSheets.find((sheet) => sheet.name === subSheetName);
  const initialSub = existing
    ? existing.columns.map((field) => ({ id: field.name, name: field.name, type: field.typeStr }))
    : [];

  let form = null;
  openModal({
    title: `Edit column '${column.name}'`,
    okLabel: 'Apply',
    build: () => (form = buildColumnForm({
      name: column.name,
      typeStr: column.typeStr,
      optional: column.optional,
      defaultValue: column.defaultValue,
      display: column.display,
      localizable: column.localizable,
      sub: initialSub,
    }, { note: templateNote(sheetName) })).node,
    onOk: () => {
      const name = form.name();
      if (!name) {
        toast('The column needs a name.', 'error');
        return false;
      }

      const type = form.type();
      if (!type) {
        toast('Choose the values or the referenced table first.', 'error');
        return false;
      }

      const sub = form.sub();
      if (isStructuredType(type) && sub.length === 0) {
        toast('A list needs at least one field.', 'error');
        return false;
      }

      send('editColumn', {
        sheet: sheetName,
        column: column.name,
        name,
        type,
        optional: form.optional(),
        default: form.defaultValue(),
        display: form.display(),
        localizable: form.localizable(),
        sub,
      });
      return true;
    },
  });
}

/**
 * Edits one field of a List / Properties sub-table, from the panel's header menu.
 *
 * A sub-field is a column of the hidden table, so the whole field list is what gets sent
 * back; `index` says where the edited (or inserted) one belongs. This dialog is also the
 * only place a sub-field's enum or flag values can be given a list of their own.
 */
function promptEditSubField({ sheet, row, column, fields, index, isNew }) {
  const list = fields.map((field) => ({ name: field.name, type: field.type }));
  const initial = isNew ? { name: '', type: '1' } : list[index];
  if (!initial) return;

  // Inserting keeps every field; editing replaces the one at `index`.
  const others = isNew ? list.slice() : list.filter((_, i) => i !== index);

  let nameInput = null;
  let typeSelect = null;
  let valuesInput = null;
  let valuesRow = null;
  let tableSelect = null;
  let tableRow = null;

  openModal({
    title: isNew ? `Insert a column in '${column}'` : `Edit column '${initial.name}'`,
    okLabel: isNew ? 'Insert' : 'Apply',
    build: () => {
      const wrap = document.createElement('div');
      wrap.className = 'form';
      const parts = splitTypeStr(initial.type);

      const nameRow = document.createElement('label');
      nameRow.className = 'form-row';
      const nameCaption = document.createElement('span');
      nameCaption.textContent = 'Name';
      nameRow.appendChild(nameCaption);
      nameInput = document.createElement('input');
      nameInput.type = 'text';
      nameInput.className = 'form-input';
      nameInput.placeholder = 'field';
      nameInput.value = initial.name;
      nameRow.appendChild(nameInput);
      wrap.appendChild(nameRow);

      const typeRow = document.createElement('label');
      typeRow.className = 'form-row';
      const typeCaption = document.createElement('span');
      typeCaption.textContent = 'Type';
      typeRow.appendChild(typeCaption);
      typeSelect = document.createElement('select');
      typeSelect.className = 'form-input';
      for (const type of SUB_TYPES) {
        const option = document.createElement('option');
        option.value = type.value;
        option.textContent = type.label;
        typeSelect.appendChild(option);
      }
      typeSelect.value = SUB_TYPES.some((type) => type.value === parts.id) ? parts.id : '1';
      typeRow.appendChild(typeSelect);
      wrap.appendChild(typeRow);

      valuesRow = document.createElement('label');
      valuesRow.className = 'form-row';
      const valuesCaption = document.createElement('span');
      valuesCaption.textContent = 'Values';
      valuesRow.appendChild(valuesCaption);
      valuesInput = document.createElement('input');
      valuesInput.type = 'text';
      valuesInput.className = 'form-input';
      valuesInput.placeholder = 'One, Two, Three';
      valuesInput.value = parts.payload;
      valuesRow.appendChild(valuesInput);
      wrap.appendChild(valuesRow);

      const valuesHint = document.createElement('p');
      valuesHint.className = 'form-hint values-hint';
      valuesHint.textContent =
        'Leave an entry blank for a value with no label: ",1,2" keeps an unnamed first value.';
      wrap.appendChild(valuesHint);

      tableRow = document.createElement('label');
      tableRow.className = 'form-row';
      const tableCaption = document.createElement('span');
      tableCaption.textContent = 'Table';
      tableRow.appendChild(tableCaption);
      tableSelect = document.createElement('select');
      tableSelect.className = 'form-input';
      const sheets = state.document ? state.document.sheets : [];
      for (const sheet of sheets) {
        const option = document.createElement('option');
        option.value = sheet.name;
        option.textContent = sheet.name;
        tableSelect.appendChild(option);
      }
      if (parts.payload) tableSelect.value = parts.payload;
      tableRow.appendChild(tableSelect);
      wrap.appendChild(tableRow);

      const sync = () => {
        const id = typeSelect.value;
        const values = id === '5' || id === '10';
        valuesRow.hidden = !values;
        valuesHint.hidden = !values;
        tableRow.hidden = id !== '6';
      };
      typeSelect.addEventListener('change', sync);
      sync();

      return wrap;
    },
    onOk: () => {
      const name = nameInput.value.trim();
      if (!name) {
        toast('The column needs a name.', 'error');
        return false;
      }
      if (others.some((field) => field.name === name)) {
        toast(`'${name}' is already a field of this list.`, 'error');
        return false;
      }

      const id = typeSelect.value;
      let type = id;
      if (id === '5' || id === '10') type = id + ':' + valuesInput.value;
      else if (id === '6') type = '6:' + (tableSelect.value || '');

      const next = others.slice();
      next.splice(Math.max(0, Math.min(index, next.length)), 0, { name, type });
      send('setSubFields', { sheet, row, column, fields: next });
      return true;
    },
  });
}

function confirmDeleteColumn() {  const column = currentColumn();
  if (!column) return;

  openModal({
    title: `Delete column '${column.name}'?`,
    okLabel: 'Delete',
    build: () => {
      const message = document.createElement('p');
      message.style.margin = '0';
      message.textContent =
        'The column and all of its values will be removed. This can be undone with Ctrl+Z.';
      return message;
    },
    onOk: () => {
      send('deleteColumn', { sheet: state.currentSheet, column: column.name });
      return true;
    },
  });
}

function moveColumn(delta) {
  const column = currentColumn();
  if (!column || !state.currentSheet) return;
  send('moveColumn', { sheet: state.currentSheet, column: column.name, delta });
}

// ---------------------------------------------------------------- tables

/** Small single-field dialog used for table names. */
function promptText({ title, label, value, okLabel, onOk }) {
  let input = null;

  openModal({
    title,
    okLabel,
    build: () => {
      const wrap = document.createElement('div');
      wrap.className = 'form';

      const row = document.createElement('label');
      row.className = 'form-row';

      const caption = document.createElement('span');
      caption.textContent = label;
      row.appendChild(caption);

      input = document.createElement('input');
      input.type = 'text';
      input.className = 'form-input';
      input.value = value || '';
      row.appendChild(input);

      wrap.appendChild(row);
      return wrap;
    },
    onOk: () => {
      const text = input.value.trim();
      if (!text) {
        toast('A name is required.', 'error');
        return false;
      }
      onOk(text);
      return true;
    },
  });
}

function promptAddTable() {
  const templates = (state.document && state.document.templates) || [];
  let nameInput = null;
  let templateSelect = null;

  openModal({
    title: 'Add table',
    okLabel: 'Add',
    build: () => {
      const wrap = document.createElement('div');
      wrap.className = 'form';

      const nameRow = document.createElement('label');
      nameRow.className = 'form-row';
      const nameCaption = document.createElement('span');
      nameCaption.textContent = 'Name';
      nameRow.appendChild(nameCaption);
      nameInput = document.createElement('input');
      nameInput.type = 'text';
      nameInput.className = 'form-input';
      nameRow.appendChild(nameInput);
      wrap.appendChild(nameRow);

      if (templates.length > 0) {
        const templateRow = document.createElement('label');
        templateRow.className = 'form-row';
        const templateCaption = document.createElement('span');
        templateCaption.textContent = 'Template';
        templateRow.appendChild(templateCaption);

        templateSelect = document.createElement('select');
        templateSelect.className = 'form-input';
        templateSelect.appendChild(new Option('None', ''));
        for (const template of templates) {
          templateSelect.appendChild(new Option(`${template.name} (${template.columns} columns)`, template.id));
        }
        templateRow.appendChild(templateSelect);
        wrap.appendChild(templateRow);

        const hint = document.createElement('p');
        hint.className = 'form-hint';
        hint.textContent =
          'A table created from a template keeps its columns in step with the other tables that use it.';
        wrap.appendChild(hint);
      }

      return wrap;
    },
    onOk: () => {
      const name = nameInput.value.trim();
      if (!name) {
        toast('A name is required.', 'error');
        return false;
      }
      const payload = { name };
      if (templateSelect && templateSelect.value) payload.template = templateSelect.value;
      send('addSheet', payload);
      return true;
    },
  });
}

function confirmDeleteTable() {
  if (!state.currentSheet) return;
  const name = state.currentSheet;

  openModal({
    title: `Delete table '${name}'?`,
    okLabel: 'Delete',
    build: () => {
      const message = document.createElement('p');
      message.style.margin = '0';
      message.textContent =
        'The table and all of its rows will be removed. Reference columns that point at ' +
        'it become text columns. This can be undone with Ctrl+Z.';
      return message;
    },
    onOk: () => {
      send('deleteSheet', { sheet: name });
      return true;
    },
  });
}

// ---------------------------------------------------------------- table templates

/**
 * Table settings: the one place where a table joins or leaves a template.
 * <para>
 * Applying is checked by the host before anything changes — a table that has columns the
 * template does not declare is refused and the reason is shown, instead of half of the
 * template being applied.
 * </para>
 */
function promptTableSettings() {
  const sheet = currentSheetData();
  if (!sheet) return;

  const templates = (state.document && state.document.templates) || [];
  let nameInput = null;
  let select = null;

  openModal({
    title: `Table settings: ${sheet.name}`,
    okLabel: 'Apply',
    build: () => {
      const wrap = document.createElement('div');
      wrap.className = 'form';

      // The name lives here rather than in a menu of its own: renaming and re-homing a
      // table are the same job, and doing them in one dialog avoids two round trips.
      const nameRow = document.createElement('label');
      nameRow.className = 'form-row';
      const nameCaption = document.createElement('span');
      nameCaption.textContent = 'Name';
      nameRow.appendChild(nameCaption);

      nameInput = document.createElement('input');
      nameInput.type = 'text';
      nameInput.className = 'form-input';
      nameInput.value = sheet.name;
      nameRow.appendChild(nameInput);
      wrap.appendChild(nameRow);

      const summary = document.createElement('p');
      summary.className = 'form-hint';
      summary.textContent = `${sheet.columns.length} columns · ${sheet.rowCount} rows`;
      wrap.appendChild(summary);

      const row = document.createElement('label');
      row.className = 'form-row';
      const caption = document.createElement('span');
      caption.textContent = 'Template';
      row.appendChild(caption);

      select = document.createElement('select');
      select.className = 'form-input';
      select.appendChild(new Option('None (stand alone)', ''));
      for (const template of templates) {
        select.appendChild(new Option(`${template.name} (${template.columns} columns)`, template.id));
      }
      select.value = sheet.template || '';
      row.appendChild(select);
      wrap.appendChild(row);

      const hint = document.createElement('p');
      hint.className = 'form-hint';
      hint.textContent =
        'Applying a template adds the columns this table is missing and puts them in the ' +
        'template order. It never deletes a column: a table with extra columns cannot join ' +
        'until they are removed or added to the template.';
      wrap.appendChild(hint);

      const actions = document.createElement('div');
      actions.className = 'form-actions';
      const save = document.createElement('button');
      save.type = 'button';
      save.className = 'tool';
      save.textContent = 'Save these columns as a new template...';
      save.addEventListener('click', () => {
        closeModal();
        promptTemplateName(sheet);
      });
      actions.appendChild(save);
      wrap.appendChild(actions);

      return wrap;
    },
    onOk: () => {
      const name = nameInput.value.trim();
      if (!name) {
        toast('A table needs a name.', 'error');
        return false;
      }

      // The rename goes first: everything after it addresses the table by its new name.
      if (name !== sheet.name) send('renameSheet', { sheet: sheet.name, name });

      const chosen = select.value;
      const current = sheet.template || '';
      if (chosen === current) return true;

      if (!chosen) {
        send('detachTemplate', { sheet: name });
        return true;
      }

      send('applyTemplate', { sheet: name, template: chosen });
      return true;
    },
  });
}

/** Names a template made from the columns of the given table. */
function promptTemplateName(sheet) {
  let input = null;

  openModal({
    title: `Save '${sheet.name}' as a template`,
    okLabel: 'Save',
    build: () => {
      const wrap = document.createElement('div');
      wrap.className = 'form';

      const row = document.createElement('label');
      row.className = 'form-row';
      const caption = document.createElement('span');
      caption.textContent = 'Name';
      row.appendChild(caption);

      input = document.createElement('input');
      input.type = 'text';
      input.className = 'form-input';
      input.value = sheet.name;
      row.appendChild(input);
      wrap.appendChild(row);

      const hint = document.createElement('p');
      hint.className = 'form-hint';
      hint.textContent = `The ${sheet.columns.length} columns of '${sheet.name}' become the template, and the table starts following it.`;
      wrap.appendChild(hint);

      return wrap;
    },
    onOk: () => {
      const name = input.value.trim();
      if (!name) {
        toast('A template needs a name.', 'error');
        return false;
      }
      send('saveTemplate', { sheet: sheet.name, name });
      return true;
    },
  });
}

/**
 * The second confirmation for a column edit that reaches other tables. The numbers come
 * from the host, which measured them on the document as it is now; the dialog only shows
 * them and sends the prepared payload back.
 */
function showColumnImpact(payload) {
  const deleting = payload.operation === 'delete';

  openModal({
    title: deleting
      ? `Delete '${payload.column}' from every table?`
      : `Convert '${payload.column}' in every table?`,
    okLabel: deleting ? 'Delete anyway' : 'Convert anyway',
    build: () => {
      const wrap = document.createElement('div');

      const intro = document.createElement('p');
      intro.textContent = deleting
        ? `'${payload.column}' is shared by ${payload.tables.length} tables. Deleting it here deletes it everywhere:`
        : `'${payload.column}' is shared by ${payload.tables.length} tables. These values cannot be carried over:`;
      wrap.appendChild(intro);

      const list = document.createElement('div');
      list.className = 'impact-list';
      for (const table of payload.tables) {
        const line = document.createElement('div');
        line.className = 'impact-row';

        const name = document.createElement('span');
        name.textContent = table.table;
        line.appendChild(name);

        const count = document.createElement('span');
        count.className = 'impact-count';
        count.textContent = `${table.values} of ${table.rows} rows`;
        line.appendChild(count);

        list.appendChild(line);
      }
      wrap.appendChild(list);

      const total = document.createElement('p');
      total.className = 'impact-total';
      total.textContent =
        `${payload.total} value${payload.total === 1 ? '' : 's'} will be ` +
        `${deleting ? 'deleted' : 'reset to the type default'}. Ctrl+Z undoes the whole change.`;
      wrap.appendChild(total);

      return wrap;
    },
    onOk: () => {
      send(payload.action, payload.retry);
      return true;
    },
  });
}

/** Explains why a table cannot take a template, and offers the one fix that is safe. */
function showTemplateReport(report) {
  openModal({
    title: `'${report.sheet}' cannot follow '${report.templateName}'`,
    okLabel: 'Close',
    hideCancel: true,
    wide: true,
    build: () => {
      const wrap = document.createElement('div');

      const intro = document.createElement('p');
      intro.textContent = 'Nothing was changed. These have to be settled first:';
      wrap.appendChild(intro);

      const list = document.createElement('div');
      list.className = 'impact-list';
      for (const conflict of report.conflicts) {
        // One sentence per conflict: the message already names the column, and a long
        // explanation reads better across the width than squeezed into a right-hand cell.
        const line = document.createElement('div');
        line.className = 'impact-row';
        const detail = document.createElement('span');
        detail.className = 'impact-detail';
        detail.textContent = conflict.detail;
        line.appendChild(detail);
        list.appendChild(line);
      }
      wrap.appendChild(list);

      if (report.canExtend) {
        const note = document.createElement('p');
        note.className = 'form-hint';
        note.textContent =
          'All of the differences are columns this table has and the template does not. ' +
          'Adding them to the template keeps the data and gives every table that follows it the same columns.';
        wrap.appendChild(note);

        const actions = document.createElement('div');
        actions.className = 'form-actions';
        const extend = document.createElement('button');
        extend.type = 'button';
        extend.className = 'tool primary';
        extend.textContent = 'Add these columns to the template';
        extend.addEventListener('click', () => {
          closeModal();
          send('extendTemplate', { sheet: report.sheet, template: report.template });
        });
        actions.appendChild(extend);
        wrap.appendChild(actions);
      }

      return wrap;
    },
  });
}

/** Lists the templates of the file so they can be renamed or removed. */
function promptTemplates() {
  openModal({
    title: 'Templates',
    okLabel: 'Close',
    hideCancel: true,
    wide: true,
    build: () => {
      const wrap = document.createElement('div');
      wrap.className = 'form';

      const templates = (state.document && state.document.templates) || [];
      if (templates.length === 0) {
        const empty = document.createElement('p');
        empty.className = 'form-hint';
        empty.textContent =
          'This file has no templates yet. Open a table, choose Table Settings, and save ' +
          'its columns as one.';
        wrap.appendChild(empty);
        return wrap;
      }

      for (const template of templates) {
        const row = document.createElement('div');
        row.className = 'template-row';

        const input = document.createElement('input');
        input.type = 'text';
        input.className = 'form-input';
        input.value = template.name;
        input.title = 'Rename this template';
        input.addEventListener('change', () => {
          const name = input.value.trim();
          if (!name) {
            input.value = template.name;
            return;
          }
          send('renameTemplate', { template: template.id, name });
        });
        row.appendChild(input);

        const meta = document.createElement('span');
        meta.className = 'template-meta';
        meta.textContent = `${template.columns} columns · ${template.members} tables`;
        row.appendChild(meta);

        const remove = document.createElement('button');
        remove.type = 'button';
        remove.className = 'tool';
        remove.textContent = 'Delete';
        let armed = false;
        remove.addEventListener('click', () => {
          if (!armed) {
            armed = true;
            remove.textContent = 'Delete?';
            remove.classList.add('danger');
            return;
          }
          send('deleteTemplate', { template: template.id });
          row.remove();
        });
        row.appendChild(remove);

        wrap.appendChild(row);
      }

      const hint = document.createElement('p');
      hint.className = 'form-hint';
      hint.textContent = 'Deleting a template leaves its tables untouched: they keep their columns and stop following it.';
      wrap.appendChild(hint);

      return wrap;
    },
  });
}

// ---------------------------------------------------------------- separators

/** Renames the separator in front of the cursor row. */
function promptSeparatorTitle(targetRow) {
  if (!state.currentSheet) return;

  // The row menu names the row it was opened on; the keyboard path uses the cursor.
  const row = Number.isFinite(targetRow) ? targetRow : grid.cursor.row;
  const current = grid.separatorTitles.get(row) || '';

  promptText({
    title: 'Name separator',
    label: 'Title',
    value: current,
    okLabel: 'Apply',
    onOk: (title) => send('setSeparatorTitle', { sheet: state.currentSheet, row, title }),
  });
}

// ---------------------------------------------------------------- display column

/**
 * Chooses the column used to label the rows of a referenced table. The property is
 * stored on the target table, so the dialog edits that table rather than the column.
 */
function promptDisplayColumn() {
  const column = currentColumn();
  if (!column || column.type !== 'Ref' || !column.refSheet || !state.document) return;

  const target = state.document.sheets.find((sheet) => sheet.name === column.refSheet);
  if (!target) return;

  let select = null;

  openModal({
    title: `Display column of '${target.name}'`,
    okLabel: 'Apply',
    build: () => {
      const wrap = document.createElement('div');
      wrap.className = 'form';

      const row = document.createElement('label');
      row.className = 'form-row';

      const caption = document.createElement('span');
      caption.textContent = 'Column';
      row.appendChild(caption);

      select = document.createElement('select');
      select.className = 'form-input';

      const idOption = document.createElement('option');
      idOption.value = '';
      idOption.textContent = '(identifier)';
      select.appendChild(idOption);

      for (const sub of target.columns) {
        if (sub.type === 'List' || sub.type === 'Properties') continue;
        const option = document.createElement('option');
        option.value = sub.name;
        option.textContent = sub.name;
        select.appendChild(option);
      }

      if (target.displayColumn) select.value = target.displayColumn;

      row.appendChild(select);
      wrap.appendChild(row);
      return wrap;
    },
    onOk: () => {
      send('setDisplayColumn', { sheet: target.name, column: select.value });
      return true;
    },
  });
}

// ---------------------------------------------------------------- list / properties

/**
 * The sub-table of a List / Properties cell, opened underneath its row.
 *
 * It is a real grid — the same class as the table above — rather than a dialog or a form
 * of inputs: it has a header, a gutter, typed cells, keyboard navigation and its own
 * editors, and it writes back while you type. Each cell keeps its own panel, so several
 * lists can be open at once, including two lists of the same row.
 */
function showSubValueEditor(payload) {
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

/** Closes one sub-table, writing anything it still holds back. */
function closePanel(id) {
  const panel = subPanels.get(id);
  if (!panel) return;
  subPanels.delete(id);
  panel.destroy();
  grid.removePanel(id);
}

/** Closes every sub-table (switching table, closing the document). */
function closeAllPanels() {
  for (const id of [...subPanels.keys()]) closePanel(id);
  grid.clearPanels();
}

/** Writes any edit the panels are still holding back. */
function flushPendingEdits() {
  for (const panel of subPanels.values()) panel.flush();
}

/** Refreshes the open sub-tables after the document changed shape (new fields, rows). */
function refreshPanels(rowCount) {
  for (const panel of [...subPanels.values()]) {
    // A deleted row takes its panel with it.
    if (typeof rowCount === 'number' && panel.row >= rowCount) {
      closePanel(panel.id);
      continue;
    }
    send('openSubValue', { sheet: panel.sheet, row: panel.row, column: panel.column });
  }
}


// ---------------------------------------------------------------- find / replace

/** The request the bar currently describes. The host fills in the actual hits. */
function searchPayload() {
  return {
    query: elements.findInput.value,
    regex: elements.findRegex.checked,
    matchCase: elements.findCase.checked,
    scope: elements.findScope.value,
    sheet: state.currentSheet,
  };
}

function openFindBar(withReplace) {
  if (!state.document) return;

  // A dialog or an open cell editor would swallow the keyboard; settle both first.
  if (grid.isEditing()) editor.commit();

  search.open = true;
  elements.findBar.hidden = false;
  elements.findReplaceRow.hidden = !withReplace;

  // A reopened bar keeps its query and re-runs it, because the table may have changed.
  if (elements.findInput.value) {
    runSearch({ reveal: false });
  } else {
    search.matches = [];
    search.index = -1;
    applySearchHits();
    updateFindCount();
  }

  elements.findInput.focus();
  elements.findInput.select();
}

function closeFindBar() {
  if (!search.open) return;

  clearTimeout(search.timer);
  search.open = false;
  search.matches = [];
  search.index = -1;
  search.truncated = false;
  search.revealPending = false;
  elements.findBar.hidden = true;
  grid.setSearchHits([], null);
  updateFindCount();
  elements.gridBody.focus();
}

/** Clears the bar without touching focus (used when the document goes away). */
function resetSearch() {
  clearTimeout(search.timer);
  search.open = false;
  search.matches = [];
  search.index = -1;
  search.truncated = false;
  search.revealPending = false;
  elements.findBar.hidden = true;
  grid.setSearchHits([], null);
  updateFindCount();
}

/** Sends a query. `reveal` moves the cursor to the first hit when the reply arrives. */
function runSearch({ reveal = false } = {}) {
  const payload = searchPayload();

  if (!payload.query || !state.currentSheet) {
    search.matches = [];
    search.index = -1;
    search.truncated = false;
    applySearchHits();
    updateFindCount();
    return;
  }

  search.revealPending = reveal;
  send('search', payload);
}

/** Coalesces keystrokes: one query per pause, not one per character. */
function scheduleSearch() {
  clearTimeout(search.timer);
  search.timer = setTimeout(() => runSearch({ reveal: false }), 160);
}

/** Index of the first hit at or after the cursor, so typing does not jump backwards. */
function firstMatchFromCursor(matches) {
  const { row, col } = grid.cursor;
  const sheet = state.currentSheet;

  for (let i = 0; i < matches.length; i += 1) {
    const match = matches[i];
    if (match.sheet !== sheet) continue;
    if (match.row > row || (match.row === row && match.column >= col)) return i;
  }
  return 0;
}

function onSearchResults(payload) {
  if (!search.open) return;

  const matches = Array.isArray(payload.matches) ? payload.matches : [];
  const previous = search.index >= 0 ? search.matches[search.index] : null;
  const reveal = search.revealPending;
  search.revealPending = false;

  search.matches = matches;
  search.truncated = Boolean(payload.truncated);

  if (matches.length === 0) {
    search.index = -1;
    applySearchHits();
    updateFindCount();
    return;
  }

  // Keep the match the user was on when it survived the edit, otherwise start at the
  // cursor. A late reply for an older query is ignored by checking the text as well.
  let index = -1;
  if (previous) {
    index = matches.findIndex((match) =>
      match.sheet === previous.sheet && match.row === previous.row &&
      match.column === previous.column && match.start === previous.start);
  }
  if (index < 0) index = firstMatchFromCursor(matches);

  search.index = index;

  if (reveal) {
    // Enter / the arrows: follow the match even into another table.
    showMatch();
    return;
  }

  // Typing only moves within the table on screen; switching tables under the user while
  // they type would be disorienting. A hit elsewhere still counts and is one Enter away.
  const match = search.matches[index];
  if (match && match.sheet === state.currentSheet) {
    grid.moveCursor(match.row, match.column, false, true, false);
  }
  applySearchHits();
  updateFindCount();
}

function updateFindCount() {
  const count = elements.findCount;
  const total = search.matches.length;

  if (!elements.findInput.value || total === 0) {
    count.textContent = '0/0';
    count.classList.toggle('none', Boolean(elements.findInput.value));
    return;
  }

  count.classList.remove('none');
  const position = search.index >= 0 ? search.index + 1 : 0;
  count.textContent = search.truncated ? `${position}/${total}+` : `${position}/${total}`;
}

/** Paints the hits of the table on screen and marks the current one. */
function applySearchHits() {
  if (!search.open || search.matches.length === 0) {
    grid.setSearchHits([], null);
    return;
  }

  const hits = new Set();
  for (const match of search.matches) {
    if (match.sheet === state.currentSheet) hits.add(match.row + ':' + match.column);
  }

  const current = search.matches[search.index];
  const currentKey = current && current.sheet === state.currentSheet
    ? current.row + ':' + current.column
    : null;
  grid.setSearchHits(hits, currentKey);
}

/** Moves the cursor to the current match, switching tables when it lives elsewhere. */
function showMatch() {
  const match = search.matches[search.index];
  if (!match) return;

  if (match.sheet !== state.currentSheet) selectSheet(match.sheet);

  // focus=false: the find field keeps the keyboard, so typing can continue.
  grid.moveCursor(match.row, match.column, false, true, false);
  applySearchHits();
  updateFindCount();
}

function stepMatch(delta) {
  if (search.matches.length === 0) {
    runSearch({ reveal: true });
    return;
  }

  search.index = (search.index + delta + search.matches.length) % search.matches.length;
  showMatch();
}

/** Position of a match among the matches of its own cell, which is what replaceOne takes. */
function matchIndexInCell(target) {
  let index = 0;
  for (const match of search.matches) {
    if (match.sheet !== target.sheet || match.row !== target.row || match.column !== target.column)
      continue;
    if (match.start === target.start && match.length === target.length) return index;
    index += 1;
  }
  return 0;
}

function replaceCurrent() {
  const match = search.matches[search.index];
  if (!match) {
    stepMatch(1);
    return;
  }

  send('replaceOne', {
    ...searchPayload(),
    sheet: match.sheet,
    row: match.row,
    column: match.column,
    matchIndex: matchIndexInCell(match),
    replacement: elements.replaceInput.value,
  });
}

function replaceAllInScope() {
  const payload = searchPayload();
  if (!payload.query) return;
  send('replaceAll', { ...payload, replacement: elements.replaceInput.value });
}

elements.findInput.addEventListener('input', scheduleSearch);

elements.findInput.addEventListener('keydown', (event) => {
  if (event.key === 'Enter') {
    event.preventDefault();
    stepMatch(event.shiftKey ? -1 : 1);
  } else if (event.key === 'Escape') {
    event.preventDefault();
    event.stopPropagation();
    closeFindBar();
  }
});

elements.replaceInput.addEventListener('keydown', (event) => {
  if (event.key === 'Enter') {
    event.preventDefault();
    replaceCurrent();
  } else if (event.key === 'Escape') {
    event.preventDefault();
    event.stopPropagation();
    closeFindBar();
  }
});

// A toggle or scope change re-runs the query; keeping the index keeps the cursor put.
elements.findRegex.addEventListener('change', () => runSearch({ reveal: false }));
elements.findCase.addEventListener('change', () => runSearch({ reveal: false }));
elements.findScope.addEventListener('change', () => runSearch({ reveal: false }));

elements.findPrev.addEventListener('click', () => {
  stepMatch(-1);
  elements.findInput.focus();
});
elements.findNext.addEventListener('click', () => {
  stepMatch(1);
  elements.findInput.focus();
});
elements.replaceOne.addEventListener('click', () => {
  replaceCurrent();
  elements.replaceInput.focus();
});
elements.replaceAll.addEventListener('click', () => {
  replaceAllInScope();
  elements.replaceInput.focus();
});
elements.findClose.addEventListener('click', closeFindBar);

// ---------------------------------------------------------------- shortcuts

/**
 * Tab collapses the table list and Tab again brings it back — everywhere in the window.
 * It no longer moves the cell cursor: a dialog or an open menu still keeps Tab for moving
 * between its fields and items, and an open cell editor commits first so nothing typed is
 * lost when the focus leaves.
 */
document.addEventListener('keydown', (event) => {
  if (event.key !== 'Tab' || event.ctrlKey || event.altKey || event.metaKey || event.shiftKey) return;

  // Dialogs and open menus need Tab to move between their fields and items.
  const target = event.target instanceof Element ? event.target : null;
  if (target && target.closest('#modal, .menu-popup')) return;

  event.preventDefault();
  // An editor under the grid writes its value first; the cursor does not move.
  if (grid.isEditing()) editor.commit();
  toggleSidebar();
});

/**
 * Copies the text selected outside the table, and says whether it did.
 * <para>
 * The table owns Ctrl+C, which is why selecting an error message in a dialog and pressing
 * it used to paste the selected cells instead of the message. Anything selected outside
 * the grid — a dialog, the status bar — is copied as text through the host, which works
 * whether or not the engine keeps its own copy accelerator.
 * </para>
 */
function copySelectedText() {
  const selection = window.getSelection();
  if (!selection || selection.isCollapsed || selection.rangeCount === 0) return false;

  const text = selection.toString();
  if (text.length === 0) return false;

  const anchor = selection.anchorNode;
  const element = anchor ? (anchor.nodeType === 1 ? anchor : anchor.parentElement) : null;
  if (element && element.closest('#grid')) return false; // the table copies cells itself

  send('copyText', { text });
  return true;
}

document.addEventListener('keydown', (event) => {
  if (!event.ctrlKey) return;

  const key = event.key.toLowerCase();

  // A text field keeps its own copy / cut / paste / undo / redo. The find and replace
  // inputs used to lose all of them to the table's handlers. The grid proxy is excluded:
  // it is an invisible composition target, not a field the user types in.
  const target = event.target instanceof Element ? event.target : null;
  const field = target && target.closest('input, textarea, select, [contenteditable="true"]');
  if (field && !field.classList.contains('grid-proxy')
      && ['c', 'x', 'v', 'z', 'y'].includes(key)) {
    return;
  }

  // A dialog field keeps its own Ctrl+F / Ctrl+H; the find bar is for the table.
  if (key === 'f') { if (modalOpen()) return; event.preventDefault(); openFindBar(false); }
  else if (key === 'h') { if (modalOpen()) return; event.preventDefault(); openFindBar(true); }
  else if (key === 'o') { event.preventDefault(); send('openFile'); }
  else if (key === 's' && event.shiftKey) { event.preventDefault(); send('saveAs'); }
  else if (key === 's') { event.preventDefault(); send('save'); }
  else if (key === 'w') { event.preventDefault(); send('closeDocument'); }
  else if (key === 'n' && event.shiftKey) { event.preventDefault(); send('newWindow'); }
  else if (key === 'n') { event.preventDefault(); send('newDatabase'); }
  else if (key === 'z') { event.preventDefault(); send(event.shiftKey ? 'redo' : 'undo'); }
  else if (key === 'y') { event.preventDefault(); send('redo'); }
  else if (key === 't' && !event.shiftKey) { event.preventDefault(); toggleTheme(); }
  else if (key === 'b' && !event.shiftKey) { event.preventDefault(); toggleSidebar(); }
  else if (key === ',') { event.preventDefault(); openSettings(); }
  else if (key === 'c' && !grid.isEditing()) {
    // A selection anywhere but the table is text: copy that, not the cells.
    if (copySelectedText()) { event.preventDefault(); return; }
    // With a dialog open the shortcut belongs to its fields.
    if (modalOpen()) return;
    event.preventDefault();
    runRowAction('copy');
  }
  else if (key === 'x' && !grid.isEditing()) {
    if (modalOpen()) return;
    event.preventDefault();
    runRowAction('cut');
  }
  else if (key === 'v' && !grid.isEditing()) {
    // Pasting into a field of a dialog is the field's business, not the table's.
    if (modalOpen()) return;
    event.preventDefault();
    runRowAction(event.shiftKey ? 'pasteInsert' : 'paste');
  }
});

// ---------------------------------------------------------------- rendering

function renderTableList() {
  elements.tableList.innerHTML = '';

  if (!state.document) return;

  for (const sheet of state.document.sheets) {
    const item = document.createElement('div');
    item.className = 'table-item' + (sheet.name === state.currentSheet ? ' active' : '');
    item.dataset.sheet = sheet.name;

    const name = document.createElement('span');
    name.textContent = sheet.name;
    item.appendChild(name);

    // A table that follows a template carries a dot: it says at a glance that its columns
    // are managed elsewhere. The name is only in the tooltip so the list stays quiet.
    if (sheet.template) {
      const dot = document.createElement('span');
      dot.className = 'tpl-dot' + (sheet.templateInSync ? '' : ' drift');
      dot.title = templateTooltip(sheet);
      item.appendChild(dot);
    }

    const count = document.createElement('span');
    count.className = 'count';
    count.textContent = String(sheet.rowCount);
    item.appendChild(count);

    item.addEventListener('click', () => selectSheet(sheet.name));
    elements.tableList.appendChild(item);
  }
}

/** One line describing the template a table follows, for a tooltip. */
function templateTooltip(sheet) {
  const columns = sheet.templateColumns || 0;
  const members = sheet.templateMembers || 1;
  const parts = [
    `Template: ${sheet.templateName}`,
    `${columns} column${columns === 1 ? '' : 's'}`,
    members === 1 ? 'used by this table only' : `used by ${members} tables`,
  ];
  if (sheet.templateInSync === false) parts.push('out of sync — see Table Settings');
  return parts.join(' · ');
}

/** Description of the table currently shown, or null when nothing is open. */
function currentSheetData() {
  if (!state.document || !state.currentSheet) return null;
  return state.document.sheets.find((s) => s.name === state.currentSheet) || null;
}

function selectSheet(name) {
  if (!state.document) return;
  const sheet = state.document.sheets.find((s) => s.name === name);
  if (!sheet) return;

  if (state.currentSheet !== name) closeAllPanels();
  state.currentSheet = name;
  grid.setSheet(sheet);
  renderTableList();
  send('selectSheet', { sheet: name });
  updateStatus({
    row: 0,
    column: 0,
    rowCount: sheet.rowCount,
    columnCount: sheet.columns.length,
  });

  // A find bar stays open across a table switch: the hits of the new table show up
  // immediately, and the count still refers to the whole result set.
  applySearchHits();
}

function showDocument(payload) {
  // Thumbnails are read from disk, so they belong to one file only. The same document is
  // re-sent after every save, which must not throw the cache away. A different file is a
  // new table even when its first sheet has the same name, so the grid must reset.
  const documentChanged = !state.document || state.document.path !== payload.path;
  if (documentChanged) clearImages();

  state.document = payload;
  state.currentSheet = payload.currentSheet || (payload.sheets[0] ? payload.sheets[0].name : null);

  // The palette travels with the file, so it is applied as soon as the document is known.
  applyFileScheme(payload.scheme);

  elements.docTitle.textContent = payload.name + (payload.dirty ? ' *' : '');
  elements.welcome.classList.toggle('hidden', true);
  elements.grid.classList.add('visible');
  toolbar.classList.add('visible');
  // The pinned toggle belongs to the document view: with no database open the whole
  // toolbar is hidden, and a lone button left behind looks like the tools vanished.
  document.body.classList.remove('no-document');
  elements.statusMode.textContent = payload.readOnly ? 'Read only' : 'Editable';
  elements.statusMode.title = payload.readOnly ? (payload.readOnlyReason || 'Read only') : 'This file can be edited';

  const previousSheet = state.currentSheet;
  const previousRows = grid.rowCount;
  const sheet = payload.sheets.find((s) => s.name === state.currentSheet) || payload.sheets[0];
  if (sheet) {
    if (previousSheet && previousSheet !== sheet.name) closeAllPanels();
    state.currentSheet = sheet.name;
    grid.setSheet(sheet, { resetView: documentChanged });
  } else {
    closeAllPanels();
    grid.setSheet(null, { resetView: documentChanged });
  }

  // A brand new database has no table yet; the grid area explains what to do next.
  elements.gridEmpty.classList.toggle('hidden', !!sheet);

  // The description is re-sent after a replace-all or a save; the highlights belong to the
  // new rows, not the old ones.
  applySearchHits();

  // Fields of a list can be renamed or added while its panel is open, so every open
  // panel is refreshed against the document that has just arrived. A table that gained
  // or lost rows has moved them, though, and a panel is anchored to a row number: it is
  // closed rather than left pointing at whatever now sits on that row.
  if (subPanels.size > 0) {
    if (!sheet || sheet.rowCount !== previousRows) closeAllPanels();
    else refreshPanels(sheet.rowCount);
  }

  renderTableList();
  refreshTemplateStatus();
  applyState({
    dirty: payload.dirty,
    canUndo: payload.canUndo,
    canRedo: payload.canRedo,
    undoLabel: payload.undoLabel,
    redoLabel: payload.redoLabel,
  });
}

function showWelcome() {
  closeAllPanels();
  state.document = null;
  state.currentSheet = null;
  elements.docTitle.textContent = 'No database open';
  elements.welcome.classList.remove('hidden');
  elements.gridEmpty.classList.add('hidden');
  elements.grid.classList.remove('visible');
  toolbar.classList.remove('visible');
  document.body.classList.add('no-document');
  elements.tableList.innerHTML = '';
  elements.statusSheet.textContent = '-';
  elements.statusCursor.textContent = '-';
  elements.statusRows.textContent = '-';
  elements.statusMode.textContent = 'Read only';
}

function updateStatus(info) {
  if (!info || !state.currentSheet) return;
  elements.statusSheet.textContent = state.currentSheet;

  const selection = info.selection;
  const hasRange = selection && (selection.rows > 1 || selection.columns > 1);

  elements.statusCursor.textContent = info.rowCount === 0
    ? '-'
    : hasRange
      ? `${selection.rows} x ${selection.columns}`
      : `R${info.row + 1} C${info.column + 1}`;

  elements.statusRows.textContent = `${info.rowCount} rows`;
  refreshTemplateStatus();
  refreshIssue();
}

/** Shows which template the current table follows, if any. */
function refreshTemplateStatus() {
  if (!elements.statusTemplate) return;

  const sheet = currentSheetData();
  if (!sheet || !sheet.template) {
    elements.statusTemplate.hidden = true;
    elements.statusTemplate.textContent = '';
    elements.statusTemplate.title = '';
    if (elements.statusTemplateSep) elements.statusTemplateSep.hidden = true;
    return;
  }

  elements.statusTemplate.hidden = false;
  if (elements.statusTemplateSep) elements.statusTemplateSep.hidden = false;
  elements.statusTemplate.textContent = `Template: ${sheet.templateName}`;
  elements.statusTemplate.title = templateTooltip(sheet);
  elements.statusTemplate.classList.toggle('drift', sheet.templateInSync === false);
}

/**
 * Surfaces the first validation problem (missing or duplicate id, bad reference) in the
 * status bar, naming the table and the row so it can be found without hunting for red.
 *
 * The host answers this for the whole table: the grid only holds the rows it has loaded,
 * so a view-driven check would stay silent until the broken row was scrolled into sight.
 */
function refreshIssue() {
  if (!elements.statusIssue) return;

  const issue = state.issue;
  elements.statusIssue.hidden = !issue;
  elements.statusIssueSep.hidden = !issue;
  if (!issue) return;

  const where = `Row ${issue.row + 1}`;
  elements.statusIssue.textContent = `${issue.sheet} · ${where}: ${issue.detail}`;
  elements.statusIssue.title = issue.column ? `${issue.detail} (column '${issue.column}')` : issue.detail;
}

/** Reflects the document state in the title and the Edit menu. */
function applyState(payload) {
  if (state.document) {
    state.document.dirty = payload.dirty;
    elements.docTitle.textContent = state.document.name + (payload.dirty ? ' *' : '');
  }

  const undoItem = document.querySelector('[data-action="undo"]');
  const redoItem = document.querySelector('[data-action="redo"]');

  if (undoItem) {
    undoItem.disabled = !payload.canUndo;
    const label = undoItem.querySelector('span');
    label.textContent = payload.canUndo && payload.undoLabel ? `Undo ${payload.undoLabel}` : 'Undo';
  }

  if (redoItem) {
    redoItem.disabled = !payload.canRedo;
    const label = redoItem.querySelector('span');
    label.textContent = payload.canRedo && payload.redoLabel ? `Redo ${payload.redoLabel}` : 'Redo';
  }
}

function toast(message, level = 'info') {
  const node = document.createElement('div');
  node.className = 'toast ' + level;
  node.textContent = message;
  elements.toasts.appendChild(node);
  setTimeout(() => node.remove(), level === 'error' ? 6000 : 3000);
}

// ---------------------------------------------------------------- host messages

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
      if (pendingAbout) {
        pendingAbout = false;
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

// ---------------------------------------------------------------- startup

elements.welcomeOpen.addEventListener('click', () => send('openFile'));
elements.gridAddTable.addEventListener('click', () => runAction('addTable'));

elements.addTable.addEventListener('click', (event) => {
  event.stopPropagation();
  promptAddTable();
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
