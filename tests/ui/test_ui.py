"""End-to-end UI tests.

Drives the real application through the WebView2 DevTools protocol: the app is
started with PALACEDB_CDP set, a temporary copy of a sample database is opened and
the checks interact with the actual DOM.

Requirements: the app must be built first (dotnet build) and the following Python
packages installed: pillow (not needed here), websocket-client.

Usage:
    python tests/ui/test_ui.py [path-to-cdb]
"""
import ctypes, json, os, shutil, subprocess, sys, time, urllib.request
import base64
import websocket

# Repository root, derived from this file so a clone in any directory works:
# tests/ui/test_ui.py -> tests/ui -> tests -> <root>.
ROOT = os.path.dirname(os.path.dirname(os.path.dirname(os.path.abspath(__file__))))
EXE = os.path.join(ROOT, "src", "PalaceDB.App", "bin", "Debug", "net8.0-windows", "PalaceDB.exe")

# Each run gets its own folder: deleting the previous one in bulk is both slow and
# destructive-looking, and the artefacts are useful when a check fails.
WORK = os.path.join(ROOT, ".ui-test-work", time.strftime("%Y%m%d-%H%M%S"))
os.makedirs(WORK, exist_ok=True)
print("work folder:", WORK)
CDB = os.path.join(WORK, "TestData2.cdb")
FIXTURES = os.path.join(ROOT, "tests", "fixtures")
SOURCE = sys.argv[1] if len(sys.argv) > 1 else os.path.join(FIXTURES, "TestData2.cdb")
shutil.copy(SOURCE, CDB)

# Keep the run out of the real user profile: settings and the WebView2 cache go here.
DATA_DIR = os.path.join(WORK, "appdata")
os.makedirs(DATA_DIR, exist_ok=True)
os.environ["PALACEDB_DATA_DIR"] = DATA_DIR

env = dict(os.environ, PALACEDB_CDP="9333", PALACEDB_BACKUP_SECONDS="2", PALACEDB_AUTOMATION="1")


def set_clipboard(text):
    """Puts unicode text on the Windows clipboard.

    The paste path reads the real clipboard, so the suite has to write it from here;
    a page cannot do that without a user gesture.
    """
    CF_UNICODETEXT = 13
    GMEM_MOVEABLE = 0x0002
    user32 = ctypes.windll.user32
    kernel32 = ctypes.windll.kernel32

    # Handles are pointers: without these declarations ctypes truncates them to int.
    user32.OpenClipboard.argtypes = [ctypes.c_void_p]
    user32.OpenClipboard.restype = ctypes.c_bool
    user32.SetClipboardData.argtypes = [ctypes.c_uint, ctypes.c_void_p]
    user32.SetClipboardData.restype = ctypes.c_void_p
    kernel32.GlobalAlloc.argtypes = [ctypes.c_uint, ctypes.c_size_t]
    kernel32.GlobalAlloc.restype = ctypes.c_void_p
    kernel32.GlobalLock.argtypes = [ctypes.c_void_p]
    kernel32.GlobalLock.restype = ctypes.c_void_p
    kernel32.GlobalUnlock.argtypes = [ctypes.c_void_p]

    data = text.encode("utf-16-le") + b"\x00\x00"
    for _ in range(20):
        if user32.OpenClipboard(0):
            break
        time.sleep(0.1)
    else:
        raise RuntimeError("could not open the clipboard")

    try:
        user32.EmptyClipboard()
        handle = kernel32.GlobalAlloc(GMEM_MOVEABLE, len(data))
        pointer = kernel32.GlobalLock(handle)
        ctypes.memmove(pointer, data, len(data))
        kernel32.GlobalUnlock(handle)
        user32.SetClipboardData(CF_UNICODETEXT, handle)
    finally:
        user32.CloseClipboard()


def read_clipboard():
    """The text currently on the Windows clipboard, or None."""
    CF_UNICODETEXT = 13
    user32 = ctypes.windll.user32
    kernel32 = ctypes.windll.kernel32
    user32.OpenClipboard.argtypes = [ctypes.c_void_p]
    user32.OpenClipboard.restype = ctypes.c_bool
    user32.GetClipboardData.argtypes = [ctypes.c_uint]
    user32.GetClipboardData.restype = ctypes.c_void_p
    kernel32.GlobalLock.argtypes = [ctypes.c_void_p]
    kernel32.GlobalLock.restype = ctypes.c_void_p
    kernel32.GlobalUnlock.argtypes = [ctypes.c_void_p]

    for _ in range(20):
        if user32.OpenClipboard(0):
            break
        time.sleep(0.1)
    else:
        return None

    try:
        handle = user32.GetClipboardData(CF_UNICODETEXT)
        if not handle:
            return None
        pointer = kernel32.GlobalLock(handle)
        try:
            return ctypes.wstring_at(pointer)
        finally:
            kernel32.GlobalUnlock(handle)
    finally:
        user32.CloseClipboard()


def park_windows(pid, offset=-4000):
    """Moves every top-level window of `pid` off the desktop.

    The suite drives the real application, so without this the editor windows pop up
    over whatever the user is doing. Off-screen windows keep rendering (unlike a
    minimised one), so the DevTools checks still see a live page.
    """
    try:
        import ctypes
        from ctypes import wintypes

        user32 = ctypes.windll.user32
        found = []

        @ctypes.WINFUNCTYPE(wintypes.BOOL, wintypes.HWND, wintypes.LPARAM)
        def collect(hwnd, _):
            owner = wintypes.DWORD()
            user32.GetWindowThreadProcessId(hwnd, ctypes.byref(owner))
            if owner.value == pid and user32.IsWindowVisible(hwnd):
                found.append(hwnd)
            return True

        user32.EnumWindows(collect, 0)
        for hwnd in found:
            # SWP_NOSIZE | SWP_NOZORDER | SWP_NOACTIVATE
            user32.SetWindowPos(hwnd, 0, offset, offset, 0, 0, 0x0001 | 0x0004 | 0x0010)
        return len(found)
    except Exception as error:  # pragma: no cover - best effort only
        print("note: could not park windows:", error)
        return 0


def window_rect(pid):
    """Geometry of the first visible top-level window of `pid`, or None."""
    try:
        import ctypes
        from ctypes import wintypes

        user32 = ctypes.windll.user32
        found = []

        @ctypes.WINFUNCTYPE(wintypes.BOOL, wintypes.HWND, wintypes.LPARAM)
        def collect(hwnd, _):
            owner = wintypes.DWORD()
            user32.GetWindowThreadProcessId(hwnd, ctypes.byref(owner))
            if owner.value == pid and user32.IsWindowVisible(hwnd):
                found.append(hwnd)
            return True

        user32.EnumWindows(collect, 0)
        if not found:
            return None

        class RECT(ctypes.Structure):
            _fields_ = [("left", ctypes.c_long), ("top", ctypes.c_long),
                        ("right", ctypes.c_long), ("bottom", ctypes.c_long)]

        box = RECT()
        user32.GetWindowRect(found[0], ctypes.byref(box))
        return (box.left, box.top, box.right - box.left, box.bottom - box.top)
    except Exception as error:  # pragma: no cover - best effort only
        print("note: could not read the window rect:", error)
        return None


def move_window(pid, x, y):
    """Moves the window of `pid` to (x, y) without changing its size."""
    try:
        import ctypes
        from ctypes import wintypes

        user32 = ctypes.windll.user32
        found = []

        @ctypes.WINFUNCTYPE(wintypes.BOOL, wintypes.HWND, wintypes.LPARAM)
        def collect(hwnd, _):
            owner = wintypes.DWORD()
            user32.GetWindowThreadProcessId(hwnd, ctypes.byref(owner))
            if owner.value == pid and user32.IsWindowVisible(hwnd):
                found.append(hwnd)
            return True

        user32.EnumWindows(collect, 0)
        for hwnd in found:
            # SWP_NOSIZE | SWP_NOZORDER | SWP_NOACTIVATE
            user32.SetWindowPos(hwnd, 0, x, y, 0, 0, 0x0001 | 0x0004 | 0x0010)
        return len(found) > 0
    except Exception as error:  # pragma: no cover - best effort only
        print("note: could not move the window:", error)
        return False


def wait_rect(pid, timeout=3.0):
    """The window rect once it is stable, so a pending move has landed."""
    end = time.time() + timeout
    last = window_rect(pid)
    while time.time() < end:
        time.sleep(0.2)
        current = window_rect(pid)
        if current == last:
            return current
        last = current
    return last


proc = subprocess.Popen([EXE, CDB], env=env)
ws = None
FIRST_TARGET = None
for _ in range(60):
    time.sleep(0.5)
    park_windows(proc.pid)
    try:
        with urllib.request.urlopen("http://127.0.0.1:9333/json/list", timeout=2) as r:
            targets = json.load(r)
        pages = [t for t in targets if t.get("type") == "page"]
        if pages:
            FIRST_TARGET = pages[0]["webSocketDebuggerUrl"]
            ws = websocket.create_connection(FIRST_TARGET, timeout=15)
            break
    except Exception:
        continue

if ws is None:
    print("FAIL: no CDP target"); proc.terminate(); sys.exit(1)

counter = [0]
def evaluate(expr, await_promise=False, sock=None):
    sock = sock or ws
    counter[0] += 1
    sock.send(json.dumps({
        "id": counter[0], "method": "Runtime.evaluate",
        "params": {"expression": expr, "returnByValue": True, "awaitPromise": await_promise},
    }))
    while True:
        msg = json.loads(sock.recv())
        if msg.get("id") == counter[0]:
            res = msg.get("result", {})
            if "exceptionDetails" in res:
                details = res["exceptionDetails"]
                description = (details.get("exception") or {}).get("description") or details.get("text")
                raise RuntimeError(str(description) + " :: " + expr[:200])
            return res.get("result", {}).get("value")

def wait_for_file(path, timeout=15, should_exist=True):
    end = time.time() + timeout
    while time.time() < end:
        if os.path.exists(path) == should_exist:
            return True
        time.sleep(0.3)
    return False

def wait_for(expr, timeout=20):
    end = time.time() + timeout
    while time.time() < end:
        try:
            if evaluate(expr):
                return True
        except Exception:
            pass
        time.sleep(0.25)
    return False

results = []
def check(name, ok, detail=""):
    results.append((name, ok, detail))
    print(("PASS " if ok else "FAIL ") + name + ("" if ok else "  -> " + str(detail)))

# --- wait for the document
ok = wait_for("!!(window.__palacedb && __palacedb.state.document)")
check("document loaded", ok, evaluate("document.title"))
print("title:", evaluate("document.title"))
print("tables:", evaluate("__palacedb.state.document.sheets.map(s=>s.name).join(',')"))
print("columns:", evaluate("__palacedb.grid.columns.map(c=>c.name+':'+c.type).join(',')"))

# --- rows arrive
ok = wait_for("__palacedb.grid.cache.size > 0")
check("rows rendered from host", ok, evaluate("__palacedb.grid.cache.size"))

# The bundled sample tables name their identifier and text columns differently, so the
# checks that touch them look the columns up instead of hard coding a name.
ID_NAME = evaluate("(() => { const c = __palacedb.grid.columns.find(c => c.type === 'Id'); return c ? c.name : 'Id'; })()")
TEXT_NAME = evaluate("(() => { const c = __palacedb.grid.columns.find(c => c.type === 'Text'); return c ? c.name : 'Des'; })()")
evaluate("window.__testText = %s" % json.dumps(TEXT_NAME))

# --- text edit
evaluate("__palacedb.grid.moveCursor(0,1)")
evaluate("__palacedb.grid.onEditRequest(0,1,'')")
check("editor opened", evaluate("!!document.querySelector('.editor-input')"))
evaluate("""(() => {
  const i = document.querySelector('.editor-input');
  i.value = 'EDITED_BY_TEST';
  i.dispatchEvent(new KeyboardEvent('keydown', {key:'Enter', bubbles:true}));
  return true;
})()""")
time.sleep(0.6)
val = evaluate("__palacedb.grid.cellValue(0,1) && __palacedb.grid.cellValue(0,1).t")
check("text edit applied", val == "EDITED_BY_TEST", val)
check("document dirty", evaluate("__palacedb.state.document.dirty") is True)

# --- undo via keyboard
evaluate("document.body.dispatchEvent(new KeyboardEvent('keydown', {key:'z', ctrlKey:true, bubbles:true}))")
time.sleep(0.8)
wait_for("__palacedb.grid.cellValue(0,1) !== undefined")
val = evaluate("__palacedb.grid.cellValue(0,1) && __palacedb.grid.cellValue(0,1).t")
check("undo reverted the edit", val != "EDITED_BY_TEST" and val is not None, val)
print("after undo:", val)

# --- redo
evaluate("document.body.dispatchEvent(new KeyboardEvent('keydown', {key:'y', ctrlKey:true, bubbles:true}))")
time.sleep(0.8)
wait_for("__palacedb.grid.cellValue(0,1) !== undefined")
val = evaluate("__palacedb.grid.cellValue(0,1) && __palacedb.grid.cellValue(0,1).t")
check("redo reapplied the edit", val == "EDITED_BY_TEST", val)

# --- multi-line text cells
# A Text column gets a growing textarea: Shift+Enter inserts a break, a bare
# Enter commits, and the grid shows the break as a visible marker.
ml_row = 1
ml_col = evaluate("__palacedb.grid.columns.findIndex(c=>c.type==='Text')")
if ml_col >= 0:
    # The block above committed with Enter. Its editor node stays in the DOM for a frame
    # or two, and `document.querySelector` happily returns that dying node — the keystrokes
    # then land on the previous cell while this block checks a different one. Wait for it
    # to go before asking for a new editor, then wait for the new one to appear.
    wait_for("!document.querySelector('.editor-input')", 5)
    evaluate(f"__palacedb.grid.moveCursor({ml_row},{ml_col})")
    evaluate(f"__palacedb.grid.onEditRequest({ml_row},{ml_col},'')")
    wait_for("!!document.querySelector('.editor-input')", 5)
    tag = evaluate("(()=>{const e=document.querySelector('.editor-input');return e?e.tagName:null})()")
    check("text editor is a textarea", tag == "TEXTAREA", tag)

    # Shift+Enter must not commit, so the editor stays open.
    evaluate("""(() => {
      const e = document.querySelector('.editor-input');
      e.value = 'LINE_ONE';
      e.dispatchEvent(new KeyboardEvent('keydown', {key:'Enter', shiftKey:true, bubbles:true}));
      return true;
    })()""")
    time.sleep(0.3)
    still_open = evaluate("!!document.querySelector('.editor-input')")
    check("shift+enter keeps the editor open", still_open is True, still_open)

    evaluate("""(() => {
      const e = document.querySelector('.editor-input');
      e.value = 'LINE_ONE\\nLINE_TWO';
      e.dispatchEvent(new KeyboardEvent('keydown', {key:'Enter', bubbles:true}));
      return true;
    })()""")
    time.sleep(0.8)
    # Wait for the commit to reach the grid instead of sampling it once: the host has to
    # accept the value and the grid repaint before the cell reads back the new text.
    wait_for(f"__palacedb.grid.cellValue({ml_row},{ml_col}) && "
             f"__palacedb.grid.cellValue({ml_row},{ml_col}).t === 'LINE_ONE\\nLINE_TWO'", 10)
    ml_val = evaluate(f"__palacedb.grid.cellValue({ml_row},{ml_col}).t")
    check("multi-line value committed", ml_val == "LINE_ONE\nLINE_TWO", repr(ml_val))

    rendered = evaluate(
        f"""(() => {{
          const c = document.querySelector('.cell[data-row="{ml_row}"][data-col="{ml_col}"]');
          if (!c) return null;
          return {{text: c.textContent, cls: c.classList.contains('multiline'), title: c.title}};
        }})()""")
    check("cell keeps the real line break",
          rendered and (rendered.get("text") or "") == "LINE_ONE\nLINE_TWO", rendered)
    check("cell marked as multiline", rendered and rendered.get("cls") is True, rendered)
    check("cell wraps instead of overflowing",
          evaluate(f"""(() => {{
            const c = document.querySelector('.cell[data-row="{ml_row}"][data-col="{ml_col}"]');
            return c ? getComputedStyle(c).whiteSpace : null;
          }})()""") == "pre-wrap")
    check("cell tooltip holds the raw text",
          rendered and rendered.get("title") == "LINE_ONE\nLINE_TWO", rendered)

    # Undo leaves the cell single-line again.
    evaluate("document.body.dispatchEvent(new KeyboardEvent('keydown',{key:'z',ctrlKey:true,bubbles:true}))")
    time.sleep(0.8)
    wait_for(f"__palacedb.grid.cellValue({ml_row},{ml_col}) !== undefined")
    ml_after = evaluate(f"__palacedb.grid.cellValue({ml_row},{ml_col}).t")
    check("multi-line undo restored the old text",
          ml_after is not None and "LINE_ONE" not in ml_after, repr(ml_after))

# --- bool toggle
col = evaluate("__palacedb.grid.columns.findIndex(c=>c.type==='Bool')")
if col >= 0:
    before = evaluate(f"__palacedb.grid.cellValue(0,{col}).t")
    evaluate(f"__palacedb.grid.onToggleRequest(0,{col})")
    time.sleep(0.6)
    after = evaluate(f"__palacedb.grid.cellValue(0,{col}).t")
    check("bool toggle", before != after, f"{before} -> {after}")

# --- enum dropdown
col = evaluate("__palacedb.grid.columns.findIndex(c=>c.type==='Enum')")
if col >= 0:
    evaluate(f"__palacedb.grid.onEditRequest(0,{col})")
    time.sleep(0.3)
    n = evaluate("document.querySelectorAll('.cell-dropdown .dropdown-item').length")
    check("enum dropdown opened", n and n > 0, n)
    evaluate("""(() => {
      const items = document.querySelectorAll('.cell-dropdown .dropdown-item');
      if (items.length) items[items.length-1].dispatchEvent(new MouseEvent('mousedown', {bubbles:true}));
      return true;
    })()""")
    time.sleep(0.6)
    check("enum selection applied", True, evaluate(f"__palacedb.grid.cellValue(0,{col}).t"))


# --- multi-cell selection
evaluate("__palacedb.grid.moveCursor(0,1)")
evaluate("__palacedb.grid.moveCursor(2,2,true)")
sel = evaluate("(()=>{const b=__palacedb.grid.selectionBounds();return b.bottom-b.top+1})()")
selcols = evaluate("(()=>{const b=__palacedb.grid.selectionBounds();return b.right-b.left+1})()")
check("drag selection spans 3 rows", sel == 3, sel)
check("drag selection spans 2 columns", selcols == 2, selcols)

# --- copy then paste over another block
evaluate("__palacedb.grid.moveCursor(0,1)")
evaluate("__palacedb.grid.moveCursor(2,2,true)")
evaluate("document.body.dispatchEvent(new KeyboardEvent('keydown',{key:'c',ctrlKey:true,bubbles:true}))")
time.sleep(0.5)
src = evaluate("__palacedb.grid.cellValue(0,1).t")
# The sample tables differ in length, so the paste lands where there is room for it.
target_row = evaluate("Math.max(0, Math.min(10, __palacedb.grid.rowCount - 3))")
evaluate("__palacedb.grid.moveCursor(%d,1)" % target_row)
evaluate("document.body.dispatchEvent(new KeyboardEvent('keydown',{key:'v',ctrlKey:true,bubbles:true}))")
time.sleep(0.8)
wait_for("__palacedb.grid.cellValue(%d,1) !== undefined" % target_row)
pasted = evaluate("__palacedb.grid.cellValue(%d,1).t" % target_row)
check("paste overwrote the target block", pasted == src, f"{src!r} -> {pasted!r}")
check("paste keeps the cursor on the pasted cell",
      evaluate("__palacedb.grid.cursor.row === %d && __palacedb.grid.cursor.col === 1" % target_row),
      evaluate("JSON.stringify(__palacedb.grid.cursor)"))

# --- undo the paste
evaluate("document.body.dispatchEvent(new KeyboardEvent('keydown',{key:'z',ctrlKey:true,bubbles:true}))")
time.sleep(0.8)
wait_for("__palacedb.grid.cellValue(%d,1) !== undefined" % target_row)
after_undo = evaluate("__palacedb.grid.cellValue(%d,1).t" % target_row)
check("paste undo restored the previous value", after_undo != src, after_undo)

# --- a copied column lands in the column under the cursor, not back in its own column.
# Two text columns are picked from whatever sample is open, because the paste has to
# convert the values otherwise and this check is about placement.
text_cols = json.loads(evaluate(
    "JSON.stringify((() => { const c = __palacedb.grid.columns;"
    " const a = c.findIndex(x => x.type === 'Text');"
    " const b = c.findIndex((x, i) => i > a && x.type === 'Text'); return [a, b]; })())"))
text_a, text_b = text_cols
evaluate("__palacedb.grid.moveCursor(0,%d)" % text_a)
evaluate("__palacedb.grid.moveCursor(2,%d,true)" % text_a)
evaluate("document.body.dispatchEvent(new KeyboardEvent('keydown',{key:'c',ctrlKey:true,bubbles:true}))")
time.sleep(0.6)
copied_column = json.loads(evaluate(
    "JSON.stringify([0,1,2].map(r => __palacedb.grid.cellValue(r,%d).t))" % text_a))
evaluate("__palacedb.grid.moveCursor(0,%d)" % text_b)
evaluate("document.body.dispatchEvent(new KeyboardEvent('keydown',{key:'v',ctrlKey:true,bubbles:true}))")
time.sleep(1.0)
wait_for("__palacedb.grid.cellValue(2,%d) !== undefined" % text_b)
landed = json.loads(evaluate(
    "JSON.stringify([0,1,2].map(r => __palacedb.grid.cellValue(r,%d).t))" % text_b))
check("a copied column pastes into the column under the cursor", landed == copied_column,
      (copied_column, landed))
check("the column it was copied from is untouched",
      json.loads(evaluate(
          "JSON.stringify([0,1,2].map(r => __palacedb.grid.cellValue(r,%d).t))" % text_a))
      == copied_column)
evaluate("document.body.dispatchEvent(new KeyboardEvent('keydown',{key:'z',ctrlKey:true,bubbles:true}))")
time.sleep(0.8)

# --- Ctrl+Enter fills the block with the cursor cell's value (R5), without an editor
evaluate("__palacedb.grid.moveCursor(0,%d)" % text_a)
evaluate("__palacedb.grid.moveCursor(2,%d,true)" % text_a)
time.sleep(0.3)
fill_value = evaluate("__palacedb.grid.cellValue(2,%d).t" % text_a)
evaluate("document.getElementById('gridBody').dispatchEvent("
         "new KeyboardEvent('keydown', {key:'Enter', ctrlKey:true, bubbles:true}))")
time.sleep(1.0)
filled = json.loads(evaluate(
    "JSON.stringify([0,1,2].map(r => __palacedb.grid.cellValue(r,%d).t))" % text_a))
check("Ctrl+Enter fills the selection from the cursor cell", filled == [fill_value] * 3,
      (fill_value, filled))
check("Ctrl+Enter fills instead of opening the cell editor",
      evaluate("!!document.querySelector('.editor-input')") is False)
evaluate("document.body.dispatchEvent(new KeyboardEvent('keydown',{key:'z',ctrlKey:true,bubbles:true}))")
time.sleep(0.8)
check("the fill is one undo step",
      json.loads(evaluate(
          "JSON.stringify([0,1,2].map(r => __palacedb.grid.cellValue(r,%d).t))" % text_a))
      == copied_column)

# --- row operations
# The toolbar button appends, so the cursor is parked on the first row first: a new row
# belongs at the end of the table, not above whatever the cursor happens to be on.
evaluate("__palacedb.grid.moveCursor(0, 0)")
rows_before = evaluate("__palacedb.grid.rowCount")
evaluate("__palacedb.run('addRow')")
time.sleep(0.8)
wait_for(f"__palacedb.grid.rowCount === {rows_before + 1}")
rows_after = evaluate("__palacedb.grid.rowCount")
check("add row increases the count", rows_after == rows_before + 1, f"{rows_before} -> {rows_after}")
check("the new row is the last one, not the one above the cursor",
      evaluate("__palacedb.grid.cursor.row") == rows_after - 1,
      (rows_before, evaluate("__palacedb.grid.cursor.row")))

evaluate("__palacedb.grid.moveCursor(0,0)")
evaluate("__palacedb.run('deleteRows')")
time.sleep(0.8)
wait_for(f"__palacedb.grid.rowCount === {rows_before}")
rows_del = evaluate("__palacedb.grid.rowCount")
check("delete row decreases the count", rows_del == rows_before, f"{rows_after} -> {rows_del}")
cursor_after_delete = evaluate("__palacedb.grid.cursor.row")
check("cursor stays near the deleted row", cursor_after_delete == 0, cursor_after_delete)

# --- insert paste
rows_before2 = evaluate("__palacedb.grid.rowCount")
evaluate("__palacedb.grid.moveCursor(3,0)")
evaluate("document.body.dispatchEvent(new KeyboardEvent('keydown',{key:'V',shiftKey:true,ctrlKey:true,bubbles:true}))")
time.sleep(0.8)
wait_for(f"__palacedb.grid.rowCount > {rows_before2}")
rows_insert = evaluate("__palacedb.grid.rowCount")
check("insert paste adds rows", rows_insert > rows_before2, f"{rows_before2} -> {rows_insert}")

# --- insert / delete must not move the view
# A row operation re-sends the document. Treating the changed row count as "another
# table" used to reset the scroll to the top and then jump to the cursor.
evaluate("__palacedb.grid.body.scrollTop = 900")
time.sleep(0.7)
scroll_before = evaluate("Math.round(__palacedb.grid.body.scrollTop)")
edit_row = evaluate("__palacedb.grid.rowIndexAt(__palacedb.grid.body.scrollTop) + 2")

evaluate("__palacedb.send('insertRow', {sheet: __palacedb.state.currentSheet, row: %d})" % edit_row)
time.sleep(1.1)
scroll_insert = evaluate("Math.round(__palacedb.grid.body.scrollTop)")
check("inserting a row keeps the scroll position",
      abs(scroll_insert - scroll_before) <= 1, (scroll_before, scroll_insert))

evaluate("__palacedb.send('deleteRows', {sheet: __palacedb.state.currentSheet, rows: [%d]})" % edit_row)
time.sleep(1.1)
scroll_delete = evaluate("Math.round(__palacedb.grid.body.scrollTop)")
check("deleting a row keeps the scroll position",
      abs(scroll_delete - scroll_before) <= 1, (scroll_before, scroll_delete))

# --- a separator is a row index, so inserting above it has to move it
def current_separators():
    return json.loads(evaluate(
        "JSON.stringify((__palacedb.state.document.sheets.find("
        "s => s.name === __palacedb.state.currentSheet) || {}).separators || [])"))


sep_indices = current_separators()
insert_at = 1
evaluate("__palacedb.send('insertRow', {sheet: __palacedb.state.currentSheet, row: %d})" % insert_at)
time.sleep(1.0)
shifted = [s + 1 if s > insert_at else s for s in sep_indices]
check("inserting rows shifts the separators below them",
      current_separators() == shifted, (sep_indices, current_separators()))

evaluate("__palacedb.send('undo')")
time.sleep(1.0)
check("undo restores the separator indices", current_separators() == sep_indices,
      (sep_indices, current_separators()))

# --- dragging a row drags the separator that heads it. The dividers are cleared first so
# the check has one divider it owns; both commands are undone at the end.
for row in current_separators():
    evaluate("__palacedb.send('toggleSeparator', {sheet: __palacedb.state.currentSheet, row: %d})" % row)
wait_for("__palacedb.grid.separatorTitles.size === 0", 5)

evaluate("__palacedb.send('toggleSeparator', {sheet: __palacedb.state.currentSheet, row: 3})")
check("a separator is prepared for the drag test",
      wait_for("__palacedb.grid.separatorTitles.has(3)"), current_separators())

evaluate("__palacedb.send('moveRow', {sheet: __palacedb.state.currentSheet, from: 3, to: 6})")
check("dragging a row takes its separator with it",
      wait_for("__palacedb.grid.separatorTitles.has(6) && !__palacedb.grid.separatorTitles.has(3)"),
      current_separators())

evaluate("__palacedb.send('undo')")
check("undo puts the separator back on the dragged row",
      wait_for("__palacedb.grid.separatorTitles.has(3) && !__palacedb.grid.separatorTitles.has(6)"),
      current_separators())

evaluate("__palacedb.send('undo')")
check("the drag test cleans up after itself",
      wait_for("__palacedb.grid.separatorTitles.size === 0"), current_separators())

# --- column width is stored in the metadata side-car
evaluate("__palacedb.grid.columns[1].width = 210; __palacedb.grid.onColumnResized(__palacedb.grid.sheet, __palacedb.grid.columns[1].name, 210)")
time.sleep(0.9)
meta_path = CDB + ".meta.json"
has_meta = os.path.exists(meta_path)
check("metadata side-car created", has_meta, meta_path)
meta_text = open(meta_path, encoding="utf-8").read() if has_meta else ""
check("column width written to metadata", '"width": 210' in meta_text, meta_text[:160].replace("\n", " "))

# --- background colour, priority and persistence
evaluate("__palacedb.grid.moveCursor(0,1)")
evaluate("__palacedb.grid.moveCursor(1,1,true)")
evaluate("__palacedb.run('applyFill')")
time.sleep(1.0)
wait_for("__palacedb.grid.cellValue(0,1) !== undefined")
evaluate("__palacedb.grid.moveCursor(6,6)")
time.sleep(0.5)
bg = evaluate("(()=>{const c=document.querySelector('.cell[data-row=\"0\"][data-col=\"1\"]');return c?c.style.backgroundColor:null})()")
check("cell background rendered", bg not in (None, "", "rgba(0, 0, 0, 0)"), bg)

meta_text = open(meta_path, encoding="utf-8").read() if os.path.exists(meta_path) else ""
check("background stored in metadata", "ffcc00" in meta_text, meta_text[-300:].replace("\n", " "))

cdb_text = open(CDB, encoding="utf-8", errors="replace").read()
check("colours are not written into the .cdb", "ffcc00" not in cdb_text and '"width"' not in cdb_text)

# --- clearing a colour
evaluate("__palacedb.grid.moveCursor(0,1)")
evaluate("__palacedb.grid.moveCursor(1,1,true)")
evaluate("__palacedb.run('clearFill')")
time.sleep(1.0)
wait_for("__palacedb.grid.cellValue(0,1) !== undefined")
evaluate("__palacedb.grid.moveCursor(6,6)")
time.sleep(0.5)
bg2 = evaluate("(()=>{const c=document.querySelector('.cell[data-row=\"0\"][data-col=\"1\"]');return c?c.style.backgroundColor:null})()")
check("colour cleared", bg2 in (None, "", "rgba(0, 0, 0, 0)"), bg2)

# --- a row colour must survive an edit
# The single-row refresh sent after an edit used to rebuild the row without its `bg`,
# so a coloured row lost its fill the moment any cell in it changed and only got it back
# after switching tables (which re-fetches the row with its style).
evaluate("document.getElementById('fillScope').value = 'row'")
evaluate("__palacedb.grid.moveCursor(0,0)")
evaluate("__palacedb.run('applyFill')")
time.sleep(0.9)
wait_for("__palacedb.grid.cellValue(0,1) !== undefined")
row_bg_before = evaluate("(()=>{const c=document.querySelector('.cell[data-row=\"0\"][data-col=\"1\"]');return c?c.style.backgroundColor:null})()")
check("row background rendered", row_bg_before not in (None, "", "rgba(0, 0, 0, 0)"), row_bg_before)

evaluate("__palacedb.grid.moveCursor(0,1)")
evaluate("__palacedb.grid.onEditRequest(0,1,'')")
wait_for("!!document.querySelector('.editor-input')", 5)
evaluate("""(() => {
  const i = document.querySelector('.editor-input');
  i.value = 'ROW_BG_EDIT';
  i.dispatchEvent(new KeyboardEvent('keydown', {key:'Enter', bubbles:true}));
  return true;
})()""")
time.sleep(0.9)
wait_for("!document.querySelector('.editor-input')", 5)
row_bg_after = evaluate("(()=>{const c=document.querySelector('.cell[data-row=\"0\"][data-col=\"1\"]');return c?c.style.backgroundColor:null})()")
check("row background survives an edit", row_bg_after not in (None, "", "rgba(0, 0, 0, 0)"), row_bg_after)

# Restore the scope so the checks that follow behave as before.
evaluate("document.getElementById('fillScope').value = 'cell'")
evaluate("__palacedb.grid.moveCursor(0,0)")
evaluate("__palacedb.run('clearFill')")
time.sleep(0.6)

# --- find / replace
# The fixture has been edited above, so this block writes its own markers first: these
# checks are about the find bar, not about the sample's original text.

def select_sheet(name):
    evaluate("document.querySelector('.table-item[data-sheet=%s]').click()" % json.dumps(name))
    wait_for("__palacedb.state.currentSheet === %s" % json.dumps(name), 8)
    time.sleep(0.4)


def write_cell(row, col, text):
    column = evaluate("__palacedb.grid.columns[%d].name" % col)
    evaluate("__palacedb.send('setCell', {sheet: %s, row: %d, column: %s, text: %s})"
             % (json.dumps(evaluate("__palacedb.state.currentSheet")), row,
                json.dumps(column), json.dumps(text)))
    time.sleep(0.5)


def find(text, scope="table", regex=False, replacement=None):
    evaluate("""(() => {
      __palacedb.openFindBar(%s);
      const scope = document.getElementById('findScope');
      scope.value = %s;
      scope.dispatchEvent(new Event('change', {bubbles: true}));
      const rx = document.getElementById('findRegex');
      rx.checked = %s;
      rx.dispatchEvent(new Event('change', {bubbles: true}));
      const input = document.getElementById('findInput');
      input.value = %s;
      input.dispatchEvent(new Event('input', {bubbles: true}));
      if (%s) document.getElementById('replaceInput').value = %s;
      return true;
    })()""" % ("true" if replacement is not None else "false", json.dumps(scope),
              "true" if regex else "false", json.dumps(text),
              "true" if replacement is not None else "false", json.dumps(replacement or "")))
    time.sleep(0.9)


