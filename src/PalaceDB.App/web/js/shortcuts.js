// Keyboard shortcuts that belong to the window rather than to a widget.
//
// They are registered on the document and step aside for a dialog, an open menu or a
// text field, which keep the keys for themselves.

import { send } from './bridge.js';
import { runRowAction } from './commands.js';
import { modal, modalOpen } from './dialogs.js';
import { openFindBar } from './findbar.js';
import { editor, grid } from './session.js';
import { openSettings } from './settingsdlg.js';
import { toggleSidebar } from './view.js';
import { toggleTheme } from './viewoptions.js';

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
