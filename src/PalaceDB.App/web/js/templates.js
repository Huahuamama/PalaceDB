// Table templates: the settings dialog that joins a table to a template, the reports
// that come back when it is applied, and the picker that copies one.

import { send } from './bridge.js';
import { closeModal, openModal } from './dialogs.js';
import { currentSheetData, state } from './session.js';
import { toast } from './toast.js';

/**
 * Table settings: the one place where a table joins or leaves a template.
 * <para>
 * Applying is checked by the host before anything changes — a table that has columns the
 * template does not declare is refused and the reason is shown, instead of half of the
 * template being applied.
 * </para>
 */
export function promptTableSettings() {
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
export function showColumnImpact(payload) {
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
export function showTemplateReport(report) {
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
export function promptTemplates() {
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