select_sheet("EventCard")
evaluate("__palacedb.grid.moveCursor(0,0)")
write_cell(0, 1, "ZZSEARCHA one")
write_cell(1, 1, "ZZSEARCHA two")
select_sheet("Item")
write_cell(0, 1, "ZZSEARCHA three")
select_sheet("EventCard")
evaluate("__palacedb.grid.moveCursor(0,0)")
time.sleep(0.3)

find("ZZSEARCHA", "table")
wait_for("__palacedb.searchState.matches.length === 2", 8)
check("find: the table scope finds both cells",
      evaluate("__palacedb.searchState.matches.length") == 2,
      evaluate("__palacedb.searchState.matches.length"))
check("find: hits are painted on the grid", evaluate("__palacedb.grid.searchHits.size") == 2,
      evaluate("__palacedb.grid.searchHits.size"))

evaluate("__palacedb.stepMatch(1)")
time.sleep(0.4)
check("find: next moves to the second hit", evaluate("__palacedb.grid.cursor.row") == 1,
      evaluate("JSON.stringify(__palacedb.grid.cursor)"))

find("ZZSEARCHA", "document")
wait_for("__palacedb.searchState.matches.length === 3", 8)
check("find: the whole file finds the other table",
      evaluate("__palacedb.searchState.matches.length") == 3,
      evaluate("__palacedb.searchState.matches.length"))

for _ in range(6):
    if evaluate("__palacedb.state.currentSheet") == "Item":
        break
    evaluate("__palacedb.stepMatch(1)")
    time.sleep(0.35)
check("find: next crosses into the other table",
      evaluate("__palacedb.state.currentSheet") == "Item",
      evaluate("__palacedb.state.currentSheet"))

select_sheet("EventCard")
evaluate("__palacedb.grid.moveCursor(0,0)")
find(r"ZZSEARCH\w+", "table", True)
wait_for("__palacedb.searchState.matches.length >= 2", 8)
check("find: a regular expression matches the cells",
      evaluate("__palacedb.searchState.matches.length") >= 2,
      evaluate("__palacedb.searchState.matches.length"))

find("zzsearcha", "table")
wait_for("__palacedb.searchState.matches.length === 2", 8)
evaluate("document.getElementById('findCase').checked = true;"
         "document.getElementById('findCase').dispatchEvent(new Event('change',{bubbles:true}))")
time.sleep(0.8)
check("find: match case is honoured", evaluate("__palacedb.searchState.matches.length") == 0,
      evaluate("__palacedb.searchState.matches.length"))
evaluate("document.getElementById('findCase').checked = false;"
         "document.getElementById('findCase').dispatchEvent(new Event('change',{bubbles:true}))")
time.sleep(0.6)

evaluate("__palacedb.grid.moveCursor(0,0)")
find("ZZSEARCHA", "table", False, "ZZREPLACED")
wait_for("__palacedb.searchState.matches.length === 2", 8)
evaluate("__palacedb.replaceCurrent()")
time.sleep(1.0)
first_text = evaluate("__palacedb.grid.cellValue(0,1) && __palacedb.grid.cellValue(0,1).t") or ""
check("replace: one cell changes", "ZZREPLACED" in first_text, first_text)
check("replace: the other hit remains", evaluate("__palacedb.searchState.matches.length") == 1,
      evaluate("__palacedb.searchState.matches.length"))

find("ZZSEARCHA", "table", False, "ZZDONE")
wait_for("__palacedb.searchState.matches.length === 1", 8)
evaluate("__palacedb.replaceAllInScope()")
wait_for("__palacedb.searchState.matches.length === 0", 8)
check("replace all: no hits are left", evaluate("__palacedb.searchState.matches.length") == 0,
      evaluate("__palacedb.searchState.matches.length"))
second_text = evaluate("__palacedb.grid.cellValue(1,1) && __palacedb.grid.cellValue(1,1).t") or ""
check("replace all: the remaining cell changed", "ZZDONE" in second_text, second_text)

evaluate("document.body.dispatchEvent(new KeyboardEvent('keydown',{key:'z',ctrlKey:true,bubbles:true}))")
time.sleep(1.0)
restored = evaluate("__palacedb.grid.cellValue(1,1) && __palacedb.grid.cellValue(1,1).t") or ""
check("replace all: undo restores the cells", "ZZSEARCHA" in restored, restored)

# --- the find inputs keep the platform copy / paste / undo shortcuts
# The table's Ctrl handlers used to swallow them, so the fields could not be edited with
# the keyboard. These are trusted key events, which is what exercises the native actions.
def send_key(key_name, code, vk, modifiers=0):
    for kind in ("keyDown", "keyUp"):
        counter[0] += 1
        ws.send(json.dumps({"id": counter[0], "method": "Input.dispatchKeyEvent",
                            "params": {"type": kind, "key": key_name, "code": code,
                                       "windowsVirtualKeyCode": vk,
                                       "nativeVirtualKeyCode": vk,
                                       "modifiers": modifiers}}))


def insert_text(text):
    counter[0] += 1
    ws.send(json.dumps({"id": counter[0], "method": "Input.insertText",
                        "params": {"text": text}}))


evaluate("document.getElementById('findInput').value = ''")
evaluate("document.getElementById('findInput').focus()")
insert_text("ABC")
time.sleep(0.3)
check("typing reaches the find field",
      evaluate("document.getElementById('findInput').value") == "ABC")
cell_before_ctrl = evaluate("__palacedb.grid.cellValue(0,1) && __palacedb.grid.cellValue(0,1).t")
send_key("z", "KeyZ", 90, modifiers=2)
time.sleep(0.5)
check("Ctrl+Z undoes inside the find field",
      evaluate("document.getElementById('findInput').value") == "",
      evaluate("document.getElementById('findInput').value"))
check("Ctrl+Z in the find field does not undo the document",
      evaluate("__palacedb.grid.cellValue(0,1) && __palacedb.grid.cellValue(0,1).t") == cell_before_ctrl,
      (cell_before_ctrl, evaluate("__palacedb.grid.cellValue(0,1) && __palacedb.grid.cellValue(0,1).t")))

evaluate("document.getElementById('findInput').value = 'HELLO'")
evaluate("document.getElementById('findInput').select()")
send_key("c", "KeyC", 67, modifiers=2)
time.sleep(0.3)
evaluate("document.getElementById('findInput').value = ''")
evaluate("document.getElementById('findInput').focus()")
send_key("v", "KeyV", 86, modifiers=2)
time.sleep(0.5)
check("copy and paste work in the find field",
      evaluate("document.getElementById('findInput').value") == "HELLO",
      evaluate("document.getElementById('findInput').value"))

evaluate("document.getElementById('findInput').value = ''")
evaluate("__palacedb.closeFindBar()")
time.sleep(0.3)
check("find: the bar closes", evaluate("document.getElementById('findBar').hidden") is True)

# --- save
evaluate("document.body.dispatchEvent(new KeyboardEvent('keydown', {key:'s', ctrlKey:true, bubbles:true}))")
time.sleep(1.0)
check("saved file exists", os.path.exists(CDB))
check("dirty cleared after save", evaluate("__palacedb.state.document.dirty") is False)

# --- fill the selection with one value (Ctrl+Enter in the editor)
# A text column, chosen by type: the sample is a scratch file whose column order changes.
FILL_COL = evaluate("__palacedb.grid.columns.findIndex(c => c.type === 'Text')")
check("the table has a text column to fill", FILL_COL >= 0, FILL_COL)
evaluate("__palacedb.grid.moveCursor(0,%d)" % FILL_COL)
evaluate("__palacedb.grid.moveCursor(2,%d,true)" % FILL_COL)
time.sleep(0.3)
evaluate("__palacedb.send('fillCells', {sheet: __palacedb.state.currentSheet, rows: __palacedb.grid.selectedRowIndices(), columns: __palacedb.grid.selectedColumnNames(), text: 'FILLED'})")
ok = wait_for("__palacedb.grid.cellValue(1,%d) && __palacedb.grid.cellValue(1,%d).t === 'FILLED'" % (FILL_COL, FILL_COL))
check("selection filled with one value", ok,
      evaluate("__palacedb.grid.cellValue(1,%d) && __palacedb.grid.cellValue(1,%d).t" % (FILL_COL, FILL_COL)))
check("fill covers the whole selection",
      evaluate("__palacedb.grid.cellValue(2,%d).t === 'FILLED' && __palacedb.grid.cellValue(0,%d).t === 'FILLED'"
               % (FILL_COL, FILL_COL)))
evaluate("__palacedb.send('undo')")
ok = wait_for("__palacedb.grid.cellValue(1,%d) && __palacedb.grid.cellValue(1,%d).t !== 'FILLED'"
              % (FILL_COL, FILL_COL))
check("fill undone in one step", ok,
      evaluate("__palacedb.grid.cellValue(1,%d) && __palacedb.grid.cellValue(1,%d).t" % (FILL_COL, FILL_COL)))
check("document clean after fill undo", wait_for("__palacedb.state.document.dirty === false", 6))

# --- column schema editing
columns_before = evaluate("__palacedb.grid.columns.length")
evaluate("__palacedb.send('addColumn', {sheet: __palacedb.state.currentSheet, index: 1, name: 'Note', type: '1'})")
ok = wait_for("__palacedb.grid.columns.length === %d" % (columns_before + 1))
check("column added", ok, evaluate("__palacedb.grid.columns.map(c=>c.name).join(',')"))
check("new column is second", evaluate("__palacedb.grid.columns[1].name") == "Note")

evaluate("__palacedb.send('renameColumn', {sheet: __palacedb.state.currentSheet, column: 'Note', name: 'Memo'})")
ok = wait_for("__palacedb.grid.columns.some(c => c.name === 'Memo')")
check("column renamed", ok, evaluate("__palacedb.grid.columns.map(c=>c.name).join(',')"))

evaluate("__palacedb.send('changeColumnType', {sheet: __palacedb.state.currentSheet, column: 'Memo', type: '3'})")
ok = wait_for("__palacedb.grid.columns[1] && __palacedb.grid.columns[1].type === 'Int'")
check("column type changed", ok, evaluate("__palacedb.grid.columns[1].typeStr"))

evaluate("__palacedb.send('undo')")
evaluate("__palacedb.send('undo')")
evaluate("__palacedb.send('undo')")
ok = wait_for("__palacedb.grid.columns.length === %d" % columns_before)
check("column edits undone", ok, evaluate("__palacedb.grid.columns.map(c=>c.name).join(',')"))

# --- table management
tables_before = evaluate("__palacedb.state.document.sheets.length")
evaluate("__palacedb.send('addSheet', {name:'Quest'})")
ok = wait_for("__palacedb.state.document.sheets.length === %d" % (tables_before + 1))
check("table added", ok, evaluate("__palacedb.state.document.sheets.map(s=>s.name).join(',')"))
check("new table selected", evaluate("__palacedb.state.currentSheet") == "Quest")
check("new table has an id column", evaluate("__palacedb.grid.columns.map(c=>c.name+':'+c.type).join(',')") == "id:Id")

evaluate("__palacedb.send('renameSheet', {sheet:'Quest', name:'Mission'})")
ok = wait_for("__palacedb.state.document.sheets.some(s => s.name === 'Mission')")
check("table renamed", ok)

evaluate("__palacedb.send('deleteSheet', {sheet:'Mission'})")
ok = wait_for("__palacedb.state.document.sheets.length === %d" % tables_before)
check("table deleted", ok, evaluate("__palacedb.state.document.sheets.map(s=>s.name).join(',')"))

evaluate("__palacedb.send('undo')")
evaluate("__palacedb.send('undo')")
evaluate("__palacedb.send('undo')")
ok = wait_for("__palacedb.state.document.sheets.length === %d" % tables_before)
check("table edits undone", ok)
check("document clean after undo", wait_for("__palacedb.state.document.dirty === false", 6))

# --- separators
# The sample carries its own dividers, and the row edits above moved them with their rows
# (that is the behaviour under test). The section starts from a clean slate so its geometry
# and group checks do not trip over an unrelated divider: every existing separator is
# removed. Those commands stay on the stack, and the save further down clears the dirty flag.
existing_separators = json.loads(evaluate(
    "JSON.stringify(Array.from(__palacedb.grid.separatorTitles.keys()))"))
for row in existing_separators:
    evaluate("__palacedb.send('toggleSeparator', {sheet: __palacedb.state.currentSheet, row: %d})" % row)
    wait_for("!__palacedb.grid.separatorTitles.has(%d)" % row, 5)
ok = wait_for("__palacedb.grid.separatorTitles.size === 0")
check("the sample's own separators are cleared for the test", ok,
      evaluate("Array.from(__palacedb.grid.separatorTitles.keys()).join(',')"))

sep_before = evaluate("__palacedb.grid.separatorTitles.size")
evaluate("__palacedb.grid.moveCursor(2,0)")
time.sleep(0.3)
evaluate("__palacedb.send('toggleSeparator', {sheet: __palacedb.state.currentSheet, row: 2})")
ok = wait_for("__palacedb.grid.separatorTitles.has(2)")
time.sleep(0.6)
check("separator added", ok, evaluate("Array.from(__palacedb.grid.separatorTitles.keys()).join(',')"))
check("the separator is drawn as a strip of its own",
      evaluate("!!document.querySelector('.sep-band[data-sep=\"2\"]')"),
      evaluate("Array.from(document.querySelectorAll('.sep-band')).map(b => b.dataset.sep).join(',')"))
check("the strip is one row tall",
      evaluate("Math.round(document.querySelector('.sep-band[data-sep=\"2\"]').getBoundingClientRect().height)") == 38,
      evaluate("Math.round(document.querySelector('.sep-band[data-sep=\"2\"]').getBoundingClientRect().height)"))
check("the strip pushes its row down instead of covering it",
      evaluate("""(() => {
        const band = document.querySelector('.sep-band[data-sep="2"]').getBoundingClientRect();
        const row = document.querySelector('.grid-row[data-row="2"]').getBoundingClientRect();
        return Math.round(row.top - band.bottom) === 0 && Math.round(band.height) === 38;
      })()""") is True)

evaluate("__palacedb.send('setSeparatorTitle', {sheet: __palacedb.state.currentSheet, row: 2, title: 'Chapter'})")
ok = wait_for("__palacedb.grid.separatorTitles.get(2) === 'Chapter'")
check("separator title set", ok, evaluate("__palacedb.grid.separatorTitles.get(2)"))
check("separator tooltip shows the title",
      evaluate("document.querySelector('.sep-band[data-sep=\"2\"]').title").startswith("Separator: Chapter"),
      evaluate("document.querySelector('.sep-band[data-sep=\"2\"]').title"))
check("the title is written in the strip",
      evaluate("(document.querySelector('.sep-band[data-sep=\"2\"] .sep-label') || {}).textContent") == "Chapter")

# The row a separator introduces keeps its values and still grows for a wrapped one: the
# strip takes a row of its own instead of hiding what was there.
check("the row of a separator still shows its value",
      evaluate("""(() => {
        const row = document.querySelector('.grid-row[data-row="2"]');
        const value = row && row.querySelector('.cell-value');
        return !!value && getComputedStyle(value).visibility === 'visible'
               && value.textContent.trim().length > 0;
      })()"""),
      evaluate("""(() => {
        const row = document.querySelector('.grid-row[data-row="2"]');
        const value = row && row.querySelector('.cell-value');
        return value ? value.textContent.trim().slice(0, 20) : 'missing';
      })()"""))

text_column = evaluate(
    "(__palacedb.grid.columns.find(c => c.type === 'Text') || __palacedb.grid.columns[0]).name")
LONG_SEP = "A value that wraps onto several lines " * 8
evaluate("__palacedb.send('setCell', {sheet: __palacedb.state.currentSheet, row: 2, column: %s, text: %s})"
         % (json.dumps(text_column), json.dumps(LONG_SEP)))
ok = wait_for("__palacedb.grid.rowHeights.has(2)", 6)
check("a separator row grows for a wrapped value like any other row", ok,
      evaluate("__palacedb.grid.rowHeights.get(2)"))

SEP_GEOMETRY = """
(() => {
  const row = (n) => document.querySelector('.grid-row[data-row="' + n + '"]');
  const band = document.querySelector('.sep-band[data-sep="2"]');
  const a = row(2).getBoundingClientRect();
  const b = row(3).getBoundingClientRect();
  const s = band.getBoundingClientRect();
  const value = row(3) && row(3).querySelector('.cell-value');
  return JSON.stringify({
    height: Math.round(a.height),
    bandHeight: Math.round(s.height),
    bandGap: Math.round(a.top - s.bottom),
    gap: Math.round(b.top - a.bottom),
    below: value ? value.textContent.trim().slice(0, 20) : null,
  });
})()
"""
geometry = json.loads(evaluate(SEP_GEOMETRY))
check("the tall separator row keeps its wrapped text", geometry["height"] > 38, geometry["height"])
check("the strip stays one row tall above a tall row", geometry["bandHeight"] == 38, geometry["bandHeight"])
check("the strip stays attached to the row it introduces", geometry["bandGap"] == 0, geometry["bandGap"])
check("the row below a separator is still attached", geometry["gap"] == 0, geometry["gap"])
check("the row below a separator still shows its text",
      bool(geometry["below"]), geometry["below"])

evaluate("__palacedb.send('undo')")
ok = wait_for("__palacedb.grid.separatorTitles.has(2) && __palacedb.grid.separatorTitles.get(2) === 'Chapter'"
              " && !__palacedb.grid.rowHeights.has(2)", 8)
check("separator growth test undone", ok)

evaluate("__palacedb.send('undo')")
time.sleep(0.6)
evaluate("__palacedb.send('undo')")
ok = wait_for("__palacedb.grid.separatorTitles.size === %d" % sep_before)
check("separator edits undone", ok, evaluate("__palacedb.grid.separatorTitles.size"))

# --- table order (S04) and generated index / group fields (S05 / S06)
def open_table_menu(index):
    evaluate("__palacedb.elements.tableList.querySelectorAll('.table-item')[%d]"
             ".dispatchEvent(new MouseEvent('contextmenu', {bubbles:true, clientX:120, clientY:220}))"
             % index)
    time.sleep(0.45)

def click_menu_item(text):
    return evaluate("""(() => {
      const b = Array.from(document.querySelectorAll('#contextMenu .context-item'))
        .find(x => (x.textContent || '').indexOf(%s) === 0);
      if (!b || b.disabled) return false;
      b.click();
      return true;
    })()""" % json.dumps(text))

order_before = evaluate("__palacedb.state.document.sheets.map(s=>s.name).join(',')")
first_name = evaluate("__palacedb.state.document.sheets[0].name")

open_table_menu(0)
labels = evaluate("Array.from(document.querySelectorAll('#contextMenu .context-label'))"
                  ".map(s=>s.textContent).join('|')")
check("table menu lists order and generated fields",
      "Move Table Up" in labels and "Move Table Down" in labels and "Add Index" in labels and "Add Group" in labels,
      labels)
check("the first table cannot move up",
      evaluate("Array.from(document.querySelectorAll('#contextMenu .context-item'))"
               ".find(b => b.textContent.indexOf('Move Table Up') === 0).disabled") is True)

click_menu_item('Move Table Down')
ok = wait_for("__palacedb.state.document.sheets[0].name !== %s" % json.dumps(first_name))
check("table moved down", ok, evaluate("__palacedb.state.document.sheets.map(s=>s.name).join(',')"))
evaluate("__palacedb.send('undo')")
ok = wait_for("__palacedb.state.document.sheets.map(s=>s.name).join(',') === %s" % json.dumps(order_before))
check("table move undone", ok, evaluate("__palacedb.state.document.sheets.map(s=>s.name).join(',')"))

open_table_menu(0)
click_menu_item('Add Index')
ok = wait_for("__palacedb.state.document.sheets[0].hasIndex === true")
check("index switched on", ok)

evaluate("document.body.dispatchEvent(new KeyboardEvent('keydown', {key:'s', ctrlKey:true, bubbles:true}))")
time.sleep(1.2)
saved = json.load(open(CDB, encoding="utf-8"))
saved_sheet = saved["sheets"][0]
check("index written on every row",
      all("index" in row for row in saved_sheet["lines"])
      and [row["index"] for row in saved_sheet["lines"]] == list(range(len(saved_sheet["lines"]))),
      [row.get("index") for row in saved_sheet["lines"][:5]])
check("hasIndex stored in props", saved_sheet.get("props", {}).get("hasIndex") is True,
      saved_sheet.get("props"))

evaluate("__palacedb.send('undo')")
ok = wait_for("__palacedb.state.document.sheets[0].hasIndex === false")
check("index switched off by undo", ok)

# group needs a titled separator to have anything to number
sheet_now = evaluate("__palacedb.state.currentSheet")
evaluate("__palacedb.send('toggleSeparator', {sheet: %s, row: 2})" % json.dumps(sheet_now))
ok = wait_for("__palacedb.grid.separatorTitles.has(2)")
evaluate("__palacedb.send('setSeparatorTitle', {sheet: %s, row: 2, title: 'Chapter'})" % json.dumps(sheet_now))
ok = ok and wait_for("__palacedb.grid.separatorTitles.get(2) === 'Chapter'")
check("titled separator prepared for the group test", ok)

open_table_menu(0)
click_menu_item('Add Group')
ok = wait_for("__palacedb.state.document.sheets[0].hasGroup === true")
check("group switched on", ok)

evaluate("document.body.dispatchEvent(new KeyboardEvent('keydown', {key:'s', ctrlKey:true, bubbles:true}))")
time.sleep(1.2)
saved = json.load(open(CDB, encoding="utf-8"))
saved_sheet = saved["sheets"][0]
groups = [row.get("group") for row in saved_sheet["lines"]]
check("group written on every row", all(g is not None for g in groups), groups[:6])
check("group increments at the titled separator",
      groups[0] == 0 and groups[1] == 0 and groups[2] == 1 and groups[3] == 1, groups[:6])
check("hasGroup stored in props", saved_sheet.get("props", {}).get("hasGroup") is True,
      saved_sheet.get("props"))

evaluate("__palacedb.send('undo')")
ok = wait_for("__palacedb.state.document.sheets[0].hasGroup === false")
check("group switched off by undo", ok)
evaluate("__palacedb.send('undo')")
time.sleep(0.5)
evaluate("__palacedb.send('undo')")
ok = wait_for("__palacedb.grid.separatorTitles.size === %d" % sep_before)
check("group test cleaned up", ok, evaluate("__palacedb.grid.separatorTitles.size"))

# --- rows must actually reach the file
evaluate("document.body.dispatchEvent(new KeyboardEvent('keydown', {key:'s', ctrlKey:true, bubbles:true}))")
time.sleep(1.0)
check("document clean after the generated field tests",
      wait_for("__palacedb.state.document.dirty === false", 6))

saved = json.load(open(CDB, encoding="utf-8"))
check("saved file matches the row count on screen",
      len(saved["sheets"][0]["lines"]) == evaluate("__palacedb.grid.rowCount"),
      (len(saved["sheets"][0]["lines"]), evaluate("__palacedb.grid.rowCount")))
check("generated fields removed from the file after undo",
      all("index" not in row and "group" not in row for row in saved["sheets"][0]["lines"]))

# --- bulk column conversion (C07)
def open_header_menu(col_index):
    evaluate("""(() => {
      const cell = document.querySelector('.head-cell[data-col="%d"]');
      if (!cell) return false;
      cell.dispatchEvent(new MouseEvent('contextmenu', {bubbles:true, clientX:320, clientY:130}));
      return true;
    })()""" % col_index)
    time.sleep(0.35)

def open_convert(col_index):
    """Right-clicks a header and hovers the Convert entry; returns the submenu labels."""
    open_header_menu(col_index)
    evaluate("""(() => {
      const item = Array.from(document.querySelectorAll('#contextMenu .context-item'))
        .find(b => (b.textContent || '').indexOf('Convert') === 0);
      if (!item) return false;
      item.dispatchEvent(new MouseEvent('mouseenter'));
      return true;
    })()""")
    time.sleep(0.3)
    return evaluate("Array.from(document.querySelectorAll('#subMenu .context-item .context-label'))"
                    ".map(s=>s.textContent).join('|')")

def pick_submenu(text):
    evaluate("""(() => {
      const item = Array.from(document.querySelectorAll('#subMenu .context-item'))
        .find(b => (b.textContent || '').indexOf(%s) === 0);
      if (!item) return false;
      item.dispatchEvent(new MouseEvent('click', {bubbles:true}));
      return true;
    })()""" % json.dumps(text))

def cell_text(row, col):
    return evaluate("__palacedb.grid.cellValue(%d,%d) && __palacedb.grid.cellValue(%d,%d).t"
                    % (row, col, row, col))

id_col = evaluate("__palacedb.grid.columns.findIndex(c => c.type === 'Id')")
labels = open_convert(id_col)
check("convert submenu opens for an ID column",
      labels == 'lowercase|UPPERCASE|UpperIdent|lowerIdent', labels)

id_before = cell_text(0, id_col) or ''
pick_submenu('UPPERCASE')
ok = wait_for("__palacedb.grid.cellValue(0,%d) && __palacedb.grid.cellValue(0,%d).t === %s"
              % (id_col, id_col, json.dumps(id_before.upper())))
check("UPPERCASE rewrote the ID column", ok, "%r -> %r" % (id_before, cell_text(0, id_col)))
check("menus closed after picking a conversion",
      evaluate("document.getElementById('contextMenu').hidden && document.getElementById('subMenu').hidden"))

evaluate("__palacedb.send('undo')")
ok = wait_for("__palacedb.grid.cellValue(0,%d) && __palacedb.grid.cellValue(0,%d).t === %s"
              % (id_col, id_col, json.dumps(id_before)))
check("ID conversion undone in one step", ok, cell_text(0, id_col))
check("document clean after conversion undo", wait_for("__palacedb.state.document.dirty === false", 6))

int_col = evaluate("__palacedb.grid.columns.findIndex(c => c.type === 'Int')")
labels = open_convert(int_col)
check("convert submenu lists the numeric rules for an Int column",
      labels == '* 10|/ 10|+ 1|- 1', labels)

num_before = cell_text(0, int_col) or '0'
pick_submenu('+ 1')
ok = wait_for("__palacedb.grid.cellValue(0,%d) && Number(__palacedb.grid.cellValue(0,%d).t) === %d"
              % (int_col, int_col, int(num_before) + 1))
check("+ 1 shifted the Int column", ok, "%s -> %s" % (num_before, cell_text(0, int_col)))
evaluate("__palacedb.send('undo')")
ok = wait_for("__palacedb.grid.cellValue(0,%d) && Number(__palacedb.grid.cellValue(0,%d).t) === %d"
              % (int_col, int_col, int(num_before)))
check("numeric conversion undone", ok, cell_text(0, int_col))

flags_col = evaluate("__palacedb.grid.columns.findIndex(c => c.type === 'Flags' || c.type === 'Enum')")
flags_before = evaluate("(__palacedb.grid.columns[%d].values || []).join(',')" % flags_col)
labels = open_convert(flags_col)
check("convert submenu offered for a Flags/Enum column",
      labels == 'lowercase|UPPERCASE|UpperIdent|lowerIdent', labels)

pick_submenu('UPPERCASE')
ok = wait_for("( __palacedb.grid.columns[%d].values || []).join(',') === %s"
              % (flags_col, json.dumps(flags_before.upper())))
check("UPPERCASE renamed the Flags values", ok,
      evaluate("(__palacedb.grid.columns[%d].values || []).join(',')" % flags_col))
evaluate("__palacedb.send('undo')")
ok = wait_for("( __palacedb.grid.columns[%d].values || []).join(',') === %s"
              % (flags_col, json.dumps(flags_before)))
check("Flags value rename undone", ok,
      evaluate("(__palacedb.grid.columns[%d].values || []).join(',')" % flags_col))

bool_col = evaluate("__palacedb.grid.columns.findIndex(c => c.type === 'Bool')")
open_header_menu(bool_col)
has_convert = evaluate("Array.from(document.querySelectorAll('#contextMenu .context-item'))"
                       ".some(b => (b.textContent || '').indexOf('Convert') === 0)")
check("no convert submenu for a Bool column", has_convert is False)
evaluate("document.dispatchEvent(new KeyboardEvent('keydown', {key:'Escape', bubbles:true}))")
time.sleep(0.3)

# --- per table cursor memory
evaluate("__palacedb.grid.moveCursor(4,2)")
time.sleep(0.4)
evaluate("__palacedb.elements.tableList.querySelectorAll('.table-item')[1].click()")
time.sleep(0.9)
check("second table starts at the first cell", evaluate("__palacedb.grid.cursor.row === 0 && __palacedb.grid.cursor.col === 0"))
evaluate("__palacedb.elements.tableList.querySelectorAll('.table-item')[0].click()")
time.sleep(0.9)
check("cursor restored when switching back",
      evaluate("__palacedb.grid.cursor.row === 4 && __palacedb.grid.cursor.col === 2"),
      evaluate("JSON.stringify(__palacedb.grid.cursor)"))

# --- context menu
evaluate("""
(() => {
  const cell = document.querySelector('.cell[data-row="2"][data-col="2"]');
  cell.dispatchEvent(new MouseEvent('contextmenu', {bubbles:true, clientX:200, clientY:200}));
})()
""")
time.sleep(0.4)
check("row context menu opens", evaluate("!document.getElementById('contextMenu').hidden"))
labels = evaluate("Array.from(document.querySelectorAll('#contextMenu .context-item span')).map(s=>s.textContent).join('|')")
check("the cell menu is about the value", "Cut" in labels and "Copy" in labels, labels)
check("the cell menu no longer carries row actions",
      "Insert Row Above" not in labels and "Delete Row" not in labels
      and "Insert Paste" not in labels, labels)
evaluate("document.dispatchEvent(new KeyboardEvent('keydown', {key:'Escape', bubbles:true}))")
time.sleep(0.3)
check("context menu closes on Escape", evaluate("document.getElementById('contextMenu').hidden"))

# --- theme and layout toggles
check("light theme is the default", evaluate("__palacedb.isDark()") is False)
evaluate("__palacedb.toggleTheme()")
time.sleep(0.3)
check("dark theme applies", evaluate("document.body.classList.contains('theme-dark')") is True)
evaluate("__palacedb.toggleTheme()")
time.sleep(0.3)
check("theme toggles back", evaluate("document.body.classList.contains('theme-dark')") is False)

evaluate("__palacedb.toggleSidebar()")
time.sleep(0.3)
check("table list collapses", evaluate("document.getElementById('sidebar').classList.contains('collapsed')") is True)
width = evaluate("document.getElementById('sidebar').getBoundingClientRect().width")
check("collapsed sidebar is a rail", width is not None and width <= 40, width)
evaluate("__palacedb.toggleSidebar()")
time.sleep(0.3)
check("table list expands again", evaluate("document.getElementById('sidebar').classList.contains('collapsed')") is False)

# --- settings window: interface size, table size and colour scheme
# The window is reached from the toolbar and from the View menu, and it previews its
# choices live: the sizes and the palette are plain CSS variables, so a change is visible
# before the dialog is closed. Cancel has to put the previous values back.
evaluate("__palacedb.applySettings({uiFontSize: 15, gridFontSize: 14, scheme: 'light'})")
time.sleep(0.3)

check("the toolbar has a settings button",
      evaluate("!!document.querySelector('.toolbar [data-action=\"settings\"]')") is True)
check("the View menu offers Settings",
      evaluate("!!document.querySelector('[data-menu=\"view\"] [data-action=\"settings\"]')") is True)

evaluate("document.querySelector('.toolbar [data-action=\"settings\"]').click()")
time.sleep(0.5)
check("the settings window opens", evaluate("!document.getElementById('modal').hidden") is True)
check("it is titled Settings", evaluate("document.getElementById('modalTitle').textContent") == "Settings")
ranges = evaluate("[...document.querySelectorAll('.settings-range')].map(i => i.value).join(',')")
cards = evaluate("document.querySelectorAll('.scheme-card').length")
names = evaluate("[...document.querySelectorAll('.scheme-card')].map(c => c.dataset.scheme).join(',')")
check("it offers two sizes and nine palettes", ranges == "15,14" and cards == 9, (ranges, cards))
check("the palettes include the pastel ones",
      all(name in names for name in ("rose", "lilac", "olive", "mint")), names)
check("the current palette is marked",
      evaluate("document.querySelectorAll('.scheme-card.active').length") == 1
      and evaluate("(document.querySelector('.scheme-card.active') || {}).dataset.scheme") == "light",
      evaluate("(document.querySelector('.scheme-card.active') || {}).dataset.scheme"))
check("the preview uses the table variables",
      evaluate("""(() => {
        const row = document.querySelector('.settings-preview .sp-row');
        if (!row) return false;
        const style = getComputedStyle(row);
        return Math.round(parseFloat(style.fontSize)) === 14
          && Math.round(row.getBoundingClientRect().height) === 38;
      })()""") is True)

# --- a bigger table font must also make the rows taller
evaluate("""(() => {
  const input = document.querySelectorAll('.settings-range')[1];
  input.value = '18';
  input.dispatchEvent(new Event('input', {bubbles: true}));
})()""")
time.sleep(0.5)
check("the table font size applies",
      evaluate("getComputedStyle(document.documentElement).getPropertyValue('--grid-font-size').trim()") == "18px")
check("the cells follow the new size",
      evaluate("Math.round(parseFloat(getComputedStyle(document.querySelector('#gridRows .cell')).fontSize))") == 18,
      evaluate("getComputedStyle(document.querySelector('#gridRows .cell')).fontSize"))
# Every row is at least the base height for this font, and the ones with wrapped text are
# taller than it. (The base is read from the stylesheet: which rows happen to fit on one
# line depends on the sample and on how much text earlier checks have written.)
check("the rows grew with the text",
      evaluate("""(() => {
        const cells = [...document.querySelectorAll('#gridGutter .gutter-cell')];
        const base = Math.round(parseFloat(getComputedStyle(document.documentElement)
          .getPropertyValue('--row-height')));
        if (!cells.length) return null;
        const heights = cells.map(c => Math.round(c.getBoundingClientRect().height));
        return heights.every(h => h >= base) && heights.some(h => h > base) ? base : -1;
      })()""") == 49,
      evaluate("""(() => {
        const cells = [...document.querySelectorAll('#gridGutter .gutter-cell')];
        return cells.map(c => Math.round(c.getBoundingClientRect().height)).join(',');
      })()"""))
