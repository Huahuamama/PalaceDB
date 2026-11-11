// Second-level table of a List / Properties cell.
//
// A List cell holds an array of records, so editing one means editing a table inside a
// cell. Rather than a dialog, the rows are drawn as a real grid under the row they belong
// to: same header, same gutter, same cell editors, same keyboard. It is built on the very
// Grid and CellEditor classes the main table uses, with a store that writes into the cell
// value instead of the document, so every editing behaviour stays in one place.

import { Grid, ROW_HEIGHT, panelId } from './grid.js';
import { CellEditor } from './editors.js';

// The body shows between two and seven records and scrolls beyond that. The bounds are
// derived from the row height, which follows the table text size setting.
const MIN_ROWS = 2;
const MAX_ROWS = 7;

export class SubPanel {
  /**
   * @param {object} app application hooks: send, grid, onClose, promptFields
   * @param {object} payload the "subValue" message from the host
   * @param {number} col index of the column in the table above
   */
  constructor(app, payload, col) {
    this.app = app;
    this.host = app.grid;
    this.sheet = payload.sheet;
    this.row = payload.row;
    this.column = payload.column;
    this.col = col;
    this.kind = payload.kind === 'props' ? 'props' : 'list';
    this.id = panelId(payload.row, col);

    this.fields = [];
    this.rows = [];
    this.clipboard = null;
    this.timer = null;
    this.pendingBrowse = null;

    this.build();
    this.setData(payload);
  }

  // ------------------------------------------------------------- structure

  build() {
    const node = document.createElement('div');
    node.className = 'sub-expansion';
    node.dataset.panel = this.id;

    const bar = document.createElement('div');
    bar.className = 'sub-bar';

    const title = document.createElement('span');
    title.className = 'sub-title';
    bar.appendChild(title);
    this.titleNode = title;

    const actions = document.createElement('div');
    actions.className = 'sub-actions';
    bar.appendChild(actions);

    if (this.kind === 'list') {
      this.button(actions, '+ Row', 'Add a row to this list', () => this.addRow());
      this.button(actions, '- Row', 'Remove the selected row', () => this.removeRow());
    }
    this.button(actions, 'Fields...', 'Add, rename or remove the fields of this list',
      () => this.app.promptFields(this.sheet, this.column));

    const close = this.button(actions, '\u00d7', 'Close this list', () => this.app.closePanel(this.id));
    close.classList.add('sub-close');

    node.appendChild(bar);

    const body = document.createElement('div');
    body.className = 'sub-grid';

    const corner = document.createElement('div');
    corner.className = 'sub-corner';
    body.appendChild(corner);

    const headWrap = document.createElement('div');
    headWrap.className = 'sub-headwrap';
    const head = document.createElement('div');
    head.className = 'sub-head';
    headWrap.appendChild(head);
    body.appendChild(headWrap);

    const gutter = document.createElement('div');
    gutter.className = 'sub-gutter';
    body.appendChild(gutter);

    const scroller = document.createElement('div');
    scroller.className = 'sub-body';
    scroller.tabIndex = 0;
    const spacer = document.createElement('div');
    spacer.className = 'sub-spacer';
    const rows = document.createElement('div');
    rows.className = 'sub-rows';
    scroller.appendChild(spacer);
    scroller.appendChild(rows);
    body.appendChild(scroller);

    node.appendChild(body);
    this.node = node;
    this.body = scroller;

    // The sub-table owns Ctrl+C / Ctrl+X / Ctrl+V while it has the keyboard; without
    // this the document handler would copy the selection of the table above.
    node.addEventListener('keydown', (event) => this.onClipboardKey(event), true);

    this.grid = new Grid({
      headScroll: headWrap,
      head,
      body: scroller,
      spacer,
      rows,
      gutter,
      container: node,
    }, {
      onCursorChanged: () => this.updateTitle(),
      // A row inside the panel grows when its text wraps, so the panel has to follow the
      // table's own height instead of staying at the size it was opened with.
      onRendered: () => this.measure(),
      loadRows: (first, last) => this.loadRows(first, last),
      store: {
        setCell: (row, columnName, text) => this.setCell(row, columnName, text),
        refOptions: (columnName) => this.sendRefOptions(columnName),
        browse: (kind, columnName, current, row) => this.browse(kind, columnName, current, row),
        fillCells: (rowIndices, columnNames, text) => this.fillCells(rowIndices, columnNames, text),
      },
    });

    this.editor = new CellEditor(this.grid);
    this.grid.onColumnResized = null;
    this.grid.onClearRequest = () => this.clearSelection();
    // Ctrl+Enter fills the block with the cell the cursor sits on, like the table above.
    this.grid.onFillRequest = () => this.fillSelection();
    // The row numbers work here too: a list is a table, so its rows can be reordered by
    // dragging them, exactly like the rows of the table above.
    this.grid.onRowMove = (from, to) => this.moveRow(from, to);
    // Right-click behaves like it does above: the cell menu is about values, the header
    // menu about the fields, the row-number menu about the records.
    this.grid.onContextMenu = ({ header, x, y }) => this.showCellMenu(header, x, y);
    this.grid.onGutterMenu = ({ row, x, y }) => this.showRowMenu(row, x, y);
  }

