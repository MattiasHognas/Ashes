#!/bin/bash
# usage: addmembers.sh <commit> <file> ; resolves a cherry-pick conflict in a C# test class where both
# sides only added members: keeps HEAD's file and appends every member the commit added (a [Test]
# method or a private static helper, by name) before the class's closing brace.
R=/home/mattias/Documents/source/Ashes/.claude/worktrees/stage1-leak-free
cd "$R" || exit 1
C=$1; F=$2
S=$(mktemp -d)
git show "$C:$F" > "$S/theirs.cs"
git show "$C^:$F" > "$S/base.cs"
git show "HEAD:$F" > "$S/ours.cs"
names() { grep -oE '^    (public async Task|private static []A-Za-z<>[, ]+) [A-Za-z0-9_]+\(' "$1" | sed -E 's/.* ([A-Za-z0-9_]+)\($/\1/' | sort -u; }
names "$S/theirs.cs" > "$S/t"; names "$S/base.cs" > "$S/b"
comm -23 "$S/t" "$S/b" > "$S/new"
: > "$S/add"
while read -r n; do
    awk -v n="$n" '
        { line[NR] = $0 }
        END {
            for (i = 1; i <= NR; i++) if (line[i] ~ ("^    (public async Task|private static []A-Za-z<>[, ]+) " n "\\(")) { s = i; break }
            if (!s) exit
            b = s; if (line[s-1] ~ /^    \[Test\]$/) b = s - 1
            for (j = s; j <= NR; j++) if (line[j] ~ /^    }$/ || line[j] ~ /^            """;$/) { e = j; break }
            print ""
            for (k = b; k <= e; k++) print line[k]
        }' "$S/theirs.cs" >> "$S/add"
done < "$S/new"
last=$(grep -n '^}$' "$S/ours.cs" | tail -1 | cut -d: -f1)
head -n $((last - 1)) "$S/ours.cs" > "$F"
cat "$S/add" >> "$F"
tail -n +$last "$S/ours.cs" >> "$F"
echo "added: $(tr '\n' ' ' < "$S/new")"
rm -rf "$S"