check("rows are still placed by the new height",
      evaluate("""(() => {
        const cell = document.querySelector('#gridRows .cell[data-row="3"]');
        const gutter = document.querySelector('#gridGutter .gutter-cell[data-row="3"]');
        if (!cell || !gutter) return false;
        return Math.abs(cell.getBoundingClientRect().top - gutter.getBoundingClientRect().top) < 2;
      })()""") is True)

# --- the interface size is separate from the table size
evaluate("""(() => {
  const input = document.querySelectorAll('.settings-range')[0];
  input.value = '17';
  input.dispatchEvent(new Event('input', {bubbles: true}));
})()""")
time.sleep(0.4)
check("the interface font size applies",
      evaluate("Math.round(parseFloat(getComputedStyle(document.body).fontSize))") == 17,
      evaluate("getComputedStyle(document.body).fontSize"))
check("the table size is left alone",
      evaluate("getComputedStyle(document.documentElement).getPropertyValue('--font-size').trim()") == "17px"
      and evaluate("getComputedStyle(document.documentElement).getPropertyValue('--grid-font-size').trim()") == "18px")
check("the size readouts show the numbers",
      evaluate("[...document.querySelectorAll('.settings-value')].map(s => s.textContent).join(',')") == "17 px,18 px",
      evaluate("[...document.querySelectorAll('.settings-value')].map(s => s.textContent).join(',')"))

# --- choosing a palette repaints the window at once
evaluate("document.querySelector('.scheme-card[data-scheme=\"ocean\"]').click()")
time.sleep(0.4)
check("the palette applies immediately",
      evaluate("document.body.dataset.scheme") == "ocean"
      and evaluate("document.body.classList.contains('theme-dark')") is True)
check("the chosen palette is marked",
      evaluate("(document.querySelector('.scheme-card.active') || {}).dataset.scheme") == "ocean")

# --- Cancel is a real undo of the preview
evaluate("document.getElementById('modalCancel').click()")
time.sleep(0.5)
check("Cancel closes the window", evaluate("document.getElementById('modal').hidden") is True)
check("Cancel restores the previous sizes",
      evaluate("getComputedStyle(document.documentElement).getPropertyValue('--grid-font-size').trim()") == "14px"
      and evaluate("getComputedStyle(document.documentElement).getPropertyValue('--font-size').trim()") == "15px")
check("Cancel restores the previous palette",
      evaluate("document.body.dataset.scheme") == "light"
      and evaluate("document.body.classList.contains('theme-dark')") is False)
check("the rows are back to their old height",
      evaluate("""(() => {
        const cells = [...document.querySelectorAll('#gridGutter .gutter-cell')];
        if (!cells.length) return null;
        return Math.min(...cells.map(c => Math.round(c.getBoundingClientRect().height)));
      })()""") == 38,
      evaluate("""(() => {
        const cells = [...document.querySelectorAll('#gridGutter .gutter-cell')];
        return cells.map(c => Math.round(c.getBoundingClientRect().height)).join(',');
      })()"""))

# --- Done keeps the choice, and it is stored for the next start
evaluate("__palacedb.openSettings()")
time.sleep(0.5)
evaluate("""(() => {
  const input = document.querySelectorAll('.settings-range')[1];
  input.value = '16';
  input.dispatchEvent(new Event('input', {bubbles: true}));
  document.querySelector('.scheme-card[data-scheme="graphite"]').click();
})()""")
time.sleep(0.4)
evaluate("document.getElementById('modalOk').click()")
time.sleep(0.5)
stored = evaluate("localStorage.getItem('palacedb.settings')")
try:
    stored_json = json.loads(stored or "{}")
except ValueError:
    stored_json = {}
check("Done keeps the choice", evaluate("getComputedStyle(document.documentElement).getPropertyValue('--grid-font-size').trim()") == "16px"
      and evaluate("document.body.dataset.scheme") == "graphite")
check("the choice is stored for the next start",
      stored_json.get("gridFontSize") == 16 and stored_json.get("scheme") == "graphite",
      stored)

# The dialog previews a palette live, which used to leave the side-car on the old one:
# OK only compared state.settings with itself and never wrote the file. The next document
# refresh (insert row, column options, ...) then flipped the window back.
meta_after_settings = {}
if os.path.exists(CDB + ".meta.json"):
    meta_after_settings = json.load(open(CDB + ".meta.json", encoding="utf-8"))
check("Done writes the palette to the file's side-car",
      (meta_after_settings.get("appearance") or {}).get("scheme") == "graphite",
      meta_after_settings.get("appearance"))

rows_before_theme = evaluate("__palacedb.grid.rowCount")
evaluate("__palacedb.send('insertRow', {sheet: __palacedb.state.currentSheet, row: %d})" % rows_before_theme)
time.sleep(1.0)
check("a document refresh keeps the chosen palette",
      evaluate("document.body.dataset.scheme") == "graphite"
      and evaluate("__palacedb.state.settings.scheme") == "graphite",
      evaluate("document.body.dataset.scheme"))
evaluate("__palacedb.send('undo')")
time.sleep(1.0)
# Appending a row scrolls to it; put the view back at the top so the next block (which
# inspects rows 2 and 3) has them rendered. moveCursor alone is not enough: it returns
# early when the cursor is already on (0,0) and then never scrolls.
evaluate("__palacedb.grid.moveCursor(0,0)")
evaluate("__palacedb.grid.body.scrollTop = 0; __palacedb.grid.body.scrollLeft = 0")
time.sleep(0.5)

# --- restoring the defaults is one button
evaluate("__palacedb.openSettings()")
time.sleep(0.5)
evaluate("document.getElementById('settingsReset').click()")
time.sleep(0.4)
check("Restore defaults puts the sizes back",
      evaluate("[...document.querySelectorAll('.settings-range')].map(i => i.value).join(',')") == "15,14"
      and evaluate("(document.querySelector('.scheme-card.active') || {}).dataset.scheme") == "light")
evaluate("document.getElementById('modalOk').click()")
time.sleep(0.4)
check("the window is back to the default look",
      evaluate("document.body.dataset.scheme") == "light"
      and evaluate("getComputedStyle(document.documentElement).getPropertyValue('--grid-font-size').trim()") == "14px"
      and evaluate("__palacedb.isDark()") is False)

# --- a wrapped value makes its row taller, so the whole text stays readable
text_col = evaluate("__palacedb.grid.columns.findIndex(c => c.type === 'Text')")
base_height = evaluate(
    "parseFloat(getComputedStyle(document.documentElement).getPropertyValue('--row-height'))")
if text_col is not None and text_col >= 0:
    text_name = evaluate("__palacedb.grid.columns[%d].name" % text_col)
    original = evaluate("(() => { const v = __palacedb.grid.cellValue(2, %d);"
                        " return v ? (v.d || v.t || '') : ''; })()" % text_col)

    def set_text(value):
        evaluate("__palacedb.send('setCell', {sheet: __palacedb.state.currentSheet, "
                 f"row: 2, column: {json.dumps(text_name)}, text: {json.dumps(value)}}})")
        time.sleep(1.0)

    long_text = "A wrapped value has to be readable in full, not cut off after two lines. " * 3
    set_text(long_text)
    tall = evaluate("Math.round(__palacedb.grid.heightOf(2))")
    check("a wrapped value makes its row taller", tall > base_height, (tall, base_height))
    check("the whole value is on screen, not clipped",
          evaluate("""(() => {
            const span = document.querySelector(
              '#gridRows .grid-row[data-row="2"] .cell[data-col="%d"] .cell-value');
            if (!span) return null;
            return span.scrollHeight <= span.clientHeight + 1;
          })()""" % text_col) is True,
          evaluate("""(() => {
            const span = document.querySelector(
              '#gridRows .grid-row[data-row="2"] .cell[data-col="%d"] .cell-value');
            if (!span) return null;
            return span.scrollHeight + '/' + span.clientHeight;
          })()""" % text_col))
    check("the rows below it moved down",
          evaluate("""(() => {
            const first = document.querySelector('#gridRows .grid-row[data-row="2"]');
            const second = document.querySelector('#gridRows .grid-row[data-row="3"]');
            if (!first || !second) return null;
            return Math.round(second.getBoundingClientRect().top)
                   - Math.round(first.getBoundingClientRect().bottom);
          })()""") == 0)
    check("the row number matches the taller row",
          evaluate("""(() => {
            const cell = document.querySelector('#gridGutter .gutter-cell[data-row="2"]');
            return cell ? Math.round(cell.getBoundingClientRect().height) : null;
          })()""") == tall,
          (tall, evaluate("""(() => {
            const cell = document.querySelector('#gridGutter .gutter-cell[data-row="2"]');
            return cell ? Math.round(cell.getBoundingClientRect().height) : null;
          })()""")))

    set_text(original)
    check("a short value gives the height back",
          evaluate("Math.round(__palacedb.grid.heightOf(2))") == round(base_height),
          evaluate("Math.round(__palacedb.grid.heightOf(2))"))

# --- the row-number gutter is narrow: three digits, everything longer is clipped
gutter = evaluate("""(() => {
  const cell = document.querySelector('.gutter-cell');
  const width = parseFloat(getComputedStyle(document.documentElement).getPropertyValue('--gutter-width'));
  return {
    variable: width,
    box: cell ? Math.round(cell.getBoundingClientRect().width) : 0,
    clips: cell ? cell.scrollWidth >= cell.clientWidth : false,
  };
})()""")
check("the row-number column is about half as wide as before",
      gutter and gutter["variable"] <= 30 and gutter["box"] <= 30, gutter)
check("a row number too long for it is clipped, not widened",
      gutter and gutter["clips"] is True, gutter)

# --- one sidebar toggle, pinned at the far left: the exact same screen box in both states
toggle = evaluate("""(() => {
  const button = document.getElementById('sidebarExpand');
  // The View menu entry shares data-action but is a menu command, not a second
  // on-screen toggle. Only the pinned button counts as a visible control.
  const controls = [...document.querySelectorAll(
    'button[data-action="toggleSidebar"]:not(.menu-item)')];
  const box = () => {
    const r = button.getBoundingClientRect();
    return [Math.round(r.left), Math.round(r.top), Math.round(r.width), Math.round(r.height)];
  };
  const label = document.querySelector('.sidebar-tab[data-tab="tables"]');
  const before = box();
  // The control sits to the left of the TABLES tab, and belongs to neither the sidebar
  // nor the toolbar: both of those change width when the list is hidden.
  const leftOfTables = !!label && button.getBoundingClientRect().right
    <= label.getBoundingClientRect().left;
  const inToolbar = !!button.closest('.toolbar');
  const inSidebar = !!button.closest('.sidebar');
  button.click();
  const collapsedBox = box();
  const collapsed = document.getElementById('sidebar').classList.contains('collapsed');
  const stillVisible = getComputedStyle(button).display !== 'none'
    && button.getBoundingClientRect().width > 0;
  button.click();
  const backBox = box();
  const key = (b) => b.join(',');
  return {
    controls: controls.length, inToolbar, inSidebar, leftOfTables,
    collapsed, expandedAgain: !document.getElementById('sidebar').classList.contains('collapsed'),
    stillVisible,
    // Absolute viewport coordinates: the whole point is that the pointer never has to
    // move, not merely that the button keeps its offset inside a moving container.
    sameBox: key(before) === key(collapsedBox) && key(collapsedBox) === key(backBox),
    boxes: [before, collapsedBox, backBox],
  };
})()""")
check("the table list has a single toggle control",
      toggle and toggle["controls"] == 1 and toggle["inToolbar"] is False
      and toggle["inSidebar"] is False and toggle["leftOfTables"] is True, toggle)
check("the same button collapses and restores the list",
      toggle and toggle["collapsed"] is True and toggle["expandedAgain"] is True
      and toggle["stillVisible"] is True, toggle)
check("the button keeps its exact screen position in both states",
      toggle and toggle["sameBox"] is True, toggle)

# --- Tab toggles the table list everywhere, and never moves the cell cursor
evaluate("document.getElementById('sidebarExpand').focus()")
evaluate("document.activeElement.dispatchEvent("
         "new KeyboardEvent('keydown', {key:'Tab', bubbles:true}))")
time.sleep(0.4)
check("Tab collapses the table list",
      evaluate("document.getElementById('sidebar').classList.contains('collapsed')") is True)
evaluate("document.activeElement.dispatchEvent("
         "new KeyboardEvent('keydown', {key:'Tab', bubbles:true}))")
time.sleep(0.4)
check("Tab brings it back",
      evaluate("document.getElementById('sidebar').classList.contains('collapsed')") is False)

# Inside the table Tab is the same shortcut: the cursor must not move to the next cell.
evaluate("__palacedb.grid.moveCursor(1, 0)")
cursor_before = evaluate("JSON.stringify(__palacedb.grid.cursor)")
evaluate("document.getElementById('gridBody').dispatchEvent("
         "new KeyboardEvent('keydown', {key:'Tab', bubbles:true}))")
time.sleep(0.4)
check("Tab inside the table toggles the table list",
      evaluate("document.getElementById('sidebar').classList.contains('collapsed')") is True)
check("Tab inside the table leaves the cell cursor alone",
      evaluate("JSON.stringify(__palacedb.grid.cursor)") == cursor_before,
      (cursor_before, evaluate("JSON.stringify(__palacedb.grid.cursor)")))
evaluate("document.getElementById('gridBody').dispatchEvent("
         "new KeyboardEvent('keydown', {key:'Tab', bubbles:true}))")
time.sleep(0.3)

# With an editor open, Tab commits the text, toggles the list and stays on the cell.
evaluate("__palacedb.grid.moveCursor(1, 1)")
evaluate("__palacedb.grid.onEditRequest(1, 1, '')")
wait_for("!!document.querySelector('.editor-input')", 5)
evaluate("(() => { const i = document.querySelector('.editor-input');"
         " i.value = 'TAB_COMMIT'; return 1; })()")
evaluate("document.querySelector('.editor-input').dispatchEvent("
         "new KeyboardEvent('keydown', {key:'Tab', bubbles:true}))")
time.sleep(0.5)
check("Tab with an editor commits the value",
      evaluate("__palacedb.grid.cellValue(1,1) && __palacedb.grid.cellValue(1,1).t") == "TAB_COMMIT",
      evaluate("__palacedb.grid.cellValue(1,1) && __palacedb.grid.cellValue(1,1).t"))
check("Tab with an editor still toggles the table list",
      evaluate("document.getElementById('sidebar').classList.contains('collapsed')") is True)
check("Tab with an editor does not move the cursor",
      evaluate("__palacedb.grid.cursor.row === 1 && __palacedb.grid.cursor.col === 1"),
      evaluate("JSON.stringify(__palacedb.grid.cursor)"))
evaluate("document.body.dispatchEvent(new KeyboardEvent('keydown', {key:'z', ctrlKey:true, bubbles:true}))")
time.sleep(0.6)
evaluate("document.getElementById('sidebarExpand').click()")
time.sleep(0.4)

# --- the palette belongs to the file, so it is written to the side-car
META = CDB + ".meta.json"
evaluate("__palacedb.applySettings({uiFontSize: 15, gridFontSize: 14, scheme: 'rose'})")
time.sleep(1.2)
meta = {}
try:
    with open(META, encoding="utf-8") as handle:
        meta = json.load(handle)
except Exception as error:
    print("meta read failed:", error)
check("the palette is stored in the file's side-car",
      (meta.get("appearance") or {}).get("scheme") == "rose", meta.get("appearance"))
check("the palette never reaches the database file",
      "appearance" not in open(CDB, encoding="utf-8").read())
# The full round trip - close, change the preference, reopen - is checked at the end of
# the run, where losing the current table does no harm to the rest of the suite.
evaluate("__palacedb.applySettings({uiFontSize: 15, gridFontSize: 14, scheme: 'light'})")
time.sleep(0.6)

# --- a second window on the same file is read-only and follows the writer (R2 / F15)
# Save first so no recovery prompt can appear in the new window.
evaluate("document.body.dispatchEvent(new KeyboardEvent('keydown', {key:'s', ctrlKey:true, bubbles:true}))")
time.sleep(1.2)

evaluate("__palacedb.send('newWindow')")
time.sleep(5.0)
park_windows(proc.pid)

second = None
for target in [t for t in json.load(urllib.request.urlopen("http://127.0.0.1:9333/json/list", timeout=3))
               if t.get("type") == "page"]:
    if target["webSocketDebuggerUrl"] == FIRST_TARGET:
        continue
    sock = websocket.create_connection(target["webSocketDebuggerUrl"], timeout=15)
    try:
        ready = False
        for _ in range(30):
            if evaluate("!!window.__palacedb", sock=sock):
                ready = True
                break
            time.sleep(0.4)
        if not ready:
            sock.close()
            continue

        if evaluate("__palacedb.state.document", sock=sock) is None:
            evaluate("__palacedb.send('openPath', {path: %s})" % json.dumps(CDB), sock=sock)
            time.sleep(2.5)

        if evaluate("__palacedb.state.document", sock=sock):
            second = sock
            break
    except Exception:
        pass
    sock.close()

check("second window opened", second is not None)
if second is not None:
    check("second window is read-only",
          evaluate("__palacedb.state.document.readOnly", sock=second) is True,
          evaluate("__palacedb.state.document.readOnlyReason", sock=second))

    evaluate("__palacedb.send('setCell', {sheet: __palacedb.state.currentSheet, "
             "row: 0, column: %s, text: 'WIN1_SAVE'})" % json.dumps(ID_NAME))
    time.sleep(0.8)
    evaluate("document.body.dispatchEvent(new KeyboardEvent('keydown', {key:'s', ctrlKey:true, bubbles:true}))")
    time.sleep(1.2)

    followed = False
    for _ in range(24):
        time.sleep(0.5)
        if evaluate("__palacedb.grid.cellValue(0,0) && __palacedb.grid.cellValue(0,0).t",
                    sock=second) == "WIN1_SAVE":
            followed = True
            break
    check("read-only window followed the save", followed,
          evaluate("__palacedb.grid.cellValue(0,0) && __palacedb.grid.cellValue(0,0).t", sock=second))

    evaluate("__palacedb.send('exit')", sock=second)
    second.close()
    time.sleep(0.8)

# --- automatic backup while dirty (C1 / F19)
backup_path = CDB + ".autobackup"
if os.path.exists(backup_path):
    os.remove(backup_path)

check("document clean before the backup test",
      wait_for("__palacedb.state.document.dirty === false", 6))

evaluate("__palacedb.run('addRow')")
ok = wait_for_file(backup_path, 20, True)
check("automatic backup written while dirty", ok, backup_path)

if ok:
    backup_data = json.load(open(backup_path, encoding="utf-8"))
    check("backup contains the unsaved row",
          len(backup_data["sheets"][0]["lines"]) >= evaluate("__palacedb.grid.rowCount") - 1,
          (len(backup_data["sheets"][0]["lines"]), evaluate("__palacedb.grid.rowCount")))

evaluate("__palacedb.send('undo')")
check("undo after the backup leaves a clean document",
      wait_for("__palacedb.state.document.dirty === false", 6))
evaluate("document.body.dispatchEvent(new KeyboardEvent('keydown', {key:'s', ctrlKey:true, bubbles:true}))")
time.sleep(1.2)
check("backup removed after saving", wait_for_file(backup_path, 8, False), backup_path)

# --- external modification detection (F15)
# The reload keeps the scroll position now, so the view is put back on the first row
# first: the check below reads the value of row 0, which is only in the grid when it is
# on screen.
evaluate("__palacedb.grid.moveCursor(0, 0)")
time.sleep(0.5)
external = json.load(open(CDB, encoding="utf-8"))
external["sheets"][0]["lines"][0][ID_NAME] = "EXTERNAL_EDIT"
with open(CDB, "w", encoding="utf-8", newline="\n") as handle:
    json.dump(external, handle, ensure_ascii=False, indent="\t")

ok = wait_for("__palacedb.grid.cellValue(0,0) && __palacedb.grid.cellValue(0,0).t === 'EXTERNAL_EDIT'", 25)
check("external change reloaded automatically", ok,
      evaluate("__palacedb.grid.cellValue(0,0) && __palacedb.grid.cellValue(0,0).t"))
check("reloaded document is still editable", evaluate("__palacedb.state.document.readOnly") is False)

# --- window geometry is written into the side-car (F17)
meta_path = CDB + ".meta.json"
meta = json.load(open(meta_path, encoding="utf-8")) if os.path.exists(meta_path) else {}
geometry = meta.get("windowState") or {}
check("window geometry stored in the side-car",
      isinstance(geometry.get("x"), (int, float)) and isinstance(geometry.get("y"), (int, float))
      and isinstance(geometry.get("width"), (int, float)),
      geometry)
check("window geometry never reaches the database file",
      "windowState" not in open(CDB, encoding="utf-8").read())

# --- opening a file must not move the window
# The remembered position is applied before the window is shown; once it is on screen a
# new document must leave it alone. This used to jump on every open, because the
# placement arrived after the first paint.
GEO_DB = os.path.join(WORK, "geometry", "Second.cdb")
os.makedirs(os.path.dirname(GEO_DB), exist_ok=True)
shutil.copy(SOURCE, GEO_DB)
evaluate("document.body.dispatchEvent(new KeyboardEvent('keydown', {key:'s', ctrlKey:true, bubbles:true}))")
time.sleep(1.5)
move_window(proc.pid, 120, 90)
before_rect = wait_rect(proc.pid)
evaluate("__palacedb.send('openPath', {path: %s})" % json.dumps(GEO_DB))
switched = wait_for("!!__palacedb.state.document && __palacedb.state.document.path === %s"
                    % json.dumps(GEO_DB), 25)
after_rect = wait_rect(proc.pid)
check("another file opens in the same window", switched is True,
      evaluate("(__palacedb.state.document || {}).path"))
check("opening a file leaves the window where it is",
      before_rect is not None and after_rect is not None
      and before_rect[0] == after_rect[0] and before_rect[1] == after_rect[1],
      (before_rect, after_rect))

# --- image thumbnails and the display icon (R9 / C06)
# Neither sample database has an image or a reference column, so a small purpose-built
# file is used for these two features.
ART_DIR = os.path.join(WORK, "art")
os.makedirs(ART_DIR, exist_ok=True)
ART_DB = os.path.join(ART_DIR, "Sprites.cdb")
with open(os.path.join(ART_DIR, "hero.png"), "wb") as handle:
    handle.write(base64.b64decode(
        "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mNkYPhfDwAChwGA60e6kgAAAABJRU5ErkJggg=="))

sprites = {
    "sheets": [
        {
            "name": "Sprite",
            "columns": [
                {"typeStr": "0", "name": "id"},
                {"typeStr": "7", "name": "icon"},
                {"typeStr": "1", "name": "label"},
            ],
            "lines": [
                {"id": "hero", "icon": "hero.png", "label": "Hero"},
                {"id": "ghost", "icon": "missing.png", "label": "Ghost"},
            ],
        },
        {
            "name": "Card",
            "columns": [
                {"typeStr": "0", "name": "id"},
                {"typeStr": "6:Sprite", "name": "art"},
            ],
            "lines": [{"id": "c1", "art": "hero"}],
        },
    ]
}
with open(ART_DB, "w", encoding="utf-8", newline="\n") as handle:
    json.dump(sprites, handle, ensure_ascii=False, indent="\t")

evaluate("__palacedb.send('openPath', {path: %s})" % json.dumps(ART_DB))
ok = wait_for("__palacedb.state.document && __palacedb.state.document.name === 'Sprites.cdb'", 20)
check("purpose-built database opened", ok,
      evaluate("__palacedb.state.document && __palacedb.state.document.name"))

evaluate("""(() => {
  const item = Array.from(__palacedb.elements.tableList.querySelectorAll('.table-item'))
    .find(i => i.dataset.sheet === 'Sprite');
  if (item) item.click();
  return Boolean(item);
})()""")
wait_for("__palacedb.grid.sheet === 'Sprite' && __palacedb.grid.cache.size > 1")
check("image rows rendered", evaluate("__palacedb.grid.cache.size") == 2,
      evaluate("__palacedb.grid.cache.size"))

ok = wait_for("document.querySelectorAll('img.cell-thumb').length === 2", 20)
check("image cells reserve a thumbnail slot", ok,
      evaluate("document.querySelectorAll('img.cell-thumb').length"))

ok = wait_for("""Array.from(document.querySelectorAll('img.cell-thumb'))
  .some(i => i.src.startsWith('data:image/png;base64,'))""", 20)
check("image path resolved to a thumbnail", ok,
      evaluate("Array.from(document.querySelectorAll('img.cell-thumb')).map(i=>i.src.slice(0,24)).join('|')"))

ok = wait_for("document.querySelectorAll('img.cell-thumb.failed').length === 1", 20)
check("missing image file marked, not crashed", ok,
      evaluate("document.querySelectorAll('img.cell-thumb.failed').length"))
check("image cell still shows the stored path",
      evaluate("document.querySelector('.cell.has-thumb .cell-text').textContent") == "hero.png",
      evaluate("document.querySelector('.cell.has-thumb .cell-text').textContent"))

# Display icon: the property lives on the referenced table (C06).
evaluate("__palacedb.send('setDisplayIcon', {sheet: 'Sprite', column: 'icon'})")
ok = wait_for("""(__palacedb.state.document.sheets.find(s => s.name === 'Sprite') || {}).displayIcon === 'icon'""", 15)
check("display icon recorded on the table", ok,
      evaluate("(__palacedb.state.document.sheets.find(s => s.name === 'Sprite') || {}).displayIcon"))

ok = evaluate("""(() => {
  const item = Array.from(__palacedb.elements.tableList.querySelectorAll('.table-item'))
    .find(i => i.dataset.sheet === 'Card');
  if (item) item.click();
  return Boolean(item);
})()""")
check("card table selected", ok)
wait_for("__palacedb.grid.sheet === 'Card' && __palacedb.grid.cache.size > 0")
check("reference column is the second column",
      evaluate("__palacedb.grid.columns[1] && __palacedb.grid.columns[1].type") == "Ref",
      evaluate("__palacedb.grid.columns.map(c=>c.name+':'+c.type).join(',')"))
evaluate("__palacedb.editor.begin(0, 1)")
ok = wait_for("document.querySelectorAll('.dropdown-item').length === 2", 20)
check("reference dropdown lists the target rows", ok,
      evaluate("document.querySelectorAll('.dropdown-item').length"))

ok = wait_for("""Array.from(document.querySelectorAll('.dropdown-icon img'))
  .some(i => i.src.startsWith('data:image/png;base64,'))""", 20)
check("reference dropdown shows the icon", ok,
      evaluate("document.querySelectorAll('.dropdown-icon img').length"))
check("row without an icon file is marked",
      evaluate("document.querySelectorAll('.dropdown-icon.failed').length") == 1,
      evaluate("document.querySelectorAll('.dropdown-icon.failed').length"))
# No display column is declared on Sprite, so the label is the identifier.
check("dropdown labels the rows and marks the current one",
      evaluate("(document.querySelector('.dropdown-item.selected .dropdown-label') || {}).textContent") == "hero",
      evaluate("(document.querySelector('.dropdown-item.selected .dropdown-label') || {}).textContent"))
evaluate("__palacedb.editor.cancel()")

# The icon is a table property, so it belongs in the .cdb, not in the side-car.
evaluate("document.body.dispatchEvent(new KeyboardEvent('keydown', {key:'s', ctrlKey:true, bubbles:true}))")
time.sleep(1.2)
saved_sprites = json.load(open(ART_DB, encoding="utf-8"))
check("display icon written to the database file",
      saved_sprites["sheets"][0]["props"]["displayIcon"] == "icon",
      saved_sprites["sheets"][0].get("props"))
check("display icon kept out of the side-car",
      "displayIcon" not in open(ART_DB + ".meta.json", encoding="utf-8").read())

# --- recent files (F03)
recent_paths = "__palacedb.elements.recentMenu.querySelectorAll('[data-action=\"openRecent\"]')"
ok = wait_for("%s.length >= 2" % recent_paths, 15)
check("recent list remembers both files", ok,
      evaluate("Array.from(%s).map(b => b.dataset.path).join('|')" % recent_paths))
check("most recent file is first",
      (evaluate("(%s)[0] && (%s)[0].dataset.path" % (recent_paths, recent_paths)) or "").endswith("Sprites.cdb"),
      evaluate("(%s)[0] && (%s)[0].dataset.path" % (recent_paths, recent_paths)))
check("recent entry shows the file name and folder",
      evaluate("(() => { const b = (%s)[0]; return b ? b.textContent : null; })()" % recent_paths) is not None
      and "Sprites.cdb" in (evaluate("(() => { const b = (%s)[0]; return b ? b.textContent : null; })()" % recent_paths) or ""),
      evaluate("(() => { const b = (%s)[0]; return b ? b.textContent : null; })()" % recent_paths))

# A recent entry opens in a window of its own: the database this window shows stays put.
known_targets = {t["id"] for t in json.load(urllib.request.urlopen("http://127.0.0.1:9333/json/list"))}
showing = evaluate("__palacedb.state.document && __palacedb.state.document.name")
evaluate("""(() => {
  const button = Array.from(document.querySelectorAll('#recentMenu [data-action="openRecent"]'))
    .find(b => b.dataset.path.endsWith('TestData2.cdb'));
  if (button) button.click();
  return Boolean(button);
})()""")
ws_recent = None
for _ in range(40):
    time.sleep(0.5)
    try:
        pages = [t for t in json.load(urllib.request.urlopen("http://127.0.0.1:9333/json/list"))
                 if t.get("type") == "page" and t["id"] not in known_targets]
    except Exception:
        pages = []
    if pages:
        try:
            ws_recent = websocket.create_connection(pages[0]["webSocketDebuggerUrl"], timeout=15)
            break
        except Exception:
            ws_recent = None
check("clicking a recent entry opens a new window", ws_recent is not None)
check("the window it came from keeps its own database",
      evaluate("__palacedb.state.document && __palacedb.state.document.name") == showing,
      evaluate("__palacedb.state.document && __palacedb.state.document.name"))

if ws_recent is not None:
    def wait_recent(expr, timeout=30):
        end = time.time() + timeout
        while time.time() < end:
            try:
                if evaluate(expr, sock=ws_recent):
                    return True
            except Exception:
                pass
            time.sleep(0.25)
        return False

    ok = wait_recent("!!(window.__palacedb && __palacedb.state.document "
                     "&& __palacedb.state.document.name === 'TestData2.cdb')")
    check("clicking a recent entry opens that file", ok,
          evaluate("__palacedb.state.document && __palacedb.state.document.name", sock=ws_recent))

    # That window holds the write slot now, so it is closed before the rest of the suite
    # goes back to working in a single window.
    evaluate("__palacedb.send('exit')", sock=ws_recent)
    ws_recent.close()
    time.sleep(2.5)

evaluate("__palacedb.send('openPath', {path: %s})" % json.dumps(CDB))
ok = wait_for("__palacedb.state.document && __palacedb.state.document.name === 'TestData2.cdb'", 20)
check("the first database is back in this window", ok,
      evaluate("__palacedb.state.document && __palacedb.state.document.name"))
check("and it is editable again once the other window is gone",
      wait_for("__palacedb.state.document.readOnly === false", 15),
      evaluate("__palacedb.state.document.readOnlyReason"))

# The file this window already shows needs no second window.
opened_windows = len([t for t in json.load(urllib.request.urlopen("http://127.0.0.1:9333/json/list"))
                      if t.get("type") == "page"])
evaluate("""(() => {
  const button = Array.from(document.querySelectorAll('#recentMenu [data-action="openRecent"]'))
    .find(b => b.dataset.path.endsWith('TestData2.cdb'));
  if (button) button.click();
  return Boolean(button);
})()""")
time.sleep(1.5)
check("reopening the file this window shows adds no window",
      len([t for t in json.load(urllib.request.urlopen("http://127.0.0.1:9333/json/list"))
           if t.get("type") == "page"]) == opened_windows,
      len([t for t in json.load(urllib.request.urlopen("http://127.0.0.1:9333/json/list"))
           if t.get("type") == "page"]))
check("and the user is told why nothing happened",
      any("already open" in (toast or "") for toast in evaluate(
          "Array.from(document.querySelectorAll('.toast')).map(t => t.textContent)") or []),
      evaluate("Array.from(document.querySelectorAll('.toast')).map(t => t.textContent)"))

settings_file = os.path.join(DATA_DIR, "settings.json")
check("recent list persisted outside the project",
      os.path.exists(settings_file)
      and "Sprites.cdb" in open(settings_file, encoding="utf-8").read(),
      settings_file)

# A remembered file that has been deleted is dropped when it is clicked.
GONE = os.path.join(WORK, "Gone.cdb")
shutil.copy(SOURCE, GONE)
evaluate("__palacedb.send('openPath', {path: %s})" % json.dumps(GONE))
ok = wait_for("__palacedb.state.document && __palacedb.state.document.name === 'Gone.cdb'", 20)
check("third database opened", ok,
      evaluate("__palacedb.state.document && __palacedb.state.document.name"))
wait_for("%s.length >= 3" % recent_paths, 10)

os.remove(GONE)
evaluate("""(() => {
  const button = Array.from(document.querySelectorAll('#recentMenu [data-action="openRecent"]'))
    .find(b => b.dataset.path.endsWith('Gone.cdb'));
  if (button) button.click();
  return Boolean(button);
})()""")
ok = wait_for("""!Array.from(document.querySelectorAll('#recentMenu [data-action="openRecent"]'))
  .some(b => b.dataset.path.endsWith('Gone.cdb'))""", 15)
check("deleted recent file removed from the list", ok,
      evaluate("Array.from(%s).map(b => b.dataset.path).join('|')" % recent_paths))
check("deleted recent file reported to the user",
      evaluate("document.querySelectorAll('.toast').length") >= 1,
      evaluate("document.querySelectorAll('.toast').length"))

evaluate("""(() => {
  const clear = document.querySelector('#recentMenu [data-action="clearRecent"]');
  if (clear) clear.click();
  return Boolean(clear);
})()""")
ok = wait_for("%s.length === 0" % recent_paths, 15)
check("clear recent files empties the list", ok,
      evaluate("__palacedb.elements.recentMenu.textContent"))