  // ------------------------------------------------------------- context menus

  /** Menu for a cell (values) or for a column header (the field itself). */
  showCellMenu(isHeader, x, y) {
    if (!this.app.menu) return;
    const col = this.grid.cursor.col;
    const field = this.fields[col];
    if (!field) return;

    if (isHeader) {
      this.app.menu(x, y, [
        { label: 'Edit Column...', onSelect: () => this.editField(col) },
        { label: 'Insert Column...', onSelect: () => this.insertField(col + 1) },
        'sep',
        { label: 'Move Column Left', disabled: col === 0, onSelect: () => this.moveField(col, -1) },
        {
          label: 'Move Column Right',
          disabled: col === this.fields.length - 1,
          onSelect: () => this.moveField(col, 1),
        },
        'sep',
        {
          label: 'Delete Column',
          // A list without fields has nowhere to put a value, so the last one stays.
          disabled: this.fields.length <= 1,
          onSelect: () => this.deleteField(col),
        },
      ]);
      return;
    }

    this.app.menu(x, y, [
      { label: 'Cut', shortcut: 'Ctrl+X', onSelect: () => this.copySelection(true) },
      { label: 'Copy', shortcut: 'Ctrl+C', onSelect: () => this.copySelection(false) },
      { label: 'Paste', shortcut: 'Ctrl+V', disabled: !this.clipboard, onSelect: () => this.paste() },
      'sep',
      { label: 'Clear Cells', shortcut: 'Del', onSelect: () => this.clearSelection() },
      { label: 'Select All', shortcut: 'Ctrl+A', onSelect: () => this.grid.selectAll() },
    ]);
  }

  /** Menu for a row number: the records of the list, in the order they are stored. */
  showRowMenu(row, x, y) {
    if (!this.app.menu) return;
    const count = this.grid.selectedRowIndices().length;
    this.app.menu(x, y, [
      { label: 'Insert Row Above', onSelect: () => this.addRow(row - 1) },
      { label: 'Insert Row Below', onSelect: () => this.addRow(row) },
      'sep',
      {
        label: count > 1 ? `Delete ${count} Rows` : 'Delete Row',
        disabled: this.kind !== 'list',
        onSelect: () => this.removeRow(),
      },
    ]);
  }

  // ------------------------------------------------------------- fields

  /** The field list as the document stores it: a name and an encoded type each. */
  fieldList() {
    return this.fields.map((field) => ({ name: field.name, type: fieldTypeStr(field) }));
  }

  /** Hands a new field list to the host, which rewrites the sub-table in one undo step. */
  applyFields(list) {
    // A cell edit may still be waiting on its timer; it has to land before the fields do,
    // or it would be written back in the old shape.
    this.flush();
    this.app.send('setSubFields', {
      sheet: this.sheet,
      row: this.row,
      column: this.column,
      fields: list,
    });
  }

  editField(index) {
    if (!this.app.promptSubField) return;
    this.app.promptSubField({
      sheet: this.sheet,
      row: this.row,
      column: this.column,
      fields: this.fieldList(),
      index,
      isNew: false,
    });
  }

  insertField(index) {
    if (!this.app.promptSubField) return;
    this.app.promptSubField({
      sheet: this.sheet,
      row: this.row,
      column: this.column,
      fields: this.fieldList(),
      index,
      isNew: true,
    });
  }

  moveField(index, delta) {
    const target = index + delta;
    if (target < 0 || target >= this.fields.length) return;

    const list = this.fieldList();
    const [field] = list.splice(index, 1);
    list.splice(target, 0, field);
    this.applyFields(list);
  }

