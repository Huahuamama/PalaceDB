// Virtualised table view.
//
// Only the rows inside the viewport (plus a small overscan) are turned into DOM
// nodes, so a table with tens of thousands of rows scrolls as smoothly as one with
// ten. Cell text is formatted by the host, which keeps the display rules in one
// place (the C# CellFormatter) instead of duplicating them in JavaScript.

import { send } from './bridge.js';
import { imageUrl, imageError, requestImage } from './images.js';

// Row height lives in the stylesheet so the editor overlay and the grid always agree;
// it is read from the CSS variable instead of being duplicated here. The table text size
// is a setting, so the value can change at runtime: `refreshMetrics` re-reads it and the
// live binding below makes every importer see the new number.
function readRowHeight() {
  const value = parseFloat(getComputedStyle(document.documentElement).getPropertyValue('--row-height'));
  return Number.isFinite(value) && value > 0 ? value : 38;
}

let ROW_HEIGHT = readRowHeight();

/** Re-reads the metrics after the appearance changed. Call before the next render. */
function refreshMetrics() {
  ROW_HEIGHT = readRowHeight();
}

export { ROW_HEIGHT, refreshMetrics };
const OVERSCAN = 6;
const DEFAULT_WIDTH = 140;
const MIN_WIDTH = 40;

// Space a cell takes around its content: 2px padding above and below plus the 1px rule
// underneath. A row has to be that much taller than its text to show all of it.
const CELL_CHROME = 5;

// Safety net for a pathological value (a whole JSON blob in one cell). The stylesheet
// caps `.cell-value` first; this only stops a measured height from exploding the table.
const MAX_ROW_FACTOR = 30;

const NUMERIC_TYPES = new Set(['Int', 'Float', 'Color']);
// Columns edited as free text, so typed characters (and IME compositions) belong in them.
const TEXT_TYPES = new Set([
  'Text', 'Id', 'Int', 'Float', 'Color', 'Image', 'File', 'Formula', 'Guid',
  'Custom', 'Dynamic', 'Gradient', 'Curve', 'Polymorph',
]);

export class Grid {
  constructor(elements, options = {}) {
    this.headScroll = elements.headScroll;
    this.head = elements.head;
    this.body = elements.body;
    this.spacer = elements.spacer;
    this.rows = elements.rows;
    this.gutter = elements.gutter || null;
    this.container = elements.container || this.body;

    this.onCursorChanged = options.onCursorChanged || (() => {});
    this.onSelectionChanged = options.onSelectionChanged || (() => {});
    this.onRendered = options.onRendered || null;

    // Where edits go. The table on screen talks to the document; a sub-table inside a
    // List cell writes into the value it belongs to instead, but uses the very same
    // editors, which is why the indirection lives here rather than in each editor.
    this.store = options.store || {
      setCell: (row, column, text) => send('setCell', {
        sheet: this.sheet, row, column, text,
      }),
      refOptions: (column) => send('refOptions', { sheet: this.sheet, column }),
      browse: (kind, column, current) => send('browseFile', { kind, column, current }),
      fillCells: (rows, columns, text) => send('fillCells', {
        sheet: this.sheet, rows, columns, text,
      }),
    };

    // A sub-table keeps its rows in memory; a real table asks the host for them.
    this.loadRows = options.loadRows || null;

    // Wired by the application once the editor exists.
    this.onEditRequest = null;
    this.onToggleRequest = null;
    this.onClearRequest = null;
    this.onContextMenu = null;
    this.onGutterMenu = null;
    this.onRowMove = null;
    this.isEditing = () => false;

    this.suspendRender = false;

    this.sheet = null;
    this.columns = [];
    this.rowCount = 0;
    this.cache = new Map();
    // Search hits for the table on screen, keyed "row:col". Kept here so every render
    // (a scroll, an edit, a sheet refresh) repaints them; the find bar owns the values.
    this.searchHits = new Set();
    this.searchCurrent = null;
    this.separatorTitles = new Map();
    // The same separators as a sorted list: a strip is one row tall, so everything below
    // it has to move down by one row per separator above it.
    this.separatorRows = [];
    this.cursors = new Map();
    this.pendingStart = -1;
    this.pendingCount = 0;

    // Rows are as tall as their tallest wrapped value. Only rows that need more than the
    // base height are stored here, so a table where nothing wraps keeps the plain
    // "index * ROW_HEIGHT" arithmetic (and its O(1) cost) it always had. `heightRows` /
    // `heightPrefix` are the sorted form of the same data, rebuilt lazily, which is what
    // makes "how much taller is everything above row N" a binary search instead of a scan.
    this.rowHeights = new Map();
    this.heightRows = [];
    this.heightPrefix = [0];
    this.heightIndexDirty = true;

    this.cursor = { row: 0, col: 0 };
    this.anchor = { row: 0, col: 0 };
    this.focus = false;
    // Nothing has been touched yet: the first cell must not show a cursor box before the
    // table has been used, which looked like a stray caret in the top-left corner.
    this.interacted = false;
    this.dragging = false;
    this.rowDrag = null;
    // A List / Properties cell opens a sub-table underneath its row, which pushes the
    // following rows down. Several can be open at once, including two on the same row,
    // so they are kept in a map keyed by "row:col" and drawn in that order.
    this.panels = new Map();

    this.frame = null;
    this.missingRequest = null;

    this.body.addEventListener('scroll', () => this.scheduleRender(), { passive: true });
    this.body.addEventListener('keydown', (event) => this.onKeyDown(event));
    this.body.addEventListener('focus', () => {
      this.focus = true;
      this.interacted = true;
      this.focusProxy();
      this.paintSelection();
    });
    this.body.addEventListener('blur', () => { this.focus = false; this.paintSelection(); });

    // Clicking anywhere outside the grid gives the keyboard back to whatever was
    // clicked. Without this the invisible proxy kept focus and swallowed typing meant
    // for the table list or a menu.
    document.addEventListener('mousedown', (event) => {
      if (!this.container.contains(event.target) && document.activeElement === this.proxy) {
        this.proxy.blur();
      }
    });
    this.body.addEventListener('mousedown', (event) => this.onMouseDown(event));
    this.body.addEventListener('click', (event) => this.onClick(event));
    this.body.addEventListener('dblclick', (event) => this.onDoubleClick(event));
    this.body.addEventListener('mousemove', (event) => this.onMouseMove(event));
    document.addEventListener('mouseup', () => { this.dragging = false; });

    // The gutter lives outside the scroller, so it needs its own listeners.
    if (this.gutter) {
      this.gutter.addEventListener('mousedown', (event) => this.onGutterMouseDown(event));
      this.gutter.addEventListener('mousemove', (event) => this.onMouseMove(event));
      this.gutter.addEventListener('contextmenu', (event) => this.onGutterContextMenu(event));
    }

    this.head.addEventListener('mousedown', (event) => this.onHeaderMouseDown(event));
    this.body.addEventListener('contextmenu', (event) => this.onContextMenuEvent(event, false));
    this.head.addEventListener('contextmenu', (event) => this.onContextMenuEvent(event, true));
    window.addEventListener('resize', () => this.scheduleRender());

    // A real, focused input sits behind the grid so an input method has somewhere to
    // compose. Without it the first keystroke of a Chinese/Japanese composition arrives
    // as a plain Latin letter on a div that cannot take text.
    this.proxy = document.createElement('textarea');
    this.proxy.className = 'grid-proxy';
    this.proxy.setAttribute('aria-hidden', 'true');
    this.proxy.autocomplete = 'off';
    this.proxy.spellcheck = false;
    // Appended to the scrolling body, not to the row container: render() rebuilds the
    // rows and would take the proxy with it.
    this.body.appendChild(this.proxy);
    this.proxy.addEventListener('input', () => this.promoteProxy());
    this.proxy.addEventListener('compositionstart', () => this.promoteProxy());
  }