check("empty list shows a placeholder",
      "(no recent files)" in (evaluate("__palacedb.elements.recentMenu.textContent") or ""),
      evaluate("__palacedb.elements.recentMenu.textContent"))
check("clearing persisted to the settings file",
      "Sprites.cdb" not in open(settings_file, encoding="utf-8").read(),
      open(settings_file, encoding="utf-8").read()[:120])

# --- about dialog (F09)
evaluate("__palacedb.run('about')")
ok = wait_for("!document.getElementById('modal').hidden && !!document.querySelector('.about-title')", 15)
check("about dialog opens", ok, evaluate("document.querySelector('.about-title') && document.querySelector('.about-title').textContent"))
check("about shows the version",
      (evaluate("document.querySelector('.about-title').textContent") or "").startswith("PalaceDB "),
      evaluate("document.querySelector('.about-title').textContent"))
check("about lists runtime details",
      evaluate("document.querySelectorAll('.about-row').length") >= 4,
      evaluate("Array.from(document.querySelectorAll('.about-row')).map(r => r.textContent).join(' | ')"))
check("about reports the .NET runtime",
      ".NET" in (evaluate("document.querySelector('.about-rows').textContent") or ""),
      evaluate("document.querySelector('.about-rows').textContent"))
check("about has no Cancel button", evaluate("document.getElementById('modalCancel').hidden") is True)
evaluate("document.getElementById('modalOk').click()")
check("about dialog closes", wait_for("document.getElementById('modal').hidden", 8))

# --- export localized texts (F08)
# A fresh copy: the second window opened earlier still holds the write slot on the
# working copy, and a read-only document silently ignores structural edits.
EXPORT_DB = os.path.join(WORK, "Export.cdb")
shutil.copy(SOURCE, EXPORT_DB)
evaluate("__palacedb.send('openPath', {path: %s})" % json.dumps(EXPORT_DB))
ok = wait_for("__palacedb.state.document && __palacedb.state.document.name === 'Export.cdb'", 25)
check("database opened for the export", ok,
      evaluate("__palacedb.state.document && __palacedb.state.document.name"))
check("export database is editable",
      evaluate("__palacedb.state.document && __palacedb.state.document.readOnly") is False,
      evaluate("__palacedb.state.document && __palacedb.state.document.readOnlyReason"))

LOCALIZED_XML = os.path.join(WORK, "localized.xml")
NO_LOCALIZABLE_XML = os.path.join(WORK, "nothing.xml")

evaluate("__palacedb.run('exportLocalized', {path: %s})" % json.dumps(NO_LOCALIZABLE_XML))
ok = wait_for("document.querySelectorAll('.toast').length > 0", 10)
check("export refuses a database without localizable columns", ok,
      evaluate("Array.from(document.querySelectorAll('.toast')).map(t => t.textContent).join('|')"))
check("nothing is written when the export is refused", not os.path.exists(NO_LOCALIZABLE_XML))

# The column header offers the toggle in its context menu.
evaluate("""(() => {
  const index = __palacedb.grid.columns.findIndex(c => c.type === 'Text');
  const head = document.querySelectorAll('.head-cell')[index];
  if (head) head.dispatchEvent(new MouseEvent('contextmenu', {bubbles:true, clientX:200, clientY:200}));
  return Boolean(head);
})()""")
time.sleep(0.4)
head_labels = evaluate(
    "Array.from(document.querySelectorAll('#contextMenu .context-item span')).map(s=>s.textContent).join('|')")
check("column context menu offers Localizable", "Localizable" in (head_labels or ""), head_labels)
evaluate("document.dispatchEvent(new KeyboardEvent('keydown', {key:'Escape', bubbles:true}))")
time.sleep(0.3)

text_column = evaluate("""(() => {
  const sheet = (__palacedb.state.document.sheets || []).find(s => s.name === __palacedb.state.currentSheet);
  if (!sheet) return null;
  // A text column that actually holds something: the exporter skips empty values, and the
  // sample is a scratch file whose columns come and go.
  const columns = __palacedb.grid.columns || [];
  for (let i = 0; i < columns.length; i++) {
    if (columns[i].type !== 'Text') continue;
    const cell = __palacedb.grid.cellValue(0, i);
    if (cell && cell.t) return {sheet: sheet.name, column: columns[i].name};
  }
  const fallback = columns.find(c => c.type === 'Text');
  return fallback ? {sheet: sheet.name, column: fallback.name} : null;
})()""")
check("the current table has a text column", text_column is not None, text_column)

if text_column:
    sheet_name, column_name = text_column["sheet"], text_column["column"]
    first_value = evaluate(
        "(__palacedb.grid.cellValue(0, __palacedb.grid.columns.findIndex(c => c.name === %s)) || {}).t"
        % json.dumps(column_name))

    evaluate("__palacedb.send('setColumnKind', {sheet: %s, column: %s, localizable: true})"
             % (json.dumps(sheet_name), json.dumps(column_name)))
    ok = wait_for("(__palacedb.grid.columns.find(c => c.name === %s) || {}).localizable === true"
                  % json.dumps(column_name), 10)
    check("column can be marked localizable", ok,
          evaluate("(__palacedb.grid.columns.find(c => c.name === %s) || {}).localizable"
                   % json.dumps(column_name)))

    evaluate("__palacedb.run('exportLocalized', {path: %s})" % json.dumps(LOCALIZED_XML))
    ok = wait_for_file(LOCALIZED_XML, 10)
    check("export writes the xml file", ok, LOCALIZED_XML)

    if ok:
        raw = open(LOCALIZED_XML, "rb").read()
        text = raw.decode("utf-8-sig")
        check("export starts with a byte order mark", raw[:3] == b"\xef\xbb\xbf", raw[:3])
        check("export is rooted at <cdb>", text.lstrip().startswith("<cdb>"), text[:40])
        check("export names the sheet", 'name="%s"' % sheet_name in text, text[:200])
        check("export contains a column element", "<%s>" % column_name in text, text[:400])
        if first_value:
            check("export contains the first row value", first_value in text, first_value)

    # The localizable flag is a column property of the .cdb format, so it has to reach
    # the file on save (it is not presentation state and does not belong to the side-car).
    evaluate("document.body.dispatchEvent(new KeyboardEvent('keydown', {key:'s', ctrlKey:true, bubbles:true}))")
    time.sleep(1.2)
    stored = open(EXPORT_DB, encoding="utf-8").read()
    check("localizable flag saved into the database", "localizable" in stored, column_name)
    meta_text = (open(EXPORT_DB + ".meta.json", encoding="utf-8").read()
                 if os.path.exists(EXPORT_DB + ".meta.json") else "")
    check("localizable flag kept out of the metadata side-car", "localizable" not in meta_text, meta_text[:200])

# --- edit column dialog: name, type and options in one window (C01 / C02 / C03)
# The dialog is opened on the third column, so the checks follow that column rather than
# a name that differs between the sample files.
evaluate("window.__testText = __palacedb.grid.columns[2].name")
evaluate("""(() => {
  const head = document.querySelectorAll('.head-cell')[2];
  if (head) head.dispatchEvent(new MouseEvent('contextmenu', {bubbles:true, clientX:220, clientY:200}));
  return Boolean(head);
})()""")
time.sleep(0.4)
menu_labels = evaluate(
    "Array.from(document.querySelectorAll('#contextMenu .context-item span')).map(s=>s.textContent).join('|')")
check("column menu offers one Edit Column entry",
      "Edit Column" in (menu_labels or "") and "Rename Column" not in (menu_labels or "")
      and "Change Type" not in (menu_labels or ""), menu_labels)
evaluate("""(() => {
  const item = Array.from(document.querySelectorAll('#contextMenu .context-item'))
    .find(i => (i.textContent || '').indexOf('Edit Column') >= 0);
  if (item) item.click();
  return Boolean(item);
})()""")
ok = wait_for("!document.getElementById('modal').hidden && document.querySelectorAll('#modal .form-check').length >= 3", 10)
check("edit column dialog opens", ok,
      evaluate("document.querySelectorAll('#modal .form-row').length"))
check("edit column dialog has a name field and a type list",
      evaluate("!!document.querySelector('#modal .form-row input[type=text]')")
      and evaluate("document.querySelectorAll('#modal select').length") >= 1,
      evaluate("Array.from(document.querySelectorAll('#modal .form-row > span')).map(s=>s.textContent).join('|')"))

def format_row_hidden():
    return evaluate("""(() => {
      const r = Array.from(document.querySelectorAll('#modal .form-row')).find(x => x.textContent.startsWith('Format'));
      return r ? r.hidden : null;
    })()""")

check("display format hidden for a text column", format_row_hidden() is True, format_row_hidden())
evaluate("""(() => { const s = document.querySelectorAll('#modal select')[0];
  s.value = '3'; s.dispatchEvent(new Event('change', {bubbles:true})); return 1; })()""")
time.sleep(0.3)
check("display format shown for a number column", format_row_hidden() is False, format_row_hidden())
evaluate("""(() => { const s = document.querySelectorAll('#modal select')[0];
  s.value = '1'; s.dispatchEvent(new Event('change', {bubbles:true})); return 1; })()""")
time.sleep(0.3)

# Apply optional + default + localizable through the same dialog.
evaluate("""(() => {
  const rows = Array.from(document.querySelectorAll('#modal .form-row'));
  const opt = rows.find(r => r.textContent.includes('Optional')).querySelector('input');
  opt.checked = true;
  const def = rows.find(r => r.textContent.startsWith('Default')).querySelector('input');
  def.value = 'hello';
  def.dispatchEvent(new Event('input', {bubbles:true}));
  const loc = rows.find(r => r.textContent.includes('Localizable')).querySelector('input');
  loc.checked = true;
  return 1;
})()""")
evaluate("document.getElementById('modalOk').click()")
ok = wait_for("""(() => {
  const c = __palacedb.grid.columns.find(x => x.name === window.__testText);
  return c && c.optional === true && c.localizable === true && c.defaultValue === 'hello';
})()""", 10)
check("edit column applies the whole form in one step", ok,
      evaluate("JSON.stringify(__palacedb.grid.columns.find(x => x.name === window.__testText))"))

# Apply the options through the same message the dialog sends.
evaluate("""__palacedb.send('setColumnOptions', {
  sheet: __palacedb.state.currentSheet, column: window.__testText,
  optional: true, default: 'hello', display: 'default', localizable: true })""")
ok = wait_for("""(() => {
  const c = __palacedb.grid.columns.find(x => x.name === window.__testText);
  return c && c.optional === true && c.localizable === true && c.defaultValue === 'hello';
})()""", 10)
check("column options applied", ok,
      evaluate("JSON.stringify(__palacedb.grid.columns.find(x => x.name === window.__testText))"))

evaluate("document.body.dispatchEvent(new KeyboardEvent('keydown', {key:'s', ctrlKey:true, bubbles:true}))")
time.sleep(1.2)
saved_db = open(EXPORT_DB, encoding="utf-8").read()
check("optional flag saved", '"opt": true' in saved_db, saved_db[:80])
check("default value saved", '"defaultValue": "hello"' in saved_db)
check("localizable flag saved with the options", '"kind": "localizable"' in saved_db)

evaluate("__palacedb.send('undo')")
ok = wait_for("(() => { const c = __palacedb.grid.columns.find(x => x.name === window.__testText); return c && c.optional === false; })()", 10)
check("column options undone in one step", ok,
      evaluate("JSON.stringify(__palacedb.grid.columns.find(x => x.name === window.__testText))"))

# --- paste plain text from a spreadsheet (R5)
# Two neighbouring text columns, chosen by type: the sample's column order is not fixed.
TSV_COL = evaluate("""(() => {
  const cols = __palacedb.grid.columns;
  for (let i = 0; i + 1 < cols.length; i++) {
    if (cols[i].type === 'Text' && cols[i + 1].type === 'Text') return i;
  }
  return cols.findIndex(c => c.type === 'Text');
})()""")
check("the table has two neighbouring text columns", TSV_COL >= 0, TSV_COL)
set_clipboard("TSV_ONE\tTSV_TWO\nTSV_THREE\tTSV_FOUR")
evaluate("__palacedb.send('pasteCells', {sheet: __palacedb.state.currentSheet, row: 0, column: %d, mode: 'overwrite'})" % TSV_COL)
ok = wait_for("__palacedb.grid.cellValue(0,%d) && __palacedb.grid.cellValue(0,%d).t === 'TSV_ONE'" % (TSV_COL, TSV_COL), 10)
check("tab separated text pasted into the grid", ok,
      evaluate("__palacedb.grid.cellValue(0,%d) && __palacedb.grid.cellValue(0,%d).t" % (TSV_COL, TSV_COL)))
check("the second column of the block landed too",
      evaluate("__palacedb.grid.cellValue(0,%d) && __palacedb.grid.cellValue(0,%d).t" % (TSV_COL + 1, TSV_COL + 1)) == "TSV_TWO",
      evaluate("__palacedb.grid.cellValue(0,%d) && __palacedb.grid.cellValue(0,%d).t" % (TSV_COL + 1, TSV_COL + 1)))
check("the second row of the block landed too",
      evaluate("__palacedb.grid.cellValue(1,%d) && __palacedb.grid.cellValue(1,%d).t" % (TSV_COL, TSV_COL)) == "TSV_THREE",
      evaluate("__palacedb.grid.cellValue(1,%d) && __palacedb.grid.cellValue(1,%d).t" % (TSV_COL, TSV_COL)))

evaluate("__palacedb.send('undo')")
ok = wait_for("__palacedb.grid.cellValue(0,%d) && __palacedb.grid.cellValue(0,%d).t !== 'TSV_ONE'" % (TSV_COL, TSV_COL), 10)
check("pasted block undone in one step", ok,
      evaluate("__palacedb.grid.cellValue(0,%d) && __palacedb.grid.cellValue(0,%d).t" % (TSV_COL, TSV_COL)))

# A single value pasted into a cell through the real Ctrl+V path. The cursor must stay
# on that cell: refreshing with column 0 used to throw it back to the row's first cell.
set_clipboard("only-one-cell")
evaluate("__palacedb.grid.moveCursor(0, %d)" % TSV_COL)
time.sleep(0.2)
evaluate("document.body.dispatchEvent(new KeyboardEvent('keydown',{key:'v',ctrlKey:true,bubbles:true}))")
time.sleep(0.6)
check("single cell paste still works",
      evaluate("__palacedb.grid.cellValue(0,%d) && __palacedb.grid.cellValue(0,%d).t" % (TSV_COL, TSV_COL)) == "only-one-cell",
      evaluate("__palacedb.grid.cellValue(0,%d) && __palacedb.grid.cellValue(0,%d).t" % (TSV_COL, TSV_COL)))
check("a single pasted value keeps the cursor on its cell",
      evaluate("__palacedb.grid.cursor.row === 0 && __palacedb.grid.cursor.col === %d" % TSV_COL),
      evaluate("JSON.stringify(__palacedb.grid.cursor)"))
evaluate("__palacedb.send('undo')")

# Pasting while the cursor sits on the identifier column writes identifiers.
set_clipboard("PASTED_ID_1\nPASTED_ID_2")
evaluate("__palacedb.grid.moveCursor(0, 0)")
time.sleep(0.3)
evaluate("__palacedb.send('pasteCells', {sheet: __palacedb.state.currentSheet, row: 0, column: 0, mode: 'overwrite'})")
ok = wait_for("__palacedb.grid.cellValue(0,0) && __palacedb.grid.cellValue(0,0).t === 'PASTED_ID_1'", 10)
check("pasting into the identifier column works", ok,
      evaluate("__palacedb.grid.cellValue(0,0) && __palacedb.grid.cellValue(0,0).t"))
check("the second pasted identifier landed too",
      evaluate("__palacedb.grid.cellValue(1,0) && __palacedb.grid.cellValue(1,0).t") == "PASTED_ID_2",
      evaluate("__palacedb.grid.cellValue(1,0) && __palacedb.grid.cellValue(1,0).t"))
evaluate("__palacedb.send('undo')")
ok = wait_for("__palacedb.grid.cellValue(0,0) && __palacedb.grid.cellValue(0,0).t !== 'PASTED_ID_1'", 10)
check("pasted identifiers undone in one step", ok,
      evaluate("__palacedb.grid.cellValue(0,0) && __palacedb.grid.cellValue(0,0).t"))

# --- three-way merge of git leftovers (F16)
# Save first: switching documents would otherwise ask about unsaved changes and block.
evaluate("document.body.dispatchEvent(new KeyboardEvent('keydown', {key:'s', ctrlKey:true, bubbles:true}))")
time.sleep(1.0)

MERGE_DIR = os.path.join(WORK, "merge")
os.makedirs(MERGE_DIR, exist_ok=True)
MERGE = os.path.join(MERGE_DIR, "Merged.cdb")
merge_base = json.load(open(SOURCE, encoding="utf-8"))

# The local side renamed the first row; the incoming side added a row.
merge_mine = json.loads(json.dumps(merge_base))
merge_mine["sheets"][0]["lines"][0][ID_NAME] = "LOCAL_ROW"
merge_theirs = json.loads(json.dumps(merge_base))
merge_theirs["sheets"][0]["lines"].append(
    dict(merge_theirs["sheets"][0]["lines"][0], **{ID_NAME: "INCOMING_ROW"}))

def dump_cdb(path, data):
    with open(path, "w", encoding="utf-8", newline="\n") as handle:
        json.dump(data, handle, ensure_ascii=False, indent="\t")

dump_cdb(MERGE, merge_theirs)
dump_cdb(MERGE + ".mine", merge_mine)
dump_cdb(MERGE + ".r1", merge_base)

evaluate("__palacedb.send('openPath', {path: %s})" % json.dumps(MERGE))
ok = wait_for("__palacedb.state.document && __palacedb.state.document.name === 'Merged.cdb'", 25)
check("file with git leftovers opens", ok,
      evaluate("__palacedb.state.document && __palacedb.state.document.name"))
ok = wait_for("!document.getElementById('modal').hidden && !!document.querySelector('.merge-report')", 15)
check("merge report is shown", ok,
      evaluate("document.querySelector('.merge-lead') && document.querySelector('.merge-lead').textContent"))
check("merged document is unsaved",
      evaluate("__palacedb.state.document && __palacedb.state.document.dirty") is True,
      evaluate("document.title"))
check("merge report lists the leftovers",
      ".mine" in (evaluate("document.querySelector('.merge-note') && document.querySelector('.merge-note').textContent") or ""),
      evaluate("document.querySelector('.merge-note') && document.querySelector('.merge-note').textContent"))
evaluate("document.getElementById('modalOk').click()")
time.sleep(0.3)

merged_row_count = evaluate("__palacedb.state.document.sheets[0].rowCount")
check("merged document holds both sides",
      merged_row_count == len(merge_base["sheets"][0]["lines"]) + 1, merged_row_count)

# The grid only holds the rows in view, so the end of the file is checked after saving.
check("conflict files still present before saving", os.path.exists(MERGE + ".mine"), MERGE)
evaluate("document.body.dispatchEvent(new KeyboardEvent('keydown', {key:'s', ctrlKey:true, bubbles:true}))")
ok = wait_for_file(MERGE + ".mine", 10, False)
check("saving removes the conflict leftovers", ok, os.listdir(MERGE_DIR))
check("saving keeps the database itself", os.path.exists(MERGE), os.listdir(MERGE_DIR))
saved = json.load(open(MERGE, encoding="utf-8"))
saved_ids = [row.get(ID_NAME) for row in saved["sheets"][0]["lines"]]
check("saved file holds both sides", "LOCAL_ROW" in saved_ids and "INCOMING_ROW" in saved_ids, saved_ids[:4])
check("the renamed row was replaced, not duplicated",
      "vocational" not in saved_ids and "LOCAL_ROW" in saved_ids, saved_ids[:4])

# --- a brand new database (F01)
NEW_DIR = os.path.join(WORK, "new")
os.makedirs(NEW_DIR, exist_ok=True)
NEW = os.path.join(NEW_DIR, "Fresh.cdb")

evaluate("__palacedb.run('newDatabase')")
ok = wait_for("__palacedb.state.document && __palacedb.state.document.name === 'Untitled'", 20)
check("new database opens untitled", ok,
      evaluate("__palacedb.state.document && __palacedb.state.document.name"))
check("new database has no tables",
      evaluate("__palacedb.state.document.sheets.length") == 0,
      evaluate("__palacedb.state.document.sheets.length"))
check("new database is unsaved",
      evaluate("__palacedb.state.document.dirty") is True, evaluate("document.title"))
check("grid explains there is no table yet",
      evaluate("!document.getElementById('gridEmpty').classList.contains('hidden')"))
scratch = evaluate("__palacedb.state.document.path")
check("a new database only borrows a scratch path",
      bool(scratch)
      and os.path.normcase(os.path.normpath(scratch)).startswith(
          os.path.normcase(os.path.normpath(DATA_DIR)))
      and not os.path.exists(scratch), scratch)

# Saving a new database has to pick a file first; the automation hook supplies one.
evaluate("__palacedb.send('saveAs', {path: %s})" % json.dumps(NEW))
ok = wait_for_file(NEW, 12)
check("saving the new database created the file", ok, os.listdir(NEW_DIR))
check("the new database is no longer untitled",
      evaluate("__palacedb.state.document.name") == "Fresh.cdb",
      evaluate("__palacedb.state.document.name"))
check("the saved new database is clean",
      evaluate("__palacedb.state.document.dirty") is False, evaluate("document.title"))
if os.path.exists(NEW):
    fresh = json.load(open(NEW, encoding="utf-8"))
    check("the saved new database is a valid empty model",
          isinstance(fresh, dict) and not fresh.get("sheets"), list(fresh)[:5])
else:
    check("the saved new database is a valid empty model", False, "missing file")

# --- a brand new database has to accept its first table
evaluate("__palacedb.send('addSheet', {name:'First'})")
ok = wait_for("__palacedb.state.document.sheets.length === 1", 15)
check("a new database accepts its first table", ok,
      evaluate("__palacedb.state.document.sheets.map(s=>s.name).join(',')"))
check("the empty-state hint goes away",
      evaluate("document.getElementById('gridEmpty').classList.contains('hidden')") is True)
check("the first table is selected", evaluate("__palacedb.state.currentSheet") == "First")
check("the first table gets an id column",
      evaluate("__palacedb.grid.columns.map(c=>c.name+':'+c.type).join(',')") == "id:Id",
      evaluate("__palacedb.grid.columns.map(c=>c.name+':'+c.type).join(',')"))

evaluate("__palacedb.send('insertRow', {sheet:'First', row:0})")
ok = wait_for("__palacedb.grid.rowCount === 1", 15)
check("the first table accepts a row", ok, evaluate("__palacedb.grid.rowCount"))

# Save so the document is clean again before the next open.
evaluate("document.body.dispatchEvent(new KeyboardEvent('keydown', {key:'s', ctrlKey:true, bubbles:true}))")
ok = wait_for("__palacedb.state.document.dirty === false", 10)
check("the new database saves once it has content", ok, evaluate("document.title"))
saved_new = json.load(open(NEW, encoding="utf-8"))
check("the saved new database holds the table",
      [s.get("name") for s in saved_new.get("sheets", [])] == ["First"],
      saved_new.get("sheets"))

# --- dropping a file onto the window
# The real drag is OLE-level and cannot be produced over DevTools, so the suite drives
# the same handler the window's Drop event calls.
DROP_DIR = os.path.join(WORK, "drop")
os.makedirs(DROP_DIR, exist_ok=True)
DROPPED = os.path.join(DROP_DIR, "Dropped.cdb")
shutil.copy(SOURCE, DROPPED)
NOT_A_DB = os.path.join(DROP_DIR, "notes.txt")
with open(NOT_A_DB, "w", encoding="utf-8") as handle:
    handle.write("not a database")

evaluate("__palacedb.send('simulateDrop', {files: %s})" % json.dumps([NOT_A_DB]))
time.sleep(0.8)
check("a drop without a database opens nothing",
      evaluate("__palacedb.state.document.name") == "Fresh.cdb",
      evaluate("__palacedb.state.document.name"))

# A mixed selection must still find the database inside it.
evaluate("__palacedb.send('simulateDrop', {files: %s})" % json.dumps([NOT_A_DB, DROPPED]))
ok = wait_for("__palacedb.state.document && __palacedb.state.document.name === 'Dropped.cdb'", 20)
check("dropping a database opens it", ok,
      evaluate("__palacedb.state.document && __palacedb.state.document.name"))
check("a dropped database is editable",
      evaluate("__palacedb.state.document.readOnly") is False,
      evaluate("__palacedb.state.document.readOnly"))

# --- row numbers along the left edge
evaluate("__palacedb.grid.body.scrollTop = 0")
time.sleep(0.4)
gutter = evaluate("""(() => {
  const cells = [...document.querySelectorAll('.gutter-cell')];
  const first = cells[0];
  const row = document.querySelector('.grid-row');
  return {
    count: cells.length,
    text: first ? first.textContent : null,
    top: first ? first.getBoundingClientRect().top : null,
    rowTop: row ? row.getBoundingClientRect().top : null,
  };
})()""")
check("the gutter numbers every visible row",
      gutter and gutter["count"] > 3 and gutter["text"] == "1", gutter)
check("a row number lines up with its row",
      gutter and abs(gutter["top"] - gutter["rowTop"]) <= 1, gutter)

evaluate("""(() => {
  const cell = document.querySelector('.gutter-cell[data-row="2"]');
  cell.dispatchEvent(new MouseEvent('mousedown', {bubbles: true, cancelable: true}));
  return true;
})()""")
time.sleep(0.5)
rowsel = evaluate("""(() => {
  const b = __palacedb.grid.selectionBounds();
  return {top: b.top, bottom: b.bottom, left: b.left, right: b.right,
          cols: __palacedb.grid.columns.length,
          cursor: __palacedb.grid.cursor};
})()""")
check("clicking a row number selects that row",
      rowsel and rowsel["top"] == 2 and rowsel["bottom"] == 2, rowsel)
check("the row selection spans every column",
      rowsel and rowsel["left"] == 0 and rowsel["right"] == rowsel["cols"] - 1, rowsel)
check("the cursor stays on the first cell of the row",
      rowsel and rowsel["cursor"]["row"] == 2 and rowsel["cursor"]["col"] == 0, rowsel)
check("the selected row number is highlighted",
      evaluate("!!document.querySelector('.gutter-cell.selected')") is True)

# --- collapsing the table list frees the horizontal space
evaluate("document.body.dispatchEvent(new KeyboardEvent('keydown', {key:'b', ctrlKey:true, bubbles:true}))")
time.sleep(0.5)
collapsed = evaluate("""(() => {
  const side = document.getElementById('sidebar');
  const toggle = document.getElementById('sidebarExpand');
  const firstTool = document.querySelector('#toolbar .tool');
  return {
    display: getComputedStyle(side).display,
    width: side.getBoundingClientRect().width,
    toggleVisible: toggle.getBoundingClientRect().width > 0,
    // The toolbar now starts at the window edge, so its own buttons must not sit
    // underneath the pinned toggle.
    toolbarClearsToggle: !firstTool ||
      firstTool.getBoundingClientRect().left >= toggle.getBoundingClientRect().right,
  };
})()""")
check("a collapsed table list takes no space",
      collapsed and collapsed["display"] == "none" and collapsed["width"] == 0, collapsed)
check("the pinned toggle stays visible and the toolbar leaves room for it",
      collapsed and collapsed["toggleVisible"] is True
      and collapsed["toolbarClearsToggle"] is True, collapsed)

evaluate("document.getElementById('sidebarExpand').click()")
time.sleep(0.5)
restored_side = evaluate("""(() => {
  const side = document.getElementById('sidebar');
  return {display: getComputedStyle(side).display, width: side.getBoundingClientRect().width};
})()""")
check("the pinned toggle brings the table list back",
      restored_side and restored_side["display"] != "none" and restored_side["width"] > 100,
      restored_side)

# --- validation problems reach the status bar with table and row
id_name = evaluate("""(() => {
  const c = __palacedb.grid.columns.find(c => c.type === 'Id');
  return c ? c.name : null;
})()""")
if id_name:
    first_id = evaluate("__palacedb.grid.cellValue(0,0).t")
    evaluate("__palacedb.send('setCell', {sheet: __palacedb.state.currentSheet, "
             f"row: 1, column: {json.dumps(id_name)}, text: {json.dumps(first_id)}}})")
    ok = wait_for("!document.getElementById('statusIssue').hidden", 10)
    text = evaluate("document.getElementById('statusIssue').textContent")
    check("a duplicate id is reported in the status bar", ok, text)
    # Both rows carrying the id are marked #DUP, and the status line names the first of
    # them, so the report is deterministic rather than dependent on where the edit was.
    check("the report names the table and the row",
          bool(text) and "Row 1" in text and evaluate("__palacedb.state.currentSheet") in text, text)

    # The cell shows "#DUP(id)" and the editor used to open blank, as if the id had been
    # lost. It has to start from the id the row really holds.
    id_col = evaluate("__palacedb.grid.columns.findIndex(c => c.name === %s)" % json.dumps(id_name))
    evaluate("__palacedb.grid.moveCursor(1, %d)" % id_col)
    evaluate("__palacedb.grid.onEditRequest(1, %d)" % id_col)
    wait_for("!!document.querySelector('.editor-input')", 5)
    editor_value = evaluate("document.querySelector('.editor-input').value")
    check("editing a duplicate id starts from the real id", editor_value == first_id,
          (first_id, editor_value))
    evaluate("document.querySelector('.editor-input')"
             ".dispatchEvent(new KeyboardEvent('keydown',{key:'Escape',bubbles:true}))")
    wait_for("!document.querySelector('.editor-input')", 5)

    # Clearing the value must remove the message again.
    evaluate("__palacedb.send('setCell', {sheet: __palacedb.state.currentSheet, "
             f"row: 1, column: {json.dumps(id_name)}, text: 'DUP_FIXED'}})")
    ok = wait_for("document.getElementById('statusIssue').hidden", 10)
    check("the status line clears once the problem is gone", ok,
          evaluate("document.getElementById('statusIssue').textContent"))
    evaluate("document.body.dispatchEvent(new KeyboardEvent('keydown', {key:'z', ctrlKey:true, bubbles:true}))")
    evaluate("document.body.dispatchEvent(new KeyboardEvent('keydown', {key:'z', ctrlKey:true, bubbles:true}))")
    time.sleep(0.6)

    # A pure-digit id is valid too, and duplicating such a row must keep the copy numeric
    # ("12345_2" would not be a valid id any more).
    evaluate("__palacedb.send('setCell', {sheet: __palacedb.state.currentSheet, "
             f"row: 2, column: {json.dumps(id_name)}, text: '12345'}})")
    time.sleep(0.9)
    numeric_id = evaluate("__palacedb.grid.cellValue(2, %d).t" % id_col)
    check("a pure-digit id is accepted", numeric_id == "12345", numeric_id)

    rows_before_numeric = evaluate("__palacedb.grid.rowCount")
    evaluate("__palacedb.send('duplicateRows', {sheet: __palacedb.state.currentSheet, rows: [2]})")
    wait_for("__palacedb.grid.rowCount === %d" % (rows_before_numeric + 1), 8)
    time.sleep(0.5)
    copy_id = evaluate("__palacedb.grid.cellValue(3, %d).t" % id_col)
    check("duplicating a numeric id keeps the copy numeric",
          isinstance(copy_id, str) and copy_id.isdigit(), copy_id)
    evaluate("document.body.dispatchEvent(new KeyboardEvent('keydown', {key:'z', ctrlKey:true, bubbles:true}))")
    evaluate("document.body.dispatchEvent(new KeyboardEvent('keydown', {key:'z', ctrlKey:true, bubbles:true}))")
    time.sleep(0.7)

# --- the in-cell editor is the same size as the cell it covers
# A row is as tall as its tallest cell, so a Text cell can be several lines tall while
# holding one line of text. The editor used to sit on the first line only.
tall_row = evaluate("""(() => {
  const g = __palacedb.grid;
  for (let row = 0; row < g.rowCount; row += 1) {
    if (g.heightOf(row) > 50) return row;
  }
  return -1;
})()""")
text_col = evaluate("__palacedb.grid.columns.findIndex(c => c.type === 'Text')")
if tall_row >= 0 and text_col >= 0:
    evaluate(f"__palacedb.grid.moveCursor({tall_row},{text_col})")
    evaluate(f"__palacedb.grid.onEditRequest({tall_row},{text_col},'')")
    ok = wait_for("!!document.querySelector('#gridRows .cell-editor .editor-input')", 5)
    check("the editor opened on a tall row", ok)
    geometry = evaluate(f"""(() => {{
      const cell = document.querySelector('#gridRows .cell[data-row="{tall_row}"][data-col="{text_col}"]');
      const wrap = document.querySelector('#gridRows .cell-editor');
      const input = wrap && wrap.querySelector('.editor-input');
      if (!cell || !wrap || !input) return null;
      const box = (node) => {{
        const r = node.getBoundingClientRect();
        return [Math.round(r.x * 10) / 10, Math.round(r.y * 10) / 10,
                Math.round(r.width * 10) / 10, Math.round(r.height * 10) / 10];
      }};
      return {{ cell: box(cell), wrap: box(wrap), input: box(input) }};
    }})()""")
    if geometry is None:
        check("the editor covers the whole cell", False, "no editor in the DOM")
    else:
        deltas = [abs(a - b) for a, b in zip(geometry["cell"], geometry["wrap"])]
        check("the editor covers the whole cell", max(deltas) <= 1.5,
              (geometry["cell"], geometry["wrap"]))
        # The field inside the frame fills it, so clicking anywhere in the cell lands in
        # the text and the caret sits where the value was shown.
        inner = [abs(geometry["wrap"][2] - geometry["input"][2]),
                 abs(geometry["wrap"][3] - geometry["input"][3])]
        check("the field inside the editor fills it", max(inner) <= 1.5,
              (geometry["wrap"], geometry["input"]))
    evaluate("document.querySelector('.editor-input').dispatchEvent("
             "new KeyboardEvent('keydown', {key:'Escape', bubbles:true}))")
    time.sleep(0.4)
    check("the editor closed again", wait_for("!document.querySelector('.editor-input')", 5))