  deleteField(index) {
    if (this.fields.length <= 1) return;

    const name = this.fields[index].name;
    const remove = () => {
      const list = this.fieldList();
      list.splice(index, 1);
      this.applyFields(list);
    };

    if (!this.app.confirm) {
      remove();
      return;
    }

    this.app.confirm({
      title: `Delete column '${name}'?`,
      message: 'The values stored in this field are removed from every record of the list. '
        + 'This can be undone with Ctrl+Z.',
      okLabel: 'Delete',
      onOk: remove,
    });
  }

  /** Delete clears the selected cells of the sub-table, like it does above. */
  clearSelection() {
    const bounds = this.grid.selectionBounds();
    let changed = false;
    for (let row = bounds.top; row <= bounds.bottom; row += 1) {
      for (let col = bounds.left; col <= bounds.right; col += 1) {
        const cell = this.rows[row] && this.rows[row][col];
        if (!cell || (cell.v === '' && cell.t === '')) continue;
        this.rows[row][col] = { t: '', v: '' };
        changed = true;
      }
    }
    if (!changed) return;
    this.grid.cache.clear();
    this.grid.invalidate();
    this.commit();
  }

  button(parent, label, title, onClick) {
    const button = document.createElement('button');
    button.type = 'button';
    button.className = 'tool sub-tool';
    button.textContent = label;
    button.title = title;
    button.addEventListener('mousedown', (event) => event.preventDefault());
    button.addEventListener('click', onClick);
    parent.appendChild(button);
    return button;
  }

  // ------------------------------------------------------------- data

  /** Replaces the content with a fresh "subValue" payload (also used after an edit). */
  setData(payload) {
    this.fields = (payload.columns || []).map((field) => ({
      name: field.name,
      type: field.type,
      typeStr: field.typeStr || '',
      optional: Boolean(field.optional),
      values: field.values || [],
      enumStr: Boolean(field.enumStr),
      options: field.options || [],
      width: 0,
    }));

    this.rows = (payload.rows || []).map((row) => this.fields.map((field, col) => {
      const cell = row[col];
      return {
        t: cell && cell.t !== undefined ? String(cell.t) : '',
        v: cell && cell.v !== undefined ? String(cell.v) : '',
      };
    }));

    this.layoutColumns();
    this.grid.cache.clear();
    this.grid.setSheet({
      name: this.sheet + '@' + this.column + '#' + this.row,
      columns: this.fields.map((field) => ({
        name: field.name,
        type: field.type,
        values: field.values,
        enumStr: field.enumStr,
        optional: field.optional,
        width: field.width,
      })),
      rowCount: this.rows.length,
    });
    // The panel keeps its cursor when it is refreshed after an edit elsewhere; only the
    // first load starts at the top left.
    if (!this.loaded) {
      this.grid.cursor = { row: 0, col: 0 };
      this.grid.anchor = { row: 0, col: 0 };
      this.loaded = true;
    }
    this.measure();
    this.updateTitle();
  }

  /** Spreads the available width over the fields, without stretching one field across
   *  the whole window: a list of a single number should still look like a table. */
  layoutColumns() {
    const count = Math.max(1, this.fields.length);
    const available = Math.max(240, (this.host.body.clientWidth || 520) - 80);
    const width = Math.max(120, Math.min(300, Math.floor(available / count)));
    for (const field of this.fields) field.width = width;
  }

  /** Rows live in memory: a List is a value inside a cell, not a table of the document. */
  loadRows(first, last) {
    for (let index = first; index < last; index += 1) {
      if (this.grid.cache.has(index)) continue;
      this.grid.cache.set(index, this.rowData(index));
    }
  }

  rowData(index) {
    const row = this.rows[index];
    return {
      i: index,
      cells: this.fields.map((field, col) => ({ t: row && row[col] ? row[col].t : '' })),
    };
  }

  /** Rewrites one cell of the sub-table and asks the host to store the whole list. */
  setCell(row, columnName, text) {
    const col = this.fields.findIndex((field) => field.name === columnName);
    if (col < 0 || row < 0 || row >= this.rows.length) return;

    const field = this.fields[col];
    const value = text === undefined || text === null ? '' : String(text);
    this.rows[row][col] = { v: value, t: displayOf(field, value) };

    this.grid.cache.set(row, this.rowData(row));
    this.grid.scheduleRender();
    this.commit();
  }

