cd /home/mattias/Documents/source/Ashes/.claude/worktrees/stage1-leak-free
dotnet build -c Release src/Ashes.Cli > /dev/null 2>&1
for t in recursive_group_threads_record_state group_sibling_result_passed_to_recursive_call record_result_through_closure_parameter; do
    echo "== $t"
    bash scripts/selfhost-dev/plateau.sh "scripts/selfhost-dev/reproducers/$t.ash" 100000 1000000 2>&1
done