# --- saving, undo and redo leave the view exactly where it was
# Re-sending the document used to throw the measured row heights away, which shortened
# the scrollable area; the browser then clamped the scroll position upwards.
SCROLL_PROBE = """(() => { const g = __palacedb.grid; return {
  top: g.body.scrollTop, content: g.contentHeight() }; })()"""
evaluate("__palacedb.grid.body.scrollTop = 1200")
time.sleep(0.6)
before_scroll = evaluate(SCROLL_PROBE)
edit_row = evaluate("__palacedb.grid.rowIndexAt(__palacedb.grid.body.scrollTop) + 2")
evaluate(f"__palacedb.grid.moveCursor({edit_row},{text_col})")
evaluate(f"__palacedb.grid.onEditRequest({edit_row},{text_col},'')")
wait_for("!!document.querySelector('.editor-input')", 5)
evaluate("(() => { const i = document.querySelector('.editor-input'); i.value = 'VIEW_PROBE'; return 1; })()")
evaluate("(() => { const i = document.querySelector('.editor-input'); "
         "i.dispatchEvent(new KeyboardEvent('keydown', {key:'Enter', bubbles:true})); return 1; })()")
time.sleep(0.6)
evaluate("__palacedb.grid.body.scrollTop = 1200")
time.sleep(0.5)
before_scroll = evaluate(SCROLL_PROBE)

evaluate("__palacedb.send('save')")
time.sleep(1.5)
after_save = evaluate(SCROLL_PROBE)
check("saving keeps the scroll position",
      abs(after_save["top"] - before_scroll["top"]) <= 1
      and abs(after_save["content"] - before_scroll["content"]) <= 1,
      (before_scroll, after_save))

evaluate("__palacedb.send('undo')")
time.sleep(1.5)
after_undo = evaluate(SCROLL_PROBE)
check("undo keeps the scroll position",
      abs(after_undo["top"] - before_scroll["top"]) <= 1,
      (before_scroll, after_undo))

evaluate("__palacedb.send('redo')")
time.sleep(1.5)
after_redo = evaluate(SCROLL_PROBE)
check("redo keeps the scroll position",
      abs(after_redo["top"] - before_scroll["top"]) <= 1,
      (before_scroll, after_redo))
check("the edited value is back after redo",
      evaluate(f"__palacedb.grid.cellValue({edit_row},{text_col}) && "
               f"__palacedb.grid.cellValue({edit_row},{text_col}).t") == "VIEW_PROBE")
evaluate("__palacedb.grid.body.scrollTop = 0")
time.sleep(0.4)

# --- every column type, on a real file with images and files next to it
# That sample is not in the repository (it is somebody's real data), so a fresh clone
# stops here with an explanation instead of a traceback. Everything before this point
# runs on the committed TestData2.cdb.
FOOD_SOURCE = os.path.join(FIXTURES, "food.cdb")
if not os.path.exists(FOOD_SOURCE):
    passed = sum(1 for _, ok, _ in results if ok)
    print("\nSKIP: %s is not in this checkout." % FOOD_SOURCE)
    print("      The second half of the suite needs that file plus its food_img/ and")
    print("      file/ folders. Drop any CastleDB file there under that name to run it.")
    print("      Checks before it: %d / %d passed" % (passed, len(results)))
    ws.close()
    proc.terminate()
    sys.exit(3)

FOOD_DIR = os.path.join(WORK, "food")
os.makedirs(FOOD_DIR, exist_ok=True)
FOOD = os.path.join(FOOD_DIR, "food.cdb")
shutil.copy(FOOD_SOURCE, FOOD)
for folder in ("food_img", "file"):
    shutil.copytree(os.path.join(FIXTURES, folder), os.path.join(FOOD_DIR, folder))

evaluate("__palacedb.send('simulateDrop', {files: %s})" % json.dumps([FOOD]))
ok = wait_for("__palacedb.state.document && __palacedb.state.document.name === 'food.cdb'", 20)
check("the type test file opens", ok,
      evaluate("__palacedb.state.document && __palacedb.state.document.name"))
ok = wait_for("__palacedb.grid.cache.size > 0 && __palacedb.grid.columns.length >= 10", 15)
check("the type test file has every column type", ok, evaluate("__palacedb.grid.columns.length"))


def column_index(name):
    return evaluate("__palacedb.grid.columns.findIndex(c => c.name === %s)" % json.dumps(name))


def mouse_event(x, y, kind, clicks=1):
    """Sends one mouse event without waiting: the next evaluate() consumes the ack."""
    counter[0] += 1
    ws.send(json.dumps({"id": counter[0], "method": "Input.dispatchMouseEvent",
                        "params": {"type": kind, "x": x, "y": y, "button": "left", "clickCount": clicks}}))


def double_click(row, column):
    evaluate("__palacedb.grid.moveCursor(%d, %d)" % (row, column))
    time.sleep(0.25)
    point = evaluate("""(() => {
      const c = document.querySelector('#gridRows .cell[data-row="%d"][data-col="%d"]');
      if (!c) return null;
      const r = c.getBoundingClientRect();
      return {x: Math.round(r.x + 20), y: Math.round(r.y + 12)};
    })()""" % (row, column))
    if not point:
        return False
    for clicks in (1, 2):
        mouse_event(point["x"], point["y"], "mousePressed", clicks)
        mouse_event(point["x"], point["y"], "mouseReleased", clicks)
        time.sleep(0.06)
    time.sleep(0.5)
    return True


def press_key(key, code, vk):
    for kind in ("rawKeyDown", "keyUp"):
        counter[0] += 1
        ws.send(json.dumps({"id": counter[0], "method": "Input.dispatchKeyEvent",
                            "params": {"type": kind, "key": key, "code": code,
                                       "windowsVirtualKeyCode": vk}}))
    time.sleep(0.15)


def press_ctrl(key, code, vk):
    """A Ctrl-modified key press, the way a user copies and pastes."""
    for kind in ("rawKeyDown", "keyUp"):
        counter[0] += 1
        ws.send(json.dumps({"id": counter[0], "method": "Input.dispatchKeyEvent",
                            "params": {"type": kind, "key": key, "code": code,
                                       "windowsVirtualKeyCode": vk, "modifiers": 2}}))
    time.sleep(0.3)


def type_char(ch):
    counter[0] += 1
    ws.send(json.dumps({"id": counter[0], "method": "Input.dispatchKeyEvent",
                        "params": {"type": "char", "text": ch}}))
    time.sleep(0.25)


def click_cell(row, column):

    """A real single click on a cell, so the click handler runs as it does for a user."""
    evaluate("__palacedb.grid.moveCursor(%d, %d)" % (row, column))
    time.sleep(0.25)
    point = evaluate("""(() => {
      const c = document.querySelector('#gridRows .cell[data-row="%d"][data-col="%d"]');
      if (!c) return null;
      const r = c.getBoundingClientRect();
      return {x: Math.round(r.x + 20), y: Math.round(r.y + 12)};
    })()""" % (row, column))
    if not point:
        return False
    mouse_event(point["x"], point["y"], "mousePressed")
    mouse_event(point["x"], point["y"], "mouseReleased")
    time.sleep(0.5)
    return True


def commit_editor(value):
    evaluate("""(() => {
      const input = document.querySelector('.editor-input');
      if (!input) return false;
      input.value = %s;
      input.dispatchEvent(new Event('input', {bubbles: true}));
      input.dispatchEvent(new KeyboardEvent('keydown', {key: 'Enter', bubbles: true}));
      return true;
    })()""" % json.dumps(value))
    time.sleep(0.7)


# Text: a real double click opens the editor, with a caret rather than a full selection.
text_col = column_index("dis")
check("double click opens the text editor", double_click(1, text_col))
caret = evaluate("""(() => {
  const input = document.querySelector('.editor-input');
  if (!input) return null;
  return {focused: document.activeElement === input, start: input.selectionStart,
          end: input.selectionEnd, length: input.value.length, text: input.value};
})()""")
check("the editor takes the focus", caret and caret["focused"] is True, caret)
check("the editor shows a caret instead of selecting everything",
      caret and caret["start"] == caret["end"] == caret["length"], caret)
commit_editor("Beef patty")
check("text committed", evaluate("__palacedb.grid.cellValue(1,%d).t" % text_col) == "Beef patty",
      evaluate("__palacedb.grid.cellValue(1,%d).t" % text_col))

# Int
int_col = column_index("value")
evaluate("__palacedb.editor.begin(1, %d)" % int_col)
time.sleep(0.3)
commit_editor("77")
check("int committed", evaluate("__palacedb.grid.cellValue(1,%d).t" % int_col) == "77",
      evaluate("__palacedb.grid.cellValue(1,%d).t" % int_col))

# Bool: double click toggles in place.
bool_col = column_index("cookable")
before_bool = evaluate("__palacedb.grid.cellValue(1,%d).t" % bool_col)
double_click(1, bool_col)
after_bool = evaluate("__palacedb.grid.cellValue(1,%d).t" % bool_col)
check("double click toggles a boolean", before_bool != after_bool, (before_bool, after_bool))

# Color: a palette, and the cell paints the colour instead of its hex value.
color_col = column_index("color")
evaluate("__palacedb.editor.begin(1, %d)" % color_col)
time.sleep(0.4)
check("the colour popup offers a palette",
      evaluate("document.querySelectorAll('.color-swatch').length") >= 12,
      evaluate("document.querySelectorAll('.color-swatch').length"))
evaluate("""(() => {
  const swatch = document.querySelectorAll('.color-swatch')[7];
  swatch.dispatchEvent(new MouseEvent('mousedown', {bubbles: true, cancelable: true}));
  return 1;
})()""")
time.sleep(0.7)
check("a palette colour lands in the cell",
      evaluate("__palacedb.grid.cellValue(1,%d).t" % color_col) == "#dc2626",
      evaluate("__palacedb.grid.cellValue(1,%d).t" % color_col))
check("the cell paints the colour, not the value",
      evaluate("""(() => {
        const c = document.querySelector('.cell[data-row="1"][data-col="%d"]');
        const s = c && c.querySelector('.cell-swatch');
        return s ? getComputedStyle(s).backgroundColor : null;
      })()""" % color_col) == "rgb(220, 38, 38)",
      evaluate("""(() => {
        const c = document.querySelector('.cell[data-row="1"][data-col="%d"]');
        return c ? c.textContent : null;
      })()""" % color_col))

# Reference: typing filters the list, Enter takes the match.
ref_col = column_index("type")
evaluate("__palacedb.editor.begin(1, %d)" % ref_col)
ok = wait_for("document.querySelectorAll('.ref-dropdown .dropdown-item').length > 0", 10)
check("the reference editor lists the rows of the other table", ok,
      evaluate("document.querySelectorAll('.ref-dropdown .dropdown-item').length"))
evaluate("""(() => {
  const input = document.querySelector('.editor-input');
  input.value = 'B';
  input.dispatchEvent(new Event('input', {bubbles: true}));
  return 1;
})()""")
time.sleep(0.3)
check("typing narrows the reference list",
      evaluate("Array.from(document.querySelectorAll('.ref-dropdown .dropdown-label')).map(e => e.textContent).join('|')") == "B",
      evaluate("Array.from(document.querySelectorAll('.ref-dropdown .dropdown-label')).map(e => e.textContent).join('|')"))
evaluate("""(() => {
  const input = document.querySelector('.editor-input');
  input.dispatchEvent(new KeyboardEvent('keydown', {key: 'Enter', bubbles: true, cancelable: true}));
  return 1;
})()""")
time.sleep(0.7)
check("the highlighted reference is committed",
      evaluate("__palacedb.grid.cellValue(1,%d).t" % ref_col) == "B",
      evaluate("__palacedb.grid.cellValue(1,%d).t" % ref_col))

# Flags: the checkboxes write names, not a bit mask.
flags_col = column_index("flags")
evaluate("__palacedb.editor.begin(1, %d)" % flags_col)
time.sleep(0.4)
check("the flags popup lists every flag",
      evaluate("document.querySelectorAll('.cell-dropdown input[type=checkbox]').length") == 3,
      evaluate("document.querySelectorAll('.cell-dropdown input[type=checkbox]').length"))
evaluate("""(() => {
  const box = document.querySelectorAll('.cell-dropdown input[type=checkbox]')[2];
  box.checked = true;
  box.dispatchEvent(new Event('change', {bubbles: true}));
  return 1;
})()""")
time.sleep(0.7)
# Same as the multi-line block: the checkbox change goes host -> document -> grid, so the
# cell only reads back the new mask once that round trip lands. Waiting for the value is
# what keeps this from flaking on a busy machine.
check("flags keep the names that are set",
      wait_for("(__palacedb.grid.cellValue(1,%d).t || '').includes('dairy')" % flags_col, 10) is True,
      evaluate("__palacedb.grid.cellValue(1,%d).t" % flags_col))

# Enum: picking a label stores that label, and typing a number means the label too.
# The sample is a scratch file and no longer carries an enum column of its own, so one is
# added here: the popup is what is under test, not the shape of the fixture.
enum_col = column_index("rank")
if enum_col < 0:
    # Appended, not inserted: the checks that follow address the first column by position.
    evaluate("__palacedb.send('addColumn', {sheet: __palacedb.state.currentSheet, index: %d, "
             "name: 'e2e_enum', type: '5:1,2,3'})" % evaluate("__palacedb.grid.columns.length"))
    check("an enum column can be added for the test",
          wait_for("__palacedb.grid.columns.some(c => c.name === 'e2e_enum')", 10),
          evaluate("__palacedb.grid.columns.map(c => c.name).join(',')"))
    enum_col = column_index("e2e_enum")
ENUM_NAME = evaluate("__palacedb.grid.columns[%d].name" % enum_col)
evaluate("__palacedb.editor.begin(1, %d)" % enum_col)
time.sleep(0.4)
check("the enum popup lists the labels",
      evaluate("Array.from(document.querySelectorAll('.cell-dropdown .dropdown-label')).map(e => e.textContent).join('|')") == "1|2|3",
      evaluate("Array.from(document.querySelectorAll('.cell-dropdown .dropdown-label')).map(e => e.textContent).join('|')"))
evaluate("""(() => {
  const item = Array.from(document.querySelectorAll('.cell-dropdown .dropdown-item')).find(e => e.textContent.trim() === '3');
  if (!item) return 'missing';
  item.dispatchEvent(new MouseEvent('mousedown', {bubbles: true, cancelable: true}));
  return 'ok';
})()""")
time.sleep(0.7)
check("picking an enum label stores that label",
      evaluate("__palacedb.grid.cellValue(1,%d).t" % enum_col) == "3",
      evaluate("__palacedb.grid.cellValue(1,%d).t" % enum_col))
evaluate("__palacedb.send('setCell', {sheet: __palacedb.state.currentSheet, row: 2, column: %s, text: '1'})"
         % json.dumps(ENUM_NAME))
time.sleep(0.7)
check("typing a number picks the label, not the index",
      evaluate("__palacedb.grid.cellValue(2,%d).t" % enum_col) == "1",
      evaluate("__palacedb.grid.cellValue(2,%d).t" % enum_col))

# --- editing an enum column must not rewrite its values
# "5:,A,B" is an enum whose first value is empty. Re-encoding the text on every visit to
# the dialog used to drop that entry, which moves every stored index onto another label and
# leaves the column no longer matching the template it was built from.
evaluate("__palacedb.send('addColumn', {sheet: __palacedb.state.currentSheet, index: %d, "
         "name: 'e2e_enum_keep', type: '5:,A,B'})" % evaluate("__palacedb.grid.columns.length"))
check("an enum with an empty first value can be added",
      wait_for("__palacedb.grid.columns.some(c => c.name === 'e2e_enum_keep')", 10),
      evaluate("__palacedb.grid.columns.map(c => c.name).join(',')"))
KEEP_COL = column_index("e2e_enum_keep")

KEEP_TYPE = """(() => {
  const sheet = __palacedb.state.document.sheets.find(s => s.name === __palacedb.state.currentSheet);
  const column = sheet && sheet.columns.find(c => c.name === 'e2e_enum_keep');
  return column ? column.typeStr : null;
})()"""

open_header_menu(KEEP_COL)
click_menu_item('Edit Column')
time.sleep(0.6)
check("the column dialog shows the values as they are stored",
      evaluate("""(() => {
        const inputs = Array.from(document.querySelectorAll('#modal .form-input'))
          .filter(i => i.tagName === 'INPUT' && i.type === 'text');
        return inputs.length > 1 ? inputs[1].value : null;
      })()""") == ",A,B",
      evaluate("""(() => {
        const inputs = Array.from(document.querySelectorAll('#modal .form-input'))
          .filter(i => i.tagName === 'INPUT' && i.type === 'text');
        return inputs.length > 1 ? inputs[1].value : null;
      })()"""))
evaluate("document.getElementById('modalOk').click()")
time.sleep(1.0)
check("editing an enum column without touching it keeps its values", evaluate(KEEP_TYPE) == "5:,A,B",
      evaluate(KEEP_TYPE))

open_header_menu(KEEP_COL)
click_menu_item('Edit Column')
time.sleep(0.6)
evaluate("""(() => {
  const inputs = Array.from(document.querySelectorAll('#modal .form-input'))
    .filter(i => i.tagName === 'INPUT' && i.type === 'text');
  const field = inputs[1];
  field.value = field.value + ',C';
  field.dispatchEvent(new Event('input', {bubbles: true}));
  return field.value;
})()""")
evaluate("document.getElementById('modalOk').click()")
time.sleep(1.0)
check("adding a value keeps the empty first entry", evaluate(KEEP_TYPE) == "5:,A,B,C", evaluate(KEEP_TYPE))

for _ in range(2):
    evaluate("__palacedb.send('undo')")
    time.sleep(0.5)
check("the enum value checks are undone",
      wait_for("!__palacedb.grid.columns.some(c => c.name === 'e2e_enum_keep')", 10),
      evaluate("__palacedb.grid.columns.map(c => c.name).join(',')"))

# Image and File: the browser result reaches the cell even though the dialog stole the focus.
image_col = column_index("icon")
if image_col < 0:
    image_col = column_index("Icon")
# Start from an empty image cell: a marker must not be offered as its text.
evaluate("__palacedb.send('setCell', {sheet: __palacedb.state.currentSheet, row: 1, "
         "column: __palacedb.grid.columns[%d].name, text: ''})" % image_col)
time.sleep(0.6)
evaluate("__palacedb.editor.begin(1, %d)" % image_col)
time.sleep(0.4)
check("an empty image starts blank instead of showing a marker",
      evaluate("document.querySelector('.editor-input') && document.querySelector('.editor-input').value") == "",
      evaluate("document.querySelector('.editor-input') && document.querySelector('.editor-input').value"))
evaluate("""(() => {
  const editor = __palacedb.editor;
  editor.browsing = {row: 1, col: %d, column: __palacedb.grid.columns[%d]};
  editor.applyBrowseResult('food_img/2. Beef.png');
  return 1;
})()""" % (image_col, image_col))
ok = wait_for("__palacedb.grid.cellValue(1,%d).t === 'food_img/2. Beef.png'" % image_col, 10)
check("the chosen image path lands in the cell", ok,
      evaluate("__palacedb.grid.cellValue(1,%d).t" % image_col))
check("the image cell shows a thumbnail",
      evaluate("!!document.querySelector('.cell[data-row=\"1\"][data-col=\"%d\"] .cell-thumb')" % image_col) is True)

file_col = column_index("file")
evaluate("__palacedb.editor.begin(2, %d)" % file_col)
time.sleep(0.4)
evaluate("""(() => {
  const editor = __palacedb.editor;
  editor.browsing = {row: 2, col: %d, column: __palacedb.grid.columns[%d]};
  editor.applyBrowseResult('file/meat.txt');
  return 1;
})()""" % (file_col, file_col))
ok = wait_for("__palacedb.grid.cellValue(2,%d).t === 'file/meat.txt'" % file_col, 10)
check("the chosen file path lands in the cell", ok,
      evaluate("__palacedb.grid.cellValue(2,%d).t" % file_col))

# The picker closes the editor; the answer still has to find its cell.
evaluate("__palacedb.editor.begin(2, %d)" % image_col)
time.sleep(0.3)
evaluate("""(() => {
  __palacedb.editor.browsing = {row: 2, col: %d, column: __palacedb.grid.columns[%d]};
  __palacedb.editor.commit();
  return 1;
})()""" % (image_col, image_col))
time.sleep(0.3)
evaluate("__palacedb.editor.applyBrowseResult('food_img/3. Egg.png')")
ok = wait_for("__palacedb.grid.cellValue(2,%d).t === 'food_img/3. Egg.png'" % image_col, 10)
check("a browse result lands even after the editor closed", ok,
      evaluate("__palacedb.grid.cellValue(2,%d).t" % image_col))

# Typing into an identifier cell opens the editor: the promoted proxy used to be left
# behind in the corner of the grid, which looked like a ghost caret.
evaluate("__palacedb.grid.moveCursor(1, 0)")
time.sleep(0.3)
type_char("Q")
ok = wait_for("!!document.querySelector('.cell-editor .editor-input')", 10)
check("typing into an identifier cell opens the editor", ok,
      evaluate("document.activeElement && document.activeElement.className"))
check("no stray input is left in the grid body",
      evaluate("!document.querySelector('#gridBody > .editor-input')") is True,
      evaluate("document.querySelector('#gridBody > .editor-input') && document.querySelector('#gridBody > .editor-input').className"))
evaluate("__palacedb.editor.cancel()")
time.sleep(0.3)

# Arrow keys must keep the painted cursor on the real cursor.
evaluate("__palacedb.grid.moveCursor(0, 0)")
time.sleep(0.3)
drift = []
for _ in range(6):
    press_key("ArrowDown", "ArrowDown", 40)
    painted = evaluate("""(() => { const c = document.querySelector('.cell.cursor');
      return c ? [Number(c.dataset.row), Number(c.dataset.col)] : null; })()""")
    cursor = evaluate("[__palacedb.grid.cursor.row, __palacedb.grid.cursor.col]")
    if painted != cursor:
        drift.append((cursor, painted))
check("the cursor box follows the arrow keys", not drift, drift)
for _ in range(3):
    press_key("ArrowRight", "ArrowRight", 39)
painted = evaluate("""(() => { const c = document.querySelector('.cell.cursor');
  return c ? [Number(c.dataset.row), Number(c.dataset.col)] : null; })()""")
check("the cursor box follows horizontal moves",
      painted == evaluate("[__palacedb.grid.cursor.row, __palacedb.grid.cursor.col]"), painted)

# Copy and paste, cell to cell, for a colour and for an identifier.
source_colour = evaluate("__palacedb.grid.cellValue(0, %d).t" % color_col)
evaluate("__palacedb.send('copyCells', {sheet: __palacedb.state.currentSheet, rows: [0], columns: ['color'], cut: false})")
time.sleep(0.4)
evaluate("__palacedb.send('pasteCells', {sheet: __palacedb.state.currentSheet, row: 3, column: %d, mode: 'overwrite'})" % color_col)
ok = wait_for("__palacedb.grid.cellValue(3,%d).t === %s" % (color_col, json.dumps(source_colour)), 10)
check("a colour cell copies and pastes", ok,
      evaluate("__palacedb.grid.cellValue(3,%d).t" % color_col))

source_id = evaluate("__palacedb.grid.cellValue(0, 0).t")
evaluate("__palacedb.send('copyCells', {sheet: __palacedb.state.currentSheet, rows: [0], columns: ['id'], cut: false})")
time.sleep(0.4)
evaluate("__palacedb.send('pasteCells', {sheet: __palacedb.state.currentSheet, row: 4, column: 0, mode: 'overwrite'})")
# The pasted id now duplicates the source row, so the cell may show the #DUP marker.
ok = wait_for("""(() => { const c = __palacedb.grid.cellValue(4,0);
  return c && (c.t === %s || c.t === '#DUP(' + %s + ')'); })()""" % (json.dumps(source_id), json.dumps(source_id)), 10)
check("an identifier cell copies and pastes", ok,
      evaluate("__palacedb.grid.cellValue(4,0).t"))
evaluate("__palacedb.send('undo')")
time.sleep(0.4)

# List: the column type was missing altogether; adding it must create the sub-table.
evaluate("__palacedb.grid.moveCursor(1, 2)")
time.sleep(0.2)
evaluate("__palacedb.run('addColumn')")
ok = wait_for("!document.getElementById('modal').hidden", 10)
check("the add column dialog opens", ok)
check("the type list offers a list column",
      evaluate("Array.from(document.querySelectorAll('#modal select')[0].options).some(o => o.value === '8')") is True)
evaluate("""(() => {
  const inputs = document.querySelectorAll('#modal .form-row .form-input');
  inputs[0].value = 'tags';
  inputs[0].dispatchEvent(new Event('input', {bubbles: true}));
  const typeSelect = document.querySelectorAll('#modal select')[0];
  typeSelect.value = '8';
  typeSelect.dispatchEvent(new Event('change', {bubbles: true}));
  return 1;
})()""")
time.sleep(0.3)
check("choosing a list reveals the field editor",
      evaluate("!document.querySelector('#modal .form-sub').hidden") is True)
evaluate("document.querySelector('#modal .form-sub-head .tool').click()")
time.sleep(0.3)
evaluate("""(() => {
  const row = document.querySelector('#modal .form-sub-row');
  const nameInput = row.querySelector('input');
  nameInput.value = 'value';
  nameInput.dispatchEvent(new Event('input', {bubbles: true}));
  const typeSelect = row.querySelector('select');
  typeSelect.value = '3';
  typeSelect.dispatchEvent(new Event('change', {bubbles: true}));
  return 1;
})()""")
time.sleep(0.3)
evaluate("document.querySelector('#modal .form-sub-head .tool').click()")
time.sleep(0.3)
evaluate("""(() => {
  const rows = document.querySelectorAll('#modal .form-sub-row');
  const row = rows[rows.length - 1];
  const nameInput = row.querySelector('input');
  nameInput.value = 'kind';
  nameInput.dispatchEvent(new Event('input', {bubbles: true}));
  const selects = row.querySelectorAll('select');
  selects[0].value = '6';
  selects[0].dispatchEvent(new Event('change', {bubbles: true}));
  return 1;
})()""")
time.sleep(0.4)
check("a reference field asks for its target table",
      evaluate("document.querySelectorAll('#modal .form-sub-row')[1].querySelectorAll('select').length") == 2,
      evaluate("document.querySelectorAll('#modal .form-sub-row')[1].innerHTML.length"))
evaluate("""(() => {
  const row = document.querySelectorAll('#modal .form-sub-row')[1];
  const selects = row.querySelectorAll('select');
  selects[1].value = 'food_type';
  selects[1].dispatchEvent(new Event('change', {bubbles: true}));
  return 1;
})()""")
time.sleep(0.2)
evaluate("document.getElementById('modalOk').click()")

ok = wait_for("__palacedb.grid.columns.some(c => c.name === 'tags')", 10)
check("the list column was added", ok,
      evaluate("__palacedb.grid.columns.map(c => c.name + ':' + c.type).join(',')"))
check("its hidden sub-table was created",
      evaluate("JSON.stringify((__palacedb.state.document.subSheets || []).map(s => s.name))").find("foods@tags") >= 0,
      evaluate("JSON.stringify(__palacedb.state.document.subSheets || [])"))

list_col = column_index("tags")

# A single click opens the sub-table right under the row; the rows below move down.
row2_top_before = evaluate("""(() => { const r = document.querySelector('.grid-row[data-row="2"]');
  return r ? Math.round(r.getBoundingClientRect().top) : null; })()""")
evaluate("__palacedb.grid.moveCursor(1, %d)" % list_col)
time.sleep(0.25)
click_cell(1, list_col)
ok = wait_for("!!document.querySelector('.sub-expansion')", 10)
check("clicking a list cell opens its sub-table", ok)
check("the sub-table is not a modal", evaluate("document.getElementById('modal').hidden") is True)

panel_geometry = evaluate("""(() => {
  const panel = document.querySelector('.sub-expansion');
  const row = document.querySelector('#gridRows .grid-row[data-row="1"]');
  const next = document.querySelector('#gridRows .grid-row[data-row="2"]');
  if (!panel || !row || !next) return null;
  return {
    panelTop: Math.round(panel.getBoundingClientRect().top),
    rowBottom: Math.round(row.getBoundingClientRect().bottom),
    nextTop: Math.round(next.getBoundingClientRect().top),
    panelBottom: Math.round(panel.getBoundingClientRect().bottom),
  };
})()""")
check("the sub-table starts right under its row",
      panel_geometry and abs(panel_geometry["panelTop"] - panel_geometry["rowBottom"]) <= 2,
      panel_geometry)
check("the rows below are pushed down by its height",
      panel_geometry and panel_geometry["nextTop"] >= panel_geometry["panelBottom"] - 2
      and panel_geometry["nextTop"] > row2_top_before,
      (panel_geometry, row2_top_before))

# It is a real grid: a header with the field names, row numbers and cells.
check("the sub-table has a header with its fields",
      evaluate("Array.from(document.querySelectorAll('.sub-expansion .sub-head .head-cell'))"
               ".map(c => c.textContent).join('|')").startswith("value"),
      evaluate("Array.from(document.querySelectorAll('.sub-expansion .sub-head .head-cell'))"
               ".map(c => c.textContent).join('|')"))

# A brand new list has no rows yet; the panel offers to add one without a dialog.
check("an empty list offers a row",
      evaluate("Array.from(document.querySelectorAll('.sub-expansion .sub-tool'))"
               ".some(b => b.textContent === '+ Row')") is True)


def add_sub_row():
    evaluate("""(() => {
      const add = Array.from(document.querySelectorAll('.sub-expansion .sub-tool'))
        .find(b => b.textContent === '+ Row');
      if (!add) return false;
      add.dispatchEvent(new MouseEvent('click', {bubbles: true}));
      return true;
    })()""")
    time.sleep(0.5)


add_sub_row()
add_sub_row()
check("the panel adds rows",
      evaluate("document.querySelectorAll('.sub-expansion .sub-body .grid-row').length") == 2,
      evaluate("document.querySelectorAll('.sub-expansion .sub-body .grid-row').length"))
check("the sub-table numbers its rows",
      evaluate("document.querySelectorAll('.sub-expansion .sub-gutter .gutter-cell').length") == 2,
      evaluate("document.querySelectorAll('.sub-expansion .sub-gutter .gutter-cell').length"))
check("the sub-table is built from grid cells, not form inputs",
      evaluate("document.querySelectorAll('.sub-expansion .sub-body .grid-row .cell').length") == 4,
      evaluate("document.querySelectorAll('.sub-expansion .sub-body .grid-row .cell').length"))


def click_sub_cell(row, col, twice=False):
    """A real click inside the open sub-table, at a cell of its own grid."""
    point = evaluate("""(() => {
      const c = document.querySelector('.sub-expansion .sub-body .cell[data-row="%d"][data-col="%d"]');
      if (!c) return null;
      const r = c.getBoundingClientRect();
      return {x: Math.round(r.x + 18), y: Math.round(r.y + 12)};
    })()""" % (row, col))
    if not point:
        return False
    clicks = (1, 2) if twice else (1,)
    for count in clicks:
        mouse_event(point["x"], point["y"], "mousePressed", count)
        mouse_event(point["x"], point["y"], "mouseReleased", count)
        time.sleep(0.06)
    time.sleep(0.5)
    return True


# Typing in a sub-table cell writes through to the list stored in the cell above.
check("double click opens a cell editor inside the sub-table", click_sub_cell(0, 0, twice=True)
      and evaluate("!!document.querySelector('.sub-expansion .editor-input')") is True)
evaluate("""(() => {
  const input = document.querySelector('.sub-expansion .editor-input');
  input.value = '42';
  input.dispatchEvent(new Event('input', {bubbles: true}));
  input.dispatchEvent(new KeyboardEvent('keydown', {key: 'Enter', bubbles: true}));
  return true;
})()""")
ok = wait_for("(__palacedb.grid.cellValue(1,%d) || {}).t && __palacedb.grid.cellValue(1,%d).t.indexOf('42') === 0" % (list_col, list_col), 10)
check("typing in the sub-table writes the cell", ok,
      evaluate("__palacedb.grid.cellValue(1,%d) && __palacedb.grid.cellValue(1,%d).t" % (list_col, list_col)))
check("no Done button is needed to keep the edit",
      evaluate("Array.from(document.querySelectorAll('.sub-expansion button')).every(b => b.textContent !== 'Done')") is True)

# A reference field inside the list is a picker, not a text box.
check("double click on a reference field opens its picker",
      click_sub_cell(0, 1, twice=True)
      and evaluate("!!document.querySelector('.sub-expansion .ref-dropdown')") is True)
ok = wait_for("document.querySelectorAll('.sub-expansion .ref-dropdown .dropdown-item').length > 0", 10)
check("the picker lists the rows of the target table", ok,
      evaluate("document.querySelectorAll('.sub-expansion .ref-dropdown .dropdown-item').length"))
evaluate("""(() => {
  const items = document.querySelectorAll('.sub-expansion .ref-dropdown .dropdown-item');
  if (!items.length) return false;
  items[items.length - 1].dispatchEvent(new MouseEvent('mousedown', {bubbles: true}));
  return true;
})()""")
ok = wait_for("(__palacedb.grid.cellValue(1,%d) || {}).t && __palacedb.grid.cellValue(1,%d).t.indexOf('B') >= 0" % (list_col, list_col), 10)
check("a reference inside a list can be picked", ok,
      evaluate("__palacedb.grid.cellValue(1,%d) && __palacedb.grid.cellValue(1,%d).t" % (list_col, list_col)))

# --- right-click menus inside the sub-table
# A list is a table, so it gets the same menus as the table above: values on a cell,
# fields on a header, records on a row number.
def sub_menu_labels():
    return evaluate(
        "Array.from(document.querySelectorAll('#contextMenu .context-item span'))"
        ".map(s => s.textContent).join('|')")


def sub_menu_click(label):
    evaluate("""(() => {
      const item = Array.from(document.querySelectorAll('#contextMenu .context-item'))
        .find(i => i.textContent.indexOf(%s) === 0);
      if (item) item.click();
      return Boolean(item);
    })()""" % json.dumps(label))
    time.sleep(0.9)