  /** Keeps the whole column of a reference field as the source of its picker. */
  sendRefOptions(columnName) {
    const field = this.fields.find((item) => item.name === columnName);
    const options = field ? field.options : [];
    // The host already sent the ids this list may point at, so the list opens at once
    // instead of waiting for a round trip.
    this.editor.applyRefOptions({ options, iconColumn: false });
  }

  browse(kind, columnName, current, row) {
    this.pendingBrowse = { row, column: columnName };
    this.app.send('browseFile', { kind, column: columnName, current });
  }

  applyBrowseResult(path) {
    const target = this.pendingBrowse;
    this.pendingBrowse = null;
    if (!target) return;

    // The dialog takes the focus, so the editor may have committed and closed already.
    if (this.editor.active && this.editor.active.row === target.row) {
      this.editor.browsing = null;
      this.editor.active.input.value = path;
      this.editor.commit();
      return;
    }

    this.setCell(target.row, target.column, path);
  }

  fillCells(rowIndices, columnNames, text) {
    for (const row of rowIndices) {
      for (const name of columnNames) this.setCell(row, name, text);
    }
  }

  /** Ctrl+Enter: copy the cursor cell's text into every cell of the selection. */
  fillSelection() {
    const { row, col } = this.grid.cursor;
    const cell = this.rows[row] && this.rows[row][col];
    if (!cell) return;
    this.fillCells(
      this.grid.selectedRowIndices(),
      this.grid.selectedColumnNames(),
      cell.t,
    );
  }

  // ------------------------------------------------------------- rows

  addRow(after) {
    const at = after === undefined ? this.rows.length : after + 1;
    this.rows.splice(at, 0, this.fields.map(() => ({ t: '', v: '' })));
    this.rebuildRows();
    this.grid.moveCursor(at, 0);
    this.commit();
  }

  removeRow() {
    if (this.kind !== 'list' || this.rows.length === 0) return;
    const row = Math.min(this.grid.cursor.row, this.rows.length - 1);
    this.rows.splice(row, 1);
    this.rebuildRows();
    this.grid.moveCursor(Math.max(0, Math.min(row, this.rows.length - 1)), 0);
    this.commit();
  }

  /** Moves one record of the list to another position (dragging its row number). */
  moveRow(from, to) {
    if (from === to || from < 0 || to < 0) return;
    if (from >= this.rows.length || to >= this.rows.length) return;

    const [row] = this.rows.splice(from, 1);
    this.rows.splice(to, 0, row);
    this.rebuildRows();
    this.grid.moveCursor(to, this.grid.cursor.col);
    this.commit();
  }

  rebuildRows() {
    this.grid.cache.clear();
    this.grid.rowCount = this.rows.length;
    this.grid.invalidate();
    this.measure();
    this.updateTitle();
  }

  // ------------------------------------------------------------- clipboard

  /** Copy / cut / paste inside the sub-table, with its own small clipboard. */
  onClipboardKey(event) {
    if (!event.ctrlKey || this.editor.active) return;

    const key = event.key.toLowerCase();

    // Select All belongs to the panel while it has the keyboard, not to the table above.
    if (key === 'a') {
      event.preventDefault();
      event.stopPropagation();
      this.grid.selectAll();
      return;
    }

    if (key !== 'c' && key !== 'x' && key !== 'v') return;

    event.preventDefault();
    event.stopPropagation();

    if (key === 'v') {
      this.paste();
      return;
    }

    this.copySelection(key === 'x');
  }

  /** Puts the selected block on the panel's clipboard; `cut` empties the cells after. */
  copySelection(cut) {
    const bounds = this.grid.selectionBounds();
    const block = [];
    for (let row = bounds.top; row <= bounds.bottom; row += 1) {
      const line = [];
      for (let col = bounds.left; col <= bounds.right; col += 1) {
        line.push(this.rows[row] && this.rows[row][col] ? this.rows[row][col].v : '');
      }
      block.push(line);
    }
    this.clipboard = block;

    if (!cut) return;

    for (let row = bounds.top; row <= bounds.bottom; row += 1) {
      for (let col = bounds.left; col <= bounds.right; col += 1) {
        if (this.rows[row] && this.rows[row][col]) this.rows[row][col] = { t: '', v: '' };
      }
    }
    this.grid.cache.clear();
    this.grid.invalidate();
    this.commit();
  }

