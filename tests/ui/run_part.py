"""Run part of the end-to-end suite.

tests/ui/test_ui.py is one linear script: each section assumes the document state left by
the ones before it, and a full run takes a bit over two minutes - almost all of it fixed
`time.sleep` calls, not protocol traffic. That is too slow to sit through while iterating
on one area.

This runner splits test_ui.py at its `# --- name` markers and executes only the sections
you ask for. It never edits test_ui.py: the file is read as text and the selected blocks
are compiled together, so the suite keeps working exactly as before when run directly.

Usage:
    python tests/ui/run_part.py --list
    python tests/ui/run_part.py --only "column schema editing"
    python tests/ui/run_part.py --only "table management" "separators"
    python tests/ui/run_part.py --from "column schema editing"
    python tests/ui/run_part.py --upto "column schema editing"
    python tests/ui/run_part.py                     # the whole suite

A section may depend on state an earlier one left behind (a column it added, a table it
created). When that happens the run stops with a NameError naming the missing variable;
add the earlier section to the selection, or use --from to keep everything before it.

The full run is still the gate before a commit: it is the only mode that covers every
section in the file.
"""
import os
import sys

HERE = os.path.dirname(os.path.abspath(__file__))
SUITE = os.path.join(HERE, "test_ui.py")
MARKER = "# --- "

# Closing the socket and the process, and printing the summary. Never skipped: without it
# the application would outlive the run and no result line would be printed.
EPILOGUE = "no console errors"

# Where the suite gives up when the real sample is not in the checkout. The sample is
# somebody's real data and is not committed, so a fresh clone stops there.
FOOD_GATE = 'FOOD_SOURCE = os.path.join(FIXTURES, "food.cdb")'
FOOD_SAMPLE = os.path.join(os.path.dirname(HERE), "fixtures", "food.cdb")

# Opening the socket is not enough: the page still has to boot and receive the document.
# Every other section assumes that, so these two always run first.
SETUP = ("wait for the document", "rows arrive")


def load():
    with open(SUITE, encoding="utf-8", newline="") as handle:
        lines = handle.read().split("\n")

    starts = [index for index, line in enumerate(lines) if line.startswith(MARKER)]
    if not starts:
        sys.exit("no section markers found in " + SUITE)

    # The suite stops with exit code 3 when the real sample is missing, so everything from
    # that point on is unreachable in a checkout without it. Knowing which sections those
    # are is the difference between "330 of 639 checks pass" and "the other half never ran".
    # The gate sits inside a section rather than between two, so that section is the first
    # one that cannot finish without the sample - hence >=, not >.
    gate = next((index for index, line in enumerate(lines) if FOOD_GATE in line), len(lines))
    gate_start = max((start for start in starts if start <= gate), default=len(lines))

    prologue = "\n".join(lines[: starts[0]])
    sections = []
    for position, start in enumerate(starts):
        end = starts[position + 1] if position + 1 < len(starts) else len(lines)
        body = "\n".join(lines[start:end])
        name = lines[start][len(MARKER):].strip()
        checks = body.count("check(")
        sections.append({
            "name": name,
            "body": body,
            "checks": checks,
            "needs_food": start >= gate_start,
            "gate": start == gate_start and gate < len(lines),
        })

    return prologue, sections


def resolve(query, sections):
    """Accepts an exact name or an unambiguous part of one."""
    exact = [section for section in sections if section["name"] == query]
    if exact:
        return exact[0]

    lowered = query.lower()
    partial = [section for section in sections if lowered in section["name"].lower()]
    if len(partial) == 1:
        return partial[0]
    if not partial:
        sys.exit(f"no section matches '{query}' - try --list")
    names = "\n  ".join(section["name"] for section in partial)
    sys.exit(f"'{query}' matches several sections:\n  {names}")


def position_of(section, sections):
    return next(index for index, item in enumerate(sections) if item is section)


