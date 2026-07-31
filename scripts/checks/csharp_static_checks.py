r"""Two static checks for sessions with no .NET SDK.

Claude Code web sessions have no `dotnet` and the network policy blocks installing one, so a
branch can accumulate a lot of unbuilt change. These two checks catch the errors that mechanical
edits actually produce, using tree-sitter's C# grammar:

  1. **parse** — syntax errors anywhere in the tree.
  2. **scope** — a variable declared inside a `using`/`try` block and used after it. This is the
     failure mode of wrapping existing statements in a new block, it is perfectly valid syntax,
     and only a compiler or this check will find it. It found exactly one real instance while
     converting 27 `CAN_SEND_MESSAGES` sites to `using (Plugin.SuppressOutbound())`
     (`SpawnReviver`'s `desertGraveInstance`).

Neither is a substitute for building. They say nothing about types, overloads, nullability or
whether a game member exists — for that last one see the `dnfile` recipe in
`docs/reverse-engineering/00-decompilation-guide.md`.

    pip install tree_sitter tree_sitter_c_sharp
    python3 scripts/checks/csharp_static_checks.py              # whole src/ tree
    python3 scripts/checks/csharp_static_checks.py FILE [FILE…] # e.g. $(git diff --name-only)

Exit code is non-zero if anything is reported, so it can gate a commit.

The scope check reports `if`/`for`/`foreach`/`while` blocks too when asked (--all-blocks), but
those produce false positives on this codebase: the same name is often re-declared in sibling
scopes, which the check does not model. `using`/`try` — the ones you introduce by wrapping — are
clean, and are what it checks by default.
"""
import glob
import sys

try:
    import tree_sitter_c_sharp
    from tree_sitter import Language, Parser
except ImportError:
    sys.exit("pip install tree_sitter tree_sitter_c_sharp")

PARSER = Parser(Language(tree_sitter_c_sharp.language()))

DEFAULT_OWNERS = ("using_statement", "try_statement")
ALL_OWNERS = DEFAULT_OWNERS + ("if_statement", "for_statement", "foreach_statement", "while_statement")

METHOD_LIKE = ("method_declaration", "constructor_declaration", "local_function_statement", "accessor_declaration")


def _nodes(root):
    stack, out = [root], []
    while stack:
        n = stack.pop()
        out.append(n)
        stack.extend(n.children)
    return out


def parse_errors(path, src, tree):
    return [
        (path, n.start_point[0] + 1, src[n.start_byte:n.start_byte + 70].decode("utf8", "replace"))
        for n in _nodes(tree.root_node)
        if n.type == "ERROR" or n.is_missing
    ]


def scope_breaks(path, src, tree, owners):
    def txt(n):
        return src[n.start_byte:n.end_byte].decode("utf8", "replace")

    def enclosing(n):
        p = n.parent
        while p is not None and p.type not in METHOD_LIKE:
            p = p.parent
        return p

    found = []
    for node in _nodes(tree.root_node):
        if node.type not in owners:
            continue
        for body in (c for c in node.children if c.type == "block"):
            declared = {}
            for x in _nodes(body):
                if x.type in ("variable_declarator", "declaration_expression"):
                    name = x.child_by_field_name("name")
                    if name is not None:
                        declared[txt(name)] = x.start_point[0] + 1
            if not declared:
                continue
            method = enclosing(node)
            if method is None:
                continue
            for x in _nodes(method):
                if x.type == "identifier" and x.start_byte >= body.end_byte and txt(x) in declared:
                    found.append((path, node.start_point[0] + 1, node.type, txt(x), declared[txt(x)], x.start_point[0] + 1))
    return found


def main(argv):
    owners = ALL_OWNERS if "--all-blocks" in argv else DEFAULT_OWNERS
    files = [a for a in argv if not a.startswith("-")] or glob.glob("src/**/*.cs", recursive=True)
    files = [f for f in files if f.endswith(".cs")]

    errors = scopes = 0
    for path in files:
        src = open(path, "rb").read()
        tree = PARSER.parse(src)

        for _, line, snippet in parse_errors(path, src, tree):
            errors += 1
            print(f"{path}:{line}  parse error near {snippet!r}")

        for _, blk, kind, name, decl_line, use_line in scope_breaks(path, src, tree, owners):
            scopes += 1
            print(f'{path}:{blk}  [{kind}] "{name}" declared at line {decl_line} inside the block, used at line {use_line}')

    print(f"checked {len(files)} files | parse errors: {errors} | scope breaks: {scopes}")
    return 1 if (errors or scopes) else 0


if __name__ == "__main__":
    sys.exit(main(sys.argv[1:]))
