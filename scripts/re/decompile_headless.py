r"""Standalone headless batch decompiler for the Megabonk Ghidra project.

analyzeHeadless.bat CANNOT run this kind of script: it launches Ghidra from launch.bat, so the
PyGhidra script provider is never initialised and any .py post-script dies with
"Ghidra was not started with PyGhidra". PyGhidra requires Ghidra to be started *from* Python.
This script does that via the pyghidra API, so it needs no GUI and no analyzeHeadless.

Run it with the venv interpreter pyghidra was installed into (NOT your system Python —
pyghidra is not on the global path):

    & "$env:APPDATA\ghidra\ghidra_12.1.2_PUBLIC\venv\Scripts\python.exe" `
        "scripts\re\decompile_headless.py" 0x18046D990 0x1803EBDE0 DamageContainer

Arguments:
    0x18046D990       hex VA — decompiles the function containing it
    DamageContainer   case-insensitive substring of a function name (capped at 40 matches)
    @0x18262EC7C      reads the CONSTANT at that address as float/int32/double

Ghidra names use '$$' separators (ChargeShrine$$Start), not '__'. Single-quote patterns in
PowerShell or $$ is eaten as a variable.

Output: megabonk-re/decompiled/<FunctionName>.c, one file per function.

Set GHIDRA_INSTALL_DIR if your Ghidra is not at the path assumed below.
Ghidra must not have the project open — it takes an exclusive lock.

Full workflow: docs/reverse-engineering/00-decompilation-guide.md
"""

import os
import sys
from pathlib import Path

import pyghidra

# Ghidra install. Override per machine with the GHIDRA_INSTALL_DIR environment variable;
# the fallback is only a convenience for the default layout described in
# docs/reverse-engineering/00-decompilation-guide.md.
GHIDRA_INSTALL = Path(
    os.environ.get("GHIDRA_INSTALL_DIR", r"D:\01 Coding\ghidra_12.1.2_PUBLIC")
)
def _find_repo_root(start: Path) -> Path:
    """Walk up until we hit the repo root (the directory containing .git)."""
    for candidate in [start, *start.parents]:
        if (candidate / ".git").exists():
            return candidate
    # Fall back to two levels up from scripts/re/ rather than guessing wrong.
    return start.parents[1]


REPO_ROOT = _find_repo_root(Path(__file__).resolve().parent)
PROJECT_DIR = REPO_ROOT / "megabonk-re" / "ghidra-re"
PROJECT_NAME = "Megabonk"
OUT_DIR = REPO_ROOT / "megabonk-re" / "decompiled"

TIMEOUT_SECONDS = 120
MAX_NAME_MATCHES = 40


def safe_filename(name: str) -> str:
    for ch in '<>:"/\\|?*':
        name = name.replace(ch, "_")
    return name[:150]


def read_constants(args, program):
    """Handle @0xADDR args: dump the bytes at an address as float/int, for resolving DAT_ globals."""
    import struct

    af = program.getAddressFactory()
    mem = program.getMemory()
    consts = [a for a in args if a.startswith("@")]
    if not consts:
        return

    print("Constants:")
    for spec in consts:
        text = spec[1:]
        addr = af.getAddress(text)
        if addr is None:
            print(f"  ! not a valid address: {text}")
            continue
        try:
            raw = bytearray(8)
            for i in range(8):
                raw[i] = mem.getByte(addr.add(i)) & 0xFF
            f32 = struct.unpack("<f", bytes(raw[:4]))[0]
            i32 = struct.unpack("<i", bytes(raw[:4]))[0]
            f64 = struct.unpack("<d", bytes(raw))[0]
            print(f"  {text}: float={f32!r}  int32={i32}  double={f64!r}  "
                  f"bytes={' '.join(f'{b:02X}' for b in raw)}")
        except Exception as exc:
            print(f"  ! could not read {text}: {exc}")


def collect_targets(args, program):
    fm = program.getFunctionManager()
    af = program.getAddressFactory()
    found = []

    for arg in args:
        if arg.startswith("@"):
            continue  # handled by read_constants
        if arg.lower().startswith("0x"):
            addr = af.getAddress(arg)
            if addr is None:
                print(f"  ! not a valid address: {arg}")
                continue
            fn = fm.getFunctionContaining(addr)
            if fn is None:
                print(f"  ! no function at {arg}")
                continue
            found.append(fn)
        else:
            needle = arg.lower()
            matches = [f for f in fm.getFunctions(True) if needle in f.getName().lower()]
            if not matches:
                print(f"  ! no function name contains '{arg}'")
            if len(matches) > MAX_NAME_MATCHES:
                print(f"  ! '{arg}' matched {len(matches)} functions; "
                      f"taking the first {MAX_NAME_MATCHES}")
                matches = matches[:MAX_NAME_MATCHES]
            found.extend(matches)

    seen, unique = set(), []
    for f in found:
        key = f.getEntryPoint().toString()
        if key not in seen:
            seen.add(key)
            unique.append(f)
    return unique


def decompile_program(program, args):
    from ghidra.app.decompiler import DecompInterface
    from ghidra.util.task import ConsoleTaskMonitor

    OUT_DIR.mkdir(parents=True, exist_ok=True)

    read_constants(args, program)

    decompiler = DecompInterface()
    decompiler.openProgram(program)
    monitor = ConsoleTaskMonitor()
    written = 0

    try:
        targets = collect_targets(args, program)
        if not targets:
            print("nothing resolved - check the addresses/names")
            return 0

        print(f"Decompiling {len(targets)} function(s) -> {OUT_DIR}")
        for fn in targets:
            name = fn.getName()
            entry = fn.getEntryPoint()
            result = decompiler.decompileFunction(fn, TIMEOUT_SECONDS, monitor)

            if not result.decompileCompleted():
                print(f"  FAIL {name} @ {entry} : {result.getErrorMessage()}")
                continue

            code = result.getDecompiledFunction().getC()
            path = OUT_DIR / (safe_filename(name) + ".c")
            path.write_text(
                f"// {name}\n// entry: {entry}\n// program: {program.getName()}\n\n{code}",
                encoding="utf-8",
            )
            print(f"  OK   {name} @ {entry}")
            written += 1
    finally:
        decompiler.dispose()

    return written


def main():
    args = sys.argv[1:]
    if not args:
        print(__doc__)
        return 1

    print("Starting Ghidra (headless)...")
    pyghidra.start(install_dir=GHIDRA_INSTALL)

    project = pyghidra.open_project(PROJECT_DIR, PROJECT_NAME)
    total = 0

    def visit(domain_file, program):
        nonlocal total
        print(f"Program: {domain_file.getPathname()}")
        total += decompile_program(program, args)

    try:
        pyghidra.walk_programs(project, visit)
    finally:
        project.close()

    print(f"Done. Wrote {total} file(s).")
    return 0 if total else 2


if __name__ == "__main__":
    sys.exit(main())
