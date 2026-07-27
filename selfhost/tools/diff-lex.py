#!/usr/bin/env python3
import json
import subprocess
import sys
from pathlib import Path

REPO_ROOT = Path(__file__).resolve().parents[2]
FRONTEND_DIR = REPO_ROOT / "selfhost" / "Frontend"
CLI_PROJECT = REPO_ROOT / "src" / "Ashes.Cli"
LEXDUMP_PROJECT = REPO_ROOT / "selfhost" / "tools" / "LexDump"

ASH_BIN = Path("/tmp/diff-lex-ashes-bin")
CS_DLL = Path("/tmp/diff-lex-cs.dll")


def build():
    print("Compiling self-hosted lexer...", file=sys.stderr)
    r = subprocess.run(
        ["dotnet", "run", "--project", str(CLI_PROJECT), "--", "compile", "--project", "ashes.json", "-o", str(ASH_BIN)],
        cwd=FRONTEND_DIR, capture_output=True, text=True,
    )
    if r.returncode != 0:
        print(r.stdout, r.stderr, file=sys.stderr)
        raise SystemExit(1)

    print("Building C# lexdump harness...", file=sys.stderr)
    r = subprocess.run(
        ["dotnet", "publish", str(LEXDUMP_PROJECT), "-c", "Release", "-o", "/tmp/diff-lex-cs-out"],
        capture_output=True, text=True,
    )
    if r.returncode != 0:
        print(r.stdout, r.stderr, file=sys.stderr)
        raise SystemExit(1)
    global CS_DLL
    CS_DLL = Path("/tmp/diff-lex-cs-out/lexdump.dll")


def run_ashes_lexer(file_path):
    r = subprocess.run([str(ASH_BIN), str(file_path)], capture_output=True, text=True, timeout=20)
    return r.stdout.strip(), r.returncode


def run_csharp_lexer(file_path):
    r = subprocess.run(["dotnet", str(CS_DLL), str(file_path)], capture_output=True, text=True, timeout=20)
    return r.stdout.strip(), r.returncode


def compare_tokens(ash_tokens, cs_tokens):
    if len(ash_tokens) != len(cs_tokens):
        return f"token count mismatch: ashes={len(ash_tokens)} cs={len(cs_tokens)}"
    for i, (a, c) in enumerate(zip(ash_tokens, cs_tokens)):
        for field in ("kind", "text", "intValue", "position", "length"):
            if a.get(field) != c.get(field):
                return f"token[{i}] field '{field}' mismatch: ashes={a.get(field)!r} cs={c.get(field)!r}"
        # Ashes.Text.Json.stringify loses precision below ~7 significant digits when
        # formatting a Float (confirmed independent of the lexer/parseFloat: a hardcoded
        # JsonFloat literal round-trips lossy through stringify alone). Use a relative
        # tolerance sized to that, not lexer correctness.
        av, cv = float(a.get("floatValue", 0)), float(c.get("floatValue", 0))
        if abs(av - cv) > 1e-6 * max(1.0, abs(av), abs(cv)):
            return f"token[{i}] field 'floatValue' mismatch: ashes={av} cs={cv}"
    return None


def main():
    if len(sys.argv) < 2:
        print("usage: diff-lex.py <file1.ash> [file2.ash ...]", file=sys.stderr)
        return 1

    files = [Path(p) for p in sys.argv[1:]]
    build()

    passed = 0
    failed = []
    errored = []

    for i, f in enumerate(files):
        if i % 100 == 0:
            print(f"...{i}/{len(files)}", file=sys.stderr)
        try:
            ash_out, ash_rc = run_ashes_lexer(f)
            cs_out, cs_rc = run_csharp_lexer(f)
        except subprocess.TimeoutExpired:
            errored.append((f, "timeout"))
            continue

        if ash_out.startswith("ERROR:") or cs_out.startswith("ERROR:"):
            errored.append((f, f"read error: ashes={ash_out[:80]!r} cs={cs_out[:80]!r}"))
            continue

        try:
            ash_tokens = json.loads(ash_out)
        except json.JSONDecodeError:
            errored.append((f, f"ashes lexer produced non-JSON output (rc={ash_rc}): {ash_out[:200]!r}"))
            continue

        try:
            cs_tokens = json.loads(cs_out)
        except json.JSONDecodeError:
            errored.append((f, f"cs lexer produced non-JSON output (rc={cs_rc}): {cs_out[:200]!r}"))
            continue

        mismatch = compare_tokens(ash_tokens, cs_tokens)
        if mismatch:
            failed.append((f, mismatch))
        else:
            passed += 1

    print(f"\n{passed}/{len(files)} files matched exactly.")
    if failed:
        print(f"\n{len(failed)} MISMATCHES:")
        for f, msg in failed:
            print(f"  {f}: {msg}")
    if errored:
        print(f"\n{len(errored)} ERRORS (crash/timeout/read failure, not a lexer mismatch):")
        for f, msg in errored:
            print(f"  {f}: {msg}")

    return 0 if not failed and not errored else 1


if __name__ == "__main__":
    sys.exit(main())
