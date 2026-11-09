# sweep.sh <tpl> <log> : stage-0 live cells at 1/2 rounds under each switch in ${SW:-work/w8c/switches.txt}
cd /home/mattias/Documents/source/Ashes/.claude/worktrees/stage1-post-guards
for s in $(cat ${SW:-work/w8c/switches.txt}); do echo "$s $(bash work/w9c/lk.sh $1 $s=1)" >> $2; done
echo DONE >> $2
