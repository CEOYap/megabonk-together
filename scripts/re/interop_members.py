r"""What type is this game field? — answered from the committed interop stubs, offline.

Il2CppInterop turns every IL2CPP field into a managed property, so a field's type is the return
type of its `get_` accessor, and that is recorded in the assembly metadata. No game install, no
`dump.cs`, no GUI: `pip install dnfile` and read the blob.

    python3 scripts/re/interop_members.py MyInputManager
    python3 scripts/re/interop_members.py ChestWindowUi BaseEncounterWindow
    python3 scripts/re/interop_members.py MyInputManager --assembly UnityEngine.CoreModule

What it answers: does the type exist, what does it extend, what members does it have, and what is
each member's type or return type.

What it CANNOT answer: what anything does, and what value a field holds. Bodies are stubs. A
signature is not behaviour — see the UNVERIFIED discipline in
docs/reverse-engineering/00-decompilation-guide.md.
"""
import sys
from pathlib import Path

try:
    import dnfile
except ImportError:
    sys.exit("pip install dnfile")

DEFAULT_ASSEMBLY = "src/plugin/stripped-libs/interop/Assembly-CSharp.dll"

# ECMA-335 II.23.1.16
ELEMENT_TYPES = {
    0x01: "void", 0x02: "bool", 0x03: "char", 0x04: "sbyte", 0x05: "byte",
    0x06: "short", 0x07: "ushort", 0x08: "int", 0x09: "uint", 0x0A: "long",
    0x0B: "ulong", 0x0C: "float", 0x0D: "double", 0x0E: "string",
    0x0F: "ptr", 0x10: "byref", 0x11: "valuetype", 0x12: "class",
    0x14: "array", 0x15: "generic", 0x18: "IntPtr", 0x19: "UIntPtr",
    0x1C: "object", 0x1D: "szarray",
}


def _raw(blob):
    for attr in ("value", "data", "item"):
        v = getattr(blob, attr, None)
        if v is not None and not callable(v):
            try:
                return bytes(v)
            except Exception:
                pass
    return bytes(bytearray(blob))


def _decompress(b, i):
    x = b[i]
    if x & 0x80 == 0:
        return x, i + 1
    if x & 0x40 == 0:
        return ((x & 0x3F) << 8) | b[i + 1], i + 2
    return ((x & 0x1F) << 24) | (b[i + 1] << 16) | (b[i + 2] << 8) | b[i + 3], i + 4


def _type_at(md, b, i):
    et = b[i]
    if et in (0x11, 0x12):
        token, _ = _decompress(b, i + 1)
        table, rid = token & 3, token >> 2
        try:
            row = (md.TypeDef.rows if table == 0 else md.TypeRef.rows)[rid - 1]
            name = f"{row.TypeNamespace}.{row.TypeName}".strip(". ")
            return f"{ELEMENT_TYPES[et]} {name}"
        except Exception:
            return f"{ELEMENT_TYPES[et]} <token {token:#x}>"
    return ELEMENT_TYPES.get(et, f"<0x{et:02x}>")


def return_type(md, signature):
    b = _raw(signature)
    i = 1                       # calling convention
    _, i = _decompress(b, i)    # parameter count
    return _type_at(md, b, i)


def main(argv):
    assembly = DEFAULT_ASSEMBLY
    if "--assembly" in argv:
        at = argv.index("--assembly")
        assembly = argv[at + 1]
        if not Path(assembly).exists():
            assembly = f"src/plugin/stripped-libs/interop/{assembly}"
            if not assembly.endswith(".dll"):
                assembly += ".dll"
        argv = argv[:at] + argv[at + 2:]

    wanted = set(argv)
    if not wanted:
        sys.exit(__doc__)

    pe = dnfile.dnPE(assembly)
    md = pe.net.mdtables

    seen = set()
    for t in md.TypeDef.rows:
        name = str(t.TypeName)
        if name not in wanted:
            continue
        seen.add(name)

        base = t.Extends.row
        base_name = str(base.TypeName) if base is not None and hasattr(base, "TypeName") else "-"
        print(f"=== {str(t.TypeNamespace) or '<global>'}.{name}  : {base_name}")

        for m in t.MethodList:
            r = m.row
            if r is None:
                continue
            member = str(r.Name)
            if member.startswith("get_"):
                print(f"    {member[4:]:44} {return_type(md, r.Signature)}")
            else:
                print(f"    {member + '()':44} -> {return_type(md, r.Signature)}")

    for missing in wanted - seen:
        print(f"!!! {missing} not found in {assembly}")


if __name__ == "__main__":
    main(sys.argv[1:])