  /**
   * Turns the focused proxy into the visible cell editor, keeping the same DOM node so
   * the composition in progress is never interrupted by a focus change.
   */
  promoteProxy() {
    if (this.isEditing() || !this.onEditRequest) return;
    if (this.rowCount === 0 || this.columns.length === 0) return;

    const column = this.columns[this.cursor.col];
    if (!column) return;

    const text = this.proxy.value;
    // Only free-text columns can take what was typed; the rest open their own control.
    if (TEXT_TYPES.has(column.type)) {
      this.onEditRequest(this.cursor.row, this.cursor.col, text, this.proxy);
    } else {
      this.proxy.value = '';
      this.onEditRequest(this.cursor.row, this.cursor.col);
    }
  }

  /** Keeps the proxy focused so typing and IME work without an editor being open. */
  focusProxy() {
    if (!this.proxy) return;

    // Sit on the cursor cell so an IME candidate window opens next to the text.
    if (this.columns.length > 0) {
      this.proxy.style.left = this.columnLeft(this.cursor.col) + 'px';
      this.proxy.style.top = this.topOf(this.cursor.row) + 'px';
    }

    if (document.activeElement !== this.proxy) this.proxy.focus({ preventScroll: true });
  }

  /** Loads a sheet description (columns and row count) and resets the view. */
  setSheet(sheet) {
    // An open sub-table survives a refresh of the same table (an edit re-sends the
    // document), but not a switch to another one.
    if (this.sheet !== (sheet ? sheet.name : null)) this.clearPanels();

    // The host re-sends the whole document after every save, undo and redo. That is a
    // refresh of the table already on screen, not a jump to another one, so the view has
    // to stay exactly where the user left it.
    const refreshing = this.sheet !== null
      && this.sheet === (sheet ? sheet.name : null)
      && this.rowCount === (sheet ? sheet.rowCount : 0);
    const previousScrollTop = this.body.scrollTop;
    const previousScrollLeft = this.body.scrollLeft;

    // Each table remembers where the cursor was, so switching back feels like a tab.
    if (this.sheet) this.cursors.set(this.sheet, { ...this.cursor });

    this.sheet = sheet ? sheet.name : null;
    this.columns = (sheet ? sheet.columns : []).map((column) => ({
      ...column,
      width: clampWidth(column.width),
    }));
    this.rowCount = sheet ? sheet.rowCount : 0;

    // Separators are row indices; the title array is parallel to the sorted list. Each one
    // draws a strip one row tall above its row, so the rows below it move down by that
    // much (see separatorShift).
    this.separatorTitles = new Map();
    const indices = sheet && Array.isArray(sheet.separators) ? sheet.separators : [];
    const titles = sheet && Array.isArray(sheet.separatorTitles) ? sheet.separatorTitles : [];
    indices.forEach((index, position) => {
      this.separatorTitles.set(index, titles[position] || '');
    });
    this.separatorRows = [...this.separatorTitles.keys()].sort((a, b) => a - b);

    this.cache.clear();
    // A different table wraps differently: nothing measured for the old one applies.
    // A refresh keeps the measurements — dropping them would shrink the scrollable area
    // (every row above the viewport falls back to the base height) and the browser would
    // clamp the scroll position, which is the view creeping upwards after a save.
    if (!refreshing) {
      this.rowHeights.clear();
      this.heightIndexDirty = true;
    }
    this.pendingStart = -1;
    this.pendingCount = 0;

    const remembered = this.sheet ? this.cursors.get(this.sheet) : null;
    this.cursor = remembered
      ? {
        row: clamp(remembered.row, 0, Math.max(0, this.rowCount - 1)),
        col: clamp(remembered.col, 0, Math.max(0, this.columns.length - 1)),
      }
      : { row: 0, col: 0 };
    this.anchor = { ...this.cursor };

    if (refreshing) {
      // Restored before the render so the first paint already covers the right rows.
      this.body.scrollTop = previousScrollTop;
      this.body.scrollLeft = previousScrollLeft;
    } else {
      this.body.scrollTop = 0;
      this.body.scrollLeft = 0;
    }
    this.renderHead();
    this.render();
    // A refresh leaves the cursor where it was and never scrolls: the user is looking at
    // something else, and moving the view is exactly what they complained about.
    if (remembered && !refreshing) this.scrollIntoView();
    this.emitCursor();
  }

  get totalWidth() {
    return this.columns.reduce((sum, column) => sum + column.width, 0);
  }

  /** Formatted value of one cell, or undefined when the row is not loaded yet. */
  cellValue(row, col) {
    const data = this.cache.get(row);
    return data ? data.cells[col] : undefined;
  }

  /** Horizontal offset of a column inside the scrolling content. */
  columnLeft(col) {
    let left = 0;
    for (let i = 0; i < col && i < this.columns.length; i += 1) left += this.columns[i].width;
    return left;
  }

  /**
   * Sets the highlighted cells of the table on screen. `hits` holds "row:col" keys and
   * `current` is the one the cursor is on, or null.
   */
  setSearchHits(hits, current) {
    this.searchHits = hits instanceof Set ? hits : new Set(hits || []);
    this.searchCurrent = current || null;
    this.render();
  }

  /** Replaces a single cached row (used after an edit). */
  replaceRow(row) {
    if (!row || row.i === undefined) return;
    this.cache.set(row.i, row);
    // The new text may wrap differently from the old one.
    this.clearRowHeights(row.i);
    this.scheduleRender();
  }

  /** Names of the columns covered by the current selection. */
  selectedColumnNames() {
    const bounds = this.selectionBounds();
    const names = [];
    for (let col = bounds.left; col <= bounds.right; col += 1) {
      if (this.columns[col]) names.push(this.columns[col].name);
    }
    return names;
  }

  /** Row indices covered by the current selection. */
  selectedRowIndices() {
    const bounds = this.selectionBounds();
    const rows = [];
    for (let row = bounds.top; row <= bounds.bottom; row += 1) rows.push(row);
    return rows;
  }

  /** Receives a block of formatted rows from the host. */
  applyRows(payload) {
    if (payload.sheet !== this.sheet) return;

    for (const row of payload.rows) this.cache.set(row.i, row);
    this.pendingStart = -1;
    this.pendingCount = 0;
    this.rowCount = payload.total;
    this.render();
  }

