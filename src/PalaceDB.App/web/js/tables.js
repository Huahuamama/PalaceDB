// Table-level dialogs that are not about columns: adding and deleting a table, the
// separator above a row and which column names a row in the table list.

import { send } from './bridge.js';
import { openModal } from './dialogs.js';
import { currentColumn } from './schema.js';
import { grid, state } from './session.js';
import { toast } from './toast.js';

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

export function promptAddTable() {
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

export function confirmDeleteTable() {
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

/** Renames the separator in front of the cursor row. */
export function promptSeparatorTitle(targetRow) {
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
export function promptDisplayColumn() {
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
