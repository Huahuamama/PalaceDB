// The settings window: interface text size, table text size and colour scheme.
//
// Every change is applied as it is made, so the window doubles as the preview - the
// sample table at the top uses the same variables as the real one.

import { openModal } from './dialogs.js';
import { grid, state } from './session.js';
import { DEFAULTS, GRID_SIZE, SCHEMES, UI_SIZE } from './settings.js';
import { applySettings } from './viewoptions.js';

/**
 * The settings window: interface text size, table text size and colour scheme.
 *
 * Every change is applied as it is made, so the window doubles as the preview — the
 * sample table at the top uses the same variables as the real one. Cancel puts the
 * values that were in force when the window opened back.
 */
export function openSettings() {
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
export function showMergeReport(report) {
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
