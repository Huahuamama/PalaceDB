// Inline cell editors.
//
// The editor is an overlay inside the scrolling row container, so it moves with the
// grid and keeps its position when the table scrolls. While an editor is open the
// grid pauses row re-rendering, which keeps the DOM node alive.

import { imageUrl, imageError, requestImage } from './images.js';
// Row height lives in the stylesheet so the overlay always matches the grid. It is
// imported rather than read here so a changed table text size is picked up as well.
import { ROW_HEIGHT } from './grid.js';

const TEXT_TYPES = new Set([
  'Text', 'Id', 'Formula', 'Guid', 'File', 'Image',
  'Dynamic', 'Gradient', 'Curve', 'Custom', 'List', 'Properties', 'Polymorph',
]);

const NUMERIC_TYPES = new Set(['Int', 'Float']);

// Swatches offered by the colour popup: a neutral row plus a spread of usable hues.
const COLOR_PALETTE = [
  '#000000', '#3f3f46', '#71717a', '#a1a1aa', '#d4d4d8', '#ffffff',
  '#7f1d1d', '#dc2626', '#f97316', '#f59e0b', '#eab308', '#84cc16',
  '#22c55e', '#10b981', '#14b8a6', '#06b6d4', '#0ea5e9', '#3b82f6',
  '#6366f1', '#8b5cf6', '#a855f7', '#d946ef', '#ec4899', '#f43f5e',
];

export class CellEditor {
  constructor(grid) {
    this.grid = grid;
    this.active = null;
    this.dropdown = null;
    this.refreshDropdown = null;

    grid.onEditRequest = (row, col, initial, reuse) => this.begin(row, col, initial, reuse);
    grid.onToggleRequest = (row, col) => this.toggle(row, col);
    grid.isEditing = () => this.active !== null;
  }

  // ------------------------------------------------------------ lifecycle

  begin(row, col, initialText, reuseInput) {
    if (this.active) this.commit();

    const column = this.grid.columns[col];
    if (!column) return;

    if (column.type === 'Bool') {
      this.toggle(row, col);
      return;
    }

    if (column.type === 'Enum' || column.type === 'Flags') {
      this.openDropdown(row, col, column);
      return;
    }

    // A reference is both a list and a text field: the author usually knows the id they
    // want, so typing filters the list instead of forcing a scroll through it.
    if (column.type === 'Ref') {
      this.openRefEditor(row, col, column, initialText, reuseInput);
      return;
    }

    if (column.type === 'Color') {
      this.openColorEditor(row, col, column);
      return;
    }

    // List and Properties cells open their sub-table underneath the row instead of a
    // dialog, so editing one never hides the row it belongs to.
    if (column.type === 'List' || column.type === 'Properties') {
      if (this.grid.onExpandRequest) this.grid.onExpandRequest(row, col);
      return;
    }

    this.openInput(row, col, column, initialText, reuseInput);
  }

