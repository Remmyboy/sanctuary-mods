"""Validates every mod.json in the repo against docs/mod.schema.json.

    python tools/check-manifests.py        (needs: pip install jsonschema)

The examples and the template are what third-party authors copy, so a
manifest that drifts from the schema spreads. Run by the CI workflow too.
"""
import json
import pathlib
import sys

import jsonschema

repo = pathlib.Path(__file__).resolve().parent.parent
schema = json.loads((repo / "docs" / "mod.schema.json").read_text(encoding="utf-8"))
validator = jsonschema.Draft7Validator(schema)

skip = {"bin", "obj", ".claude", ".git", "release"}
manifests = [p for p in repo.rglob("mod.json") if not skip & set(p.relative_to(repo).parts)]


def render_template(text, kind):
    """The dotnet-new template's manifest as `dotnet new sanctuary-mod --kind <kind>`
    would write it: //#if (gameplay) blocks kept or dropped, TEMPLATEKIND filled."""
    out, keep = [], [True]
    for line in text.splitlines():
        s = line.strip()
        if s.startswith("//#if"):
            keep.append(keep[-1] and ("gameplay" in s) == (kind == "gameplay"))
        elif s.startswith("//#else"):
            keep[-1] = not keep[-1] and keep[-2]
        elif s.startswith("//#endif"):
            keep.pop()
        elif keep[-1]:
            out.append(line)
    return "\n".join(out).replace("TEMPLATEKIND", kind)


failed = checked = 0
for path in sorted(manifests):
    rel = path.relative_to(repo).as_posix()
    text = path.read_text(encoding="utf-8-sig")
    variants = ([(f"{rel} [{k}]", render_template(text, k)) for k in ("ui", "gameplay")]
                if "templates" in path.relative_to(repo).parts else [(rel, text)])
    for name, body in variants:
        checked += 1
        try:
            data = json.loads(body)
        except json.JSONDecodeError as e:
            print(f"{name}: not valid JSON: {e}")
            failed += 1
            continue
        errors = sorted(validator.iter_errors(data), key=lambda e: list(e.path))
        for e in errors:
            where = "/".join(str(p) for p in e.path) or "(root)"
            print(f"{name}: {where}: {e.message}")
        failed += bool(errors)

print(f"{checked} manifest(s) checked, {failed} with problems.")
sys.exit(1 if failed else 0)
