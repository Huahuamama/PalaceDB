// Thumbnail cache for the image paths stored in cells.
//
// PalaceDB stores only a path string (decision Q5), so the host reads the file and answers
// with a data URL. This module keeps those URLs, remembers which paths are still in flight
// and notifies the views when a batch arrives. Everything is keyed by the stored path so
// the same image is only transferred once per session.

import { send } from './bridge.js';

/** Host limit is 64; staying below keeps each message small. */
const BATCH = 48;

const urls = new Map();     // stored path -> data URL
const failures = new Map(); // stored path -> reason
const pending = new Set();  // asked for, no answer yet
const listeners = new Set();

let queue = [];
let scheduled = false;

/** Data URL of a path, or undefined while it is unknown. */
export function imageUrl(path) {
  return urls.get(path);
}

/** Reason a path could not be shown, or undefined. */
export function imageError(path) {
  return failures.get(path);
}

/** Registers a callback fired once per answered batch. */
export function onImagesLoaded(handler) {
  listeners.add(handler);
}

/** Forgets everything; called when another document is opened. */
export function clearImages() {
  urls.clear();
  failures.clear();
  pending.clear();
  queue = [];
}

/**
 * Asks the host for a path. Safe to call on every render: known, failed and in-flight
 * paths are ignored, so a scroll does not flood the message channel.
 */
export function requestImage(path) {
  if (!path || urls.has(path) || failures.has(path) || pending.has(path)) return;
  pending.add(path);
  queue.push(path);
  schedule();
}

function schedule() {
  if (scheduled) return;
  scheduled = true;
  queueMicrotask(flush);
}

function flush() {
  scheduled = false;
  while (queue.length > 0) {
    send('resolveImages', { paths: queue.splice(0, BATCH) });
  }
}

/** Applies a host answer and notifies the views. */
export function applyImages(entries) {
  if (!Array.isArray(entries) || entries.length === 0) return;

  let changed = false;
  for (const entry of entries) {
    if (!entry || typeof entry.path !== 'string') continue;
    pending.delete(entry.path);
    if (entry.url) {
      urls.set(entry.path, entry.url);
      failures.delete(entry.path);
    } else {
      urls.delete(entry.path);
      failures.set(entry.path, entry.error || 'unavailable');
    }
    changed = true;
  }

  if (!changed) return;
  for (const listener of listeners) listener();
}