  openInput(row, col, column, initialText, reuseInput) {
    const data = this.grid.cellValue(row, col);
    // What the cell holds now. The edit is only written when the text ends up different
    // from THIS, not from the character that happened to start the edit, otherwise typing
    // a single character into an empty cell compares "1" against "1" and writes nothing.
    //
    // A placeholder marker is not a value: editing an empty image must start blank
    // instead of offering to save the literal text "#MISSING".
    const currentText = data && !isMarker(data.t) ? data.t : '';
    const startText = initialText !== undefined && initialText !== null
      ? initialText
      : currentText;

    const left = this.grid.columnLeft(col);
    const width = column.width;

    // Text cells may hold line breaks, so they get an editor that can grow. Every
    // other type stays a single-line input where Enter simply commits.
    const multiline = column.type === 'Text';

    const wrapper = document.createElement('div');
    wrapper.className = 'cell-editor';
    wrapper.style.left = left + 'px';
    wrapper.style.top = this.grid.topOf(row) + 'px';
    wrapper.style.width = width + 'px';
    // An inline height would beat the stylesheet, so a growing editor sets none. The
    // row's own height is used, because a row with wrapped text is taller than the base.
    if (!multiline) wrapper.style.height = this.grid.heightOf(row) + 'px';

    // A promoted proxy is already focused and already holds what was typed, so it is
    // reused as the editor: moving focus would cancel the composition in progress.
    const input = reuseInput || document.createElement(multiline ? 'textarea' : 'input');
    input.className = multiline ? 'editor-input editor-multiline' : 'editor-input';
    // The promoted proxy is a <textarea>, and `type` is read-only on one: assigning it
    // throws, which used to abort the editor and leave the proxy stranded in the corner
    // of the grid — the "ghost caret". Only a fresh <input> needs its type set.
    if (input.tagName === 'INPUT') {
      input.type = 'text';
    } else if (multiline) {
      wrapper.classList.add('multiline');
    }
    if (input.tagName === 'TEXTAREA') input.rows = 1;
    input.value = startText;
    if (reuseInput) input.style.cssText = '';
    input.spellcheck = false;
    if (NUMERIC_TYPES.has(column.type)) input.inputMode = 'decimal';

    wrapper.appendChild(input);

    if (multiline) {
      const grow = () => {
        input.style.height = 'auto';
        input.style.height = input.scrollHeight + 'px';
      };
      input.addEventListener('input', grow);
      // The wrapper is laid out before the text is measured, so grow on the next frame.
      requestAnimationFrame(grow);
    }

    if (column.type === 'Image' || column.type === 'File') {
      const browse = document.createElement('button');
      browse.type = 'button';
      browse.className = 'editor-browse';
      browse.textContent = '...';
      browse.title = 'Browse';
      browse.addEventListener('mousedown', (event) => {
        event.preventDefault();
        // The native dialog takes the focus away, which would commit and close this
        // editor before the chosen path came back. The flag keeps it alive.
        this.browsing = { row, col, column };
        this.grid.store.browse(
          column.type === 'Image' ? 'image' : 'file', column.name, input.value, row);
      });
      wrapper.appendChild(browse);
    }

    this.grid.rows.appendChild(wrapper);
    this.grid.suspendRender = true;

    input.focus();
    // Put the caret at the end instead of selecting everything: a full selection has no
    // visible caret, which made a freshly opened cell look read-only.
    const caret = input.value.length;
    try { input.setSelectionRange(caret, caret); } catch { /* textarea in some engines */ }

    this.active = { row, col, column, input, wrapper, original: currentText, wasProxy: Boolean(reuseInput) };

    const onKeyDown = (event) => {
      // A reused proxy outlives this editor. Once the edit is over the same node keeps
      // listening, and a leftover handler moved the cursor from the cell the editor had
      // captured — which is what made the arrows jump around after a few edits.
      if (!this.active || this.active.input !== input) return;

      // The grid listens for the same keys; an editor key must never reach it.
      event.stopPropagation();

      // A Chinese/Japanese IME uses Enter to accept a candidate. That keystroke belongs
      // to the composition, not to the grid, so it must not commit the cell.
      if (event.isComposing || event.keyCode === 229) return;

      if (event.key === 'Enter' && multiline && event.shiftKey) {
        // Shift+Enter inserts a line break; a bare Enter still commits and moves on.
        return;
      }

      if (event.key === 'Enter') {
        event.preventDefault();
        // Ctrl+Enter writes the typed value into the whole selection (requirement R5).
        this.commit(event.ctrlKey);
        if (!event.ctrlKey) this.grid.moveCursor(row + 1, col);
      } else if (event.key === 'Escape') {
        event.preventDefault();
        this.cancel();
      } else if (event.key === 'Tab') {
        event.preventDefault();
        this.commit();
        if (event.shiftKey) this.grid.moveCursor(row, col - 1);
        else this.grid.moveCursor(row, col + 1);
      } else if (event.key === 'ArrowDown' && !event.altKey) {
        // Inside a multi-line value the arrows belong to the caret: pressing Down has to
        // move to the next line of the text, not to the cell below. Alt+Arrow is the way
        // out — it commits and moves to the neighbouring cell.
        if (multiline) return;
        event.preventDefault();
        this.commit();
        this.grid.moveCursor(row + 1, col);
      } else if (event.key === 'ArrowUp' && !event.altKey) {
        if (multiline) return;
        event.preventDefault();
        this.commit();
        this.grid.moveCursor(row - 1, col);
      }
    };

    const onBlur = () => {
      // Clicking elsewhere commits, matching spreadsheet behaviour.
      if (this.active && this.active.input === input) this.commit();
    };

    input.addEventListener('keydown', onKeyDown);
    input.addEventListener('blur', onBlur);
    this.active.listeners = [
      () => input.removeEventListener('keydown', onKeyDown),
      () => input.removeEventListener('blur', onBlur),
    ];
  }

