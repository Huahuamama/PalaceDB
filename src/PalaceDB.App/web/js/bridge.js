// Bridge between the page and the native host.
// Messages are plain JSON objects: { type, payload }.

const host = (typeof window !== 'undefined' && window.chrome && window.chrome.webview) || null;

/** True when the page runs inside the PalaceDB shell rather than a plain browser. */
export const connected = host !== null;

/** Sends a command to the host. */
export function send(type, payload) {
  if (!host) {
    console.info('[bridge:out]', type, payload ?? '');
    return;
  }
  const message = { type };
  if (payload !== undefined) message.payload = payload;
  host.postMessage(message);
}

/** Registers the handler for host notifications. */
export function onMessage(handler) {
  if (!host) return;
  host.addEventListener('message', (event) => handler(event.data));
}
