// The menu bar: closing the popups, the recent-file list and the About window.

import { runAction } from './commands.js';
import { hideContextMenu } from './contextmenu.js';
import { modalOpen, openModal } from './dialogs.js';
import { elements } from './dom.js';
import { closeFindBar } from './findbar.js';
import { editor, grid, search } from './session.js';

function closeMenus() {
  for (const menu of elements.menubar.querySelectorAll('.menu.open')) {
    menu.classList.remove('open');
  }
}

elements.menubar.addEventListener('click', (event) => {
  const title = event.target.closest('.menu-title');
  if (title) {
    const menu = title.parentElement;
    const wasOpen = menu.classList.contains('open');
    closeMenus();
    if (!wasOpen) menu.classList.add('open');
    return;
  }

  const item = event.target.closest('.menu-item');
  if (item) {
    closeMenus();
    if (!item.disabled) runAction(item.dataset.action, item.dataset.path);
  }
});

document.addEventListener('mousedown', (event) => {
  if (!event.target.closest('.menu')) closeMenus();
});

document.addEventListener('keydown', (event) => {
  if (event.key === 'Escape') {
    closeMenus();
    hideContextMenu();
    // Escape leaves find mode, unless the table is being edited (Escape cancels the
    // editor there) or a dialog is up.
    if (search.open && !grid.isEditing() && !modalOpen()) closeFindBar();
  }
});

/**
 * Fills the "Open Recent" submenu. The host owns the list; this only draws it, so the
 * order and the pruning rules live in one place (AppSettings).
 */
export function renderRecentFiles(files) {
  const menu = elements.recentMenu;
  if (!menu) return;

  menu.innerHTML = '';
  const entries = Array.isArray(files) ? files : [];

  if (entries.length === 0) {
    const empty = document.createElement('div');
    empty.className = 'menu-item';
    empty.style.color = 'var(--text-faint)';
    empty.textContent = '(no recent files)';
    menu.appendChild(empty);
    return;
  }

  for (const entry of entries) {
    const button = document.createElement('button');
    button.type = 'button';
    button.className = 'menu-item' + (entry.exists ? '' : ' missing');
    button.dataset.action = 'openRecent';
    button.dataset.path = entry.path;
    button.title = entry.path;

    const name = document.createElement('span');
    name.textContent = entry.name;
    const folder = document.createElement('kbd');
    folder.textContent = entry.folder;

    button.appendChild(name);
    button.appendChild(folder);
    menu.appendChild(button);
  }

  const separator = document.createElement('div');
  separator.className = 'menu-sep';
  menu.appendChild(separator);

  const clear = document.createElement('button');
  clear.type = 'button';
  clear.className = 'menu-item';
  clear.dataset.action = 'clearRecent';
  const clearLabel = document.createElement('span');
  clearLabel.textContent = 'Clear Recent Files';
  clear.appendChild(clearLabel);
  menu.appendChild(clear);
}

/** Build details dialog (F09). */
export function showAbout(info) {
  openModal({
    title: 'About PalaceDB',
    okLabel: 'Close',
    hideCancel: true,
    build: () => {
      const wrap = document.createElement('div');
      wrap.className = 'about';

      const heading = document.createElement('div');
      heading.className = 'about-title';
      heading.textContent = 'PalaceDB ' + (info.version || '1.0');
      wrap.appendChild(heading);

      const subtitle = document.createElement('div');
      subtitle.className = 'about-subtitle';
      subtitle.textContent = 'Structured static database editor';
      wrap.appendChild(subtitle);

      const rows = [
        ['Runtime', info.runtime],
        ['WebView2', info.webView],
        ['System', info.os],
        ['Format', info.format],
        ['Settings', info.dataFolder],
      ];

      const table = document.createElement('div');
      table.className = 'about-rows';
      for (const [label, value] of rows) {
        if (!value) continue;
        const row = document.createElement('div');
        row.className = 'about-row';

        const key = document.createElement('span');
        key.className = 'about-key';
        key.textContent = label;

        const text = document.createElement('span');
        text.className = 'about-value';
        text.textContent = value;
        text.title = value;

        row.appendChild(key);
        row.appendChild(text);
        table.appendChild(row);
      }
      wrap.appendChild(table);

      return wrap;
    },
    onOk: () => true,
  });
}