  // ------------------------------------------------------------- rendering

  scheduleRender() {
    if (this.frame !== null) return;
    this.frame = requestAnimationFrame(() => {
      this.frame = null;
      this.render();
    });
  }

  renderHead() {
    this.head.innerHTML = '';
    this.head.style.width = this.totalWidth + 'px';

    this.columns.forEach((column, index) => {
      const cell = document.createElement('div');
      cell.className = 'head-cell' + (index === this.cursor.col ? ' cursor-col' : '');
      cell.style.width = column.width + 'px';
      cell.dataset.col = String(index);
      if (column.bg) cell.style.backgroundColor = column.bg;

      const name = document.createElement('span');
      name.textContent = column.name;
      cell.appendChild(name);

      const type = document.createElement('span');
      type.className = 'type';
      type.textContent = column.type;
      cell.appendChild(type);

      if (column.optional) {
        const opt = document.createElement('span');
        opt.className = 'type';
        opt.textContent = 'opt';
        cell.appendChild(opt);
      }

      const resizer = document.createElement('div');
      resizer.className = 'head-resizer';
      resizer.dataset.col = String(index);
      cell.appendChild(resizer);

      this.head.appendChild(cell);
    });
  }

  /**
   * Repaints the cursor and the selection on the rows that are already in the DOM.
   *
   * Moving the cursor must not rebuild the rows: a mousedown that replaces the element
   * under the pointer means the browser sees mouseup on a different node, so no `click`
   * and no `dblclick` is ever dispatched — which is why double-clicking a cell used to
   * do nothing. Rebuilding is left to scrolling and to data changes.
   */
  paintSelection() {
    if (this.suspendRender) return;

    const selection = this.selectionBounds();
    // A block selection has no single focused cell: the block itself is the focus.
    const single = selection.top === selection.bottom && selection.left === selection.right;

    for (const row of this.rows.querySelectorAll('.grid-row')) {
      const index = Number(row.dataset.row);
      const inRows = index >= selection.top && index <= selection.bottom;
      row.classList.toggle('selected-row', Boolean(selection.multiRow) && inRows);

      for (const cell of row.querySelectorAll('.cell')) {
        const col = Number(cell.dataset.col);
        const isCursor = single && index === this.cursor.row && col === this.cursor.col;
        const isSelected = inRows && col >= selection.left && col <= selection.right;

        cell.classList.toggle('cursor', isCursor);
        cell.classList.toggle('in-selection', isSelected && !isCursor);
        // The box only appears after the table has been used, and turns dashed while the
        // grid does not hold the keyboard (the caret is then the focus indicator).
        cell.classList.toggle('live', isCursor && (this.focus || this.interacted));
        cell.classList.toggle('unfocused', !this.focus);
      }
    }

    this.paintHeadCursor();
    this.paintGutterSelection(selection);
  }

  paintHeadCursor() {
    for (const cell of this.head.children) {
      cell.classList.toggle('cursor-col', Number(cell.dataset.col) === this.cursor.col);
    }
  }

  paintGutterSelection(selection) {
    if (!this.gutter) return;

    const wholeRow = selection.left === 0 && selection.right === this.columns.length - 1;
    for (const cell of this.gutter.querySelectorAll('.gutter-cell')) {
      const index = Number(cell.dataset.row);
      const inRange = index >= selection.top && index <= selection.bottom;
      cell.classList.toggle('in-range', inRange);
      cell.classList.toggle('selected', inRange && wholeRow);
    }
  }

  /**
   * Y offset of a row inside the scrolling content. Rows below an open sub-table are
   * pushed down by its height, which is what makes the panel feel inserted in the table
   * rather than floating over it.
   */
  topOf(index) {
    return index * ROW_HEIGHT + this.heightDeltaBefore(index) + this.separatorShift(index)
      + this.shiftBefore(index);
  }

  /**
   * Height taken by the separator strips above `index`, the strip of `index` itself
   * included: a separator at row N is drawn between row N-1 and row N, so row N and
   * everything below it move down by one row.
   */
  separatorShift(index) {
    let count = 0;
    for (const separator of this.separatorRows) {
      if (separator > index) break;
      count += 1;
    }
    return count * ROW_HEIGHT;
  }

  // ------------------------------------------------------------- row heights

  /** Height of one row: the base height unless its content needs more. */
  heightOf(index) {
    const height = this.rowHeights.get(index);
    return height === undefined ? ROW_HEIGHT : height;
  }

  /** How much taller everything above `index` is than the uniform-height assumption. */
  heightDeltaBefore(index) {
    if (this.rowHeights.size === 0) return 0;
    this.ensureHeightIndex();

    let lo = 0;
    let hi = this.heightRows.length;
    while (lo < hi) {
      const mid = (lo + hi) >> 1;
      if (this.heightRows[mid] < index) lo = mid + 1;
      else hi = mid;
    }
    return this.heightPrefix[lo];
  }

  /** Sorts the grown rows and sums their extra height, so lookups stay logarithmic. */
  ensureHeightIndex() {
    if (!this.heightIndexDirty) return;
    this.heightIndexDirty = false;

    const rows = [];
    const deltas = [];
    for (const [row, height] of this.rowHeights) {
      rows.push(row);
      deltas.push(height - ROW_HEIGHT);
    }

    // Rows are measured in the order they are scrolled past, so the map is usually
    // already sorted; the check is linear and skips the sort in the common case.
    let sorted = true;
    for (let i = 1; i < rows.length; i += 1) {
      if (rows[i] < rows[i - 1]) {
        sorted = false;
        break;
      }
    }

    let order = rows.map((_, position) => position);
    if (!sorted) order = order.sort((a, b) => rows[a] - rows[b]);

    const prefix = [0];
    for (const position of order) prefix.push(prefix[prefix.length - 1] + deltas[position]);

    this.heightRows = order.map((position) => rows[position]);
    this.heightPrefix = prefix;
  }

  /** Total height of the scrolling content, panels and separator strips included. */
  contentHeight() {
    return this.rowCount * ROW_HEIGHT
      + this.heightDeltaBefore(this.rowCount)
      + this.separatorShift(this.rowCount)
      + this.panelHeight();
  }

  /**
   * Forgets the measured heights. Needed whenever the wrapping changes: a new table, a
   * new font size, or a column that was resized. `from` keeps the rows above an edit,
   * which cannot have moved.
   */
  clearRowHeights(from = 0) {
    if (this.rowHeights.size === 0) return;
    for (const row of [...this.rowHeights.keys()]) {
      if (row >= from) this.rowHeights.delete(row);
    }
    this.heightIndexDirty = true;
  }

  /** Row index at a y offset inside the scrolling content (binary search over `topOf`). */
  rowIndexAt(y) {
    if (this.rowCount === 0) return 0;

    let lo = 0;
    let hi = this.rowCount - 1;
    let best = 0;
    while (lo <= hi) {
      const mid = (lo + hi) >> 1;
      if (this.topOf(mid) <= y) {
        best = mid;
        lo = mid + 1;
      } else {
        hi = mid - 1;
      }
    }

    // A separator strip sits above its row, so a click on it lands in the gap before that
    // row: it belongs to the row below it, which is the one the separator introduces.
    const next = best + 1;
    if (next < this.rowCount && this.separatorTitles.has(next)
        && y >= this.topOf(next) - ROW_HEIGHT) {
      return next;
    }
    return best;
  }

