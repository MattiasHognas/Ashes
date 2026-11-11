#!/usr/bin/env python3
"""Render exact candidate query boundaries beside an immutable historical classifier."""
import pathlib
import re
import subprocess
root = pathlib.Path(__file__).resolve().parents[3]
fixture = pathlib.Path(__file__).resolve().parent
out = root / "work/narrow-consumers"
out.mkdir(parents=True, exist_ok=True)
path = "selfhost/packages/semantics/src/AshesCompiler/Semantics/"
reference = subprocess.check_output(["git", "show", "b19daa03:" + path + "HeapLayoutClassification.ash"], cwd=root).decode()
(out / "ReferenceHeapLayoutClassification.ash.inc").write_text(reference)
names = set(re.findall(r"^type (\w+)", reference, re.M) + re.findall(r"^    \| ([A-Z]\w*)", reference, re.M))
for name in sorted(names, key=len, reverse=True):
    reference = re.sub(r"\b" + name + r"\b", "Ref" + name, reference)
for name in re.findall(r"^    value (\w+)", reference, re.M):
    reference = re.sub(r"\b" + name + r"\b", "ref_" + name, reference)
(out / "ReferenceHeapLayoutClassification.ash").write_text(reference)
core = (root / path / "CoreLowering.ash").read_text()
def extract(name):
    match = re.search(r"^let " + name + r"\b.*?(?=^let |^type |\Z)", core, re.M | re.S)
    if match is None:
        raise RuntimeError(name)
    return match.group(0)
body = (fixture / "Prelude.ash.inc").read_text()
body += "\n" + (fixture / "Environment.ash.inc").read_text()
for name in ["scalarCaptureText", "classifyClosureCaptureLayout", "heapClosureCaptureLayoutOf", "canNormalizeClosureCapture", "ownedChildrenDroppable"]:
    body += "\n" + extract(name)
old_core = subprocess.check_output(["git", "show", "b19daa03:" + path + "CoreLowering.ash"], cwd=root).decode()
old_capture = re.search(r"^let canNormalizeClosureCapture\b.*?(?=^let |^type |\Z)", old_core, re.M | re.S).group(0)
old_capture = old_capture.replace("let canNormalizeClosureCapture", "let oldCanNormalizeClosureCapture")
old_capture = old_capture.replace("recordAdtSupported(heapFactsOf(named)(state))", "oldFact(named)(state)(0)").replace("ownedChildAdtSupported(heapFactsOf(named)(state))", "oldFact(named)(state)(1)")
assertions = (fixture / "Assertions.ash.inc").read_text()
marker = "let agrees "
assertions = assertions.replace(marker, old_capture + "\n" + marker, 1)
body += "\n" + assertions
(out / "Main.ash").write_text(body)
(out / "ashes.json").write_text('{"name":"narrow-consumers","entry":"Main.ash","sourceRoots":["."],"devDependencies":{"semantics":{"path":"../../selfhost/packages/semantics"}},"overrides":{"AshesCompiler.Frontend":{"path":"../../selfhost/packages/frontend"}}}')
(out / "ashes.lock").write_bytes((root / "selfhost/tests/type-graph-path-cut/ashes.lock").read_bytes())
print(out / "ashes.json")
