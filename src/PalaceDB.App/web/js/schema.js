// Column and public-type schema: what a column is called, what it holds and how it is
// encoded into the type string the host stores.
//
// The form is the largest piece: the type decides which of the extra fields appear, so
// one builder walks the whole list rather than a dialog per type.

import { send } from './bridge.js';
import { openModal } from './dialogs.js';
import { grid, publicTypesOfDocument, state } from './session.js';
import { toast } from './toast.js';

const COLUMN_TYPES = [
  // A table normally arrives with an id column, but one can be deleted on purpose; the
  // type stays in the list so it can be added back.
  { value: '0', label: 'Id (primary key)' },
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

// Types a field of a list may have: a list inside a list has no usable editor, and an id
// belongs to the row of a real table, not to a record inside one.
const SUB_TYPES = COLUMN_TYPES.filter((type) => !['0', '8', '17', '21'].includes(type.value));

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

/** The public type a field of a list follows, or null when it follows none. */
function fieldPublicType(field) {
  const name = field && field.publicType;
  if (!name) return null;
  return publicTypesOfDocument().find((type) => type.name === name) || null;
}

/**
 * The type string a bound column or field carries in the .cdb: the definition's kind and
 * values, spelled exactly like every other copy so the file stays a plain CastleDB file.
 */
function encodedPublicType(definition) {
  return (definition.kind === 'flags' ? '10:' : '5:') + (definition.values || []).join(',');
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
  let publicRow = null;
  let publicSelect = null;
  let publicHint = null;
  let autoRow = null;
  let autoInput = null;
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
      // A field can follow a public enum / flags type, exactly like a column of a real
      // table; the binding has to survive this dialog or editing any other field of the
      // list would silently unbind it.
      publicType: field.publicType || '',
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

    // A public type is the one place its values are edited: choosing it here makes the
    // column follow the definition instead of keeping a private copy of the list.
    publicRow = document.createElement('label');
    publicRow.className = 'form-row';
    const publicCaption = document.createElement('span');
    publicCaption.textContent = 'Public type';
    publicRow.appendChild(publicCaption);

    publicSelect = document.createElement('select');
    publicSelect.className = 'form-input';
    publicRow.appendChild(publicSelect);
    wrap.appendChild(publicRow);

    publicHint = document.createElement('p');
    publicHint.className = 'form-hint';
    publicHint.textContent =
      'The values come from the public type. Edit them in the Public Types list to change every column that uses it.';
    wrap.appendChild(publicHint);

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

        // A bound field is a copy of its definition, so the definition decides both the
        // kind and the values: the type picker and the value box are read-only while it
        // follows one, and the stored string is kept equal to the shared one.
        const definition = fieldPublicType(field);
        if (definition) field.type = encodedPublicType(definition);

        const fieldType = document.createElement('select');
        fieldType.className = 'form-input';
        for (const type of SUB_TYPES) {
          const option = document.createElement('option');
          option.value = type.value;
          option.textContent = type.label;
          fieldType.appendChild(option);
        }
        fieldType.value = SUB_TYPES.some((type) => type.value === parts.id) ? parts.id : '1';
        fieldType.disabled = Boolean(definition);
        fieldType.title = definition
          ? `The values come from the public type '${definition.name}'. Choose "— none —" to give this field its own list.`
          : '';
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
          values.value = definition
            ? (definition.values || []).join(',')
            : (field.values === undefined ? parts.payload : field.values);
          values.disabled = Boolean(definition);
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

        // A field of a list can follow a public type just like a column of a real table.
        // The line is only there when the file has any definition to offer.
        const types = publicTypesOfDocument();
        if (types.length > 0) {
          const publicLine = document.createElement('div');
          publicLine.className = 'form-sub-public';

          const publicCaption = document.createElement('span');
          publicCaption.textContent = 'Public type';
          publicLine.appendChild(publicCaption);

          const publicChoice = document.createElement('select');
          publicChoice.className = 'form-input';
          publicChoice.title = 'Share one enum / flags definition with other fields and columns';
          const noType = document.createElement('option');
          noType.value = '';
          noType.textContent = '\u2014 none \u2014';
          publicChoice.appendChild(noType);
          for (const type of types) {
            const option = document.createElement('option');
            option.value = type.name;
            option.textContent = `${type.name} (${type.kind === 'flags' ? 'flags' : 'enum'})`;
            publicChoice.appendChild(option);
          }
          publicChoice.value = definition ? definition.name : '';
          publicChoice.addEventListener('change', () => {
            field.publicType = publicChoice.value;
            const chosen = types.find((type) => type.name === publicChoice.value);
            // Detaching keeps the values the field already has; they become its own.
            if (chosen) field.type = encodedPublicType(chosen);
            renderSubRows();
          });
          publicLine.appendChild(publicChoice);

          subRowsBox.appendChild(publicLine);
        }
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

    // Options for the public type picker: every named enum / flags of the file, plus
    // "none" so a column can be detached from a definition again.
    const publicTypes = publicTypesOfDocument();
    const noneOption = document.createElement('option');
    noneOption.value = '';
    noneOption.textContent = '\u2014 none \u2014';
    publicSelect.appendChild(noneOption);
    for (const type of publicTypes) {
      const option = document.createElement('option');
      option.value = type.name;
      option.textContent = `${type.name} (${type.kind === 'flags' ? 'flags' : 'enum'})`;
      publicSelect.appendChild(option);
    }
    publicSelect.value = initial.publicType && publicTypes.some((t) => t.name === initial.publicType)
      ? initial.publicType
      : '';

    publicSelect.addEventListener('change', () => {
      const chosen = publicTypes.find((type) => type.name === publicSelect.value);
      if (!chosen) {
        valuesInput.disabled = false;
        sync();
        return;
      }
      // The definition decides the kind and the values; the column only follows it.
      typeSelect.value = chosen.kind === 'flags' ? '10' : '5';
      valuesInput.value = (chosen.values || []).join(',');
      valuesInput.disabled = true;
      sync();
    });

    typeSelect.value = COLUMN_TYPES.some((t) => t.value === base) ? base : '1';

    const sync = () => {
      const value = typeSelect.value;
      valuesRow.style.display = value === '5' || value === '10' ? '' : 'none';
      valuesHint.style.display = value === '5' || value === '10' ? '' : 'none';
      refRow.style.display = value === '6' ? '' : 'none';
      subRow.hidden = !isStructuredType(value);
      publicRow.hidden = publicTypes.length === 0;
      const bound = Boolean(publicSelect.value);
      publicHint.hidden = !bound;
      valuesInput.disabled = bound;
      syncOptions(value);
    };
    typeSelect.addEventListener('change', () => {
      // Leaving enum / flags detaches the column: a public type describes a value list.
      if (typeSelect.value !== '5' && typeSelect.value !== '10') publicSelect.value = '';
      sync();
    });
    sync();
  }

  // ---- optional fields (C01 / C02) ----

  // Display format only applies to numbers, and the localizable flag to text; the rows
  // are shown and hidden with the type rather than rejected after the fact.
  const currentType = () => (typeSelect ? typeSelect.value : base);

  function syncOptions(value) {
    if (displayRow) displayRow.hidden = !(value === '3' || value === '4');
    if (localizableRow) localizableRow.hidden = !(value === '1' || value === '0');
    // Only an integer column can number its own rows.
    if (autoRow) autoRow.hidden = value !== '3';
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

    // An integer column can number the rows it lives in: every new or inserted row takes
    // one more than the highest number already there.
    autoRow = document.createElement('label');
    autoRow.className = 'form-row form-check';

    autoInput = document.createElement('input');
    autoInput.type = 'checkbox';
    autoInput.checked = Boolean(initial.autoIncrement);
    autoRow.appendChild(autoInput);

    const autoText = document.createElement('span');
    autoText.textContent = 'Auto-increment — new rows get the next number (1, 2, 3 ...)';
    autoRow.appendChild(autoText);
    wrap.appendChild(autoRow);

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
        publicType: field.publicType || '',
      }))
      .filter((field) => field.name.length > 0),
    localizable: () => (localizableInput ? localizableInput.checked : false),
    publicType: () => (publicSelect ? publicSelect.value : ''),
    autoIncrement: () => Boolean(autoInput && autoInput.checked),
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