  /** Rows to draw for a scroll position: the visible ones plus a little overscan. */
  visibleRange(scrollTop, viewportHeight) {
    if (this.rowCount === 0) return { first: 0, last: 0 };

    const anchor = this.rowIndexAt(scrollTop);
    const first = Math.max(0, anchor - OVERSCAN);
    const limit = scrollTop + viewportHeight + OVERSCAN * ROW_HEIGHT;

    let last = anchor;
    while (last < this.rowCount && this.topOf(last) <= limit) last += 1;

    return { first, last: Math.min(this.rowCount, last + OVERSCAN) };
  }

  /**
   * Grows the rows that need it, after the visible ones are in the DOM.
   *
   * The content element is measured, never the row: a row's height therefore cannot feed
   * back into its own measurement, which is what keeps this from oscillating. All reads
   * happen before the first write, so the browser lays the table out once.
   */
  measureRows() {
    let changed = false;

    for (const row of this.rows.children) {
      const index = Number(row.dataset.row);
      if (!Number.isFinite(index)) continue;

      // A row whose values have not arrived yet only shows the "..." placeholder. It
      // says nothing about how tall the real content is, and measuring it used to forget
      // the height the row had, which shortened the table under the viewport.
      if (!this.cache.has(index)) continue;

      let needed = ROW_HEIGHT;

      // A separator is only a line drawn above a row: the row still shows its values, so
      // it is measured and grows like any other row.
      for (const cell of row.children) {
        if (!cell.classList.contains('cell')) continue;
        const content = cell.firstElementChild;
        if (!content) continue;
        const height = content.getBoundingClientRect().height + CELL_CHROME;
        if (height > needed) needed = height;
      }

      needed = Math.min(Math.round(needed), Math.round(ROW_HEIGHT * MAX_ROW_FACTOR));
      const known = this.rowHeights.get(index);

      if (needed <= ROW_HEIGHT) {
        if (known !== undefined) {
          this.rowHeights.delete(index);
          this.heightIndexDirty = true;
          changed = true;
        }
      } else if (known !== needed) {
        this.rowHeights.set(index, needed);
        this.heightIndexDirty = true;
        changed = true;
      }
    }

    if (!changed) return;

    // Everything below a grown row moves, so the rows are placed again from the new
    // heights. Only the rendered window is touched: the rest is derived on demand.
    for (const row of this.rows.children) {
      const index = Number(row.dataset.row);
      if (Number.isFinite(index)) {
        row.style.top = this.topOf(index) + 'px';
        row.style.height = this.heightOf(index) + 'px';
        continue;
      }
      const separator = Number(row.dataset.sep);
      if (Number.isFinite(separator)) {
        row.style.top = this.topOf(separator) - ROW_HEIGHT + 'px';
      }
    }
    this.spacer.style.height = this.contentHeight() + 'px';
  }

  /** Height added above a row by the panels of the rows before it. */
  shiftBefore(index) {
    let shift = 0;
    for (const panel of this.panels.values()) {
      if (panel.row < index) shift += panel.height;
    }
    return shift;
  }

  /** Panels in drawing order: by row, then by column. */
  orderedPanels() {
    return [...this.panels.values()].sort((a, b) => (a.row - b.row) || (a.col - b.col));
  }

  /** Total height taken by the open sub-tables. */
  panelHeight() {
    let total = 0;
    for (const panel of this.panels.values()) total += panel.height;
    return total;
  }

  /** Where a panel starts: exactly where the next row would have been, pushed down by
   *  the panels drawn above it (its own row included, for a second column of that row). */
  panelTop(panel) {
    // The strip of row+1 belongs below the panel, so it is not counted here: the panel is
    // anchored to the bottom of its own row.
    let top = (panel.row + 1) * ROW_HEIGHT
      + this.heightDeltaBefore(panel.row + 1)
      + this.separatorShift(panel.row);
    for (const other of this.orderedPanels()) {
      if (other === panel) break;
      top += other.height;
    }
    return top;
  }

  /** Opens a sub-table under a row, or moves it when it is already open. */
  mountPanel(id, row, col, node, height) {
    const existing = this.panels.get(id);
    if (existing) {
      existing.node.remove();
      this.panels.delete(id);
    }

    this.panels.set(id, { id, row, col, height, node });
    node.classList.add('sub-expansion');
    this.body.appendChild(node);
    this.layoutPanels();

    // A list opened on one of the last rows would otherwise grow below the viewport.
    const top = this.panelTop(this.panels.get(id));
    const bottom = top + height;
    const viewBottom = this.body.scrollTop + this.body.clientHeight;
    if (bottom > viewBottom) {
      this.body.scrollTop = Math.min(top, bottom - this.body.clientHeight);
    }

    this.scheduleRender();
  }

  /** Grows or shrinks an open sub-table (a row was added, or the window was resized). */
  resizePanel(id, height) {
    const panel = this.panels.get(id);
    if (!panel || panel.height === height) return;
    panel.height = height;
    this.layoutPanels();
    this.scheduleRender();
  }

  /** Places every panel and keeps it inside the visible part of the table. */
  layoutPanels() {
    if (this.panels.size === 0) return;
    // The panel belongs to a row, not to a column: it is drawn as wide as the window and
    // follows the horizontal scroll, so a list in the last column is never half hidden.
    const width = Math.max(320, this.body.clientWidth - 2);
    for (const panel of this.panels.values()) {
      panel.node.style.top = this.panelTop(panel) + 'px';
      panel.node.style.left = this.body.scrollLeft + 'px';
      panel.node.style.width = width + 'px';
    }
  }

  removePanel(id) {
    const panel = this.panels.get(id);
    if (!panel) return;
    this.panels.delete(id);
    if (panel.node.parentElement) panel.node.remove();
    this.scheduleRender();
  }

  clearPanels() {
    if (this.panels.size === 0) return;
    for (const panel of this.panels.values()) {
      if (panel.node.parentElement) panel.node.remove();
    }
    this.panels.clear();
    this.scheduleRender();
  }

  hasPanel(id) {
    return this.panels.has(id);
  }

  /** The panel element a DOM node belongs to, or null when it is not inside one. */
  panelOf(node) {
    const host = node && node.closest ? node.closest('.sub-expansion') : null;
    if (!host) return null;
    for (const panel of this.panels.values()) {
      if (panel.node === host) return panel;
    }
    return null;
  }

