// The transient messages in the corner of the window.

import { elements } from './dom.js';

export function toast(message, level = 'info') {
  const node = document.createElement('div');
  node.className = 'toast ' + level;
  node.textContent = message;
  elements.toasts.appendChild(node);
  setTimeout(() => node.remove(), level === 'error' ? 6000 : 3000);
}