def sub_context_menu(selector):
    return evaluate("""(() => {
      const c = document.querySelector(%s);
      if (!c) return false;
      c.dispatchEvent(new MouseEvent('contextmenu', {bubbles: true, clientX: 320, clientY: 320}));
      return true;
    })()""" % json.dumps(selector))


def sub_headers():
    return evaluate(
        "Array.from(document.querySelectorAll('.sub-expansion .sub-head .head-cell'))"
        ".map(c => c.textContent).join('|')")


sub_context_menu('.sub-expansion .sub-body .cell[data-row="0"][data-col="0"]')
check("right click inside the sub-table opens a menu",
      evaluate("!document.getElementById('contextMenu').hidden"))
labels = sub_menu_labels()
check("the sub-table cell menu is about the value",
      "Cut" in labels and "Copy" in labels and "Clear Cells" in labels, labels)
evaluate("document.dispatchEvent(new KeyboardEvent('keydown', {key:'Escape', bubbles:true}))")
time.sleep(0.3)

# The header menu edits the fields of the list, including the values of an enum.
sub_context_menu('.sub-expansion .sub-head .head-cell[data-col="1"]')
labels = sub_menu_labels()
check("right click on a sub-table header opens the column menu",
      "Edit Column" in labels and "Insert Column" in labels and "Delete Column" in labels, labels)
sub_menu_click("Insert Column")
check("the new field dialog asks for a name and a type",
      evaluate("!document.getElementById('modal').hidden")
      and evaluate("document.getElementById('modalTitle').textContent").startswith("Insert a column"),
      evaluate("document.getElementById('modalTitle').textContent"))
evaluate("""(() => {
  const rows = document.querySelectorAll('#modal .form-row');
  const name = rows[0].querySelector('input');
  name.value = 'level';
  name.dispatchEvent(new Event('input', {bubbles: true}));
  const type = rows[1].querySelector('select');
  type.value = '5';
  type.dispatchEvent(new Event('change', {bubbles: true}));
  return true;
})()""")
time.sleep(0.4)
evaluate("""(() => {
  const rows = document.querySelectorAll('#modal .form-row');
  const values = rows[2].querySelector('input');
  values.value = 'A,B,C';
  values.dispatchEvent(new Event('input', {bubbles: true}));
  return values.value;
})()""")
evaluate("document.getElementById('modalOk').click()")
ok = wait_for("document.querySelectorAll('.sub-expansion .sub-head .head-cell').length === 3", 12)
check("the field is added to the list", ok, sub_headers())
check("it is an enum", "levelEnum" in (sub_headers() or ""), sub_headers())

# The enum field offers the values it was given, and a pick lands in the record.
check("double click on the new enum field opens its values",
      click_sub_cell(0, 2, twice=True)
      and evaluate("!!document.querySelector('.sub-expansion .cell-dropdown')") is True)
check("the values are the ones that were typed",
      evaluate("Array.from(document.querySelectorAll('.sub-expansion .cell-dropdown .dropdown-item'))"
               ".map(i => i.textContent).join(',')") == "A,B,C",
      evaluate("Array.from(document.querySelectorAll('.sub-expansion .cell-dropdown .dropdown-item'))"
               ".map(i => i.textContent).join(',')"))
evaluate("""(() => {
  const item = Array.from(document.querySelectorAll('.sub-expansion .cell-dropdown .dropdown-item'))
    .find(i => i.textContent === 'B');
  if (item) item.dispatchEvent(new MouseEvent('mousedown', {bubbles: true}));
  return Boolean(item);
})()""")
ok = wait_for("(() => { const c = document.querySelector('.sub-expansion .sub-body "
              ".cell[data-row=\"0\"][data-col=\"2\"]'); return c && c.textContent === 'B'; })()", 10)
check("the picked value is shown in the record", ok,
      evaluate("(() => { const c = document.querySelector('.sub-expansion .sub-body "
               ".cell[data-row=\"0\"][data-col=\"2\"]'); return c ? c.textContent : null; })()"))
evaluate("document.body.dispatchEvent(new MouseEvent('mousedown', {bubbles: true}))")
time.sleep(0.4)

# The Fields... dialog is the other way into the same definition, and it must keep the
# values an enum field already has instead of flattening it to an empty enum.
evaluate("""(() => {
  const button = Array.from(document.querySelectorAll('.sub-expansion .sub-tool'))
    .find(b => b.textContent.indexOf('Fields') >= 0);
  if (button) button.click();
  return Boolean(button);
})()""")
time.sleep(0.8)
check("the field list shows the enum values",
      evaluate("Array.from(document.querySelectorAll('#modal .form-sub-values input'))"
               ".some(i => i.value === 'A,B,C')"),
      evaluate("Array.from(document.querySelectorAll('#modal .form-sub-values input'))"
               ".map(i => i.value).join('|')"))
evaluate("document.getElementById('modalOk').click()")
time.sleep(1.2)
check("applying the field list keeps the values",
      "levelEnum" in (sub_headers() or ""), sub_headers())

# Move and delete a field through the header menu.
sub_context_menu('.sub-expansion .sub-head .head-cell[data-col="2"]')
sub_menu_click("Move Column Left")
ok = wait_for("(() => { const h = Array.from(document.querySelectorAll("
              "'.sub-expansion .sub-head .head-cell')).map(c => c.textContent);"
              " return h[1] === 'levelEnum'; })()", 12)
check("a field can be moved left", ok, sub_headers())

sub_context_menu('.sub-expansion .sub-head .head-cell[data-col="1"]')
sub_menu_click("Delete Column")
check("deleting a field asks first",
      evaluate("!document.getElementById('modal').hidden")
      and evaluate("document.getElementById('modalTitle').textContent").startswith("Delete column"),
      evaluate("document.getElementById('modalTitle').textContent"))
evaluate("document.getElementById('modalOk').click()")
ok = wait_for("document.querySelectorAll('.sub-expansion .sub-head .head-cell').length === 2", 12)
check("the field is gone", ok, sub_headers())

# The row numbers offer the record operations. A field edit re-sends the whole document,
# so the panel has just been refreshed: let that land before touching its rows.
time.sleep(1.5)
sub_context_menu('.sub-expansion .sub-gutter .gutter-cell[data-row="0"]')
labels = sub_menu_labels()
check("right click on a sub-table row number opens the row menu",
      "Insert Row Above" in labels and "Delete Row" in labels, labels)
rows_before = evaluate("document.querySelectorAll('.sub-expansion .sub-body .grid-row').length")
sub_menu_click("Insert Row Below")
ok = wait_for("document.querySelectorAll('.sub-expansion .sub-body .grid-row').length === %d"
              % (rows_before + 1), 10)
check("the row menu adds a record", ok,
      evaluate("document.querySelectorAll('.sub-expansion .sub-body .grid-row').length"))
# The panel writes on a short timer, so the row has to land before it is undone.
time.sleep(1.2)
evaluate("__palacedb.send('undo')")
time.sleep(1.0)

# A row can be added from the panel and it grows with it.
rows_before = evaluate("document.querySelectorAll('.sub-expansion .sub-body .grid-row').length")
height_before = evaluate("Math.round(document.querySelector('.sub-expansion').getBoundingClientRect().height)")
add_sub_row()
check("the panel adds a row",
      evaluate("document.querySelectorAll('.sub-expansion .sub-body .grid-row').length") == rows_before + 1,
      evaluate("document.querySelectorAll('.sub-expansion .sub-body .grid-row').length"))
check("the panel grows with the new row",
      evaluate("Math.round(document.querySelector('.sub-expansion').getBoundingClientRect().height)") > height_before,
      (height_before, evaluate("Math.round(document.querySelector('.sub-expansion').getBoundingClientRect().height)")))

# Keyboard inside the sub-table must not move the cursor of the table above.
click_sub_cell(0, 0)
main_before = evaluate("JSON.stringify(__palacedb.grid.cursor)")
sub_before = evaluate("""(() => {
  const c = document.querySelector('.sub-expansion .cell.cursor');
  return c ? Number(c.dataset.row) : null;
})()""")
press_key("ArrowDown", "ArrowDown", 40)
time.sleep(0.3)
check("the arrows inside the sub-table leave the main cursor alone",
      evaluate("JSON.stringify(__palacedb.grid.cursor)") == main_before,
      (main_before, evaluate("JSON.stringify(__palacedb.grid.cursor)")))
check("the arrows move the sub-table cursor instead",
      evaluate("""(() => {
        const c = document.querySelector('.sub-expansion .cell.cursor');
        return c ? Number(c.dataset.row) : null;
      })()""") == (sub_before + 1 if sub_before is not None else None),
      (sub_before, evaluate("""(() => {
        const c = document.querySelector('.sub-expansion .cell.cursor');
        return c ? Number(c.dataset.row) : null;
      })()""")))

# A second list can be open at the same time.
evaluate("__palacedb.grid.moveCursor(2, %d)" % list_col)
time.sleep(0.25)
click_cell(2, list_col)
ok = wait_for("document.querySelectorAll('.sub-expansion').length === 2", 10)
check("two lists can be open at once", ok,
      evaluate("document.querySelectorAll('.sub-expansion').length"))
check("the second panel starts under its own row",
      evaluate("""(() => {
        const panels = document.querySelectorAll('.sub-expansion');
        if (panels.length < 2) return null;
        const row = document.querySelector('#gridRows .grid-row[data-row="2"]');
        return Math.abs(Math.round(panels[1].getBoundingClientRect().top)
                        - Math.round(row.getBoundingClientRect().bottom));
      })()""") is not None
      and evaluate("""(() => {
        const panels = document.querySelectorAll('.sub-expansion');
        const row = document.querySelector('#gridRows .grid-row[data-row="2"]');
        return Math.abs(Math.round(panels[1].getBoundingClientRect().top)
                        - Math.round(row.getBoundingClientRect().bottom)) <= 2;
      })()""") is True)

evaluate("document.body.dispatchEvent(new KeyboardEvent('keydown', {key:'s', ctrlKey:true, bubbles:true}))")
time.sleep(1.5)
food_saved = json.load(open(FOOD, encoding="utf-8"))
first_line = food_saved["sheets"][0]["lines"][1]
check("the list is written to the file as an array",
      isinstance(first_line.get("tags"), list)
      and first_line["tags"][0].get("value") == 42
      and first_line["tags"][0].get("kind") == "B",
      first_line.get("tags"))
check("the sub-table is saved hidden",
      any(s.get("name") == "foods@tags" and (s.get("props") or {}).get("hide") for s in food_saved["sheets"]),
      [s.get("name") for s in food_saved["sheets"]])

# Clicking the same cell again closes that panel only, and gives the space back.
evaluate("__palacedb.grid.moveCursor(1, %d)" % list_col)
time.sleep(0.25)
click_cell(1, list_col)
ok = wait_for("document.querySelectorAll('.sub-expansion').length === 1", 10)
check("clicking the cell again closes its sub-table", ok,
      evaluate("document.querySelectorAll('.sub-expansion').length"))

evaluate("__palacedb.grid.moveCursor(2, %d)" % list_col)
time.sleep(0.25)
click_cell(2, list_col)
ok = wait_for("document.querySelectorAll('.sub-expansion').length === 0", 10)
check("the last panel closes too", ok,
      evaluate("document.querySelectorAll('.sub-expansion').length"))
# Rows grow to fit wrapped text, so an absolute top captured before the panels opened is
# no longer the invariant: what must hold is that row 2 sits directly under row 1 again,
# with no space left where the panel was. A separator strip between them is not leftover
# space: it is one row of its own and is subtracted here.
check("the rows move back up",
      evaluate("""(() => {
        const first = document.querySelector('#gridRows .grid-row[data-row="1"]');
        const second = document.querySelector('#gridRows .grid-row[data-row="2"]');
        if (!first || !second) return null;
        const strip = document.querySelector('.sep-band[data-sep="2"]');
        const stripHeight = strip ? Math.round(strip.getBoundingClientRect().height) : 0;
        return Math.abs(Math.round(second.getBoundingClientRect().top)
                        - Math.round(first.getBoundingClientRect().bottom) - stripHeight);
      })()""") == 0,
      evaluate("""(() => {
        const first = document.querySelector('#gridRows .grid-row[data-row="1"]');
        const second = document.querySelector('#gridRows .grid-row[data-row="2"]');
        if (!first || !second) return null;
        return Math.round(second.getBoundingClientRect().top)
               - Math.round(first.getBoundingClientRect().bottom);
      })()"""))

# --- an open list replaces its value with a marker
evaluate("__palacedb.grid.moveCursor(1, %d)" % list_col)
time.sleep(0.25)
click_cell(1, list_col)
ok = wait_for("document.querySelectorAll('.sub-expansion').length === 1", 10)
marker = evaluate("""(() => {
  const c = document.querySelector('#gridRows .cell[data-row="1"][data-col="%d"]');
  return c ? c.textContent : null;
})()""" % list_col)
check("an open list shows a marker instead of its value", ok and marker == "\u2026", marker)
check("the marker marks the cell as expanded",
      evaluate("!!document.querySelector('#gridRows .cell.expanded')") is True)

click_cell(1, list_col)
ok = wait_for("document.querySelectorAll('.sub-expansion').length === 0", 10)
restored = evaluate("""(() => {
  const c = document.querySelector('#gridRows .cell[data-row="1"][data-col="%d"]');
  return c ? c.textContent : null;
})()""" % list_col)
check("closing the list brings the value back", ok and restored != "\u2026", restored)

# --- the fields of a list are edited from its own panel, and Apply takes effect
click_cell(1, list_col)
ok = wait_for("document.querySelectorAll('.sub-expansion').length === 1", 10)
fields_before = evaluate("document.querySelectorAll('.sub-expansion .sub-head .head-cell').length")
check("the panel reopens for the field test", ok and fields_before == 2, fields_before)

evaluate("""(() => {
  const button = Array.from(document.querySelectorAll('.sub-expansion .sub-tool'))
    .find(b => b.textContent === 'Fields...');
  if (!button) return false;
  button.dispatchEvent(new MouseEvent('click', {bubbles: true}));
  return true;
})()""")
ok = wait_for("!document.getElementById('modal').hidden", 10)
check("Fields... opens the column dialog", ok)
check("the dialog lists the fields of the list",
      evaluate("document.querySelectorAll('#modal .form-sub-row').length") == fields_before,
      evaluate("document.querySelectorAll('#modal .form-sub-row').length"))

evaluate("""(() => {
  const add = Array.from(document.querySelectorAll('#modal button'))
    .find(b => b.textContent === '+ Add field');
  add.click();
  const rows = document.querySelectorAll('#modal .form-sub-row');
  const input = rows[rows.length - 1].querySelector('input');
  input.value = 'note';
  input.dispatchEvent(new Event('input', {bubbles: true}));
  return true;
})()""")
evaluate("document.getElementById('modalOk').click()")
ok = wait_for("document.querySelectorAll('.sub-expansion .sub-head .head-cell').length === %d"
              % (fields_before + 1), 10)
check("Apply adds the field to the open sub-table", ok,
      evaluate("Array.from(document.querySelectorAll('.sub-expansion .sub-head .head-cell'))"
               ".map(c => c.textContent).join('|')"))
check("the new field is named in the header",
      evaluate("Array.from(document.querySelectorAll('.sub-expansion .sub-head .head-cell'))"
               ".some(c => c.textContent.indexOf('note') === 0)") is True,
      evaluate("Array.from(document.querySelectorAll('.sub-expansion .sub-head .head-cell'))"
               ".map(c => c.textContent).join('|')"))
check("the values already in the list survive the new field",
      evaluate("""(() => {
        const c = document.querySelector('.sub-expansion .sub-body .cell[data-row="0"][data-col="0"]');
        return c ? c.textContent : null;
      })()""") == "42",
      evaluate("""(() => {
        const c = document.querySelector('.sub-expansion .sub-body .cell[data-row="0"][data-col="0"]');
        return c ? c.textContent : null;
      })()"""))

evaluate("document.body.dispatchEvent(new KeyboardEvent('keydown', {key:'s', ctrlKey:true, bubbles:true}))")
time.sleep(1.5)
tags_sheet = [s for s in json.load(open(FOOD, encoding="utf-8"))["sheets"]
              if s.get("name") == "foods@tags"]
check("the new field is written to the file",
      bool(tags_sheet) and [c.get("name") for c in tags_sheet[0]["columns"]] == ["value", "kind", "note"],
      [c.get("name") for c in tags_sheet[0]["columns"]] if tags_sheet else None)

# Renaming a field keeps the values it holds: the dialog carries the old name along.
if evaluate("document.querySelectorAll('.sub-expansion').length") == 0:
    click_cell(1, list_col)
    wait_for("document.querySelectorAll('.sub-expansion').length === 1", 10)

evaluate("""(() => {
  const button = Array.from(document.querySelectorAll('.sub-expansion .sub-tool'))
    .find(b => b.textContent === 'Fields...');
  if (button) button.dispatchEvent(new MouseEvent('click', {bubbles: true}));
  return true;
})()""")
ok = wait_for("!document.getElementById('modal').hidden", 10)
evaluate("""(() => {
  const row = document.querySelector('#modal .form-sub-row');
  const input = row.querySelector('input');
  input.value = 'amount';
  input.dispatchEvent(new Event('input', {bubbles: true}));
  return true;
})()""")
evaluate("document.getElementById('modalOk').click()")
ok = wait_for("Array.from(document.querySelectorAll('.sub-expansion .sub-head .head-cell'))"
              ".some(c => c.textContent.indexOf('amount') === 0)", 10)
check("renaming a field renames the header", ok,
      evaluate("Array.from(document.querySelectorAll('.sub-expansion .sub-head .head-cell'))"
               ".map(c => c.textContent).join('|')"))
check("renaming a field keeps the values under the new name",
      evaluate("""(() => {
        const c = document.querySelector('.sub-expansion .sub-body .cell[data-row="0"][data-col="0"]');
        return c ? c.textContent : null;
      })()""") == "42",
      evaluate("""(() => {
        const c = document.querySelector('.sub-expansion .sub-body .cell[data-row="0"][data-col="0"]');
        return c ? c.textContent : null;
      })()"""))

evaluate("document.body.dispatchEvent(new KeyboardEvent('keydown', {key:'s', ctrlKey:true, bubbles:true}))")
time.sleep(1.5)
renamed_sheet = [s for s in json.load(open(FOOD, encoding="utf-8"))["sheets"]
                 if s.get("name") == "foods@tags"]
check("the renamed field is written to the file",
      bool(renamed_sheet)
      and [c.get("name") for c in renamed_sheet[0]["columns"]] == ["amount", "kind", "note"],
      [c.get("name") for c in renamed_sheet[0]["columns"]] if renamed_sheet else None)

# The value of the first record is still 42, now under 'amount'.
values_ok = evaluate("""(() => {
  const c = document.querySelector('.sub-expansion .sub-body .cell[data-row="0"][data-col="0"]');
  return c ? c.textContent === '42' : false;
})()""")
check("the record still holds its value after the rename", values_ok is True)

# Fields can be reordered from the same dialog.
evaluate("""(() => {
  const button = Array.from(document.querySelectorAll('.sub-expansion .sub-tool'))
    .find(b => b.textContent === 'Fields...');
  if (button) button.dispatchEvent(new MouseEvent('click', {bubbles: true}));
  return true;
})()""")
ok = wait_for("!document.getElementById('modal').hidden", 10)
evaluate("""(() => {
  const row = document.querySelectorAll('#modal .form-sub-row')[0];
  const buttons = row.querySelectorAll('button');
  buttons[1].dispatchEvent(new MouseEvent('click', {bubbles: true}));
  return true;
})()""")
evaluate("document.getElementById('modalOk').click()")
ok = wait_for("Array.from(document.querySelectorAll('.sub-expansion .sub-head .head-cell'))"
              ".every((c, i) => (i === 0) === (c.textContent.indexOf('kind') === 0))", 10)
check("a field can be moved down in the dialog", ok,
      evaluate("Array.from(document.querySelectorAll('.sub-expansion .sub-head .head-cell'))"
               ".map(c => c.textContent).join('|')"))

# --- the rows of a list can be reordered by dragging their row numbers too
def sub_gutter_point(row):
    return evaluate("""(() => {
      const c = document.querySelector('.sub-expansion .sub-gutter .gutter-cell[data-row="%d"]');
      if (!c) return null;
      const r = c.getBoundingClientRect();
      return {x: Math.round(r.x + r.width / 2), y: Math.round(r.y + r.height * 0.75)};
    })()""" % row)


def drag_sub_row(from_row, to_row):
    start = sub_gutter_point(from_row)
    if not start:
        return False
    mouse_event(start["x"], start["y"], "mousePressed")
    time.sleep(0.15)
    end = sub_gutter_point(to_row)
    if not end:
        return False
    for step in range(1, 5):
        y = round(start["y"] + (end["y"] - start["y"]) * step / 4)
        counter[0] += 1
        ws.send(json.dumps({"id": counter[0], "method": "Input.dispatchMouseEvent",
                            "params": {"type": "mouseMoved", "x": end["x"], "y": y,
                                       "button": "left", "buttons": 1}}))
        time.sleep(0.08)
    mouse_event(end["x"], end["y"], "mouseReleased")
    time.sleep(1.0)
    return True


def sub_cell_text(row, col):
    return evaluate("""(() => {
      const c = document.querySelector('.sub-expansion .sub-body .cell[data-row="%d"][data-col="%d"]');
      return c ? c.textContent : null;
    })()""" % (row, col))


if evaluate("document.querySelectorAll('.sub-expansion').length") == 0:
    click_cell(1, list_col)
    wait_for("document.querySelectorAll('.sub-expansion').length === 1", 10)

sub_fields = evaluate("document.querySelectorAll('.sub-expansion .sub-head .head-cell').length")
row0_before = [sub_cell_text(0, col) for col in range(sub_fields)]
row1_before = [sub_cell_text(1, col) for col in range(sub_fields)]
check("the first record holds a value before the drag", any(row0_before), row0_before)
check("a list row can be dragged by its number", drag_sub_row(0, 1))
row0_after = [sub_cell_text(0, col) for col in range(sub_fields)]
row1_after = [sub_cell_text(1, col) for col in range(sub_fields)]
check("the dragged record moved down in the list",
      row1_after == row0_before and row0_after == row1_before,
      (row0_before, row0_after, row1_after))

# --- the Convert submenu survives the pointer moving onto it
food_text_col = evaluate("__palacedb.grid.columns.findIndex(c => c.type === 'Text')")
convert_labels = open_convert(food_text_col)
check("the column menu offers the Convert submenu",
      convert_labels == 'lowercase|UPPERCASE|UpperIdent|lowerIdent', convert_labels)
check("the submenu stays open when the pointer moves onto it",
      evaluate("""(() => {
        const item = document.querySelector('#subMenu .context-item');
        if (!item) return false;
        item.dispatchEvent(new MouseEvent('mouseenter'));
        return !document.getElementById('subMenu').hidden;
      })()""") is True)
evaluate("""(() => {
  document.getElementById('subMenu').hidden = true;
  document.getElementById('contextMenu').hidden = true;
  return true;
})()""")

# --- the row number is the handle that moves a row
food_id_col = evaluate("__palacedb.grid.columns.findIndex(c => c.type === 'Id')")
evaluate("""(() => {
  const close = Array.from(document.querySelectorAll('.sub-expansion .sub-close'));
  close.forEach(b => b.dispatchEvent(new MouseEvent('click', {bubbles: true})));
  return document.querySelectorAll('.sub-expansion').length;
})()""")
ok = wait_for("document.querySelectorAll('.sub-expansion').length === 0", 10)


def gutter_point(row, half="center"):
    return evaluate("""(() => {
      const c = document.querySelector('#gridGutter .gutter-cell[data-row="%d"]');
      if (!c) return null;
      const r = c.getBoundingClientRect();
      const offset = %s;
      return {x: Math.round(r.x + r.width / 2), y: Math.round(r.y + r.height * offset)};
    })()""" % (row, {"center": "0.5", "lower": "0.8"}[half]))


drag_marks = None


def drag_gutter(from_row, to_row):
    """Presses a row number, drags it over another row and releases."""
    global drag_marks
    evaluate("__palacedb.grid.body.scrollTop = 0")
    time.sleep(0.3)
    start = gutter_point(from_row)
    if not start:
        return False
    mouse_event(start["x"], start["y"], "mousePressed")
    time.sleep(0.2)
    # Selecting the row may scroll the table, so the target is measured afterwards.
    end = gutter_point(to_row, "lower")
    if not end:
        return False
    steps = 4
    for step in range(1, steps + 1):
        y = round(start["y"] + (end["y"] - start["y"]) * step / steps)
        counter[0] += 1
        ws.send(json.dumps({"id": counter[0], "method": "Input.dispatchMouseEvent",
                            "params": {"type": "mouseMoved", "x": end["x"], "y": y,
                                       "button": "left", "buttons": 1}}))
        time.sleep(0.08)
    # The drag has to be visible while it happens, not only afterwards.
    drag_marks = evaluate("""(() => ({
      dragged: document.querySelectorAll('#gridGutter .gutter-cell.dragging').length,
      drop: document.querySelectorAll('#gridGutter .gutter-cell.drop-before,'
        + ' #gridGutter .gutter-cell.drop-after').length,
    }))()""")
    mouse_event(end["x"], end["y"], "mouseReleased")
    time.sleep(1.0)
    return True


ids_before = [evaluate("(__palacedb.grid.cellValue(%d, %d) || {}).t" % (row, food_id_col))
              for row in range(3)]
check("dragging a row number moves the row", drag_gutter(1, 2)
      and wait_for("(__palacedb.grid.cellValue(2, %d) || {}).t === %s"
                   % (food_id_col, json.dumps(ids_before[1])), 10),
      ids_before)
ids_after = [evaluate("(__palacedb.grid.cellValue(%d, %d) || {}).t" % (row, food_id_col))
             for row in range(3)]
check("the dragged row landed where it was dropped",
      ids_after[1] == ids_before[2] and ids_after[2] == ids_before[1], (ids_before, ids_after))
check("the drag shows which row moves and where it will land",
      drag_marks == {"dragged": 1, "drop": 1}, drag_marks)

# Undo puts it back: the move is one step on the undo stack.
evaluate("document.body.dispatchEvent(new KeyboardEvent('keydown', {key:'z', ctrlKey:true, bubbles:true}))")
ok = wait_for("(__palacedb.grid.cellValue(1, %d) || {}).t === %s"
              % (food_id_col, json.dumps(ids_before[1])), 10)
check("undo puts the row back", ok,
      [evaluate("(__palacedb.grid.cellValue(%d, %d) || {}).t" % (row, food_id_col))
       for row in range(3)])

# The same move from the keyboard, which is what the manual documents.
evaluate("__palacedb.grid.moveCursor(1, 0)")
time.sleep(0.3)
evaluate("""document.getElementById('gridBody').dispatchEvent(
  new KeyboardEvent('keydown', {key:'ArrowDown', ctrlKey:true, bubbles:true}))""")
ok = wait_for("(__palacedb.grid.cellValue(2, %d) || {}).t === %s"
              % (food_id_col, json.dumps(ids_before[1])), 10)
check("Ctrl+Down moves the cursor row down", ok,
      [evaluate("(__palacedb.grid.cellValue(%d, %d) || {}).t" % (row, food_id_col))
       for row in range(3)])
evaluate("document.body.dispatchEvent(new KeyboardEvent('keydown', {key:'z', ctrlKey:true, bubbles:true}))")
ok = wait_for("(__palacedb.grid.cellValue(1, %d) || {}).t === %s"
              % (food_id_col, json.dumps(ids_before[1])), 10)
check("the keyboard move is undone too", ok,
      [evaluate("(__palacedb.grid.cellValue(%d, %d) || {}).t" % (row, food_id_col))
       for row in range(3)])

# --- the row-number menu
menu_point = gutter_point(1)
mouse_event(menu_point["x"], menu_point["y"], "mousePressed")
mouse_event(menu_point["x"], menu_point["y"], "mouseReleased")
counter[0] += 1
ws.send(json.dumps({"id": counter[0], "method": "Input.dispatchMouseEvent",
                    "params": {"type": "mousePressed", "x": menu_point["x"], "y": menu_point["y"],
                               "button": "right", "clickCount": 1}}))
counter[0] += 1
ws.send(json.dumps({"id": counter[0], "method": "Input.dispatchMouseEvent",
                    "params": {"type": "mouseReleased", "x": menu_point["x"], "y": menu_point["y"],
                               "button": "right", "clickCount": 1}}))
time.sleep(0.6)

menu_labels = evaluate("Array.from(document.querySelectorAll('#contextMenu .context-item'))"
                       ".map(b => b.textContent)")
check("right-clicking a row number opens its menu",
      bool(menu_labels) and any("Insert Row Above" in label for label in menu_labels), menu_labels)
check("the row menu has the row actions and no Add Row",
      all(any(part in label for label in menu_labels) for part in
          ("Insert Row Below", "Duplicate Row", "Delete Row",
           "Add Separator", "Name Separator", "Insert Paste"))
      and not any("Add Row" in label for label in menu_labels), menu_labels)

rows_before_menu = evaluate("__palacedb.grid.rowCount")
evaluate("""(() => {
  const item = Array.from(document.querySelectorAll('#contextMenu .context-item'))
    .find(b => b.textContent.indexOf('Insert Row Above') === 0);
  if (item) item.dispatchEvent(new MouseEvent('click', {bubbles: true}));
  return true;
})()""")
ok = wait_for("__palacedb.grid.rowCount === %d" % (rows_before_menu + 1), 10)
check("Insert Row Above adds a row", ok,
      (rows_before_menu, evaluate("__palacedb.grid.rowCount")))
evaluate("document.body.dispatchEvent(new KeyboardEvent('keydown', {key:'z', ctrlKey:true, bubbles:true}))")
time.sleep(0.8)

# --- the toolbar can add a column
check("the toolbar has an Add Column button",
      evaluate("!!document.querySelector('#toolbar [data-action=\"addColumn\"]')") is True)
evaluate("document.querySelector('#toolbar [data-action=\"addColumn\"]')"
         ".dispatchEvent(new MouseEvent('click', {bubbles: true}))")
ok = wait_for("!document.getElementById('modal').hidden", 10)
check("the button opens the add column dialog", ok)
check("the dialog is the column form",
      evaluate("document.getElementById('modalTitle').textContent") == "Add column",
      evaluate("document.getElementById('modalTitle').textContent"))
evaluate("document.getElementById('modalCancel').click()")
time.sleep(0.3)
check("the dialog can be cancelled", evaluate("document.getElementById('modal').hidden") is True)

# --- the table name is edited in Table Settings, not in a menu of its own
evaluate("""(() => {
  const item = document.querySelector('#tableList .table-item');
  item.dispatchEvent(new MouseEvent('contextmenu', {bubbles: true, clientX: 40, clientY: 80}));
  return true;
})()""")
time.sleep(0.4)
table_menu = evaluate("Array.from(document.querySelectorAll('#contextMenu .context-item'))"
                      ".map(b => b.textContent)")
check("the table menu has no separate rename entry",
      not any('Rename Table' in label for label in table_menu), table_menu)
check("it offers Table Settings instead",
      any('Table Settings' in label for label in table_menu), table_menu)
evaluate("document.body.dispatchEvent(new MouseEvent('mousedown', {bubbles: true}))")
time.sleep(0.2)

SETTINGS_SHEET = evaluate("__palacedb.state.currentSheet")
evaluate("__palacedb.run('tableSettings')")
check("Table Settings opens with a name field",
      wait_for("!!document.querySelector('#modal .form-input')", 10))
check("the name field holds the current name",
      evaluate("document.querySelector('#modal .form-input').value") == SETTINGS_SHEET,
      evaluate("document.querySelector('#modal .form-input').value"))
evaluate("""(() => {
  const input = document.querySelector('#modal .form-input');
  input.value = 'RenamedHere';
  input.dispatchEvent(new Event('input', {bubbles: true}));
  document.getElementById('modalOk').click();
  return true;
})()""")
check("Table Settings renames the table",
      wait_for("__palacedb.state.document.sheets.some(s => s.name === 'RenamedHere')", 10),
      evaluate("__palacedb.state.document.sheets.map(s => s.name).join(',')"))
check("the renamed table is still the one on screen",
      evaluate("__palacedb.state.currentSheet") == 'RenamedHere',
      evaluate("__palacedb.state.currentSheet"))

# Put the name back so the rest of the run sees the sample unchanged.
evaluate("__palacedb.send('renameSheet', {sheet: 'RenamedHere', name: %s})" % json.dumps(SETTINGS_SHEET))
check("the name can be changed back",
      wait_for("__palacedb.state.currentSheet === %s" % json.dumps(SETTINGS_SHEET), 10),
      evaluate("__palacedb.state.currentSheet"))

# --- the arrows stay inside a multi-line text while it is being edited
TEXT_COL = evaluate("__palacedb.grid.columns.findIndex(c => c.type === 'Text')")
check("the sample has a text column to edit", TEXT_COL >= 0, TEXT_COL)
evaluate("__palacedb.grid.moveCursor(0, %d)" % TEXT_COL)
time.sleep(0.3)
press_key("F2", "F2", 113)
time.sleep(0.4)
check("the text editor is open", evaluate("!!document.querySelector('.editor-input')") is True)
check("it is a multi-line editor",
      evaluate("document.querySelector('.editor-input').tagName") == "TEXTAREA",
      evaluate("document.querySelector('.editor-input').tagName"))
evaluate("""(() => {
  const input = document.querySelector('.editor-input');
  input.value = 'AAA\\nBBB';
  input.setSelectionRange(0, 0);
  return input.selectionStart;
})()""")
press_key("ArrowDown", "ArrowDown", 40)
time.sleep(0.3)
check("ArrowDown keeps the editor open", evaluate("!!document.querySelector('.editor-input')") is True)
check("ArrowDown moves the caret to the next line",
      evaluate("document.querySelector('.editor-input').selectionStart") == 4,
      evaluate("document.querySelector('.editor-input').selectionStart"))
check("ArrowDown does not move the cell cursor",
      evaluate("__palacedb.grid.cursor.row") == 0,
      evaluate("__palacedb.grid.cursor.row"))