export function currentColumn() {
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

export function promptAddColumn() {
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
        publicType: form.publicType(),
        autoIncrement: form.autoIncrement(),
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
export function promptEditColumn(columnOverride, sheetOverride) {
  const column = columnOverride || currentColumn();
  const sheetName = sheetOverride || state.currentSheet;
  if (!column || !sheetName) return;

  // A List or Properties column already has a hidden sub-table; show its fields.
  const subSheetName = `${sheetName}@${column.name}`;
  const subSheets = (state.document && state.document.subSheets) || [];
  const existing = subSheets.find((sheet) => sheet.name === subSheetName);
  const initialSub = existing
    ? existing.columns.map((field) => ({
        id: field.name,
        name: field.name,
        type: field.typeStr,
        publicType: field.publicType || '',
      }))
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
      publicType: column.publicType || '',
      autoIncrement: Boolean(column.autoIncrement),
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
        publicType: form.publicType(),
        autoIncrement: form.autoIncrement(),
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
export function promptEditSubField({ sheet, row, column, fields, index, isNew }) {
  const list = fields.map((field) => ({
    name: field.name,
    type: field.type,
    publicType: field.publicType || '',
  }));
  const initial = isNew ? { name: '', type: '1', publicType: '' } : list[index];
  if (!initial) return;

  // Inserting keeps every field; editing replaces the one at `index`.
  const others = isNew ? list.slice() : list.filter((_, i) => i !== index);

  let nameInput = null;
  let typeSelect = null;
  let valuesInput = null;
  let valuesRow = null;
  let tableSelect = null;
  let tableRow = null;
  // Declared out here, not inside build(): onOk has to read the choice too.
  let publicSelect = null;

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

      // The public type this field follows, if any: the definition then owns the kind and
      // the values, so the two rows below are read-only until it is detached.
      const publicTypes = publicTypesOfDocument();
      if (publicTypes.length > 0) {
        const publicRow = document.createElement('label');
        publicRow.className = 'form-row';
        const publicCaption = document.createElement('span');
        publicCaption.textContent = 'Public type';
        publicRow.appendChild(publicCaption);
        publicSelect = document.createElement('select');
        publicSelect.className = 'form-input';
        const noType = document.createElement('option');
        noType.value = '';
        noType.textContent = '\u2014 none \u2014';
        publicSelect.appendChild(noType);
        for (const type of publicTypes) {
          const option = document.createElement('option');
          option.value = type.name;
          option.textContent = `${type.name} (${type.kind === 'flags' ? 'flags' : 'enum'})`;
          publicSelect.appendChild(option);
        }
        publicSelect.value = fieldPublicType(initial) ? initial.publicType : '';
        publicRow.appendChild(publicSelect);
        wrap.appendChild(publicRow);
      }

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
        const definition = publicSelect ? fieldPublicType({ publicType: publicSelect.value }) : null;
        if (definition) {
          typeSelect.value = definition.kind === 'flags' ? '10' : '5';
          valuesInput.value = (definition.values || []).join(',');
        }
        typeSelect.disabled = Boolean(definition);
        valuesInput.disabled = Boolean(definition);

        const id = typeSelect.value;
        const values = id === '5' || id === '10';
        valuesRow.hidden = !values;
        valuesHint.hidden = !values;
        tableRow.hidden = id !== '6';
      };
      typeSelect.addEventListener('change', sync);
      if (publicSelect) publicSelect.addEventListener('change', sync);
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

      const publicType = publicSelect ? publicSelect.value : '';
      const definition = fieldPublicType({ publicType });
      const id = typeSelect.value;
      let type = id;
      if (definition) type = encodedPublicType(definition);
      else if (id === '5' || id === '10') type = id + ':' + valuesInput.value;
      else if (id === '6') type = '6:' + (tableSelect.value || '');

      const next = others.slice();
      next.splice(Math.max(0, Math.min(index, next.length)), 0, { name, type, publicType });
      send('setSubFields', { sheet, row, column, fields: next });
      return true;
    },
  });
}