  // ------------------------------------------------------------ dropdowns

  /**
   * Positions a popup under its cell in viewport coordinates.
   *
   * The grid body clips its children, so a dropdown drawn inside it is cut off as soon as
   * the column sits near the right edge — which is exactly where the reference and colour
   * columns tend to be. Popups are therefore fixed-positioned and clamped to the window,
   * flipping above the cell when there is no room below.
   */
  placePopup(box, row, col, minWidth) {
    const cell = this.grid.rows.querySelector(
      '.cell[data-row="' + row + '"][data-col="' + col + '"]');
    const rect = cell ? cell.getBoundingClientRect() : null;

    const anchorLeft = rect ? rect.left : 0;
    const anchorTop = rect ? rect.bottom : (row + 1) * ROW_HEIGHT;
    const anchorTopEdge = rect ? rect.top : row * ROW_HEIGHT;

    if (minWidth) box.style.minWidth = minWidth + 'px';

    // Measure with the popup already in the document but off to the side.
    box.style.left = '0px';
    box.style.top = '0px';
    const width = box.offsetWidth || minWidth || 160;
    const height = box.offsetHeight || 0;

    const margin = 8;
    const maxLeft = Math.max(margin, window.innerWidth - width - margin);
    const left = Math.max(margin, Math.min(anchorLeft, maxLeft));

    const fitsBelow = anchorTop + height <= window.innerHeight - margin;
    const top = fitsBelow
      ? anchorTop
      : Math.max(margin, anchorTopEdge - height - 2);

    box.style.left = Math.round(left) + 'px';
    box.style.top = Math.round(top) + 'px';
  }

  /** Keeps an open popup against its cell while the table scrolls. */
  watchScroll() {
    this.unwatchScroll();
    this.onScrollReposition = () => {
      const state = this.active;
      if (!state || !state.popup) return;
      this.placePopup(state.popup, state.row, state.col, state.popupMinWidth);
    };
    this.grid.body.addEventListener('scroll', this.onScrollReposition, { passive: true });
  }

  unwatchScroll() {
    if (!this.onScrollReposition) return;
    this.grid.body.removeEventListener('scroll', this.onScrollReposition);
    this.onScrollReposition = null;
  }

  openDropdown(row, col, column) {
    const data = this.grid.cellValue(row, col);
    const current = data ? data.t : '';
    const width = Math.max(column.width, 160);

    const box = document.createElement('div');
    box.className = 'cell-dropdown';
    box.style.minWidth = width + 'px';

    this.active = { row, col, column, dropdown: box, popup: box, popupMinWidth: width };
    this.grid.suspendRender = true;
    this.grid.rows.appendChild(box);

    if (column.type === 'Flags') {
      this.buildFlags(box, column, current);
    } else {
      const options = (column.values || []).map((value) => ({
        value,
        label: value,
        selected: value === current,
      }));
      this.buildOptions(box, options, (value) => this.applyValue(value));
    }

    this.placePopup(box, row, col, width);
    this.watchScroll();

    document.addEventListener('mousedown', this.onOutsideClick = (event) => {
      if (!box.contains(event.target)) this.cancel();
    });
  }