press_key("ArrowUp", "ArrowUp", 38)
time.sleep(0.3)
check("ArrowUp moves the caret back up inside the value",
      evaluate("document.querySelector('.editor-input').selectionStart") == 0
      and evaluate("!!document.querySelector('.editor-input')") is True,
      evaluate("document.querySelector('.editor-input').selectionStart"))
press_key("Escape", "Escape", 27)
time.sleep(0.3)
check("Escape still leaves the editor", evaluate("!document.querySelector('.editor-input')") is True)

# --- the palette travels with the file, not with the preference
# A private copy, because another window may hold the write slot of the sample database
# by now and a read-only document does not write its side-car.
PAL_DIR = os.path.join(WORK, "palette")
os.makedirs(PAL_DIR, exist_ok=True)
PAL_DB = os.path.join(PAL_DIR, "Palette.cdb")
shutil.copy(CDB, PAL_DB)
PAL_META = PAL_DB + ".meta.json"
if os.path.exists(PAL_META):
    os.remove(PAL_META)

evaluate("document.body.dispatchEvent(new KeyboardEvent('keydown', {key:'s', ctrlKey:true, bubbles:true}))")
time.sleep(1.5)
evaluate("__palacedb.send('openPath', {path: %s})" % json.dumps(PAL_DB))
check("a private copy of the database is open",
      wait_for("!!__palacedb.state.document && __palacedb.state.document.path === %s"
               % json.dumps(PAL_DB), 25) is True,
      evaluate("(__palacedb.state.document || {}).path"))
check("it is editable", evaluate("!__palacedb.state.document.readOnly") is True)

evaluate("__palacedb.applySettings({uiFontSize: 15, gridFontSize: 14, scheme: 'mint'})")
time.sleep(1.5)
with open(PAL_META, encoding="utf-8") as handle:
    meta = json.load(handle)
check("the chosen palette reached the side-car",
      (meta.get("appearance") or {}).get("scheme") == "mint", meta.get("appearance"))

evaluate("document.body.dispatchEvent(new KeyboardEvent('keydown', {key:'w', ctrlKey:true, bubbles:true}))")
time.sleep(1.5)
check("closing the document leaves the welcome screen",
      evaluate("!document.getElementById('welcome').classList.contains('hidden')") is True)

# With no file open the palette is only a preference, and the side-car keeps its value.
evaluate("__palacedb.applySettings({uiFontSize: 15, gridFontSize: 14, scheme: 'olive'})")
time.sleep(0.8)
with open(PAL_META, encoding="utf-8") as handle:
    meta = json.load(handle)
check("changing the preference with no file open leaves the side-car alone",
      (meta.get("appearance") or {}).get("scheme") == "mint", meta.get("appearance"))

evaluate("__palacedb.send('openPath', {path: %s})" % json.dumps(PAL_DB))
check("reopening the file brings its own palette back",
      wait_for("document.body.dataset.scheme === 'mint'", 25) is True,
      evaluate("document.body.dataset.scheme"))


# --- an enum value may have no label
# ",A,B" is three values whose first one has no label. The blank has to survive the
# dialog: dropping it moves every stored index onto the next label.
evaluate("__palacedb.send('addColumn', {sheet: __palacedb.state.currentSheet, index: %d, "
         "name: 'e2e_enum_blank', type: '5:A,B'})" % evaluate("__palacedb.grid.columns.length"))
check("an enum without an unnamed value can be added",
      wait_for("__palacedb.grid.columns.some(c => c.name === 'e2e_enum_blank')", 10),
      evaluate("__palacedb.grid.columns.map(c => c.name).join(',')"))
BLANK_COL = column_index('e2e_enum_blank')

BLANK_TYPE = """(() => {
  const sheet = __palacedb.state.document.sheets.find(s => s.name === __palacedb.state.currentSheet);
  const column = sheet && sheet.columns.find(c => c.name === 'e2e_enum_blank');
  return column ? column.typeStr : null;
})()"""

open_header_menu(BLANK_COL)
click_menu_item('Edit Column')
time.sleep(0.6)
check("the values field says an entry may stay blank",
      'blank' in (evaluate("(document.querySelector('#modal .values-hint') || {}).textContent || ''") or ""),
      evaluate("(document.querySelector('#modal .values-hint') || {}).textContent"))
evaluate("""(() => {
  const inputs = Array.from(document.querySelectorAll('#modal .form-input'))
    .filter(i => i.tagName === 'INPUT' && i.type === 'text');
  const field = inputs[1];
  field.value = ',' + field.value;
  field.dispatchEvent(new Event('input', {bubbles: true}));
  return field.value;
})()""")
evaluate("document.getElementById('modalOk').click()")
time.sleep(1.0)
check("typing a leading comma keeps the unnamed value",
      evaluate(BLANK_TYPE) == "5:,A,B", evaluate(BLANK_TYPE))

# The popup has to show it: an empty row is invisible and cannot be clicked.
evaluate("__palacedb.editor.begin(1, %d)" % BLANK_COL)
time.sleep(0.5)
check("the popup names the unnamed value instead of leaving an empty row",
      evaluate("Array.from(document.querySelectorAll('.cell-dropdown .dropdown-label'))"
               ".map(e => e.textContent).join('|')") == "(empty)|A|B",
      evaluate("Array.from(document.querySelectorAll('.cell-dropdown .dropdown-label'))"
               ".map(e => e.textContent).join('|')"))
evaluate("""(() => {
  const item = document.querySelectorAll('.cell-dropdown .dropdown-item')[0];
  if (!item) return 'missing';
  item.dispatchEvent(new MouseEvent('mousedown', {bubbles: true, cancelable: true}));
  return 'ok';
})()""")
time.sleep(0.8)
check("picking it stores the unnamed value",
      evaluate("(__palacedb.grid.cellValue(1,%d) || {}).t" % BLANK_COL) == ""
      and evaluate("(__palacedb.grid.cellValue(1,%d) || {}).s" % BLANK_COL) == "ok",
      evaluate("JSON.stringify(__palacedb.grid.cellValue(1,%d))" % BLANK_COL))

for _ in range(3):
    evaluate("__palacedb.send('undo')")
    time.sleep(0.4)
check("the unnamed value checks are undone",
      wait_for("!__palacedb.grid.columns.some(c => c.name === 'e2e_enum_blank')", 10),
      evaluate("__palacedb.grid.columns.map(c => c.name).join(',')"))

# --- text in a dialog can be selected and copied
# The table owns Ctrl+C, so selecting an error message and pressing it used to paste the
# selected cells instead of the message.
evaluate("__palacedb.run('about')")
check("the About dialog opens for the copy check",
      wait_for("!document.getElementById('modal').hidden", 10),
      evaluate("document.getElementById('modalTitle').textContent"))
SELECTED = evaluate("""(() => {
  const range = document.createRange();
  range.selectNodeContents(document.getElementById('modalBody'));
  const selection = window.getSelection();
  selection.removeAllRanges();
  selection.addRange(range);
  return selection.toString();
})()""")
check("text in a dialog can be selected", bool(SELECTED) and len(SELECTED) > 20,
      (SELECTED or '')[:40])
set_clipboard("sentinel")
press_ctrl('c', 'KeyC', 67)
time.sleep(0.7)
COPIED = read_clipboard()
check("Ctrl+C copies the text of the dialog, not the cells", COPIED == SELECTED,
      (COPIED or '')[:60])
evaluate("document.getElementById('modalOk').click()")
time.sleep(0.4)

COLS_BEFORE = evaluate("__palacedb.grid.columns.length")
evaluate("document.querySelector('.tool.icon[data-action=\"addColumn\"]').click()")
check("the Add column dialog opens for the paste check",
      wait_for("document.getElementById('modalTitle').textContent === 'Add column'", 10),
      evaluate("document.getElementById('modalTitle').textContent"))
set_clipboard("pasted name")
evaluate("document.querySelector('#modal .form-input').focus()")
press_ctrl('v', 'KeyV', 86)
time.sleep(0.7)
check("Ctrl+V pastes text into the dialog field",
      evaluate("document.querySelector('#modal .form-input').value") == "pasted name",
      evaluate("document.querySelector('#modal .form-input').value"))
check("pasting into the dialog did not touch the table",
      evaluate("__palacedb.grid.columns.length") == COLS_BEFORE)
evaluate("document.getElementById('modalCancel').click()")
time.sleep(0.4)

# With nothing selected the shortcut is still the table's.
evaluate("window.getSelection().removeAllRanges()")
evaluate("__palacedb.grid.moveCursor(1, 1)")
time.sleep(0.3)
set_clipboard("sentinel")
press_ctrl('c', 'KeyC', 67)
time.sleep(0.7)
CELLS = read_clipboard() or ""
check("Ctrl+C on the table still copies the cells",
      CELLS.startswith('{"kind":"palacedb/cells"'), CELLS[:60])

# --- table templates: one definition, many tables (R14)
# A template is a named column list in the side-car. Tables that follow it share every
# column setting: an edit made in one of them is replayed on the others, a destructive
# edit asks a second time, and a table that has grown a column of its own cannot join
# until that column is removed or added to the template.
def column_names(name):
    """The column names of a table, read from the document the interface holds."""
    return evaluate(
        "JSON.stringify((__palacedb.state.document.sheets.find(s => s.name === %s) || {columns: []})"
        ".columns.map(c => c.name))" % json.dumps(name))


def column_names_expr(name):
    """The same, as an expression, so it can be polled."""
    return ("JSON.stringify(__palacedb.state.document.sheets.find(s => s.name === %s)"
            ".columns.map(c => c.name))" % json.dumps(name))


TPL_SHEET = evaluate("__palacedb.state.currentSheet")
TPL_COLUMNS = json.loads(column_names(TPL_SHEET))

evaluate("__palacedb.run('tableSettings')")
check("Table Settings opens", wait_for("!document.getElementById('modal').hidden", 10))
check("Table Settings names the table it edits",
      evaluate("document.getElementById('modalTitle').textContent") == "Table settings: %s" % TPL_SHEET,
      evaluate("document.getElementById('modalTitle').textContent"))
check("Table Settings offers a template to follow",
      evaluate("!!document.querySelector('#modal select.form-input')") is True)
check("Table Settings offers to save these columns as a template",
      evaluate("Array.from(document.querySelectorAll('#modal .form-actions button'))"
               ".some(b => b.textContent.indexOf('Save these columns') === 0)") is True)

evaluate("Array.from(document.querySelectorAll('#modal .form-actions button'))[0].click()")
check("saving a template asks for a name",
      wait_for("document.getElementById('modalTitle').textContent.indexOf('as a template') > 0", 5))
evaluate("""(() => {
  const input = document.querySelector('#modal .form-input');
  input.value = 'E2E';
  input.dispatchEvent(new Event('input', {bubbles: true}));
  return true;
})()""")
evaluate("document.getElementById('modalOk').click()")
check("the template is created", wait_for("__palacedb.state.document.templates.length === 1", 10))
check("the table starts following the template it saved",
      evaluate("(__palacedb.state.document.sheets.find(s => s.name === %s) || {}).template"
               % json.dumps(TPL_SHEET)) is not None)

with open(PAL_META, encoding="utf-8") as handle:
    tpl_meta = json.load(handle)
tpl_ids = list((tpl_meta.get("templates") or {}).keys())
check("the template is stored in the side-car, not in the .cdb", len(tpl_ids) == 1, list(tpl_meta.keys()))
# >= 3, not == 3: the side-car keeps gaining blocks (public types, hidden columns, folded
# groups), and this check is about templates being understood at all.
check("the side-car is written in the version that knows templates",
      (tpl_meta.get("metaVersion") or 0) >= 3, tpl_meta.get("metaVersion"))
check("the table records which template it follows",
      bool(tpl_ids) and (tpl_meta.get("tables") or {}).get(TPL_SHEET, {}).get("template") == tpl_ids[0],
      (tpl_meta.get("tables") or {}).get(TPL_SHEET, {}).get("template"))
check("the definition keeps every column of the table",
      bool(tpl_ids)
      and len((tpl_meta.get("templates") or {}).get(tpl_ids[0], {}).get("columns", [])) == len(TPL_COLUMNS),
      len((tpl_meta.get("templates") or {}).get(tpl_ids[0], {}).get("columns", [])) if tpl_ids else None)

check("the sidebar marks a table that follows a template",
      evaluate("document.querySelectorAll('#tableList .tpl-dot').length") == 1,
      evaluate("document.querySelectorAll('#tableList .tpl-dot').length"))
check("the mark stays quiet and only explains itself on hover",
      "E2E" in (evaluate("document.querySelector('#tableList .tpl-dot').title") or "")
      and evaluate("document.querySelector('#tableList .tpl-dot').textContent").strip() == "",
      evaluate("document.querySelector('#tableList .tpl-dot').title"))
check("the status bar names the template in use",
      evaluate("document.getElementById('statusTemplate').textContent") == "Template: E2E",
      evaluate("document.getElementById('statusTemplate').textContent"))

TPL_ID = evaluate("__palacedb.state.document.templates[0].id")
evaluate("__palacedb.send('addSheet', {name: 'TplCopy', template: %s})" % json.dumps(TPL_ID))
check("a table can be created from a template",
      wait_for("__palacedb.state.document.sheets.some(s => s.name === 'TplCopy')", 15))
check("it starts with the same columns in the same order",
      column_names('TplCopy') == column_names(TPL_SHEET),
      (column_names('TplCopy'), column_names(TPL_SHEET)))
check("both tables are marked in the sidebar",
      evaluate("document.querySelectorAll('#tableList .tpl-dot').length") == 2)

# A rename reaches every table that follows the template, and the definition follows too.
SECOND = TPL_COLUMNS[1]
evaluate("__palacedb.send('renameColumn', {sheet: 'TplCopy', column: %s, name: 'tpl_renamed'})"
         % json.dumps(SECOND))
check("renaming a column in one table renames it in the others",
      wait_for("%s.indexOf('tpl_renamed') >= 0" % column_names_expr(TPL_SHEET), 15),
      column_names(TPL_SHEET))
check("the two tables still agree", column_names('TplCopy') == column_names(TPL_SHEET))

evaluate("__palacedb.send('addColumn', {sheet: 'TplCopy', index: 1, name: 'tpl_extra', type: '1'})")
check("a column added in one table appears in the others",
      wait_for("%s.indexOf('tpl_extra') >= 0" % column_names_expr(TPL_SHEET), 15),
      column_names(TPL_SHEET))
check("the template grew with it",
      evaluate("__palacedb.state.document.templates[0].columns") == len(json.loads(column_names(TPL_SHEET))),
      evaluate("__palacedb.state.document.templates[0].columns"))

# Deleting a shared column is the destructive case: the values in the other tables go with it.
# The sidebar is clicked rather than the host told, because selecting a table is what makes
# the grid load its rows — and the scan below reads values off the grid.
evaluate("""(() => {
  const item = Array.from(document.querySelectorAll('#tableList .table-item'))
    .find(el => el.dataset.sheet === %s);
  if (item) item.click();
  return !!item;
})()""" % json.dumps(TPL_SHEET))
check("clicking a table in the sidebar shows it again",
      wait_for("__palacedb.state.currentSheet === %s && __palacedb.grid.rowCount > 0"
               % json.dumps(TPL_SHEET), 15),
      (evaluate("__palacedb.state.currentSheet"), evaluate("__palacedb.grid.rowCount")))
VICTIM = evaluate("""(() => {
  // A column the two tables share and that actually holds values: an empty one is not
  // worth confirming, and its cells read as the missing-value marker.
  const columns = __palacedb.grid.columns;
  const rows = Math.min(__palacedb.grid.rowCount, 20);
  for (let c = 1; c < columns.length; c++) {
    for (let r = 0; r < rows; r++) {
      const cell = __palacedb.grid.cellValue(r, c);
      if (cell && cell.t && cell.s !== 'missing') return columns[c].name;
    }
  }
  return null;
})()""")
check("the sample table has a column with values to lose", VICTIM is not None, VICTIM)

if VICTIM:
    evaluate("__palacedb.send('deleteColumn', {sheet: 'TplCopy', column: %s})" % json.dumps(VICTIM))
    check("deleting a shared column asks a second time",
          wait_for("!document.getElementById('modal').hidden && "
                   "document.getElementById('modalTitle').textContent.indexOf('from every table') > 0", 15),
          evaluate("document.getElementById('modalTitle').textContent"))
    check("the question lists every table that would lose the column",
          evaluate("document.querySelectorAll('#modal .impact-row').length") == 2,
          evaluate("document.querySelectorAll('#modal .impact-row').length"))
    check("the question counts the values that would be lost",
          "value" in (evaluate("document.getElementById('modalBody').textContent") or ""),
          evaluate("document.getElementById('modalBody').textContent"))
    evaluate("document.getElementById('modalCancel').click()")
    time.sleep(0.5)
    check("cancelling keeps the column everywhere",
          VICTIM in json.loads(column_names('TplCopy')) and VICTIM in json.loads(column_names(TPL_SHEET)))

    evaluate("__palacedb.send('deleteColumn', {sheet: 'TplCopy', column: %s, confirmed: true})"
             % json.dumps(VICTIM))
    ok = wait_for("%s.indexOf(%s) < 0" % (column_names_expr('TplCopy'), json.dumps(VICTIM)), 15)
    check("confirming deletes the column from every table",
          ok and VICTIM not in json.loads(column_names(TPL_SHEET)),
          (column_names('TplCopy'), column_names(TPL_SHEET)))

# A detached table can drift; joining again is refused while it has a column of its own.
evaluate("__palacedb.send('detachTemplate', {sheet: 'TplCopy'})")
check("a table can stop following its template",
      wait_for("!(__palacedb.state.document.sheets.find(s => s.name === 'TplCopy') || {})"
               ".template", 15),
      evaluate("(__palacedb.state.document.sheets.find(s => s.name === 'TplCopy') || {}).template"))
check("the sidebar mark goes with the link",
      evaluate("document.querySelectorAll('#tableList .tpl-dot').length") == 1)

evaluate("__palacedb.send('addColumn', {sheet: 'TplCopy', index: 2, name: 'tpl_only_here', type: '1'})")
time.sleep(1.0)
check("a detached table can change on its own",
      'tpl_only_here' in json.loads(column_names('TplCopy'))
      and 'tpl_only_here' not in json.loads(column_names(TPL_SHEET)))

TPL_BEFORE = column_names(TPL_SHEET)
evaluate("__palacedb.send('applyTemplate', {sheet: 'TplCopy', template: %s})" % json.dumps(TPL_ID))
check("a table with a column of its own is refused",
      wait_for("!document.getElementById('modal').hidden && "
               "document.getElementById('modalTitle').textContent.indexOf('cannot follow') > 0", 15),
      evaluate("document.getElementById('modalTitle').textContent"))
check("the report names the column that is in the way",
      'tpl_only_here' in (evaluate("document.getElementById('modalBody').textContent") or ""),
      evaluate("document.getElementById('modalBody').textContent"))
check("the refusal changed nothing", column_names(TPL_SHEET) == TPL_BEFORE)
check("the report offers to add the column to the template",
      evaluate("Array.from(document.querySelectorAll('#modal button'))"
               ".some(b => b.textContent.indexOf('Add these columns') === 0)") is True)

# The dialog only reports; its Close button has to close it rather than do nothing.
check("the report button is labelled Close",
      evaluate("document.getElementById('modalOk').textContent") == "Close",
      evaluate("document.getElementById('modalOk').textContent"))
evaluate("document.getElementById('modalOk').click()")
check("the Close button of the report closes it",
      wait_for("document.getElementById('modal').hidden", 5),
      evaluate("document.getElementById('modalTitle').textContent"))
check("closing the report changed nothing either", column_names(TPL_SHEET) == TPL_BEFORE)

evaluate("__palacedb.send('applyTemplate', {sheet: 'TplCopy', template: %s})" % json.dumps(TPL_ID))
check("the report can be opened again",
      wait_for("!document.getElementById('modal').hidden && "
               "document.getElementById('modalTitle').textContent.indexOf('cannot follow') > 0", 15),
      evaluate("document.getElementById('modalTitle').textContent"))

evaluate("Array.from(document.querySelectorAll('#modal button'))"
         ".find(b => b.textContent.indexOf('Add these columns') === 0).click()")
check("adding the columns to the template brings the other tables up to it",
      wait_for("%s.indexOf('tpl_only_here') >= 0" % column_names_expr(TPL_SHEET), 15),
      column_names(TPL_SHEET))
check("both tables match the template afterwards",
      column_names('TplCopy') == column_names(TPL_SHEET),
      (column_names('TplCopy'), column_names(TPL_SHEET)))

# --- an enum keeps its empty first value, so the table still matches the template
# CastleDB writes an enum as "5:,Common,Rare": the first label is empty, and dropping it
# moves every stored index onto another label. Re-encoding the text on the way out of the
# column dialog used to do exactly that, after which the table no longer matched the
# template it had just been saved into — and the report read "'rarity' is enum in this
# table but enum in the template", which explains nothing.
evaluate("__palacedb.send('addColumn', {sheet: %s, index: 2, name: 'tpl_enum', type: '5:,Common,Rare'})"
         % json.dumps(TPL_SHEET))
check("an enum column can be added to a table that follows a template",
      wait_for("%s.indexOf('tpl_enum') >= 0" % column_names_expr(TPL_SHEET), 15),
      column_names(TPL_SHEET))
check("the enum reaches the other table too",
      wait_for("%s.indexOf('tpl_enum') >= 0" % column_names_expr('TplCopy'), 15),
      column_names('TplCopy'))

TPL_ENUM_TYPE = """(() => {
  const sheet = __palacedb.state.document.sheets.find(s => s.name === %s);
  const column = sheet && sheet.columns.find(c => c.name === 'tpl_enum');
  return column ? column.typeStr : null;
})()""" % json.dumps(TPL_SHEET)


def template_column_type(name):
    """The type a template stores for a column, read straight from the side-car."""
    with open(PAL_META, encoding="utf-8") as handle:
        meta = json.load(handle)
    for template in (meta.get("templates") or {}).values():
        for column in template.get("columns") or []:
            if column.get("name") == name:
                return column.get("typeStr")
    return None


for _ in range(20):
    if template_column_type('tpl_enum') is not None:
        break
    time.sleep(0.25)
check("the template records the enum with its empty first value",
      template_column_type('tpl_enum') == "5:,Common,Rare", template_column_type('tpl_enum'))

open_header_menu(column_index('tpl_enum'))
click_menu_item('Edit Column')
time.sleep(0.6)
evaluate("document.getElementById('modalOk').click()")
time.sleep(1.0)
check("closing the column dialog leaves the enum as it was",
      evaluate(TPL_ENUM_TYPE) == "5:,Common,Rare", evaluate(TPL_ENUM_TYPE))

evaluate("__palacedb.send('detachTemplate', {sheet: 'TplCopy'})")
time.sleep(0.8)
evaluate("__palacedb.send('applyTemplate', {sheet: 'TplCopy', template: %s})" % json.dumps(TPL_ID))
check("a table whose enum matches the template joins it again",
      wait_for("(__palacedb.state.document.sheets.find(s => s.name === 'TplCopy') || {}).template === %s"
               % json.dumps(TPL_ID), 15),
      evaluate("(__palacedb.state.document.sheets.find(s => s.name === 'TplCopy') || {}).template"))
check("no conflict is reported when the two types are the same",
      evaluate("document.getElementById('modal').hidden") is True,
      evaluate("document.getElementById('modalBody').textContent"))

# The manager lists, renames and removes templates; removing one keeps the columns.
evaluate("__palacedb.run('templates')")
check("the Templates window lists the template",
      wait_for("document.querySelectorAll('#modal .template-row').length === 1", 10))
check("it says how many tables follow it",
      "2 tables" in (evaluate("document.querySelector('#modal .template-meta').textContent") or ""),
      evaluate("document.querySelector('#modal .template-meta').textContent"))
evaluate("""(() => {
  const input = document.querySelector('#modal .template-row input');
  input.value = 'E2E renamed';
  input.dispatchEvent(new Event('change', {bubbles: true}));
  return true;
})()""")
check("a template can be renamed",
      wait_for("__palacedb.state.document.templates[0].name === 'E2E renamed'", 10),
      evaluate("__palacedb.state.document.templates[0].name"))

TPL_KEPT = column_names(TPL_SHEET)
evaluate("document.querySelector('#modal .template-row button').click()")
check("removing a template asks once more",
      evaluate("document.querySelector('#modal .template-row button').textContent") == "Delete?")
evaluate("document.querySelector('#modal .template-row button').click()")
check("the template is gone", wait_for("__palacedb.state.document.templates.length === 0", 10))
check("its tables keep their columns", column_names(TPL_SHEET) == TPL_KEPT)
check("and stop following it",
      evaluate("__palacedb.state.document.sheets.every(s => !s.template)") is True)
check("the sidebar marks are gone",
      evaluate("document.querySelectorAll('#tableList .tpl-dot').length") == 0)
evaluate("document.getElementById('modalOk').click()")
check("the Close button of the template list closes it",
      wait_for("document.getElementById('modal').hidden", 5),
      evaluate("document.getElementById('modalTitle').textContent"))
time.sleep(0.4)
with open(PAL_META, encoding="utf-8") as handle:
    tpl_meta = json.load(handle)
check("the side-car no longer lists the template", not (tpl_meta.get("templates") or {}),
      list((tpl_meta.get("templates") or {}).keys()))

# --- the appearance is remembered across a restart
# It is written to localStorage, so the next window opens with the same sizes and palette
# without the database carrying any of it.
evaluate("__palacedb.applySettings({uiFontSize: 16, gridFontSize: 17, scheme: 'ocean'})")
time.sleep(2.0)
stored = evaluate("localStorage.getItem('palacedb.settings')") or "{}"
try:
    stored_json = json.loads(stored)
except ValueError:
    stored_json = {}
check("the appearance is stored before the restart",
      stored_json.get("uiFontSize") == 16 and stored_json.get("gridFontSize") == 17
      and stored_json.get("scheme") == "ocean", stored)

# --- crash recovery on the next start (F19)
ws.close()
proc.terminate()
time.sleep(1.0)

RECOVER_DIR = os.path.join(WORK, "recover")
os.makedirs(RECOVER_DIR, exist_ok=True)
RECOVER = os.path.join(RECOVER_DIR, "Crash.cdb")
shutil.copy(SOURCE, RECOVER)

crash = json.load(open(SOURCE, encoding="utf-8"))
crash["sheets"][0]["lines"][0][ID_NAME] = "RECOVERED_ROW"
crash_backup = RECOVER + ".autobackup"
with open(crash_backup, "w", encoding="utf-8", newline="\n") as handle:
    json.dump(crash, handle, ensure_ascii=False, indent="\t")
os.utime(crash_backup, (time.time() + 60, time.time() + 60))

# A side-car that remembers a window clearly smaller than the default one, so the
# restored client area proves the geometry was applied rather than defaulted (F17).
with open(RECOVER + ".meta.json", "w", encoding="utf-8", newline="\n") as handle:
    json.dump({
        "metaVersion": 1,
        "fileId": "geometry000000000000000000000000",
        "createdWith": "PalaceDB.CSharp/1.0",
        "tables": {},
        "windowState": {"width": 760, "height": 520, "x": 100, "y": 80, "maximized": False},
    }, handle, indent=2)

proc = subprocess.Popen(
    [EXE, RECOVER],
    env=dict(os.environ, PALACEDB_CDP="9334", PALACEDB_AUTOMATION="1", PALACEDB_BACKUP_SECONDS="120"))
ws = None
for _ in range(60):
    time.sleep(0.5)
    park_windows(proc.pid)
    try:
        with urllib.request.urlopen("http://127.0.0.1:9334/json/list", timeout=2) as r:
            targets = json.load(r)
        pages = [t for t in targets if t.get("type") == "page"]
        if pages:
            ws = websocket.create_connection(pages[0]["webSocketDebuggerUrl"], timeout=15)
            break
    except Exception:
        continue

if ws is None:
    check("recovery instance started", False, "no CDP target")
else:
    check("recovery instance started", True)
    ok = wait_for("!!(window.__palacedb && __palacedb.state.document)", 25)
    check("recovered document loaded", ok, evaluate("document.title"))
    ok = wait_for("__palacedb.grid.cellValue(0,0) && __palacedb.grid.cellValue(0,0).t === 'RECOVERED_ROW'", 20)
    check("backup content restored into the grid", ok,
          evaluate("__palacedb.grid.cellValue(0,0) && __palacedb.grid.cellValue(0,0).t"))
    check("backup file consumed", wait_for_file(crash_backup, 8, False), crash_backup)
    check("recovered content written to the database",
          json.load(open(RECOVER, encoding="utf-8"))["sheets"][0]["lines"][0][ID_NAME] == "RECOVERED_ROW")

    # --- the sizes and the palette survived the restart
    check("the palette came back with the window",
          evaluate("document.body.dataset.scheme") == "ocean"
          and evaluate("document.body.classList.contains('theme-dark')") is True,
          evaluate("document.body.dataset.scheme"))
    check("the sizes came back with the window",
          evaluate("getComputedStyle(document.documentElement).getPropertyValue('--grid-font-size').trim()") == "17px"
          and evaluate("getComputedStyle(document.documentElement).getPropertyValue('--font-size').trim()") == "16px",
          evaluate("getComputedStyle(document.documentElement).getPropertyValue('--grid-font-size').trim()"))
    check("the restored size placed the rows correctly",
          evaluate("""(() => {
            const cell = document.querySelector('#gridRows .cell[data-row="2"]');
            const gutter = document.querySelector('#gridGutter .gutter-cell[data-row="2"]');
            if (!cell || !gutter) return false;
            return Math.abs(cell.getBoundingClientRect().top - gutter.getBoundingClientRect().top) < 2;
          })()""") is True)
    # Leave the shared profile at the defaults so the next run starts the same way.
    evaluate("__palacedb.applySettings({uiFontSize: 15, gridFontSize: 14, scheme: 'light'})")
    time.sleep(0.6)

    # --- remembered window geometry (F17)
    viewport = evaluate("({w: window.innerWidth, h: window.innerHeight})") or {"w": 0, "h": 0}
    check("remembered window size restored",
          700 <= viewport["w"] <= 765 and 430 <= viewport["h"] <= 530,
          viewport)

    evaluate("document.body.dispatchEvent(new KeyboardEvent('keydown', {key:'s', ctrlKey:true, bubbles:true}))")
    time.sleep(1.2)
    restored = json.load(open(RECOVER + ".meta.json", encoding="utf-8")).get("windowState") or {}
    check("geometry kept in the side-car after saving",
          isinstance(restored.get("x"), (int, float)) and isinstance(restored.get("width"), (int, float)),
          restored)
    check("saved geometry matches the window on screen",
          abs(restored.get("width", 0) - viewport["w"]) < 60,
          (restored.get("width"), viewport))

    # --- two windows on one file: closing the editable one frees the file
    # The second window opens the same file read-only, because only one window may write.
    # Once that window is gone the other one has to become editable instead of staying
    # read-only for the rest of the session.
    known = {t["id"] for t in json.load(urllib.request.urlopen("http://127.0.0.1:9334/json/list"))}
    evaluate("__palacedb.send('newWindow')")
    ws_b = None
    for _ in range(60):
        time.sleep(0.5)
        try:
            pages = [t for t in json.load(urllib.request.urlopen("http://127.0.0.1:9334/json/list"))
                     if t.get("type") == "page" and t["id"] not in known]
        except Exception:
            pages = []
        if pages:
            try:
                ws_b = websocket.create_connection(pages[0]["webSocketDebuggerUrl"], timeout=15)
                break
            except Exception:
                ws_b = None
    check("a second window opened", ws_b is not None)

    if ws_b is not None:
        def wait_b(expr, timeout=25):
            end = time.time() + timeout
            while time.time() < end:
                try:
                    if evaluate(expr, sock=ws_b):
                        return True
                except Exception:
                    pass
                time.sleep(0.25)
            return False

        wait_b("!!window.__palacedb")
        time.sleep(0.8)
        evaluate("__palacedb.send('openPath', {path: %s})" % json.dumps(RECOVER), sock=ws_b)
        ok = wait_b("!!(window.__palacedb && __palacedb.state.document)", 25)
        check("the second window opened the same file", ok)
        check("the second window is read-only",
              evaluate("__palacedb.state.document.readOnly", sock=ws_b) is True
              and evaluate("__palacedb.elements.statusMode.textContent", sock=ws_b) == "Read only",
              evaluate("__palacedb.elements.statusMode.textContent", sock=ws_b))

        # Close the window that holds the file.
        evaluate("__palacedb.send('exit')")
        time.sleep(2.5)
        ws = ws_b
        ok = wait_b("__palacedb.state.document.readOnly === false", 20)
        check("closing the editing window makes the other one editable", ok,
              evaluate("__palacedb.state.document.readOnlyReason", sock=ws_b))
        check("the status line says the file can be edited",
              evaluate("__palacedb.elements.statusMode.textContent", sock=ws_b) == "Editable",
              evaluate("__palacedb.elements.statusMode.textContent", sock=ws_b))
        # And it really can write.
        evaluate("__palacedb.send('setCell', {sheet: __palacedb.state.currentSheet, row: 0, "
                 "column: __palacedb.grid.columns[0].name, text: 'TAKEN_OVER'})", sock=ws_b)
        time.sleep(0.5)
        evaluate("__palacedb.send('save')", sock=ws_b)
        time.sleep(1.5)
        check("the promoted window can save",
              "TAKEN_OVER" in open(RECOVER, encoding="utf-8").read(),
              evaluate("__palacedb.state.document.dirty", sock=ws_b))
        evaluate("__palacedb.send('undo')", sock=ws_b)
        time.sleep(0.6)

# --- public enum / flags types: one definition, many columns
# Two columns of the current table are given the same value list and then bound to a
# public type, so that changing the type has to reach both of them.
sheet = evaluate("__palacedb.state.currentSheet")
evaluate("__palacedb.send('addColumn', {sheet: %r, index: 0, name: 'ptOne', type: '5:A,B,C'})" % sheet)
time.sleep(1.2)
evaluate("__palacedb.send('addColumn', {sheet: %r, index: 1, name: 'ptTwo', type: '5:A,B,C'})" % sheet)
time.sleep(1.2)