export function confirmDeleteColumn() {  const column = currentColumn();
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

export function moveColumn(delta) {
  const column = currentColumn();
  if (!column || !state.currentSheet) return;
  send('moveColumn', { sheet: state.currentSheet, column: column.name, delta });
}

/**
 * The one dialog that creates a public type or edits an existing one: its name, whether
 * it is an enum or a set of flags, and its values. Applying it is one undoable step that
 * reaches every column bound to it.
 */
export function promptPublicType(existing) {
  const wrap = document.createElement('div');
  wrap.className = 'form';

  const nameRow = document.createElement('label');
  nameRow.className = 'form-row';
  const nameCaption = document.createElement('span');
  nameCaption.textContent = 'Name';
  nameRow.appendChild(nameCaption);

  const nameInput = document.createElement('input');
  nameInput.type = 'text';
  nameInput.className = 'form-input';
  nameInput.placeholder = 'Rarity';
  nameInput.value = existing ? existing.name : '';
  nameRow.appendChild(nameInput);
  wrap.appendChild(nameRow);

  const kindRow = document.createElement('label');
  kindRow.className = 'form-row';
  const kindCaption = document.createElement('span');
  kindCaption.textContent = 'Kind';
  kindRow.appendChild(kindCaption);

  const kindSelect = document.createElement('select');
  kindSelect.className = 'form-input';
  for (const [value, label] of [['enum', 'Enum (one value)'], ['flags', 'Flags (bit set)']]) {
    const option = document.createElement('option');
    option.value = value;
    option.textContent = label;
    kindSelect.appendChild(option);
  }
  kindSelect.value = existing && existing.kind === 'flags' ? 'flags' : 'enum';
  kindRow.appendChild(kindSelect);
  wrap.appendChild(kindRow);

  const valuesRow = document.createElement('label');
  valuesRow.className = 'form-row';
  const valuesCaption = document.createElement('span');
  valuesCaption.textContent = 'Values';
  valuesRow.appendChild(valuesCaption);

  const valuesInput = document.createElement('input');
  valuesInput.type = 'text';
  valuesInput.className = 'form-input';
  valuesInput.placeholder = 'One, Two, Three';
  valuesInput.value = existing ? (existing.values || []).join(',') : '';
  valuesRow.appendChild(valuesInput);
  wrap.appendChild(valuesRow);

  const hint = document.createElement('p');
  hint.className = 'form-hint';
  hint.textContent =
    'Leave an entry blank for a value with no label. Every column that uses this type gets these values.';
  wrap.appendChild(hint);

  if (existing && existing.columns && existing.columns.length > 0) {
    const users = document.createElement('p');
    users.className = 'form-hint';
    users.textContent = `Used by ${existing.columns
      .map((entry) => `${entry.sheet}.${entry.column}`)
      .join(', ')}.`;
    wrap.appendChild(users);
  }

  openModal({
    title: existing ? `Public type '${existing.name}'` : 'New public type',
    okLabel: existing ? 'Apply' : 'Create',
    build: () => wrap,
    onOk: () => {
      const name = nameInput.value.trim();
      if (!name) {
        toast('The type needs a name.', 'error');
        return false;
      }

      const values = valuesInput.value.split(',').map((value) => value.trim());
      if (values.every((value) => value.length === 0)) {
        toast('At least one value is needed.', 'error');
        return false;
      }

      send('savePublicType', {
        name,
        kind: kindSelect.value,
        values: values.join(','),
        renameFrom: existing ? existing.name : '',
      });
      return true;
    },
  });
}

/** Deletes a public type; the columns keep the values they already carry. */
export function confirmDeletePublicType(type) {
  const users = (type.columns || []).length;
  const body = document.createElement('div');
  body.className = 'form';

  const text = document.createElement('p');
  text.className = 'form-hint';
  text.textContent = users === 0
    ? `Delete the public type '${type.name}'?`
    : `Delete the public type '${type.name}'? The ${users} column(s) using it keep their values and become independent.`;
  body.appendChild(text);

  openModal({
    title: 'Delete public type',
    okLabel: 'Delete',
    build: () => body,
    onOk: () => {
      send('deletePublicType', { name: type.name });
      return true;
    },
  });
}
