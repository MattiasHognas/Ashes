#!/usr/bin/env bash
# Differential test: compare the self-hosted Ashes lexer's token stream against
# the reference C# lexer (via the LexDump harness) for a list of .ash files.
set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
REPO_ROOT="$(cd "$SCRIPT_DIR/../.." && pwd)"
FRONTEND_DIR="$REPO_ROOT/selfhost/Frontend"
CLI_PROJECT="$REPO_ROOT/src/Ashes.Cli"
LEXDUMP_PROJECT="$REPO_ROOT/selfhost/tools/LexDump"

ASH_BIN=/tmp/diff-lex-ashes-bin
CS_OUT_DIR=/tmp/diff-lex-cs-out
CS_DLL="$CS_OUT_DIR/lexdump.dll"

if [ "$#" -lt 1 ]; then
  echo "usage: diff-lex.sh <file1.ash> [file2.ash ...]" >&2
  exit 1
fi

if ! command -v jq >/dev/null 2>&1; then
  echo "diff-lex.sh requires jq on PATH" >&2
  exit 1
fi

echo "Compiling self-hosted lexer..." >&2
if ! (cd "$FRONTEND_DIR" && dotnet run --project "$CLI_PROJECT" -- compile --project ashes.json -o "$ASH_BIN") >/tmp/diff-lex-build-ash.log 2>&1; then
  cat /tmp/diff-lex-build-ash.log >&2
  exit 1
fi

echo "Building C# lexdump harness..." >&2
if ! dotnet publish "$LEXDUMP_PROJECT" -c Release -o "$CS_OUT_DIR" >/tmp/diff-lex-build-cs.log 2>&1; then
  cat /tmp/diff-lex-build-cs.log >&2
  exit 1
fi

# Compares two JSON token arrays ($a = ashes lexer, $b = C# lexer). Emits nothing
# on an exact match; emits a description of the first mismatch otherwise.
# floatValue uses a relative tolerance: Ashes.Text.Json.stringify loses precision
# below ~7 significant digits when formatting a Float (confirmed independent of the
# lexer/parseFloat: a hardcoded JsonFloat literal round-trips lossy through
# stringify alone), so exact equality would flag that formatting quirk, not a real
# lexer divergence.
# Reads both token arrays from stdin (slurped, one JSON value per line) rather than
# as --argjson arguments: large token streams can exceed the OS argv size limit.
read -r -d '' COMPARE_JQ <<'JQ_EOF' || true
def abs(x): if x < 0 then -x else x end;
def approx(x; y): (abs(x - y)) <= (0.000001 * ([1, abs(x), abs(y)] | max));

.[0] as $a | .[1] as $b |
if ($a | length) != ($b | length) then
  "token count mismatch: ashes=\($a|length) cs=\($b|length)"
else
  (
    [
      range(0; $a | length)
      | . as $i
      | ($a[$i]) as $ta
      | ($b[$i]) as $tb
      | (["kind", "text", "intValue", "position", "length"] | map(select($ta[.] != $tb[.])) | .[0]) as $field
      | if $field != null then
          "token[\($i)] field \($field) mismatch: ashes=\($ta[$field]) cs=\($tb[$field])"
        elif (approx(($ta.floatValue // 0); ($tb.floatValue // 0)) | not) then
          "token[\($i)] field floatValue mismatch: ashes=\($ta.floatValue // 0) cs=\($tb.floatValue // 0)"
        else
          empty
        end
    ] | first // empty
  )
end
JQ_EOF

trim() {
  local s="$1"
  s="${s#"${s%%[![:space:]]*}"}"
  s="${s%"${s##*[![:space:]]}"}"
  printf '%s' "$s"
}

passed=0
failed_files=()
failed_msgs=()
errored_files=()
errored_msgs=()

total="$#"
count=0
for f in "$@"; do
  if (( count % 100 == 0 )); then
    echo "...$count/$total" >&2
  fi
  count=$((count + 1))

  ash_rc=0
  ash_out=$(timeout 20 "$ASH_BIN" "$f" 2>/dev/null) || ash_rc=$?
  ash_out="$(trim "$ash_out")"
  if [ "$ash_rc" -eq 124 ]; then
    errored_files+=("$f")
    errored_msgs+=("timeout")
    continue
  fi

  cs_rc=0
  cs_out=$(timeout 20 dotnet "$CS_DLL" "$f" 2>/dev/null) || cs_rc=$?
  cs_out="$(trim "$cs_out")"
  if [ "$cs_rc" -eq 124 ]; then
    errored_files+=("$f")
    errored_msgs+=("timeout")
    continue
  fi

  if [[ "$ash_out" == ERROR:* || "$cs_out" == ERROR:* ]]; then
    errored_files+=("$f")
    errored_msgs+=("read error: ashes=${ash_out:0:80} cs=${cs_out:0:80}")
    continue
  fi

  if ! jq -e . >/dev/null 2>&1 <<<"$ash_out"; then
    errored_files+=("$f")
    errored_msgs+=("ashes lexer produced non-JSON output (rc=$ash_rc): ${ash_out:0:200}")
    continue
  fi

  if ! jq -e . >/dev/null 2>&1 <<<"$cs_out"; then
    errored_files+=("$f")
    errored_msgs+=("cs lexer produced non-JSON output (rc=$cs_rc): ${cs_out:0:200}")
    continue
  fi

  mismatch=$(jq -s -r "$COMPARE_JQ" <<<"$ash_out"$'\n'"$cs_out")

  if [ -n "$mismatch" ]; then
    failed_files+=("$f")
    failed_msgs+=("$mismatch")
  else
    passed=$((passed + 1))
  fi
done

echo ""
echo "$passed/$total files matched exactly."

if [ "${#failed_files[@]}" -gt 0 ]; then
  echo ""
  echo "${#failed_files[@]} MISMATCHES:"
  for i in "${!failed_files[@]}"; do
    echo "  ${failed_files[$i]}: ${failed_msgs[$i]}"
  done
fi

if [ "${#errored_files[@]}" -gt 0 ]; then
  echo ""
  echo "${#errored_files[@]} ERRORS (crash/timeout/read failure, not a lexer mismatch):"
  for i in "${!errored_files[@]}"; do
    echo "  ${errored_files[$i]}: ${errored_msgs[$i]}"
  done
fi

if [ "${#failed_files[@]}" -eq 0 ] && [ "${#errored_files[@]}" -eq 0 ]; then
  exit 0
else
  exit 1
fi
