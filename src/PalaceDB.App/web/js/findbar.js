// Find and replace across the open table.
//
// The host owns the matching: this module describes the query, the host answers with the
// hits, and the cursor in them is all that is kept here.

import { send } from './bridge.js';
import { elements } from './dom.js';
import { editor, grid, search, state } from './session.js';
import { applySearchHits, selectSheet } from './view.js';

/** The request the bar currently describes. The host fills in the actual hits. */
function searchPayload() {
  return {
    query: elements.findInput.value,
    regex: elements.findRegex.checked,
    matchCase: elements.findCase.checked,
    scope: elements.findScope.value,
    sheet: state.currentSheet,
  };
}

export function openFindBar(withReplace) {
  if (!state.document) return;

  // A dialog or an open cell editor would swallow the keyboard; settle both first.
  if (grid.isEditing()) editor.commit();

  search.open = true;
  elements.findBar.hidden = false;
  elements.findReplaceRow.hidden = !withReplace;

  // A reopened bar keeps its query and re-runs it, because the table may have changed.
  if (elements.findInput.value) {
    runSearch({ reveal: false });
  } else {
    search.matches = [];
    search.index = -1;
    applySearchHits();
    updateFindCount();
  }

  elements.findInput.focus();
  elements.findInput.select();
}

export function closeFindBar() {
  if (!search.open) return;

  clearTimeout(search.timer);
  search.open = false;
  search.matches = [];
  search.index = -1;
  search.truncated = false;
  search.revealPending = false;
  elements.findBar.hidden = true;
  grid.setSearchHits([], null);
  updateFindCount();
  elements.gridBody.focus();
}

/** Clears the bar without touching focus (used when the document goes away). */
export function resetSearch() {
  clearTimeout(search.timer);
  search.open = false;
  search.matches = [];
  search.index = -1;
  search.truncated = false;
  search.revealPending = false;
  elements.findBar.hidden = true;
  grid.setSearchHits([], null);
  updateFindCount();
}

/** Sends a query. `reveal` moves the cursor to the first hit when the reply arrives. */
export function runSearch({ reveal = false } = {}) {
  const payload = searchPayload();

  if (!payload.query || !state.currentSheet) {
    search.matches = [];
    search.index = -1;
    search.truncated = false;
    applySearchHits();
    updateFindCount();
    return;
  }

  search.revealPending = reveal;
  send('search', payload);
}

/** Coalesces keystrokes: one query per pause, not one per character. */
function scheduleSearch() {
  clearTimeout(search.timer);
  search.timer = setTimeout(() => runSearch({ reveal: false }), 160);
}

/** Index of the first hit at or after the cursor, so typing does not jump backwards. */
function firstMatchFromCursor(matches) {
  const { row, col } = grid.cursor;
  const sheet = state.currentSheet;

  for (let i = 0; i < matches.length; i += 1) {
    const match = matches[i];
    if (match.sheet !== sheet) continue;
    if (match.row > row || (match.row === row && match.column >= col)) return i;
  }
  return 0;
}

export function onSearchResults(payload) {
  if (!search.open) return;

  const matches = Array.isArray(payload.matches) ? payload.matches : [];
  const previous = search.index >= 0 ? search.matches[search.index] : null;
  const reveal = search.revealPending;
  search.revealPending = false;

  search.matches = matches;
  search.truncated = Boolean(payload.truncated);

  if (matches.length === 0) {
    search.index = -1;
    applySearchHits();
    updateFindCount();
    return;
  }

  // Keep the match the user was on when it survived the edit, otherwise start at the
  // cursor. A late reply for an older query is ignored by checking the text as well.
  let index = -1;
  if (previous) {
    index = matches.findIndex((match) =>
      match.sheet === previous.sheet && match.row === previous.row &&
      match.column === previous.column && match.start === previous.start);
  }
  if (index < 0) index = firstMatchFromCursor(matches);

  search.index = index;

  if (reveal) {
    // Enter / the arrows: follow the match even into another table.
    showMatch();
    return;
  }

  // Typing only moves within the table on screen; switching tables under the user while
  // they type would be disorienting. A hit elsewhere still counts and is one Enter away.
  const match = search.matches[index];
  if (match && match.sheet === state.currentSheet) {
    grid.moveCursor(match.row, match.column, false, true, false);
  }
  applySearchHits();
  updateFindCount();
}