  render() {
    // An open editor keeps its DOM node alive; re-rendering would destroy it.
    if (this.suspendRender) return;

    const scrollTop = this.body.scrollTop;
    const scrollLeft = this.body.scrollLeft;
    const viewportHeight = this.body.clientHeight;

    this.spacer.style.width = this.totalWidth + 'px';
    this.spacer.style.height = this.contentHeight() + 'px';

    this.head.style.transform = `translateX(${-scrollLeft}px)`;

    const { first, last } = this.visibleRange(scrollTop, viewportHeight);

    this.ensureRows(first, last);

    const fragment = document.createDocumentFragment();
    const selection = this.selectionBounds();

    for (let index = first; index < last; index += 1) {
      fragment.appendChild(this.buildRow(index, selection));
    }

    // Separator strips belong to the same window of rows: one just above `first` can still
    // be on screen, and one at `last` is only half a row below the fold.
    for (const separator of this.separatorRows) {
      if (separator < first - 1 || separator > last) continue;
      fragment.appendChild(this.buildSeparator(separator));
    }

    this.rows.innerHTML = '';
    this.rows.appendChild(fragment);

    // A row that turned out taller moves everything below it, so the heights are settled
    // before the row numbers and the panels are placed.
    this.measureRows();

    this.renderGutter(first, last, scrollTop, selection);
    this.layoutPanels();

    if (this.rowCount === 0) {
      const empty = document.createElement('div');
      empty.className = 'grid-row';
      empty.style.top = '0px';
      empty.style.padding = '8px';
      empty.style.color = 'var(--text-faint)';
      empty.textContent = 'This table has no rows.';
      this.rows.appendChild(empty);
    }

    if (this.onRendered) this.onRendered();
  }

  /**
   * Paints the row numbers for the visible window. The gutter does not scroll with the
   * grid, so each number is offset by the current scroll position instead.
   */
  renderGutter(first, last, scrollTop, selection) {
    if (!this.gutter) return;

    const fragment = document.createDocumentFragment();

    for (let index = first; index < last; index += 1) {
      const cell = document.createElement('div');
      cell.className = 'gutter-cell';
      if (index % 2 === 1) cell.classList.add('alt');
      if (this.separatorTitles.has(index)) cell.classList.add('sep');
      cell.style.top = this.topOf(index) - scrollTop + 'px';
      cell.style.height = this.heightOf(index) + 'px';
      cell.dataset.row = String(index);
      cell.textContent = String(index + 1);
      cell.title = `Row ${index + 1}`;
      fragment.appendChild(cell);
    }

    // The strip the separator is drawn in also takes its row in the gutter, so the column
    // of numbers stays continuous instead of showing a hole.
    for (const separator of this.separatorRows) {
      if (separator < first - 1 || separator > last) continue;
      const band = document.createElement('div');
      band.className = 'gutter-cell sep-band';
      band.style.top = this.topOf(separator) - ROW_HEIGHT - scrollTop + 'px';
      band.style.height = ROW_HEIGHT + 'px';
      fragment.appendChild(band);
    }

    this.gutter.innerHTML = '';
    this.gutter.appendChild(fragment);
    this.paintGutterSelection(selection);
    // A render (a scroll while dragging, an incoming edit) rebuilds the numbers, so the
    // drag marks are put back on the fresh nodes.
    if (this.rowDrag && this.rowDrag.active) this.paintDropTarget(this.rowDrag);
  }

  buildRow(index, selection) {
    const row = document.createElement('div');
    row.className = 'grid-row' + (index % 2 === 1 ? ' alt' : '');
    row.style.top = this.topOf(index) + 'px';
    row.style.height = this.heightOf(index) + 'px';
    row.style.width = this.totalWidth + 'px';
    row.dataset.row = String(index);

    // A separator is not part of the row: it is a strip of its own drawn above it (see
    // buildSeparator). The row below keeps its values, its height and its editing.

    if (selection && index >= selection.top && index <= selection.bottom && selection.multiRow) {
      row.classList.add('selected-row');
    }

    const data = this.cache.get(index);

    this.columns.forEach((column, colIndex) => {
      const cell = document.createElement('div');
      cell.className = 'cell';
      if (NUMERIC_TYPES.has(column.type)) cell.classList.add('num');
      cell.style.width = column.width + 'px';
      cell.dataset.col = String(colIndex);
      cell.dataset.row = String(index);

      // Find hits are painted as an extra layer, not as the cell's background, so a
      // coloured cell keeps its fill while it is highlighted.
      const hitKey = index + ':' + colIndex;
      if (this.searchHits.has(hitKey)) {
        cell.classList.add('search-hit');
        if (hitKey === this.searchCurrent) cell.classList.add('search-current');
      }

      const value = data ? data.cells[colIndex] : null;

      // An open sub-table replaces the value it belongs to: the cell shows a marker
      // instead of the text, so the expanded state is visible at a glance (the full text
      // stays in the tooltip, and closing the panel brings it back).
      const expanded = this.panels.has(panelId(index, colIndex));

      if (expanded) {
        cell.classList.add('expanded');
        const marker = document.createElement('span');
        marker.className = 'cell-value expanded-marker';
        marker.textContent = '\u2026';
        cell.appendChild(marker);
        if (value && value.t) cell.title = value.t;
      } else if (data) {
        if (value) {
          // Image columns store a path, so the cell shows a thumbnail beside it (R9).
          if (column.type === 'Image') this.buildImageCell(cell, value.t);
          // A colour cell shows the colour; the hex value stays in the tooltip.
          else if (column.type === 'Color') this.buildColorCell(cell, value.t);
          else this.setCellText(cell, value.t);

          if (value.s && value.s !== 'ok') cell.classList.add('state-' + value.s);
          // A multi-line cell already uses its tooltip for the full text, so a
          // validation detail is prepended instead of replacing it.
          if (value.d) {
            cell.title = cell.classList.contains('multiline') && cell.title
              ? value.d + '\n\n' + cell.title
              : value.d;
          }
        }
      } else {
        cell.textContent = '\u2026';
        cell.classList.add('state-empty');
      }

      const isSelected = selection && selection.contains(index, colIndex);
      // A block selection has no single focused cell (see paintSelection).
      const isCursor = Boolean(selection) &&
        selection.top === selection.bottom && selection.left === selection.right &&
        index === this.cursor.row && colIndex === this.cursor.col;

      // Priority: cell colour, then row colour, then column colour.
      // Selection highlighting wins so the selection always stays visible.
      const background = (value && value.bg) || (data && data.bg) || column.bg;
      // Always paint the fill: the selection is a tint over it, not a replacement.
      if (background) cell.style.backgroundColor = background;

      if (isSelected && !isCursor) cell.classList.add('in-selection');

      if (isCursor) {
        cell.classList.add('cursor');
        // The row of a separator is a normal data row, so the cursor box is shown there
        // like everywhere else: the divider is drawn above the row, not over it.
        if (this.focus || this.interacted) {
          cell.classList.add('live');
          if (!this.focus) cell.classList.add('unfocused');
        }
      }

      row.appendChild(cell);
    });

    return row;
  }