  buildOptions(box, options, onPick) {
    box.innerHTML = '';

    // A table that declares an icon column shows a slot on every row, so the labels stay
    // aligned even when a single row has no icon (C06).
    const withIcons = options.some((option) => option.icon !== undefined);

    for (const option of options) {
      const item = document.createElement('div');
      item.className = 'dropdown-item' + (option.selected ? ' selected' : '');

      if (withIcons) item.appendChild(this.buildOptionIcon(option.icon));

      const label = document.createElement('span');
      // A value may deliberately have no label ("5:,A,B" starts with an unnamed value);
      // an empty row would be invisible and unclickable, so it says what it is.
      label.className = 'dropdown-label' + (option.label === '' ? ' unnamed' : '');
      label.textContent = option.label === '' ? '(empty)' : option.label;
      item.appendChild(label);

      item.addEventListener('mousedown', (event) => {
        event.preventDefault();
        onPick(option.value);
      });
      box.appendChild(item);
    }

    if (options.length === 0) {
      const empty = document.createElement('div');
      empty.className = 'dropdown-item empty';
      empty.textContent = '(no values)';
      box.appendChild(empty);
    }

    // Thumbnails may still be in flight; rebuilding when they arrive keeps the open
    // dropdown in step with the grid.
    this.refreshDropdown = () => {
      if (this.active && this.active.dropdown === box) this.buildOptions(box, options, onPick);
    };
  }

  /** Small icon slot of one dropdown row; empty when the row has no icon. */
  buildOptionIcon(path) {
    const slot = document.createElement('span');
    slot.className = 'dropdown-icon';

    if (!path) return slot;

    const url = imageUrl(path);
    if (url) {
      const img = document.createElement('img');
      img.src = url;
      img.alt = '';
      img.draggable = false;
      slot.appendChild(img);
    } else if (imageError(path)) {
      slot.classList.add('failed');
    } else {
      requestImage(path);
    }

    return slot;
  }

  /** Called by the application once a batch of thumbnails has been resolved. */
  refreshImages() {
    if (this.refreshDropdown) this.refreshDropdown();
  }

  buildFlags(box, column, current) {
    const selected = new Set();
    if (current && current !== '\u2205') {
      for (const name of current.split('|')) selected.add(name.trim());
    }

    box.innerHTML = '';
    const values = column.values || [];
    values.forEach((name, index) => {
      const item = document.createElement('label');
      item.className = 'dropdown-item';

      const check = document.createElement('input');
      check.type = 'checkbox';
      check.checked = selected.has(name);
      check.addEventListener('change', () => {
        if (check.checked) selected.add(name);
        else selected.delete(name);
        // Names rather than the bit mask: the host resolves names first, and a flag
        // literally called "3" would otherwise be confused with the mask 3.
        this.applyValue(values.filter((value) => selected.has(value)).join(','));
      });

      const text = document.createElement('span');
      // Same as an enum: a flag with no name is a real flag, so it needs a row that can
      // be seen and clicked.
      text.className = name === '' ? 'dropdown-label unnamed' : '';
      text.textContent = name === '' ? '(empty)' : name;

      item.appendChild(check);
      item.appendChild(text);
      box.appendChild(item);
    });
  }

  /**
   * Called by the application when the native file picker returns a path.
   *
   * The dialog takes the focus, so by the time the answer arrives the editor may have
   * been committed and closed. In that case the value is written straight into the cell
   * instead of being dropped, which is what made browsing look like a no-op.
   */
  applyBrowseResult(path) {
    const target = this.browsing;
    this.browsing = null;

    if (this.active && this.active.input && (!target || this.active.row === target.row)) {
      this.active.input.value = path;
      this.commit();
      return;
    }

    if (!target) return;

    this.grid.store.setCell(target.row, target.column.name, path);
  }

