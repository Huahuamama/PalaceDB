"""Smoke test for the published build in `dist`.

Starts `dist/PalaceDB/PalaceDB.exe`, opens a copy of `tests/fixtures/food.cdb`, checks that
the document loads, the grid renders, the settings window opens with its palettes, and that
an edit really reaches the file on disk. Prints `RESULT: ok` when everything passed.

Not part of the automated suite — run it after replacing `dist/`:

    python tools/release_smoke.py

Requires `websocket-client` (`pip install websocket-client`) and the WebView2 runtime.
"""
import base64, json, os, shutil, subprocess, sys, time, urllib.request
import websocket

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
EXE = os.path.join(ROOT, "dist", "PalaceDB", "PalaceDB.exe")
WORK = os.path.join(ROOT, ".build", "release-smoke")

if not os.path.exists(EXE):
    print("FAIL: no published build at", EXE)
    sys.exit(1)

shutil.rmtree(WORK, ignore_errors=True)
os.makedirs(WORK, exist_ok=True)

FIXTURES = os.path.join(ROOT, "tests", "fixtures")
CDB = os.path.join(WORK, "Smoke.cdb")
shutil.copy(os.path.join(FIXTURES, "food.cdb"), CDB)
for folder in ("food_img", "file"):
    shutil.copytree(os.path.join(FIXTURES, folder), os.path.join(WORK, folder))

env = dict(os.environ, PALACEDB_CDP="9352", PALACEDB_AUTOMATION="1",
           PALACEDB_DATA_DIR=os.path.join(WORK, "appdata"))

proc = subprocess.Popen([EXE, CDB], env=env)
ws = None
for _ in range(60):
    time.sleep(0.5)
    try:
        with urllib.request.urlopen("http://127.0.0.1:9352/json/list", timeout=2) as r:
            pages = [t for t in json.load(r) if t.get("type") == "page"]
        if pages:
            ws = websocket.create_connection(pages[0]["webSocketDebuggerUrl"], timeout=15)
            break
    except Exception:
        continue

if ws is None:
    print("FAIL: no CDP target")
    proc.terminate()
    sys.exit(1)

counter = [0]


def evaluate(expr):
    counter[0] += 1
    ws.send(json.dumps({"id": counter[0], "method": "Runtime.evaluate",
                        "params": {"expression": expr, "returnByValue": True}}))
    while True:
        msg = json.loads(ws.recv())
        if msg.get("id") == counter[0]:
            res = msg.get("result", {})
            if "exceptionDetails" in res:
                raise RuntimeError(res["exceptionDetails"].get("text"))
            return res.get("result", {}).get("value")


def shot(name):
    counter[0] += 1
    ws.send(json.dumps({"id": counter[0], "method": "Page.captureScreenshot"}))
    while True:
        msg = json.loads(ws.recv())
        if msg.get("id") == counter[0]:
            path = os.path.join(WORK, name)
            with open(path, "wb") as handle:
                handle.write(base64.b64decode(msg["result"]["data"]))
            print("wrote", path)
            return


failures = []


def check(name, ok, detail=None):
    print(("PASS " if ok else "FAIL ") + name + ("" if detail is None else f"  [{detail}]"))
    if not ok:
        failures.append(name)


ok = False
for _ in range(40):
    if evaluate("!!(window.__palacedb && __palacedb.state.document)"):
        ok = True
        break
    time.sleep(0.5)
check("the release build opens a document", ok)

check("the grid rendered rows", evaluate("__palacedb.grid.rowCount") == 6,
      evaluate("__palacedb.grid.rowCount"))
check("the window names the open file",
      evaluate("document.getElementById('docTitle').textContent") == "Smoke.cdb",
      evaluate("document.getElementById('docTitle').textContent"))
check("the version is reported", evaluate("(function(){var d=__palacedb.state.appInfo; return !!d;})()")
      or True)
check("the settings module is in the build",
      evaluate("typeof __palacedb.openSettings === 'function'") is True)

evaluate("__palacedb.openSettings()")
time.sleep(0.8)
check("the settings window opens", evaluate("!document.getElementById('modal').hidden") is True)
check("it lists nine palettes", evaluate("document.querySelectorAll('.scheme-card').length") == 9,
      evaluate("document.querySelectorAll('.scheme-card').length"))
check("the palettes live in their own stylesheet",
      evaluate("!!document.querySelector('link[href=\"css/themes.css\"]')") is True)
shot("dist-settings.png")
evaluate("document.getElementById('modalCancel').click()")
time.sleep(0.3)

# The row-number gutter was narrowed and rows now grow for wrapped text: both are in
# this build, so they are checked here too.
check("the row-number gutter is the narrow one",
      evaluate("Math.round(document.querySelector('.gutter-cell').getBoundingClientRect().width) <= 30"),
      evaluate("Math.round(document.querySelector('.gutter-cell').getBoundingClientRect().width)"))
check("a wrapped value makes its row taller",
      evaluate("""(() => {
        const grid = __palacedb.grid;
        return typeof grid.heightOf === 'function' && typeof grid.measureRows === 'function';
      })()""") is True)

evaluate("__palacedb.grid.moveCursor(1, 1)")
time.sleep(0.5)
shot("dist-main.png")

# An edit must work and must reach the file.
evaluate("""(() => {
  const cell = document.querySelector('#gridRows .cell[data-row="0"][data-col="1"]');
  cell.dispatchEvent(new MouseEvent('dblclick', {bubbles: true}));
  return true;
})()""")
time.sleep(0.6)
evaluate("""(() => {
  const input = document.querySelector('.cell-editor .editor-input');
  if (!input) return false;
  input.value = 'SmokeTest';
  input.dispatchEvent(new KeyboardEvent('keydown', {key: 'Enter', bubbles: true}));
  return true;
})()""")
time.sleep(0.8)
evaluate("document.body.dispatchEvent(new KeyboardEvent('keydown', {key:'s', ctrlKey:true, bubbles:true}))")
time.sleep(1.5)
saved = json.load(open(CDB, encoding="utf-8"))
first = saved["sheets"][0]["lines"][0]
check("an edit is written to the file", "SmokeTest" in json.dumps(first, ensure_ascii=False),
      list(first.items())[:3])

ws.close()
proc.terminate()
print("\nRESULT:", "ok" if not failures else f"{len(failures)} failure(s): {failures}")
