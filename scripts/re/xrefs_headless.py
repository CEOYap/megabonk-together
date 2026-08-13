r"""Who calls this, and who touches that field? — cross-references, headless.

The companion to `decompile_headless.py`, and it exists because `dump.cs` cannot answer this
question **by construction**: the dump lists declarations, while calls and delegate subscriptions
(`+=`) live in method *bodies*. Grepping the dump for a caller therefore always comes back empty,
which reads like "nothing calls it" rather than "the dump does not record that".

Same launch constraints as the decompiler — see its docstring. PyGhidra requires Ghidra to be
started *from* Python, so run this with the venv interpreter and with the Ghidra project closed:

    & "$env:APPDATA\ghidra\ghidra_12.1.2_PUBLIC\venv\Scripts\python.exe" `
        "scripts\re\xrefs_headless.py" 0x180452FB0 RunStats$$AddValue

Arguments (mix freely):
    0x180452FB0        hex VA — every reference *to* this address
    RunStats$$AddValue case-insensitive substring of a symbol name; each match is resolved to its
                       address and then cross-referenced

For an IL2CPP static field such as `InteractableChest.A_ChestOpened`, pass the address of the
field's slot, not of a function. Static field slots live in the type's static-fields block, which
`dump.cs` gives only as an offset (`// 0x8`) — so the usual route is to find a function that already
touches the block, decompile it, and read the concrete address out of that.

Output goes to stdout and to megabonk-re/decompiled/xrefs-<name>.txt so a result can be cited later.
"""

import os
import sys
from pathlib import Path

import pyghidra

GHIDRA_INSTALL = Path(
    os.environ.get("GHIDRA_INSTALL_DIR", r"D:\01 Coding\ghidra_12.1.2_PUBLIC")
)


def _find_repo_root(start: Path) -> Path:
    for candidate in [start, *start.parents]:
        if (candidate / ".git").exists():
            return candidate
    return start.parents[1]


REPO_ROOT = _find_repo_root(Path(__file__).resolve().parent)
PROJECT_DIR = REPO_ROOT / "megabonk-re" / "ghidra-re"
PROJECT_NAME = "Megabonk"
OUT_DIR = REPO_ROOT / "megabonk-re" / "decompiled"

MAX_NAME_MATCHES = 25
MAX_REFS_REPORTED = 200


def safe_filename(name: str) -> str:
    for ch in '<>:"/\\|?*$':
        name = name.replace(ch, "_")
    return name[:120]


def describe(program, addr):
    """Name the function containing an address, so a raw reference means something."""
    fn = program.getFunctionManager().getFunctionContaining(addr)
    if fn is not None:
        return f"{fn.getName()} @ {fn.getEntryPoint()}"
    sym = program.getSymbolTable().getPrimarySymbol(addr)
    if sym is not None:
        return f"{sym.getName()} @ {addr}"
    return f"<no function> @ {addr}"


def resolve_targets(program, args):
    """Turn the arguments into (label, address) pairs."""
    af = program.getAddressFactory()
    targets = []

    for spec in args:
        if spec.lower().startswith("0x"):
            addr = af.getAddress(spec)
            if addr is None:
                print(f"  ! not a valid address: {spec}")
                continue
            targets.append((describe(program, addr), addr))
            continue

        needle = spec.lower()
        matches = []
        for sym in program.getSymbolTable().getAllSymbols(True):
            if needle in sym.getName().lower():
                matches.append(sym)
                if len(matches) > MAX_NAME_MATCHES:
                    break

        if not matches:
            print(f"  ! no symbol matched: {spec}")
            continue

        if len(matches) > MAX_NAME_MATCHES:
            print(f"  ! '{spec}' matched more than {MAX_NAME_MATCHES} symbols; narrow it")
            continue

        for sym in matches:
            targets.append((f"{sym.getName()} @ {sym.getAddress()}", sym.getAddress()))

    return targets


def xref_program(program, args):
    """Report every reference to each resolved target. Returns how many targets were reported."""
    targets = resolve_targets(program, args)
    if not targets:
        return 0

    ref_mgr = program.getReferenceManager()

    for label, addr in targets:
        print("")
        print(f"=== references to {label}")
        refs = list(ref_mgr.getReferencesTo(addr))

        lines = []
        for ref in refs[:MAX_REFS_REPORTED]:
            src = ref.getFromAddress()
            lines.append(f"  {ref.getReferenceType()!s:<14} from {describe(program, src)}")

        # Deduplicated: a caller that references the same target twice is still one caller.
        unique = sorted(set(lines))

        if not unique:
            print("  (none - nothing in the binary references this address)")
        for line in unique:
            print(line)

        if len(refs) > MAX_REFS_REPORTED:
            print(f"  ... {len(refs) - MAX_REFS_REPORTED} more")

        out = OUT_DIR / f"xrefs-{safe_filename(label.split(' @ ')[0])}.txt"
        body = os.linesep.join(unique)
        out.write_text(
            f"references to {label}{os.linesep}{os.linesep}{body}{os.linesep}",
            encoding="utf-8",
        )
        print(f"  -> {out.name}")

    return len(targets)


def main():
    args = sys.argv[1:]
    if not args:
        print(__doc__)
        return 1

    print("Starting Ghidra (headless)...")
    OUT_DIR.mkdir(parents=True, exist_ok=True)
    pyghidra.start(install_dir=GHIDRA_INSTALL)

    project = pyghidra.open_project(PROJECT_DIR, PROJECT_NAME)
    total = 0

    def visit(domain_file, program):
        nonlocal total
        print(f"Program: {domain_file.getPathname()}")
        total += xref_program(program, args)

    try:
        pyghidra.walk_programs(project, visit)
    finally:
        project.close()

    print("")
    print(f"Done. Reported {total} target(s).")
    return 0 if total else 2


if __name__ == "__main__":
    sys.exit(main())
