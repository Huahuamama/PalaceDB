// The document on screen: the sidebar lists, the status bar and the table selection.
//
// Everything here draws what the host has sent. It sits below the dialogs: they read the
// current table through this module, and it never opens one of theirs.

import { send } from './bridge.js';
import { showContextMenu } from './contextmenu.js';
import { elements } from './dom.js';
import { clearImages } from './images.js';
import { confirmDeletePublicType, promptPublicType } from './schema.js';
import { currentSheetData, grid, publicTypesOfDocument, search, state, subPanels } from './session.js';
import { closeAllPanels, refreshPanels } from './subtables.js';
import { applyFileScheme } from './viewoptions.js';

export const SIDEBAR_KEY = 'palacedb.sidebar.collapsed';

export function applySidebar(collapsed) {
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

export function toggleSidebar() {
  applySidebar(!elements.sidebar.classList.contains('collapsed'));
}

export const SIDEBAR_TAB_KEY = 'palacedb.sidebar.tab';
const SIDEBAR_TABS = ['tables', 'types', 'columns'];

/**
 * Opens one sidebar section and closes the others.
 *
 * Three lists stacked in a 232px column left every one of them too short to read, so they
 * became tabs: whichever is open gets the whole height. The choice is remembered for the
 * next session, like the collapsed state.
 */
export function applySidebarTab(name, { persist = true } = {}) {
  const tab = SIDEBAR_TABS.includes(name) ? name : 'tables';
  state.sidebarTab = tab;

  for (const button of elements.sidebarTabs.querySelectorAll('.sidebar-tab')) {
    button.setAttribute('aria-selected', String(button.dataset.tab === tab));
  }
  for (const panel of elements.sidebar.querySelectorAll('.sidebar-panel')) {
    panel.hidden = panel.dataset.panel !== tab;
  }

  // The + follows the tab: a table on one, a public type on the other. Columns are made
  // from the grid's header menu, so there is nothing to add there.
  const add = elements.sidebarAdd;
  // Hidden but still in the layout: taking it out would let the three tabs grow and the
  // whole strip would jump every time Columns is opened.
  const canAdd = tab !== 'columns';
  add.classList.toggle('off', !canAdd);
  add.disabled = !canAdd;
  if (canAdd) {
    add.title = tab === 'types' ? 'Add a public enum or flags type' : 'Add a table';
    add.setAttribute('aria-label', add.title);
  }

  if (persist) {
    try { localStorage.setItem(SIDEBAR_TAB_KEY, tab); } catch { /* ignore */ }
  }

  // The lists are kept up to date while hidden, so switching is just a show.
  renderTableList();
}

/** Paints the hits of the table on screen and marks the current one. */
export function applySearchHits() {
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

export function renderTableList() {
  elements.tableList.innerHTML = '';
  renderPublicList();
  renderColumnsList();

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

// ---------------------------------------------------------------- columns panel

/**
 * Lists the columns of the table on screen with a visibility switch.
 *
 * Hiding a column is a way of looking at a table, not an edit, so the click only writes
 * the side-car; the document that comes back carries the new flag and the grid follows.
 * The panel is also the only way back: the header a hidden column would be reached from
 * is exactly the part that is gone.
 */
function renderColumnsList() {
  elements.columnList.innerHTML = '';

  const sheet = currentSheetData();
  if (!sheet) {
    const empty = document.createElement('div');
    empty.className = 'column-empty';
    empty.textContent = 'No table selected.';
    elements.columnList.appendChild(empty);
    return;
  }

  for (const column of sheet.columns) {
    const item = document.createElement('div');
    item.className = 'column-item' + (column.hidden ? ' hidden' : '');
    item.dataset.column = column.name;
    item.title = column.hidden
      ? `${column.name} is hidden \u2014 click to show it`
      : `${column.name} \u2014 click to hide it`;

    const eye = document.createElement('span');
    eye.className = 'column-eye';
    eye.textContent = column.hidden ? '\u25CB' : '\u25C9';
    item.appendChild(eye);

    const name = document.createElement('span');
    name.className = 'column-name';
    name.textContent = column.name;
    item.appendChild(name);

    const type = document.createElement('span');
    type.className = 'column-type';
    type.textContent = column.type;
    item.appendChild(type);

    item.addEventListener('click', () => {
      if (!state.currentSheet) return;
      send('setColumnHidden', {
        sheet: state.currentSheet,
        column: column.name,
        hidden: !column.hidden,
      });
    });

    elements.columnList.appendChild(item);
  }
}


/**
 * Lists the public types under the table list. A type is a named value list several
 * columns share: the count next to it says how many follow it, and its tooltip names
 * them, so the effect of an edit is visible before it is made.
 */
function renderPublicList() {
  elements.publicList.innerHTML = '';
  if (!state.document) return;

  const types = publicTypesOfDocument();
  if (types.length === 0) {
    const empty = document.createElement('div');
    empty.className = 'public-empty';
    empty.textContent = 'None yet \u2014 + adds one.';
    elements.publicList.appendChild(empty);
    return;
  }

  for (const type of types) {
    const item = document.createElement('div');
    item.className = 'public-item';
    item.dataset.publicType = type.name;

    const name = document.createElement('span');
    name.className = 'public-name';
    name.textContent = type.name;
    item.appendChild(name);

    const kind = document.createElement('span');
    kind.className = 'public-kind';
    kind.textContent = type.kind === 'flags' ? 'flags' : 'enum';
    item.appendChild(kind);

    const columns = type.columns || [];
    const count = document.createElement('span');
    count.className = 'count';
    count.textContent = String(columns.length);
    count.title = columns.length === 0
      ? 'Not used by any column yet'
      : columns.map((entry) => `${entry.sheet}.${entry.column}`).join('\n');
    item.appendChild(count);

    item.addEventListener('click', () => promptPublicType(type));
    item.addEventListener('contextmenu', (event) => {
      event.preventDefault();
      event.stopPropagation();
      showContextMenu(event.clientX, event.clientY, [
        { label: 'Edit Values...', action: 'editPublicType' },
        { label: 'Delete', action: 'deletePublicType' },
      ], (menuItem) => {
        if (menuItem.action === 'editPublicType') promptPublicType(type);
        else confirmDeletePublicType(type);
      });
    });

    elements.publicList.appendChild(item);
  }
}

/** One line describing the template a table follows, for a tooltip. */
function templateTooltip(sheet) {  const columns = sheet.templateColumns || 0;
  const members = sheet.templateMembers || 1;
  const parts = [
    `Template: ${sheet.templateName}`,
    `${columns} column${columns === 1 ? '' : 's'}`,
    members === 1 ? 'used by this table only' : `used by ${members} tables`,
  ];
  if (sheet.templateInSync === false) parts.push('out of sync — see Table Settings');
  return parts.join(' · ');
}

export function selectSheet(name) {
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

export function showDocument(payload) {
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
  elements.toolbar.classList.add('visible');
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

export function showWelcome() {
  closeAllPanels();
  state.document = null;
  state.currentSheet = null;
  elements.docTitle.textContent = 'No database open';
  elements.welcome.classList.remove('hidden');
  elements.gridEmpty.classList.add('hidden');
  elements.grid.classList.remove('visible');
  elements.toolbar.classList.remove('visible');
  document.body.classList.add('no-document');
  elements.tableList.innerHTML = '';
  elements.columnList.innerHTML = '';
  elements.statusSheet.textContent = '-';
  elements.statusCursor.textContent = '-';
  elements.statusRows.textContent = '-';
  elements.statusMode.textContent = 'Read only';
}

export function updateStatus(info) {
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
export function refreshIssue() {
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
export function applyState(payload) {
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