  /** Called by the application when the host answers a reference query. */
  applyRefOptions(payload) {
    if (!this.active || !this.active.box) return;

    this.active.refOptions = payload.options.map((option) => ({
      id: option.id,
      display: option.display,
      icon: option.icon || '',
    }));
    this.active.refIconColumn = Boolean(payload.iconColumn);
    this.active.refMatches = this.active.refOptions;
    this.active.refIndex = 0;

    this.renderRefList();
  }

  // ------------------------------------------------------------ reference editor

  /**
   * A reference opens as a text field with the matching rows listed underneath. Typing
   * narrows the list, the arrow keys move through it, Enter takes the highlighted entry
   * and a click takes the one clicked — so a table with hundreds of rows is usable
   * without scrolling through all of them.
   */
  openRefEditor(row, col, column, initialText, reuseInput) {
    const data = this.grid.cellValue(row, col);
    // The formatted cell keeps the raw id in `d`, which is what a reference stores.
    const current = data ? (data.d || data.t || '') : '';

    const left = this.grid.columnLeft(col);
    const wrapper = document.createElement('div');
    wrapper.className = 'cell-editor';
    wrapper.style.left = left + 'px';
    wrapper.style.top = this.grid.topOf(row) + 'px';
    wrapper.style.width = column.width + 'px';
    wrapper.style.height = this.grid.heightOf(row) + 'px';

    const input = reuseInput || document.createElement('input');
    input.className = 'editor-input';
    input.type = 'text';
    if (reuseInput) input.style.cssText = '';
    input.value = initialText !== undefined && initialText !== null ? initialText : current;
    input.spellcheck = false;
    wrapper.appendChild(input);

    const box = document.createElement('div');
    box.className = 'cell-dropdown ref-dropdown';
    box.style.minWidth = Math.max(column.width, 200) + 'px';
    box.textContent = 'Loading...';

    this.grid.rows.appendChild(wrapper);
    this.grid.rows.appendChild(box);
    this.grid.suspendRender = true;

    this.active = {
      row,
      col,
      column,
      input,
      wrapper,
      box,
      original: current,
      wasProxy: Boolean(reuseInput),
      refOptions: null,
      refMatches: [],
      refIndex: 0,
      refIconColumn: false,
      popup: box,
      popupMinWidth: Math.max(column.width, 200),
    };

    const filter = () => {
      const state = this.active;
      if (!state || !state.refOptions) return;
      const query = input.value.trim().toLowerCase();
      state.refMatches = query.length === 0
        ? state.refOptions
        : state.refOptions.filter((option) =>
            option.id.toLowerCase().includes(query) ||
            String(option.display || '').toLowerCase().includes(query));
      state.refIndex = 0;
      this.renderRefList();
    };

    input.addEventListener('input', filter);

    const onKeyDown = (event) => {
      // A reused proxy keeps its listeners after the editor is gone (see openInput).
      if (!this.active || this.active.input !== input) return;
      event.stopPropagation();
      if (event.isComposing || event.keyCode === 229) return;

      const state = this.active;
      const matches = state ? state.refMatches || [] : [];

      if (event.key === 'ArrowDown') {
        event.preventDefault();
        if (matches.length) {
          state.refIndex = (state.refIndex + 1) % matches.length;
          this.renderRefList();
        }
      } else if (event.key === 'ArrowUp') {
        event.preventDefault();
        if (matches.length) {
          state.refIndex = (state.refIndex - 1 + matches.length) % matches.length;
          this.renderRefList();
        }
      } else if (event.key === 'Enter') {
        event.preventDefault();
        if (matches.length) this.applyValue(matches[state.refIndex].id);
        else this.commit();
      } else if (event.key === 'Escape') {
        event.preventDefault();
        this.cancel();
      } else if (event.key === 'Tab') {
        event.preventDefault();
        this.commit();
        this.grid.moveCursor(row, col + (event.shiftKey ? -1 : 1));
      }
    };
    input.addEventListener('keydown', onKeyDown);

    const onBlur = () => {
      if (this.browsing) return;
      if (this.active && this.active.input === input) this.commit();
    };
    input.addEventListener('blur', onBlur);

    this.active.listeners = [
      () => input.removeEventListener('keydown', onKeyDown),
      () => input.removeEventListener('blur', onBlur),
      () => input.removeEventListener('input', filter),
    ];

    document.addEventListener('mousedown', this.onOutsideClick = (event) => {
      if (!box.contains(event.target) && !wrapper.contains(event.target)) this.cancel();
    });

    this.placePopup(box, row, col, Math.max(column.width, 200));
    this.watchScroll();

    input.focus();
    const caret = input.value.length;
    try { input.setSelectionRange(caret, caret); } catch { /* ignore */ }

    this.grid.store.refOptions(column.name);
  }