function updateFindCount() {
  const count = elements.findCount;
  const total = search.matches.length;

  if (!elements.findInput.value || total === 0) {
    count.textContent = '0/0';
    count.classList.toggle('none', Boolean(elements.findInput.value));
    return;
  }

  count.classList.remove('none');
  const position = search.index >= 0 ? search.index + 1 : 0;
  count.textContent = search.truncated ? `${position}/${total}+` : `${position}/${total}`;
}

/** Moves the cursor to the current match, switching tables when it lives elsewhere. */
function showMatch() {
  const match = search.matches[search.index];
  if (!match) return;

  if (match.sheet !== state.currentSheet) selectSheet(match.sheet);

  // focus=false: the find field keeps the keyboard, so typing can continue.
  grid.moveCursor(match.row, match.column, false, true, false);
  applySearchHits();
  updateFindCount();
}

export function stepMatch(delta) {
  if (search.matches.length === 0) {
    runSearch({ reveal: true });
    return;
  }

  search.index = (search.index + delta + search.matches.length) % search.matches.length;
  showMatch();
}

/** Position of a match among the matches of its own cell, which is what replaceOne takes. */
function matchIndexInCell(target) {
  let index = 0;
  for (const match of search.matches) {
    if (match.sheet !== target.sheet || match.row !== target.row || match.column !== target.column)
      continue;
    if (match.start === target.start && match.length === target.length) return index;
    index += 1;
  }
  return 0;
}

export function replaceCurrent() {
  const match = search.matches[search.index];
  if (!match) {
    stepMatch(1);
    return;
  }

  send('replaceOne', {
    ...searchPayload(),
    sheet: match.sheet,
    row: match.row,
    column: match.column,
    matchIndex: matchIndexInCell(match),
    replacement: elements.replaceInput.value,
  });
}

export function replaceAllInScope() {
  const payload = searchPayload();
  if (!payload.query) return;
  send('replaceAll', { ...payload, replacement: elements.replaceInput.value });
}

elements.findInput.addEventListener('input', scheduleSearch);

elements.findInput.addEventListener('keydown', (event) => {
  if (event.key === 'Enter') {
    event.preventDefault();
    stepMatch(event.shiftKey ? -1 : 1);
  } else if (event.key === 'Escape') {
    event.preventDefault();
    event.stopPropagation();
    closeFindBar();
  }
});

elements.replaceInput.addEventListener('keydown', (event) => {
  if (event.key === 'Enter') {
    event.preventDefault();
    replaceCurrent();
  } else if (event.key === 'Escape') {
    event.preventDefault();
    event.stopPropagation();
    closeFindBar();
  }
});

// A toggle or scope change re-runs the query; keeping the index keeps the cursor put.
elements.findRegex.addEventListener('change', () => runSearch({ reveal: false }));
elements.findCase.addEventListener('change', () => runSearch({ reveal: false }));
elements.findScope.addEventListener('change', () => runSearch({ reveal: false }));

elements.findPrev.addEventListener('click', () => {
  stepMatch(-1);
  elements.findInput.focus();
});
elements.findNext.addEventListener('click', () => {
  stepMatch(1);
  elements.findInput.focus();
});
elements.replaceOne.addEventListener('click', () => {
  replaceCurrent();
  elements.replaceInput.focus();
});
elements.replaceAll.addEventListener('click', () => {
  replaceAllInScope();
  elements.replaceInput.focus();
});
elements.findClose.addEventListener('click', closeFindBar);