  /**
   * The strip a separator is drawn as. It is one row tall and sits right above the row it
   * belongs to, so that row (and everything below it) moves down instead of the divider
   * being painted over a data row.
   */
  buildSeparator(index) {
    const band = document.createElement('div');
    band.className = 'grid-row sep-band';
    band.style.top = this.topOf(index) - ROW_HEIGHT + 'px';
    band.style.height = ROW_HEIGHT + 'px';
    band.style.width = this.totalWidth + 'px';
    band.dataset.sep = String(index);

    const title = this.separatorTitles.get(index) || '';
    band.title = title ? `Separator: ${title}` : 'Separator';

    // The name has to be visible, not only in the tooltip: a named separator is a heading
    // for the rows below it.
    if (title) {
      const label = document.createElement('div');
      label.className = 'sep-label';
      label.textContent = title;
      band.appendChild(label);
    }

    return band;
  }

  /**
   * Writes a text value into a cell. Line breaks stay line breaks: the cell wraps
   * (white-space: pre-wrap), so a multi-line value reads as several lines, and the full
   * text is also kept in the tooltip because the row height still caps the display.
   */
  setCellText(cell, text) {
    const span = document.createElement('span');
    span.className = 'cell-value';
    span.textContent = typeof text === 'string' ? text.replace(/\r\n/g, '\n') : text;

    if (typeof text === 'string' && text.indexOf('\n') >= 0) {
      cell.classList.add('multiline');
      cell.title = text;
    } else if (typeof text === 'string' && text.length > 80) {
      // The cell shows two lines at most, so a long value keeps its whole text here.
      cell.title = text;
    }

    cell.appendChild(span);
  }

  /** Colour cells paint the colour instead of its hex value. */
  buildColorCell(cell, text) {
    cell.classList.add('has-swatch');

    const swatch = document.createElement('span');
    swatch.className = 'cell-swatch';
    const match = /#?([0-9a-fA-F]{6})/.exec(text || '');
    if (match) swatch.style.backgroundColor = '#' + match[1];
    cell.appendChild(swatch);

    if (text) cell.title = text;
  }

  /**
   * Draws an image cell: a small thumbnail followed by the stored path. The host turns the
   * path into a data URL; until it arrives the slot stays empty so the row height never
   * jumps. A path that cannot be read keeps the text and gains a marker.
   */
  buildImageCell(cell, path) {
    cell.classList.add('has-thumb');

    const thumb = document.createElement('img');
    thumb.className = 'cell-thumb';
    thumb.alt = '';
    thumb.draggable = false;

    const url = imageUrl(path);
    if (url) {
      thumb.src = url;
    } else if (imageError(path)) {
      thumb.classList.add('failed');
      cell.classList.add('state-nothumb');
    } else {
      requestImage(path);
    }

    const text = document.createElement('span');
    text.className = 'cell-text';
    text.textContent = path;

    cell.appendChild(thumb);
    cell.appendChild(text);
  }

  /** Requests any rows in the window that are not cached yet. */
  ensureRows(first, last) {
    // A sub-table holds its rows in memory, so there is nothing to ask for.
    if (this.loadRows) {
      this.loadRows(first, last);
      return;
    }

    let missing = false;
    for (let index = first; index < last; index += 1) {
      if (!this.cache.has(index)) { missing = true; break; }
    }
    if (!missing) return;
    if (this.pendingStart === first && this.pendingCount === last - first) return;

    this.pendingStart = first;
    this.pendingCount = last - first;
    send('requestRows', { sheet: this.sheet, start: first, count: last - first });
  }

  // ------------------------------------------------------------- selection

  selectionBounds() {
    const top = Math.min(this.cursor.row, this.anchor.row);
    const bottom = Math.max(this.cursor.row, this.anchor.row);
    const left = Math.min(this.cursor.col, this.anchor.col);
    const right = Math.max(this.cursor.col, this.anchor.col);
    const multiRow = top !== bottom;

    return {
      top, bottom, left, right, multiRow,
      contains(row, col) {
        return row >= top && row <= bottom && col >= left && col <= right;
      },
    };
  }

  emitCursor() {
    const bounds = this.selectionBounds();
    this.onCursorChanged({
      sheet: this.sheet,
      row: this.cursor.row,
      column: this.cursor.col,
      anchor: { ...this.anchor },
      rowCount: this.rowCount,
      columnCount: this.columns.length,
      selection: {
        top: bounds.top,
        bottom: bounds.bottom,
        left: bounds.left,
        right: bounds.right,
        rows: bounds.bottom - bounds.top + 1,
        columns: bounds.right - bounds.left + 1,
      },
    });
    this.onSelectionChanged(bounds);
  }

  /**
   * Moves the cursor. `reveal` scrolls the row into view; undo and redo turn it off,
   * because they move the cursor to the change without taking the viewport along.
   * `focus` keeps the keyboard where it is — find/replace steps the cursor while the
   * user is still typing in the find field, which must not lose focus.
   */
  moveCursor(row, col, extend = false, reveal = true, focus = true) {
    if (this.rowCount === 0 || this.columns.length === 0) return;

    const nextRow = clamp(row, 0, this.rowCount - 1);
    const nextCol = clamp(col, 0, this.columns.length - 1);
    if (nextRow === this.cursor.row && nextCol === this.cursor.col && !extend) return;

    if (!extend) this.anchor = { row: nextRow, col: nextCol };
    this.cursor = { row: nextRow, col: nextCol };

    const scrollTop = this.body.scrollTop;
    const scrollLeft = this.body.scrollLeft;
    if (reveal) this.scrollIntoView();

    // Only scrolling needs new rows; everything else is a repaint of the ones already
    // on screen, which keeps the element under the pointer alive between mouse events.
    if (this.body.scrollTop !== scrollTop || this.body.scrollLeft !== scrollLeft) {
      this.scheduleRender();
    } else {
      this.paintSelection();
    }

    if (focus && !this.isEditing()) this.focusProxy();
    this.emitCursor();
  }

  scrollIntoView() {
    const top = this.topOf(this.cursor.row);
    const bottom = top + this.heightOf(this.cursor.row);
    const viewTop = this.body.scrollTop;
    const viewBottom = viewTop + this.body.clientHeight;

    if (top < viewTop) this.body.scrollTop = top;
    else if (bottom > viewBottom) this.body.scrollTop = bottom - this.body.clientHeight;

    let left = 0;
    for (let i = 0; i < this.cursor.col; i += 1) left += this.columns[i].width;
    const right = left + this.columns[this.cursor.col].width;
    const viewLeft = this.body.scrollLeft;
    const viewRight = viewLeft + this.body.clientWidth;

    if (left < viewLeft) this.body.scrollLeft = left;
    else if (right > viewRight) this.body.scrollLeft = right - this.body.clientWidth;
  }

  // ------------------------------------------------------------- input

  onMouseDown(event) {
    // A sub-table has its own grid with its own listeners; a click inside one belongs to
    // it, not to the table that hosts it.
    if (this.panelOf(event.target)) return;

    const cell = event.target.closest('.cell');
    if (!cell) return;
    if (!this.rows.contains(cell)) return;

    this.body.focus();
    this.focusProxy();
    const row = Number(cell.dataset.row);
    const col = Number(cell.dataset.col);
    this.moveCursor(row, col, event.shiftKey);
    this.dragging = true;
    this.dragMoved = false;
    event.preventDefault();
  }