  /** Draws the filtered reference list under the input. */
  renderRefList() {
    const state = this.active;
    if (!state || !state.box) return;

    const matches = state.refMatches || [];
    const box = state.box;
    box.innerHTML = '';

    if (matches.length === 0) {
      const empty = document.createElement('div');
      empty.className = 'dropdown-item empty';
      empty.textContent = '(no match)';
      box.appendChild(empty);
      return;
    }

    matches.forEach((option, index) => {
      const item = document.createElement('div');
      item.className = 'dropdown-item' + (index === state.refIndex ? ' selected' : '');

      if (state.refIconColumn) item.appendChild(this.buildOptionIcon(option.icon || ''));

      const label = document.createElement('span');
      label.className = 'dropdown-label';
      label.textContent = option.display && option.display !== option.id
        ? `${option.id}  -  ${option.display}`
        : option.id;
      item.appendChild(label);

      item.addEventListener('mousedown', (event) => {
        event.preventDefault();
        this.applyValue(option.id);
      });
      box.appendChild(item);
    });

    this.placePopup(box, state.row, state.col, state.popupMinWidth);

    const current = box.children[state.refIndex];
    if (current && current.scrollIntoView) current.scrollIntoView({ block: 'nearest' });
  }

  // ------------------------------------------------------------ colour editor

