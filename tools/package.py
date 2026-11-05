#!/usr/bin/env python3
"""Package PalaceDB into the local `dist/` directory for a release.

This is the one-command equivalent of the manual release flow:

  1. `dotnet publish` the app to .build/release (Release, win-x64,
     framework-dependent — needs the .NET 8 Desktop Runtime + WebView2 Runtime).
  2. Back up the current dist/PalaceDB to .build/previous-dist.
  3. Swap the fresh build into dist/PalaceDB.
  4. Rebuild the flat zip dist/PalaceDB-<version>-win-x64.zip
     (entries sit at the archive root, e.g. PalaceDB.exe, web/index.html).
  5. Optionally run tools/release_smoke.py to verify the package works.

Usage:
    python tools/package.py                 # version 1.0, builds + smoke test
    python tools/package.py --version 1.1   # custom version string
    python tools/package.py --skip-build    # reuse .build/release, don't rebuild
    python tools/package.py --skip-smoke    # build + pack, skip verification
    python tools/package.py --dry-run       # print every step, change nothing

The script is non-interactive and safe to re-run: it always keeps the last
known-good dist under .build/previous-dist so you can roll back by copying it
back (or by running `python tools/package.py` again — that restores from the
backup before rebuilding).
"""
import argparse
import datetime
import os
import shutil
import subprocess
import sys
import zipfile
from pathlib import Path

ROOT = Path(__file__).resolve().parent.parent
DIST = ROOT / "dist"
BUILD = ROOT / ".build"
RELEASE = BUILD / "release"
PREV = BUILD / "previous-dist"
APP_CSPROJ = ROOT / "src" / "PalaceDB.App" / "PalaceDB.App.csproj"
SMOKE = ROOT / "tools" / "release_smoke.py"


def run(cmd, dry_run=False, **kw):
    print("+ " + " ".join(str(c) for c in cmd))
    if dry_run:
        return
    subprocess.run(cmd, check=True, **kw)


def fmt_size(n):
    units = ("B", "KB", "MB", "GB")
    size = float(n)
    for unit in units:
        if size < 1024 or unit == "GB":
            return f"{size:.0f} {unit}" if unit == "B" else f"{size:.1f} {unit}"
        size /= 1024
    return f"{size:.0f} B"


def main():
    ap = argparse.ArgumentParser(description="Package PalaceDB into dist/")
    ap.add_argument("--version", default="1.0",
                    help="version label baked into the zip name (default: 1.0)")
    ap.add_argument("--skip-build", action="store_true",
                    help="reuse .build/release instead of rebuilding")
    ap.add_argument("--skip-smoke", action="store_true",
                    help="pack but do not run the smoke test")
    ap.add_argument("--dry-run", action="store_true",
                    help="print every step without touching the filesystem")
    args = ap.parse_args()

    dry = args.dry_run
    zip_name = f"PalaceDB-{args.version}-win-x64.zip"
    zip_path = DIST / zip_name
    app_exe = RELEASE / "PalaceDB.exe"
    web_dir = RELEASE / "web"

    print(f"Packaging PalaceDB v{args.version}  (root: {ROOT})")
    if dry:
        print("[dry-run] no files will be changed\n")

    # --- 1. build ---------------------------------------------------------
    if args.skip_build:
        print("Step 1/4  Reusing existing .build/release (--skip-build).")
        if not app_exe.exists():
            print(f"FAIL: no build at {app_exe} — drop --skip-build to rebuild.")
            sys.exit(1)
    else:
        print("Step 1/4  dotnet publish ...")
        if RELEASE.exists():
            print(f"         removing {RELEASE}")
            if not dry:
                shutil.rmtree(RELEASE)
        run(["dotnet", "publish", str(APP_CSPROJ), "-c", "Release", "-r", "win-x64",
             "--self-contained", "false", "-o", str(RELEASE)], dry_run=dry)

    if not dry:
        for needed in (app_exe, web_dir):
            if not needed.exists():
                print(f"FAIL: publish output missing {needed}")
                sys.exit(1)

    # --- 2. back up current dist -----------------------------------------
    print("Step 2/4  Backing up current dist/PalaceDB ...")
    current = DIST / "PalaceDB"
    if current.exists():
        if PREV.exists():
            print(f"         removing stale {PREV}")
            if not dry:
                shutil.rmtree(PREV)
        print(f"         {current} -> {PREV}")
        if not dry:
            shutil.copytree(current, PREV)
    else:
        print("         (no existing dist/PalaceDB to back up)")

    # --- 3. swap in the fresh build --------------------------------------
    print("Step 3/4  Swapping build into dist/PalaceDB ...")
    if current.exists():
        print(f"         removing {current}")
        if not dry:
            shutil.rmtree(current)
    print(f"         {RELEASE} -> {current}")
    if not dry:
        shutil.copytree(RELEASE, current)

    # --- 4. rebuild the flat zip -----------------------------------------
    print(f"Step 4/4  Writing {zip_name} (flat archive) ...")
    if zip_path.exists():
        print(f"         removing old {zip_path}")
        if not dry:
            zip_path.unlink()
    if not dry:
        count = 0
        total = 0
        with zipfile.ZipFile(zip_path, "w", zipfile.ZIP_DEFLATED) as z:
            for path in sorted(current.rglob("*")):
                if path.is_file():
                    arcname = path.relative_to(current).as_posix()
                    z.write(path, arcname)
                    count += 1
                    total += path.stat().st_size
        print(f"         {count} files, {fmt_size(total)} -> {fmt_size(zip_path.stat().st_size)} on disk")
    else:
        print(f"         (would write {zip_path})")

    # --- optional verification -------------------------------------------
    if args.skip_smoke:
        print("\nSmoke test skipped (--skip-smoke).")
    else:
        try:
            import websocket  # noqa: F401
        except ImportError:
            print("\nWARN: `websocket-client` not installed — skipping smoke test.")
            print("      install with: pip install websocket-client")
        else:
            print("\nRunning smoke test (tools/release_smoke.py) ...")
            if not dry:
                run([sys.executable, str(SMOKE)])

    print("\nDONE.")
    print(f"  dist/PalaceDB/         built app (run PalaceDB.exe)")
    if not dry:
        print(f"  {zip_name}  {fmt_size(zip_path.stat().st_size)}")
    print(f"  .build/previous-dist/  previous dist, kept for rollback")


if __name__ == "__main__":
    main()