  /**
   * The row number is the row's handle: a click selects the row, a vertical drag moves it.
   * Shift-click still extends the selection, so range selection is not lost.
   */
  onGutterMouseDown(event) {
    const cell = event.target.closest('.gutter-cell');
    if (!cell || event.button !== 0) return;

    this.body.focus();
    this.focusProxy();
    this.selectRow(Number(cell.dataset.row), event.shiftKey);
    this.beginRowDrag(Number(cell.dataset.row), event);
    event.preventDefault();
  }

  /**
   * Starts a possible row move. Nothing happens until the pointer travels far enough to
   * mean "drag" rather than "click", so a plain click still only selects the row.
   */
  beginRowDrag(from, event) {
    if (!this.onRowMove || this.rowCount < 2) return;

    const state = { from, startY: event.clientY, active: false, insert: from };
    this.rowDrag = state;

    const onMove = (moveEvent) => {
      if (!state.active) {
        if (Math.abs(moveEvent.clientY - state.startY) < 4) return;
        state.active = true;
      }

      this.autoScrollFor(moveEvent.clientY);
      const hit = this.rowAtY(moveEvent.clientY);
      if (hit) state.insert = hit.after ? hit.row + 1 : hit.row;
      this.paintDropTarget(state);
    };

    const onUp = () => {
      document.removeEventListener('mousemove', onMove);
      document.removeEventListener('mouseup', onUp);
      this.rowDrag = null;
      this.clearDropTarget();

      if (!state.active) return;

      // Dropping "after" a row that is above the dragged one cancels out the gap the row
      // leaves behind when it is taken out of the list.
      let to = state.insert > state.from ? state.insert - 1 : state.insert;
      to = clamp(to, 0, this.rowCount - 1);
      if (to !== state.from) this.onRowMove(state.from, to);
    };

    document.addEventListener('mousemove', onMove);
    document.addEventListener('mouseup', onUp);
  }

  /** Row under a viewport y, and whether the pointer is in its lower half. */
  rowAtY(clientY) {
    if (!this.gutter) return null;

    const rect = this.gutter.getBoundingClientRect();
    const y = clientY - rect.top;

    let best = null;
    let bestDistance = Infinity;

    for (const cell of this.gutter.children) {
      const top = parseFloat(cell.style.top) || 0;
      const height = this.heightOf(Number(cell.dataset.row));
      const bottom = top + height;
      const row = Number(cell.dataset.row);

      if (y >= top && y < bottom) return { row, after: y - top > height / 2 };

      const distance = y < top ? top - y : y - bottom;
      if (distance < bestDistance) {
        bestDistance = distance;
        best = { row, after: y > top + height / 2 };
      }
    }

    return best;
  }

  paintDropTarget(state) {
    this.clearDropTarget();

    const dragged = this.gutter.querySelector(`.gutter-cell[data-row="${state.from}"]`);
    if (dragged) dragged.classList.add('dragging');

    const last = this.rowCount - 1;
    const at = clamp(state.insert, 0, this.rowCount);
    const cell = at <= last
      ? this.gutter.querySelector(`.gutter-cell[data-row="${at}"]`)
      : this.gutter.querySelector(`.gutter-cell[data-row="${last}"]`);

    if (cell) cell.classList.add(at <= last ? 'drop-before' : 'drop-after');
  }

  clearDropTarget() {
    if (!this.gutter) return;
    for (const cell of this.gutter.querySelectorAll('.drop-before, .drop-after, .dragging')) {
      cell.classList.remove('drop-before', 'drop-after', 'dragging');
    }
  }

  /** Scrolls while a row is dragged past the edge of the viewport. The margin is small on
   *  purpose: a wide one made the table run away from the pointer during a drag. */
  autoScrollFor(clientY) {
    const rect = this.body.getBoundingClientRect();
    const margin = 14;
    if (clientY < rect.top + margin) this.body.scrollTop -= ROW_HEIGHT / 2;
    else if (clientY > rect.bottom - margin) this.body.scrollTop += ROW_HEIGHT / 2;
  }

  /** Right-click on a row number: the same actions as on the row, offered where the row
   *  is grabbed. */
  onGutterContextMenu(event) {
    const cell = event.target.closest('.gutter-cell');
    if (!cell) return;
    event.preventDefault();

    const row = Number(cell.dataset.row);
    if (!this.selectionBounds().contains(row, this.cursor.col)) this.selectRow(row);

    if (this.onGutterMenu) {
      this.onGutterMenu({ row, rowCount: this.rowCount, x: event.clientX, y: event.clientY });
    }
  }

  /** Dragging across cells extends the selection, like a spreadsheet. */
  onMouseMove(event) {
    if (!this.dragging) return;
    const cell = event.target.closest('.cell');
    if (!cell || !this.rows.contains(cell)) return;
    const row = Number(cell.dataset.row);
    const col = Number(cell.dataset.col);
    if (row === this.cursor.row && col === this.cursor.col) return;
    // The pointer actually travelled, so the release must not count as a click.
    this.dragMoved = true;
    this.moveCursor(row, col, true);
  }

  onDoubleClick(event) {
    if (this.panelOf(event.target)) return;
    const cell = event.target.closest('.cell');
    if (!cell || !this.rows.contains(cell) || !this.onEditRequest) return;
    this.onEditRequest(Number(cell.dataset.row), Number(cell.dataset.col));
    event.preventDefault();
  }

  onHeaderMouseDown(event) {
    const resizer = event.target.closest('.head-resizer');
    if (resizer) {
      this.beginResize(Number(resizer.dataset.col), event);
      return;
    }

    const cell = event.target.closest('.head-cell');
    if (!cell) return;
    this.moveCursor(this.cursor.row, Number(cell.dataset.col), event.shiftKey);
    event.preventDefault();
  }

  /** Drags a column edge; the width is committed to the side-car on mouse up. */
  beginResize(col, event) {
    const column = this.columns[col];
    if (!column) return;

    const startX = event.clientX;
    const startWidth = column.width;

    const onMove = (moveEvent) => {
      const width = Math.max(40, Math.round(startWidth + (moveEvent.clientX - startX)));
      if (width === column.width) return;
      column.width = width;
      this.renderHead();
      this.scheduleRender();
    };

    const onUp = () => {
      document.removeEventListener('mousemove', onMove);
      document.removeEventListener('mouseup', onUp);
      if (this.onColumnResized) this.onColumnResized(this.sheet, column.name, column.width);
    };

    document.addEventListener('mousemove', onMove);
    document.addEventListener('mouseup', onUp);
    event.preventDefault();
    event.stopPropagation();
  }

  /**
   * Selects one whole row: the anchor sits on the last column and the cursor on the
   * first, so the range covers every column while the cursor keeps a sensible cell to
   * type into. `extend` keeps the anchor and grows the block, like shift-clicking.
   */
  selectRow(row, extend = false) {
    if (this.rowCount === 0 || this.columns.length === 0) return;

    const target = clamp(row, 0, this.rowCount - 1);
    const lastCol = this.columns.length - 1;
    if (!extend) this.anchor = { row: target, col: lastCol };
    this.cursor = { row: target, col: 0 };

    const scrollTop = this.body.scrollTop;
    const scrollLeft = this.body.scrollLeft;
    this.scrollIntoView();
    if (this.body.scrollTop !== scrollTop || this.body.scrollLeft !== scrollLeft) {
      this.scheduleRender();
    } else {
      this.paintSelection();
    }

    if (!this.isEditing()) this.focusProxy();
    this.emitCursor();
  }