def main():
    args = sys.argv[1:]
    mode = "all"
    wanted = []
    cdb = None

    index = 0
    while index < len(args):
        arg = args[index]
        if arg == "--list":
            mode = "list"
        elif arg in ("--only", "--from", "--upto"):
            mode = arg[2:]
            index += 1
            if index >= len(args):
                sys.exit(f"{arg} needs a section name")
            wanted.append(args[index])
            while index + 1 < len(args) and not args[index + 1].startswith("--") \
                    and not args[index + 1].endswith(".cdb"):
                index += 1
                wanted.append(args[index])
        elif arg.endswith(".cdb"):
            cdb = arg
        else:
            sys.exit(f"unknown argument '{arg}' - try --list")
        index += 1

    prologue, sections = load()

    if mode == "list":
        total = sum(section["checks"] for section in sections)
        print(f"{len(sections)} sections, {total} check() calls")

        # A section marked here is not broken - it simply does not run in a full run,
        # because the suite gives up at the gate before reaching it.
        blocked = [section for section in sections if section["needs_food"]]
        behind = sum(section["checks"] for section in blocked)
        missing = not os.path.exists(FOOD_SAMPLE)
        if missing:
            print(f"sample missing: {FOOD_SAMPLE}")
            print(f"  {len(blocked)} sections ({behind} checks) do not run; "
                  f"{total - behind} checks are reachable")
        else:
            print(f"sample present: {FOOD_SAMPLE}")
        print()

        width = max(len(section["name"]) for section in sections)
        for position, section in enumerate(sections):
            mark = "  [needs the sample]" if missing and section["needs_food"] else ""
            print(f"{position:3d}  {section['name']:<{width}}  {section['checks']:4d}{mark}")
        return 0

    selected = []
    if mode == "all":
        selected = list(sections)
    elif mode == "only":
        chosen = [resolve(name, sections) for name in wanted]
        selected = [section for section in sections if any(section is c for c in chosen)]
    elif mode == "from":
        first = position_of(resolve(wanted[0], sections), sections)
        selected = sections[first:]
    elif mode == "upto":
        last = position_of(resolve(wanted[0], sections), sections)
        selected = sections[: last + 1]

    epilogue = next((section for section in sections if section["name"] == EPILOGUE), None)
    setup = [section for section in sections if section["name"] in SETUP]

    for section in setup + ([epilogue] if epilogue else []):
        if not any(section is item for item in selected):
            selected.append(section)

    # Keep file order regardless of how the pieces were gathered.
    selected = [section for section in sections if any(section is item for item in selected)]

    checks = sum(section["checks"] for section in selected)
    print(f"running {len(selected)} of {len(sections)} sections ({checks} checks)")
    for section in selected:
        print("   +", section["name"])

    # Say this before the two minutes, not after. Only the section that holds the gate
    # carries the exit; the ones after it merely depend on what it sets up, and fail with
    # a NameError instead - which the handler below turns into an explanation.
    gate_section = next((section for section in sections if section["gate"]), None)
    if gate_section is not None and not os.path.exists(FOOD_SAMPLE) \
            and any(section is gate_section for section in selected):
        print(f"\nnote: {os.path.basename(FOOD_SAMPLE)} is not in this checkout, so the run "
              f"stops in '{gate_section['name']}' with exit code 3.")

    # test_ui.py reads sys.argv[1] as the source database and __file__ as its own path,
    # so both are handed over the way a direct run would see them.
    sys.argv = [SUITE] + ([cdb] if cdb else [])
    scope = {"__name__": "__main__", "__file__": SUITE, "__builtins__": __builtins__}

    source = prologue + "\n" + "\n".join(section["body"] for section in selected)
    try:
        exec(compile(source, SUITE, "exec"), scope)
    except NameError as error:
        process = scope.get("proc")
        if process is not None:
            process.terminate()
        sys.exit(
            f"\nthis section needs state an earlier one left behind: {error}\n"
            "include that section too, or use --from to keep everything before it."
        )
    except SystemExit:
        raise
    except BaseException:
        process = scope.get("proc")
        if process is not None:
            process.terminate()
        raise

    return 0


if __name__ == "__main__":
    sys.exit(main())