  paste() {
    if (!this.clipboard || this.clipboard.length === 0) return;

    const start = { ...this.grid.cursor };
    while (this.rows.length < start.row + this.clipboard.length) {
      this.rows.push(this.fields.map(() => ({ t: '', v: '' })));
    }

    this.clipboard.forEach((line, rowOffset) => {
      line.forEach((value, colOffset) => {
        const row = start.row + rowOffset;
        const col = start.col + colOffset;
        const field = this.fields[col];
        if (!field || !this.rows[row]) return;
        this.rows[row][col] = { v: value, t: displayOf(field, value) };
      });
    });

    this.grid.cache.clear();
    this.grid.rowCount = this.rows.length;
    this.grid.invalidate();
    this.measure();
    this.updateTitle();
    this.commit();
  }

  // ------------------------------------------------------------- output

  /** Sends the list back to the document. Rapid edits are coalesced into one write. */
  commit(immediate = false) {
    if (this.timer) {
      clearTimeout(this.timer);
      this.timer = null;
    }

    const write = () => {
      this.app.send('setSubValue', {
        sheet: this.sheet,
        row: this.row,
        column: this.column,
        rows: this.rows.map((row) => row.map((cell) => cell.v)),
      });
    };

    if (immediate) write();
    else this.timer = setTimeout(write, 250);
  }

  /** Writes any pending edit now (used before saving, undoing or closing). */
  flush() {
    if (!this.timer) return;
    clearTimeout(this.timer);
    this.timer = null;
    this.app.send('setSubValue', {
      sheet: this.sheet,
      row: this.row,
      column: this.column,
      rows: this.rows.map((row) => row.map((cell) => cell.v)),
    });
  }

  // ------------------------------------------------------------- layout

  /** Height of the whole panel, which is how far the rows below are pushed down. */
  measure() {
    // Rows grow when a value wraps, so the panel follows the table's own content height
    // instead of counting rows: a list of three long notes has to be as tall as they are.
    const content = this.grid.contentHeight();
    const minimum = ROW_HEIGHT * MIN_ROWS;
    // Still capped, because the body scrolls: a hundred-row list must not cover the
    // window, but a couple of tall rows should fit without scrolling.
    const maximum = ROW_HEIGHT * MAX_ROWS * 2;
    const bodyHeight = Math.min(maximum, Math.max(minimum, content));
    this.body.style.height = bodyHeight + 'px';
    // Before the panel is mounted its offsetHeight is zero, which would hide it.
    if (this.node.isConnected) this.host.resizePanel(this.id, this.node.offsetHeight);
  }

  updateTitle() {
    const count = this.rows.length;
    const label = this.kind === 'list'
      ? `${count} row${count === 1 ? '' : 's'}`
      : 'properties';
    this.titleNode.textContent = `${this.column} \u00b7 ${label}`;
  }

  focus() {
    this.grid.body.focus();
    this.grid.focusProxy();
  }

  destroy() {
    this.flush();
    if (this.timer) clearTimeout(this.timer);
    this.timer = null;
    this.editor.close();
    this.node.remove();
  }
}

/**
 * The encoded type of one field, which is how a new field list is sent back to the host.
 * An enum or a flag carries its values in that string, so they travel with the field.
 */
function fieldTypeStr(field) {
  switch (field.type) {
    case 'Enum':
      return '5:' + (field.values || []).join(',');
    case 'Flags':
      return '10:' + (field.values || []).join(',');
    default:
      return field.typeStr || '';
  }
}

/**
 * What the grid should print for a raw value. The host formats a fresh payload, so this
 * only has to cover what an edit produces until the answer comes back.
 */
function displayOf(field, value) {
  switch (field.type) {
    case 'Bool':
      if (value === 'true') return 'Y';
      if (value === 'false') return 'N';
      return value;
    case 'Flags':
      // The editor reports the chosen names separated by commas, the table shows them
      // separated by a bar; until the host answers, the cell has to say the same thing.
      return String(value || '')
        .split(',')
        .map((name) => name.trim())
        .filter((name) => name.length > 0)
        .join('|');
    case 'Color':
      return colorText(value);
    default:
      return value;
  }
}

function colorText(value) {
  const text = String(value || '').trim();
  if (/^#[0-9a-fA-F]{6,8}$/.test(text)) return text.toUpperCase();
  if (/^0x[0-9a-fA-F]{6,8}$/.test(text)) return '#' + text.slice(2).toUpperCase();
  if (/^[0-9]+$/.test(text)) {
    return '#' + (Number(text) & 0xFFFFFF).toString(16).padStart(6, '0').toUpperCase();
  }
  return text;
}