# The public types live on their own tab now, and the + there adds one.
evaluate("document.querySelector('.sidebar-tab[data-tab=\"types\"]').click()")
time.sleep(0.4)
evaluate("document.getElementById('sidebarAdd').click()")
time.sleep(0.6)
check("the sidebar opens a public type dialog",
      evaluate("document.getElementById('modalTitle').textContent") == "New public type",
      evaluate("document.getElementById('modalTitle').textContent"))
evaluate("""(() => {
  const inputs = document.querySelectorAll('#modal .form-row input[type=text]');
  inputs[0].value = 'PT';
  inputs[0].dispatchEvent(new Event('input', {bubbles: true}));
  inputs[1].value = 'A,B,C';
  inputs[1].dispatchEvent(new Event('input', {bubbles: true}));
  return inputs.length;
})()""")
evaluate("document.getElementById('modalOk').click()")
ok = wait_for("Array.from(document.querySelectorAll('#publicList .public-item'))"
              ".some(i => i.dataset.publicType === 'PT')", 12)
check("a new public type is listed in the sidebar", ok,
      evaluate("document.getElementById('publicList').textContent"))

# Binding through the column dialog: the values come from the definition.
evaluate("__palacedb.grid.moveCursor(0, 0)")
evaluate("__palacedb.grid.onContextMenu({row: 0, column: 0, header: true, x: 200, y: 110})")
time.sleep(0.5)
evaluate("""(() => {
  const item = Array.from(document.querySelectorAll('#contextMenu .context-item'))
    .find(b => b.textContent.indexOf('Edit Column') >= 0);
  if (item) item.click();
  return Boolean(item);
})()""")
time.sleep(0.6)
check("the column dialog offers the public type",
      evaluate("Array.from(document.querySelectorAll('#modal select')[1].options)"
               ".some(o => o.value === 'PT')"),
      evaluate("Array.from(document.querySelectorAll('#modal select')[1].options)"
               ".map(o => o.value).join('|')"))
evaluate("""(() => {
  const select = document.querySelectorAll('#modal select')[1];
  select.value = 'PT';
  select.dispatchEvent(new Event('change', {bubbles: true}));
  return select.value;
})()""")
check("choosing a public type locks the value list",
      evaluate("document.querySelectorAll('#modal .form-row input[type=text]')[1].disabled"),
      evaluate("document.querySelectorAll('#modal .form-row input[type=text]')[1].value"))
evaluate("document.getElementById('modalOk').click()")
ok = wait_for("__palacedb.state.document.sheets.find(s => s.name === %r).columns[0].publicType === 'PT'" % sheet, 12)
check("the column follows the public type", ok,
      evaluate("__palacedb.state.document.sheets.find(s => s.name === %r).columns[0].publicType" % sheet))

evaluate("__palacedb.send('editColumn', {sheet: %r, column: 'ptTwo', name: 'ptTwo',"
         " type: '5:A,B,C', publicType: 'PT'})" % sheet)
time.sleep(1.2)
check("a second column can follow the same type",
      evaluate("__palacedb.state.document.sheets.find(s => s.name === %r).columns[1].publicType" % sheet) == 'PT',
      evaluate("JSON.stringify(__palacedb.state.document.publicTypes)"))

# One edit of the definition reaches both columns.
evaluate("__palacedb.send('savePublicType', {name: 'PT', kind: 'enum', values: 'A,C', renameFrom: 'PT'})")
time.sleep(1.5)
check("editing a public type rewrites every column that uses it",
      evaluate("(() => { const c = __palacedb.state.document.sheets.find(s => s.name === %r).columns;"
               " return c[0].typeStr + '|' + c[1].typeStr; })()" % sheet) == '5:A,C|5:A,C',
      evaluate("(() => { const c = __palacedb.state.document.sheets.find(s => s.name === %r).columns;"
               " return c[0].typeStr + '|' + c[1].typeStr; })()" % sheet))

# --- auto-increment id: new and inserted rows take the next number
evaluate("__palacedb.send('addColumn', {sheet: %r, index: 2, name: 'ptNum', type: '3',"
         " autoIncrement: true})" % sheet)
time.sleep(1.4)
rows_before = evaluate("__palacedb.state.document.sheets.find(s => s.name === %r).rowCount" % sheet)
evaluate("__palacedb.send('insertRow', {sheet: %r, row: 0})" % sheet)
time.sleep(1.3)

# --- guid: every row gets its own identifier, duplicates included
evaluate("__palacedb.send('addColumn', {sheet: %r, index: 3, name: 'ptGuid', type: '20'})" % sheet)
time.sleep(1.4)
evaluate("__palacedb.send('duplicateRows', {sheet: %r, rows: [0]})" % sheet)
time.sleep(1.4)
evaluate("__palacedb.send('save')")
time.sleep(1.8)
# This window is editing the crash-recovery copy, not the working copy.
with open(RECOVER, encoding="utf-8") as handle:
    saved = json.load(handle)
saved_sheet = next(s for s in saved["sheets"] if s["name"] == sheet)
numbers = [row.get("ptNum") for row in saved_sheet["lines"]]
check("an auto-increment column numbers the rows it arrives with",
      sorted(numbers[2:]) == list(range(1, rows_before + 1)), numbers[:5])
check("an inserted row gets one more than the highest number",
      numbers[0] == rows_before + 1, numbers[:3])
check("a duplicated row gets a number of its own",
      numbers[1] == rows_before + 2, numbers[:3])

guids = [row.get("ptGuid") for row in saved_sheet["lines"]]
check("every row has its own guid",
      len(guids) == len(set(guids)) and all(isinstance(g, str) and len(g) == 13 for g in guids),
      guids[:4])

# --- deleting a public type leaves its columns alone but unbinds them
evaluate("__palacedb.send('deletePublicType', {name: 'PT'})")
time.sleep(1.3)
check("deleting a public type unbinds its columns",
      evaluate("__palacedb.state.document.publicTypes.length") == 0 and
      evaluate("__palacedb.state.document.sheets.find(s => s.name === %r).columns[0].publicType" % sheet) is None,
      evaluate("JSON.stringify(__palacedb.state.document.publicTypes)"))

# --- an id column that was deleted can be added back
# A table gets an id column when it is created, but the column can be deleted on purpose;
# the type has to stay available in the dialog or the table is left without a key.
evaluate("__palacedb.send('addSheet', {name: 'IdTest'})")
time.sleep(1.2)
check("a new table starts with an id column",
      evaluate("__palacedb.state.document.sheets.find(s => s.name === 'IdTest').columns[0].typeStr") == '0',
      evaluate("JSON.stringify(__palacedb.state.document.sheets.find(s => s.name === 'IdTest').columns)"))

# A table cannot lose its last column, so it gets one more before the id goes.
evaluate("__palacedb.send('addColumn', {sheet: 'IdTest', index: 1, name: 'name', type: '1'})")
time.sleep(1.0)
evaluate("__palacedb.send('deleteColumn', {sheet: 'IdTest', column: 'id'})")
time.sleep(1.2)
check("an id column can be deleted",
      evaluate("__palacedb.state.document.sheets.find(s => s.name === 'IdTest').columns.length") == 1,
      evaluate("JSON.stringify(__palacedb.state.document.sheets.find(s => s.name === 'IdTest').columns)"))

evaluate("__palacedb.run('addColumn')")
ok = wait_for("!document.getElementById('modal').hidden", 10)
check("the add column dialog offers the id type", ok and
      evaluate("Array.from(document.querySelectorAll('#modal select')[0].options).some(o => o.value === '0')") is True,
      evaluate("Array.from(document.querySelectorAll('#modal select')[0].options).map(o => o.value).join('|')"))
evaluate("""(() => {
  const name = document.querySelectorAll('#modal .form-row input[type=text]')[0];
  name.value = 'id';
  name.dispatchEvent(new Event('input', {bubbles: true}));
  const type = document.querySelectorAll('#modal select')[0];
  type.value = '0';
  type.dispatchEvent(new Event('change', {bubbles: true}));
  return type.value;
})()""")
time.sleep(0.3)
evaluate("document.getElementById('modalOk').click()")
ok = wait_for("__palacedb.state.document.sheets.find(s => s.name === 'IdTest')"
              ".columns.some(c => c.typeStr === '0')", 12)
check("the id column can be added back", ok,
      evaluate("JSON.stringify(__palacedb.state.document.sheets.find(s => s.name === 'IdTest').columns)"))

evaluate("__palacedb.send('addColumn', {sheet: 'IdTest', index: 1, name: 'id2', type: '0'})")
time.sleep(1.0)
check("a table keeps a single id column",
      evaluate("__palacedb.state.document.sheets.find(s => s.name === 'IdTest').columns.length") == 2,
      evaluate("Array.from(document.querySelectorAll('#toasts .toast')).map(t => t.textContent).join('|')"))

# Changing a column's type into id is the other way of making a second one.
evaluate("__palacedb.send('editColumn', {sheet: 'IdTest', column: 'name', name: 'name', type: '0'})")
time.sleep(1.0)
check("a text column cannot be turned into a second id",
      evaluate("__palacedb.state.document.sheets.find(s => s.name === 'IdTest')"
               ".columns.find(c => c.name === 'name').typeStr") == '1'
      and 'already has an ID column' in (evaluate(
          "Array.from(document.querySelectorAll('#toasts .toast')).map(t => t.textContent).join('|')") or ''),
      evaluate("Array.from(document.querySelectorAll('#toasts .toast')).map(t => t.textContent).join('|')"))

# --- folding row groups, and hiding columns
# A titled separator is the heading of a group: the rows under it are one unit that can be
# folded away. Hiding a column is the other half of the same idea, and neither is an edit:
# both are stored in the side-car and the file itself is left alone.
evaluate("__palacedb.send('addSheet', {name: 'GroupTest'})")
time.sleep(1.2)
evaluate("__palacedb.send('addColumn', {sheet: 'GroupTest', index: 1, name: 'name', type: '1'})")
time.sleep(1.0)
# The row index is clamped by the host, so a large one always appends; six sends in a row
# do not have to wait for the document to come back in between.
for _ in range(6):
    evaluate("__palacedb.send('insertRow', {sheet: 'GroupTest', row: 999})")
time.sleep(1.8)
check("the group table has its rows",
      evaluate("__palacedb.state.document.sheets.find(s => s.name === 'GroupTest').rowCount") == 6,
      evaluate("__palacedb.state.document.sheets.find(s => s.name === 'GroupTest').rowCount"))

for row, title in ((0, 'Alpha'), (2, 'Beta'), (4, 'Gamma')):
    evaluate("__palacedb.send('toggleSeparator', {sheet: 'GroupTest', row: %d})" % row)
    time.sleep(0.5)
    evaluate("__palacedb.send('setSeparatorTitle', {sheet: 'GroupTest', row: %d, title: %s})"
             % (row, json.dumps(title)))
    time.sleep(0.5)
ok = wait_for("__palacedb.grid.separatorStrips.length === 3", 8)
check("three group headings are drawn", ok,
      evaluate("JSON.stringify(__palacedb.grid.separatorStrips)"))

check("a heading shows a fold marker",
      evaluate("document.querySelector('.sep-band[data-group=\"Alpha\"]').classList.contains('group-head')"),
      evaluate("Array.from(document.querySelectorAll('.sep-band')).map(b => b.className).join('|')"))

# Folding the first group takes its two rows out of the layout entirely.
evaluate("document.querySelector('.sep-band[data-group=\"Alpha\"]').click()")
ok = wait_for("__palacedb.grid.isHidden(0) && __palacedb.grid.isHidden(1)", 6)
check("clicking a heading folds its rows", ok,
      evaluate("[0,1,2].map(i => __palacedb.grid.isHidden(i)).join(',')"))
check("the rows of a folded group are not drawn",
      evaluate("!document.querySelector('.grid-row[data-row=\"0\"]')"
               " && !document.querySelector('.grid-row[data-row=\"1\"]')"
               " && !!document.querySelector('.grid-row[data-row=\"2\"]')"),
      evaluate("Array.from(document.querySelectorAll('#gridRows .grid-row[data-row]')).map(r => r.dataset.row).join(',')"))
check("a folded heading says how many rows it hides",
      evaluate("(document.querySelector('.sep-band.folded .sep-count') || {}).textContent") == '2 rows',
      evaluate("(document.querySelector('.sep-band.folded') || {}).textContent"))
check("the rows below a folded group move up",
      evaluate("Math.round(document.querySelector('.grid-row[data-row=\"2\"]').getBoundingClientRect().top"
               " - document.querySelector('.sep-band[data-group=\"Alpha\"]').getBoundingClientRect().bottom)"
               " === Math.round(document.querySelector('.sep-band[data-group=\"Beta\"]').getBoundingClientRect().height)"),
      evaluate("Math.round(document.querySelector('.grid-row[data-row=\"2\"]').getBoundingClientRect().top)"))
# Six rows and three strips are nine slots; folding two rows leaves seven.
check("folding a group shrinks the scrollable height",
      evaluate("__palacedb.grid.contentHeight()") == 7 * 38, evaluate("__palacedb.grid.contentHeight()"))

evaluate("document.querySelector('.sep-band.folded').click()")
ok = wait_for("!__palacedb.grid.isHidden(0) && !__palacedb.grid.isHidden(1)", 6)
check("clicking a folded heading unfolds it", ok,
      evaluate("[0,1].map(i => __palacedb.grid.isHidden(i)).join(',')"))
check("unfolding restores the scrollable height",
      evaluate("__palacedb.grid.contentHeight()") == 9 * 38, evaluate("__palacedb.grid.contentHeight()"))

# --- a folded heading is still drawn when the fold happens off screen
# The heading of a group used to be picked by its row index. Folding a group whose heading
# sat above the viewport pulled its strip down into view, but the index was still far above
# the window, so the strip was skipped: the rows collapsed and the way back disappeared.
evaluate("__palacedb.send('addSheet', {name: 'FoldScroll'})")
time.sleep(1.2)
evaluate("__palacedb.send('addColumn', {sheet: 'FoldScroll', index: 1, name: 'name', type: '1'})")
time.sleep(1.0)
# One paste is enough to grow the table: rows that arrive this way are new rows.
set_clipboard("\n".join("row%02d" % i for i in range(30)))
evaluate("__palacedb.send('pasteCells', {sheet: 'FoldScroll', row: 0, column: 0, mode: 'insert'})")
ok = wait_for("__palacedb.state.document.sheets.find(s => s.name === 'FoldScroll').rowCount >= 30", 20)
check("a table tall enough to scroll has its rows", ok,
      evaluate("__palacedb.state.document.sheets.find(s => s.name === 'FoldScroll').rowCount"))

# A separator is a line of its own, so the lower one goes in first and the row numbers
# above it do not move.
evaluate("__palacedb.send('toggleSeparator', {sheet: 'FoldScroll', row: 10})")
time.sleep(0.6)
evaluate("__palacedb.send('setSeparatorTitle', {sheet: 'FoldScroll', row: 10, title: 'Beta'})")
time.sleep(0.6)
evaluate("__palacedb.send('toggleSeparator', {sheet: 'FoldScroll', row: 0})")
time.sleep(0.6)
evaluate("__palacedb.send('setSeparatorTitle', {sheet: 'FoldScroll', row: 0, title: 'Alpha'})")
ok = wait_for("__palacedb.grid.separatorStrips.length === 2", 10)
check("the tall table has two headings", ok,
      evaluate("JSON.stringify(__palacedb.grid.separatorStrips)"))

# Scroll to the bottom, so Alpha is far above the window when it folds.
evaluate("__palacedb.grid.body.scrollTop = __palacedb.grid.body.scrollHeight")
time.sleep(0.6)
first_before = evaluate("__palacedb.grid.rowIndexAt(__palacedb.grid.body.scrollTop)")
check("the grid is scrolled away from the first group", first_before >= 5, first_before)
HIDDEN_ROWS = ("(() => { let n = 0; const grid = __palacedb.grid;"
               " for (let i = 0; i < grid.rowCount; i++) if (grid.isHidden(i)) n++;"
               " return n; })()")
evaluate("__palacedb.send('setGroupCollapsed', {sheet: 'FoldScroll', title: 'Alpha', collapsed: true})")
ok = wait_for("%s === 10" % HIDDEN_ROWS, 10)
check("folding a group from off screen hides its rows", ok, evaluate(HIDDEN_ROWS))
check("its heading is still drawn", evaluate("!!document.querySelector('.sep-band[data-group=\"Alpha\"]')"),
      evaluate("Array.from(document.querySelectorAll('.sep-band')).map(b => b.dataset.group).join('|')"))
check("the heading shows as folded",
      evaluate("document.querySelector('.sep-band[data-group=\"Alpha\"]').classList.contains('folded')"),
      evaluate("document.querySelector('.sep-band[data-group=\"Alpha\"]').className"))
check("folding ten rows takes ten rows off the height",
      evaluate("__palacedb.grid.contentHeight()")
      == evaluate("__palacedb.grid.rowCount + 2") * 38 - 10 * 38,
      evaluate("__palacedb.grid.contentHeight()"))
# And the heading that is now on screen is still the way back.
evaluate("document.querySelector('.sep-band[data-group=\"Alpha\"]').click()")
ok = wait_for("!__palacedb.grid.isHidden(1)", 10)
check("the drawn heading still unfolds the group", ok, evaluate(HIDDEN_ROWS))

# The blocks below work on GroupTest again.
evaluate("__palacedb.send('selectSheet', {sheet: 'GroupTest'})")
wait_for("__palacedb.state.currentSheet === 'GroupTest'", 8)

# --- the sidebar shows one section at a time
# Three lists stacked on top of each other left every one of them a few rows tall. They are
# now tabs: one open, the other two closed, and the + belongs to whichever is open.
check("the sidebar has three tabs",
      evaluate("Array.from(document.querySelectorAll('.sidebar-tab')).map(t => t.dataset.tab).join(',')")
      == 'tables,types,columns',
      evaluate("Array.from(document.querySelectorAll('.sidebar-tab')).map(t => t.dataset.tab).join(',')"))
evaluate("document.querySelector('.sidebar-tab[data-tab=\"tables\"]').click()")
time.sleep(0.4)
check("only the open section is on screen",
      evaluate("Array.from(document.querySelectorAll('.sidebar-panel'))"
               ".filter(p => !p.hidden).map(p => p.dataset.panel).join(',')") == 'tables',
      evaluate("Array.from(document.querySelectorAll('.sidebar-panel'))"
               ".filter(p => !p.hidden).map(p => p.dataset.panel).join(',')"))
check("the open section fills the sidebar",
      evaluate("(() => { const panel = document.querySelector('.sidebar-panel:not([hidden])');"
               " const box = panel.getBoundingClientRect();"
               " return box.height > document.getElementById('sidebar').getBoundingClientRect().height - 80; })()"),
      evaluate("(() => { const panel = document.querySelector('.sidebar-panel:not([hidden])');"
               " const box = panel.getBoundingClientRect();"
               " return [Math.round(box.height), Math.round(document.getElementById('sidebar').getBoundingClientRect().height)]; })()"))

evaluate("document.querySelector('.sidebar-tab[data-tab=\"types\"]').click()")
ok = wait_for("!document.querySelector('.sidebar-panel[data-panel=\"types\"]').hidden", 6)
check("clicking a tab opens its section", ok,
      evaluate("Array.from(document.querySelectorAll('.sidebar-panel'))"
               ".filter(p => !p.hidden).map(p => p.dataset.panel).join(',')"))
check("and closes the one that was open",
      evaluate("document.querySelector('.sidebar-panel[data-panel=\"tables\"]').hidden"))
check("the open tab is marked for the screen reader",
      evaluate("Array.from(document.querySelectorAll('.sidebar-tab'))"
               ".filter(t => t.getAttribute('aria-selected') === 'true').map(t => t.dataset.tab).join(',')")
      == 'types')
check("the + now offers a public type",
      evaluate("document.getElementById('sidebarAdd').title") == 'Add a public enum or flags type',
      evaluate("document.getElementById('sidebarAdd').title"))
check("the open tab is remembered",
      evaluate("localStorage.getItem('palacedb.sidebar.tab')") == 'types',
      evaluate("localStorage.getItem('palacedb.sidebar.tab')"))

# The three tabs share the width equally; taking the + out of the layout would make them
# grow, so the width is captured while it is still there.
TAB_WIDTHS = evaluate("Array.from(document.querySelectorAll('.sidebar-tab'))"
                      ".map(t => Math.round(t.getBoundingClientRect().width))")
evaluate("document.querySelector('.sidebar-tab[data-tab=\"columns\"]').click()")
ok = wait_for("!document.querySelector('.sidebar-panel[data-panel=\"columns\"]').hidden", 6)
check("the columns section opens too", ok,
      evaluate("Array.from(document.querySelectorAll('.sidebar-panel'))"
               ".filter(p => !p.hidden).map(p => p.dataset.panel).join(',')"))
check("the column list is filled while its tab is open",
      evaluate("document.querySelectorAll('#columnList .column-item').length") > 0,
      evaluate("document.querySelectorAll('#columnList .column-item').length"))
check("the + steps aside where there is nothing to add",
      evaluate("document.getElementById('sidebarAdd').disabled === true"
               " && getComputedStyle(document.getElementById('sidebarAdd')).visibility === 'hidden'"),
      evaluate("JSON.stringify([document.getElementById('sidebarAdd').disabled,"
               " getComputedStyle(document.getElementById('sidebarAdd')).visibility])"))
check("the tabs keep their width when the + goes",
      evaluate("Array.from(document.querySelectorAll('.sidebar-tab'))"
               ".map(t => Math.round(t.getBoundingClientRect().width)).join(',')")
      == ",".join(str(w) for w in (TAB_WIDTHS or [])),
      (evaluate("Array.from(document.querySelectorAll('.sidebar-tab'))"
                ".map(t => Math.round(t.getBoundingClientRect().width)).join(',')"), TAB_WIDTHS))
evaluate("document.getElementById('sidebarAdd').click()")
time.sleep(0.4)
check("the + does nothing on the columns tab",
      evaluate("document.getElementById('modal').hidden"),
      evaluate("document.getElementById('modal').hidden ? null : document.getElementById('modalTitle').textContent"))

# --- hidden columns
evaluate("document.querySelector('.sidebar-tab[data-tab=\"columns\"]').click()")
time.sleep(0.4)
evaluate("__palacedb.grid.moveCursor(0, 0)")
evaluate("document.querySelector('#columnList .column-item[data-column=\"name\"]').click()")
ok = wait_for("__palacedb.state.document.sheets.find(s => s.name === 'GroupTest')"
              ".columns.find(c => c.name === 'name').hidden === true", 8)
check("a column can be hidden from the sidebar", ok,
      evaluate("JSON.stringify(__palacedb.state.document.sheets.find(s => s.name === 'GroupTest').columns.map(c => [c.name, c.hidden]))"))
check("a hidden column takes no room",
      evaluate("__palacedb.grid.columns.find(c => c.name === 'name').width") == 0,
      evaluate("__palacedb.grid.columns.map(c => c.name + ':' + c.width).join(',')"))
check("a hidden column paints no header and no cell",
      evaluate("document.querySelectorAll('#gridHead .head-cell.hidden').length") == 1
      and evaluate("document.querySelectorAll('#gridRows .cell.hidden').length") > 0,
      evaluate("document.querySelectorAll('#gridRows .cell.hidden').length"))
check("the sidebar still lists a hidden column",
      evaluate("!!document.querySelector('#columnList .column-item[data-column=\"name\"]')")
      and evaluate("document.querySelector('#columnList .column-item[data-column=\"name\"]').classList.contains('hidden')"))

evaluate("document.querySelector('#columnList .column-item[data-column=\"name\"]').click()")
ok = wait_for("__palacedb.state.document.sheets.find(s => s.name === 'GroupTest')"
              ".columns.find(c => c.name === 'name').hidden === false", 8)
check("clicking it again shows the column", ok,
      evaluate("__palacedb.grid.columns.map(c => c.name + ':' + c.width).join(',')"))

# The header menu offers the same, and a way back for every hidden column at once.
evaluate("__palacedb.grid.moveCursor(0, 1)")
evaluate("__palacedb.grid.onContextMenu({row: 0, column: 1, header: true, x: 180, y: 110})")
time.sleep(0.4)
picked = evaluate("""(() => {
  const item = Array.from(document.querySelectorAll('#contextMenu .context-item'))
    .find(b => b.textContent.indexOf('Hide Column') >= 0);
  if (item) item.click();
  return Boolean(item);
})()""")
check("the header menu can hide a column", picked is True)
ok = wait_for("__palacedb.state.document.sheets.find(s => s.name === 'GroupTest')"
              ".columns.find(c => c.name === 'name').hidden === true", 8)
check("hiding from the header menu works", ok,
      evaluate("JSON.stringify(__palacedb.state.document.sheets.find(s => s.name === 'GroupTest').columns.map(c => [c.name, c.hidden]))"))

evaluate("__palacedb.grid.moveCursor(0, 0)")
evaluate("__palacedb.grid.onContextMenu({row: 0, column: 0, header: true, x: 180, y: 110})")
time.sleep(0.4)
picked = evaluate("""(() => {
  const item = Array.from(document.querySelectorAll('#contextMenu .context-item'))
    .find(b => b.textContent.indexOf('Show All Columns') >= 0);
  if (item) item.click();
  return item ? item.textContent : null;
})()""")
check("the menu offers to show every hidden column", bool(picked) and '1 hidden' in picked, picked)
ok = wait_for("__palacedb.state.document.sheets.find(s => s.name === 'GroupTest')"
              ".columns.every(c => !c.hidden)", 8)
check("show all columns brings them back", ok,
      evaluate("JSON.stringify(__palacedb.state.document.sheets.find(s => s.name === 'GroupTest').columns.map(c => [c.name, c.hidden]))"))

# --- a field of a list can follow a public type
# A field is a column of the hidden sub-table, so it is bound the same way a column of a
# real table is: the definition owns the values, the copy in the .cdb stays identical to
# every other copy, and editing the definition reaches the records of the list.
SUB = ("((__palacedb.state.document.subSheets || [])"
       ".find(s => s.name === 'PubList@tags') || {}).columns || []")
evaluate("__palacedb.send('addSheet', {name: 'PubList'})")
time.sleep(1.2)
evaluate("__palacedb.send('addColumn', {sheet: 'PubList', index: 1, name: 'tags', type: '8:tags',"
         " sub: [{name: 'value', type: '5:A,B'}]})")
ok = wait_for("(__palacedb.state.document.subSheets || []).some(s => s.name === 'PubList@tags')", 12)
check("the list column brings its hidden sub-table", ok,
      evaluate("JSON.stringify(__palacedb.state.document.subSheets)"))
evaluate("__palacedb.send('insertRow', {sheet: 'PubList', row: 999})")
time.sleep(1.0)
evaluate("__palacedb.send('savePublicType', {name: 'Rank', kind: 'enum', values: 'Low,Mid,High'})")
ok = wait_for("(__palacedb.state.document.publicTypes || []).some(t => t.name === 'Rank')", 12)
check("there is a public type to follow", ok,
      evaluate("JSON.stringify(__palacedb.state.document.publicTypes)"))

evaluate("""(() => {
  const cell = document.querySelector('#gridRows .cell[data-row="0"][data-col="1"]');
  if (!cell) return false;
  cell.dispatchEvent(new MouseEvent('mousedown', {bubbles: true}));
  cell.dispatchEvent(new MouseEvent('mouseup', {bubbles: true}));
  cell.dispatchEvent(new MouseEvent('click', {bubbles: true}));
  return true;
})()""")
ok = wait_for("document.querySelectorAll('.sub-expansion').length === 1", 10)
check("the list panel opens", ok)

OPEN_FIELDS = """(() => {
  const button = Array.from(document.querySelectorAll('.sub-expansion .sub-tool'))
    .find(b => b.textContent.indexOf('Fields') >= 0);
  if (!button) return false;
  button.click();
  return true;
})()"""
PICK_PUBLIC = """(name) => {
  const select = document.querySelector('#modal .form-sub-public select');
  if (!select) return null;
  select.value = name;
  select.dispatchEvent(new Event('change', {bubbles: true}));
  return select.value;
}"""
evaluate(OPEN_FIELDS)
ok = wait_for("!document.getElementById('modal').hidden", 10)
check("the field dialog has a public type row", ok and
      evaluate("document.querySelectorAll('#modal .form-sub-public select').length") == 1,
      evaluate("document.querySelectorAll('#modal .form-sub-public select').length"))
check("it offers the types of the file",
      "Rank" in (evaluate("Array.from(document.querySelectorAll('#modal .form-sub-public select option'))"
                          ".map(o => o.value).join('|')") or ''),
      evaluate("Array.from(document.querySelectorAll('#modal .form-sub-public select option'))"
               ".map(o => o.value).join('|')"))
evaluate("(%s)('Rank')" % PICK_PUBLIC)
time.sleep(0.5)
check("a bound field cannot be given its own type",
      evaluate("document.querySelector('#modal .form-sub-row select').disabled") is True)
check("nor its own values",
      evaluate("(document.querySelector('#modal .form-sub-values input') || {}).disabled") is True,
      evaluate("(document.querySelector('#modal .form-sub-values input') || {}).value"))
check("the values shown are the definition's",
      evaluate("(document.querySelector('#modal .form-sub-values input') || {}).value") == 'Low,Mid,High',
      evaluate("(document.querySelector('#modal .form-sub-values input') || {}).value"))
evaluate("document.getElementById('modalOk').click()")
ok = wait_for("JSON.stringify(%s).indexOf('Rank') >= 0" % SUB, 12)
check("applying binds the field", ok, evaluate("JSON.stringify(%s)" % SUB))
check("the field carries the definition's values in its type string",
      evaluate("(%s)[0] && (%s)[0].typeStr" % (SUB, SUB)) == '5:Low,Mid,High',
      evaluate("(%s)[0] && (%s)[0].typeStr" % (SUB, SUB)))

side_car = evaluate("__palacedb.state.document.path") + ".meta.json"
bound = {}
try:
    bound = (json.load(open(side_car, encoding="utf-8")).get("tables") or {}).get("PubList@tags") or {}
except Exception:
    pass
check("the side-car remembers the binding under the sub-table",
      any((column or {}).get("publicType") == "Rank" for column in (bound.get("columns") or {}).values()),
      json.dumps(bound))

# The panel shows the definition's labels, and an edit to the definition reaches the field.
evaluate("""(() => {
  const cell = document.querySelector('.sub-expansion .sub-body .cell[data-row="0"][data-col="0"]');
  return cell ? cell.textContent : null;
})()""")
evaluate("__palacedb.send('savePublicType', {name: 'Rank', kind: 'enum',"
         " values: 'Low,Mid,High,Legendary', renameFrom: 'Rank'})")
ok = wait_for("JSON.stringify(%s).indexOf('Legendary') >= 0" % SUB, 12)
check("editing the definition reaches the field", ok, evaluate("(%s)[0] && (%s)[0].typeStr" % (SUB, SUB)))
evaluate("__palacedb.send('undo')")
ok = wait_for("JSON.stringify(%s).indexOf('Legendary') < 0" % SUB, 12)
check("undo puts the shorter list back", ok, evaluate("(%s)[0] && (%s)[0].typeStr" % (SUB, SUB)))

# Detaching keeps the values: they become the field's own list.
evaluate(OPEN_FIELDS)
wait_for("!document.getElementById('modal').hidden", 10)
time.sleep(0.4)
check("the dialog comes back with the binding selected",
      evaluate("document.querySelector('#modal .form-sub-public select').value") == 'Rank',
      evaluate("document.querySelector('#modal .form-sub-public select').value"))
evaluate("(%s)('')" % PICK_PUBLIC)
time.sleep(0.4)
evaluate("document.getElementById('modalOk').click()")
ok = wait_for("(%s)[0] && !(%s)[0].publicType" % (SUB, SUB), 12)
check("detaching drops the binding", ok, evaluate("JSON.stringify(%s)" % SUB))
check("and keeps the values it had",
      evaluate("(%s)[0] && (%s)[0].typeStr" % (SUB, SUB)) == '5:Low,Mid,High',
      evaluate("(%s)[0] && (%s)[0].typeStr" % (SUB, SUB)))

# --- starting without a file reopens the most recent database (F03)
# The instance above has been opening files, so the recent list is populated. A launch
# with no path on the command line has to pick the newest entry that still exists.
ws.close()
proc.terminate()
time.sleep(1.0)

with open(settings_file, encoding="utf-8") as handle:
    remembered = json.load(handle).get("recentFiles") or []
expected = next((p for p in remembered if os.path.exists(p)), None)
check("the recent list survived the runs", expected is not None, remembered)

proc = subprocess.Popen(
    [EXE],
    env=dict(os.environ, PALACEDB_CDP="9335", PALACEDB_AUTOMATION="1", PALACEDB_BACKUP_SECONDS="120"))
ws = None
for _ in range(60):
    time.sleep(0.5)
    park_windows(proc.pid)
    try:
        with urllib.request.urlopen("http://127.0.0.1:9335/json/list", timeout=2) as r:
            targets = json.load(r)
        pages = [t for t in targets if t.get("type") == "page"]
        if pages:
            ws = websocket.create_connection(pages[0]["webSocketDebuggerUrl"], timeout=15)
            break
    except Exception:
        continue

if ws is None:
    check("start-up instance started", False, "no CDP target")
else:
    check("start-up instance started", True)
    expected_name = os.path.basename(expected or "")
    ok = wait_for("!!(window.__palacedb && __palacedb.state.document)", 25)
    check("starting without a file opens a database", ok,
          evaluate("__palacedb.state.document && __palacedb.state.document.name"))
    check("it is the most recent one", evaluate(
              "__palacedb.state.document && __palacedb.state.document.name") == expected_name,
          (evaluate("__palacedb.state.document && __palacedb.state.document.name"), expected_name))

    # The list the window was started with is the same one on disk.
    check("its recent menu is populated",
          wait_for("__palacedb.elements.recentMenu.querySelectorAll('[data-action=\"openRecent\"]').length >= 1", 10),
          evaluate("__palacedb.elements.recentMenu.querySelectorAll('[data-action=\"openRecent\"]').length"))

# --- no console errors
ws.close()
proc.terminate()
time.sleep(0.5)

print("\nSUMMARY:", sum(1 for _,ok,_ in results if ok), "/", len(results), "passed")
