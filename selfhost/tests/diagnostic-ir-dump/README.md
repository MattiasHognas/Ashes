# Opt-in self-hosted IR capture

Compile this fixture plain/poisoned with stage0 and with stage1, with the normal
scoped memory budgets. Run `bash scripts/selfhost-dev/check-diagnostic-ir-dump.sh
<binary>` for each binary. The script retains evidence under a fresh `work/`
directory, checks unset/empty-prefix behavior, compares both dump files byte for
byte with the existing formatter invoked directly, checks case-insensitive
substring filtering and unmatched selections, and checks requested write errors.

The fixture imports only the diagnostic module and IR data/printer modules, so
it does not rebuild CoreLowering. Full debug compiler builds validate the single
CLI integration call. No compiler IR or ownership facts are mutated by the hook.

After the next full compiler build, compile the same tiny input with the prefix
unset and set, compare emitted executables byte for byte, and inspect both dump
files. This integration check confirms that diagnostics preserve native output;
it is separate from this small module-level fixture.

Every hook invocation also renders both original IR inputs afterward and compares
them with independently reconstructed expected programs. This covers the CLI's
reuse of optimized IR for code generation after a successful or failed dump;
poisoned execution catches premature releases during that reuse.

Validation: stage0 plain and poisoned pass every output/filter/error/reuse check,
with **1 live cell** in every case. Bootstrap17 stage1 also passes correctness
and input reuse, but its existing IR formatter retains cells: a direct full
formatter oracle exits at10 (baseline1), filtered oracle4, unmatched oracle1.
The reuse guard invokes four full formatter calls, so disabled/empty exits at37;
full hook55, filtered43, unmatched37, first-write failure46, second-write failure55.
Each is exactly the guard baseline plus the corresponding direct-formatter oracle
cost. The script asserts this matched baseline rather than claiming stage1 flatness.
No source allocation workaround or general formatter/RC fix is included.

Final fixture compilation: stage0 22.79s, poisoned22.50s, stage1 1:53.94;
maximum measured RSS1,148,648KiB. The full debug compiler integration/binary-identity
smoke remains for the next build. Original parser premature-owner-release evidence
is retained separately under `work/parser-original-crash` and the ownership worker's
`work/ownership-lookup/s1-original-peer-*.log`.