  /**
   * Colours get a palette instead of a text field: a grid of ready-made swatches plus a
   * free picker and a hex field, since typing #rrggbb by hand is the last thing anyone
   * wants to do while filling in a table.
   */
  openColorEditor(row, col, column) {
    const data = this.grid.cellValue(row, col);
    const current = data ? data.t : '';

    const box = document.createElement('div');
    box.className = 'cell-dropdown color-popup';

    const grid = document.createElement('div');
    grid.className = 'color-grid';
    for (const hex of COLOR_PALETTE) {
      const swatch = document.createElement('button');
      swatch.type = 'button';
      swatch.className = 'color-swatch';
      swatch.style.backgroundColor = hex;
      swatch.title = hex;
      if (hex.toLowerCase() === current.toLowerCase()) swatch.classList.add('current');
      swatch.addEventListener('mousedown', (event) => {
        event.preventDefault();
        this.applyValue(hex);
      });
      grid.appendChild(swatch);
    }
    box.appendChild(grid);

    const custom = document.createElement('div');
    custom.className = 'color-custom';

    const picker = document.createElement('input');
    picker.type = 'color';
    picker.className = 'editor-color';
    picker.value = toColorInput(current);

    const hexInput = document.createElement('input');
    hexInput.type = 'text';
    hexInput.className = 'form-input color-hex';
    hexInput.value = toColorInput(current);
    hexInput.spellcheck = false;

    picker.addEventListener('input', () => { hexInput.value = picker.value; });
    hexInput.addEventListener('input', () => {
      if (/^#[0-9a-fA-F]{6}$/.test(hexInput.value)) picker.value = hexInput.value;
    });
    hexInput.addEventListener('keydown', (event) => {
      event.stopPropagation();
      if (event.key === 'Enter') {
        event.preventDefault();
        this.applyValue(hexInput.value);
      } else if (event.key === 'Escape') {
        event.preventDefault();
        this.cancel();
      }
    });

    custom.appendChild(picker);
    custom.appendChild(hexInput);
    box.appendChild(custom);

    this.active = { row, col, column, dropdown: box, popup: box, popupMinWidth: 176 };
    this.grid.suspendRender = true;
    this.grid.rows.appendChild(box);
    this.placePopup(box, row, col, 176);
    this.watchScroll();

    document.addEventListener('mousedown', this.onOutsideClick = (event) => {
      if (!box.contains(event.target)) this.cancel();
    });

    hexInput.focus();
    hexInput.select();
  }

  // ------------------------------------------------------------ actions

  toggle(row, col) {
    const data = this.grid.cellValue(row, col);
    const next = data && data.t === 'Y' ? 'false' : 'true';
    this.applyValue(next, row, col);
  }

  applyValue(value, row, col) {
    const targetRow = row !== undefined ? row : this.active.row;
    const targetCol = col !== undefined ? col : this.active.col;
    const column = this.grid.columns[targetCol];
    this.close();
    this.grid.store.setCell(targetRow, column.name, value);
  }

  commit(fillSelection = false) {
    if (!this.active) return;
    const { row, col, column, input, original } = this.active;
    const value = input.value;
    this.close();

    if (fillSelection) {
      this.grid.store.fillCells(
        this.grid.selectedRowIndices(),
        this.grid.selectedColumnNames(),
        value,
      );
      return;
    }

    if (value !== original) {
      this.grid.store.setCell(row, column.name, value);
    }
  }

  cancel() {
    this.close();
  }

  close() {
    this.unwatchScroll();
    if (this.onOutsideClick) {
      document.removeEventListener('mousedown', this.onOutsideClick);
      this.onOutsideClick = null;
    }

    // Clear the state BEFORE touching the DOM. Removing a focused node fires its blur
    // handler synchronously, and that handler commits — which calls close() again. With
    // the state already cleared the nested call returns instead of fighting over nodes
    // that are halfway out of the document.
    const active = this.active;
    this.active = null;
    // `browsing` survives the close: the file dialog takes the focus, the editor commits
    // and closes, and the answer still has to find its cell when it comes back.

    const wasProxy = Boolean(active && active.wasProxy);
    if (active) {
      // Detach the handlers first: a reused proxy is handed back to the grid, and a
      // listener left on it would keep committing and moving the cursor for a cell that
      // is no longer being edited.
      if (active.listeners) {
        for (const off of active.listeners) {
          try { off(); } catch { /* node already gone */ }
        }
      }
      // A reference editor owns two nodes: the input and the list under it.
      for (const node of [active.wrapper, active.dropdown, active.box]) {
        if (node) node.remove();
      }
    }

    if (!wasProxy && this.grid.proxy) this.grid.proxy.value = '';

    // The promoted proxy goes back to being the invisible input the grid keeps focused.
    if (wasProxy && this.grid.proxy) {
      const proxy = this.grid.proxy;
      proxy.className = 'grid-proxy';
      proxy.value = '';
      proxy.style.cssText = '';
      this.grid.body.appendChild(proxy);
      proxy.focus({ preventScroll: true });
    }
    this.pendingRefBox = null;
    this.refreshDropdown = null;
    this.grid.suspendRender = false;
    this.grid.scheduleRender();
    this.grid.body.focus();
  }
}

/** True for the placeholders the formatter uses when a value is missing or invalid. */
function isMarker(text) {
  return typeof text === 'string' && /^#(NULL|MISSING|DUP|REF|INVALID|NOTFOUND)\b/.test(text);
}

function toColorInput(text) {
  const match = /#?([0-9a-fA-F]{6})/.exec(text || '');
  return match ? '#' + match[1].toLowerCase() : '#000000';
}
