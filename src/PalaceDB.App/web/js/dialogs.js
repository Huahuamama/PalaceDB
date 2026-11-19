// The modal dialog: one at a time, a form in the middle and an OK / Cancel footer.
//
// It is a plain element that is filled and shown rather than a <dialog>, so the caller
// builds the body and decides what OK means. Escape and the backdrop close it the same
// way the Cancel button does.

import { editor, grid } from './session.js';

export const modal = {
  root: document.getElementById('modal'),
  box: document.querySelector('#modal .modal-box'),
  title: document.getElementById('modalTitle'),
  body: document.getElementById('modalBody'),
  ok: document.getElementById('modalOk'),
  cancel: document.getElementById('modalCancel'),
};

let modalSubmit = null;
let modalCancel = null;

export function closeModal() {
  modal.root.hidden = true;
  modalSubmit = null;
  modalCancel = null;
}

/** True while a dialog covers the table. */
export function modalOpen() {
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
export function openModal({ title, build, onOk, onCancel = null, okLabel = 'OK', wide = false, hideCancel = false }) {
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