  /** Selects the whole table. */
  selectAll() {
    if (this.rowCount === 0 || this.columns.length === 0) return;
    this.anchor = { row: 0, col: 0 };
    this.cursor = { row: this.rowCount - 1, col: this.columns.length - 1 };
    this.scheduleRender();
    this.emitCursor();
  }

  /** Right-click on a cell or a header: move the cursor there and ask the app for a menu. */
  onContextMenuEvent(event, isHeader) {
    if (this.panelOf(event.target)) return;
    event.preventDefault();

    const cell = event.target.closest(isHeader ? '.head-cell' : '.cell');
    if (!cell) return;
    if (!isHeader && !this.rows.contains(cell)) return;

    const column = Number(cell.dataset.col);

    if (isHeader) {
      this.moveCursor(this.cursor.row, column, event.shiftKey);
    } else {
      const row = Number(cell.dataset.row);
      // Right-clicking inside the current selection keeps it; outside moves it.
      if (!this.selectionBounds().contains(row, column)) this.moveCursor(row, column, false);
    }

    if (this.onContextMenu) {
      this.onContextMenu({
        row: this.cursor.row,
        column: this.cursor.col,
        header: isHeader,
        x: event.clientX,
        y: event.clientY,
      });
    }
  }

  /**
   * A click on a List / Properties cell opens its sub-table; clicking the same cell again
   * closes it. A drag is a selection, not a toggle.
   */
  onClick(event) {
    if (this.dragMoved) return;
    if (this.panelOf(event.target)) return;

    const cell = event.target.closest('.cell');
    if (!cell || !this.rows.contains(cell)) return;

    const col = Number(cell.dataset.col);
    const column = this.columns[col];
    if (!column || (column.type !== 'List' && column.type !== 'Properties')) return;

    const row = Number(cell.dataset.row);
    const id = panelId(row, col);

    if (this.panels.has(id)) {
      if (this.onCollapseRequest) this.onCollapseRequest(row, col);
    } else if (this.onExpandRequest) {
      this.onExpandRequest(row, col);
    }
  }

  onKeyDown(event) {
    // The open sub-table owns the keyboard while the focus is inside it.
    if (this.panelOf(event.target)) return;
    if (this.rowCount === 0 || this.isEditing()) return;

    const extend = event.shiftKey;
    const page = Math.max(1, Math.floor(this.body.clientHeight / ROW_HEIGHT) - 1);
    let handled = true;

    switch (event.key) {
      case 'ArrowUp':
        if (event.ctrlKey && !extend) this.moveRowBy(-1);
        else this.moveCursor(this.cursor.row - 1, this.cursor.col, extend);
        break;
      case 'ArrowDown':
        if (event.ctrlKey && !extend) this.moveRowBy(1);
        else this.moveCursor(this.cursor.row + 1, this.cursor.col, extend);
        break;
      case 'ArrowLeft': this.moveCursor(this.cursor.row, this.cursor.col - 1, extend); break;
      case 'ArrowRight': this.moveCursor(this.cursor.row, this.cursor.col + 1, extend); break;
      case 'PageUp': this.moveCursor(this.cursor.row - page, this.cursor.col, extend); break;
      case 'PageDown': this.moveCursor(this.cursor.row + page, this.cursor.col, extend); break;
      case 'Home':
        if (event.ctrlKey) this.moveCursor(0, 0, extend);
        else this.moveCursor(this.cursor.row, 0, extend);
        break;
      case 'End':
        if (event.ctrlKey) this.moveCursor(this.rowCount - 1, this.columns.length - 1, extend);
        else this.moveCursor(this.cursor.row, this.columns.length - 1, extend);
        break;
      case 'Tab':
        if (event.shiftKey) {
          if (this.cursor.col === 0) this.moveCursor(this.cursor.row - 1, this.columns.length - 1);
          else this.moveCursor(this.cursor.row, this.cursor.col - 1);
        } else if (this.cursor.col === this.columns.length - 1) {
          this.moveCursor(this.cursor.row + 1, 0);
        } else {
          this.moveCursor(this.cursor.row, this.cursor.col + 1);
        }
        break;
      case 'Enter':
      case 'F2':
        if (this.onEditRequest) this.onEditRequest(this.cursor.row, this.cursor.col);
        break;
      case 'Delete':
      case 'Backspace':
        if (this.onClearRequest) this.onClearRequest();
        break;
      case ' ':
        if (this.currentType() === 'Bool' && this.onToggleRequest) {
          this.onToggleRequest(this.cursor.row, this.cursor.col);
        } else if (this.onEditRequest) {
          this.onEditRequest(this.cursor.row, this.cursor.col, ' ');
        }
        break;
      default:
        handled = false;
        // A key typed into the proxy already reaches the cell through its input event,
        // which is what keeps an IME composition in one piece. Re-opening the editor here
        // would steal focus and break the composition, so this branch only runs when the
        // key came from somewhere else (the tests drive the body directly).
        if (event.target !== this.proxy &&
            event.key.length === 1 && !event.ctrlKey && !event.altKey && !event.metaKey && this.onEditRequest) {
          this.onEditRequest(this.cursor.row, this.cursor.col, event.key);
          handled = true;
        }
        break;
    }

    if (handled) event.preventDefault();
  }

  /** Moves the cursor row up or down by one (Ctrl+Up / Ctrl+Down). */
  moveRowBy(delta) {
    if (!this.onRowMove || this.rowCount < 2) return;
    const to = clamp(this.cursor.row + delta, 0, this.rowCount - 1);
    if (to === this.cursor.row) return;
    this.onRowMove(this.cursor.row, to);
  }

  currentType() {
    const column = this.columns[this.cursor.col];
    return column ? column.type : '';
  }

  /**
   * Drops cached rows so the next render refetches them from the host.
   *
   * `remeasure` throws the measured row heights away as well. Only a font change needs
   * that: after an edit or an undo the text may be different, but the rows are measured
   * again as they are painted, while discarding every height above the viewport shortens
   * the scrollable area and makes the browser clamp the scroll position upwards.
   */
  invalidate({ remeasure = false } = {}) {
    this.cache.clear();
    this.pendingStart = -1;
    this.pendingCount = 0;
    if (remeasure) this.clearRowHeights();
    this.scheduleRender();
  }
}

function clamp(value, min, max) {
  return value < min ? min : value > max ? max : value;
}

/** Key of the sub-table opened on a cell: one panel per cell, several per row. */
export function panelId(row, col) {
  return row + ':' + col;
}

function clampWidth(width) {
  if (typeof width !== 'number' || !Number.isFinite(width)) return DEFAULT_WIDTH;
  return Math.max(MIN_WIDTH, Math.round(width));
}
