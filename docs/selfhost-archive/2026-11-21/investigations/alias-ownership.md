# Alias stage ownership, corrected S0 alias ABI

Compiler: step-f-alias-evidence/src/Ashes.Cli/bin/Release/net10.0/ashes after2 fix.
Source: copied work/alias-evidence/Main.ash, three Eq dictionaries (Str,Str,Int).
Native --debug -O0 prints `dictionary positions passed`, final `ashes-rc-live 3`.

Hardware watchpoint on live counter0x41d018 records exactly three allocations:
- two in alias's last dictionary wrapper lambda9 (source line11)
- one later program baseline cell at source line19.

The two lambda9 cells were separately watched from allocation to process exit:
- raw0x100000001010, RC1,total size72, environment payload0x100000001020 (56bytes).
- raw0x100000001058, RC1,total size48, closure payload0x100000001068 (32bytes).
The closure stores code0x419fb4, env0x100000001020, flags0x2000000000000038, dropper0.
Each refcount is initialized to1, never decremented, and remains1 at exit.
See counter.gdb/stdout and cells.gdb/stdout. Native disassembly is native.asm.

IR lambda9 uses three CallClosure instructions to supply evidence and CopyOutClosure to
normalize the resulting source-function closure. Lambda8 already advertises lambda9's
ReturnsRuntimeManaged=true contract. The missing owner is therefore the result of the LAST
hidden dictionary application, before source arguments are applied.

ApplyTraitDictionaryCallChain currently starts stageTemps only when dictionaries.Count==1.
The three-dictionary alias therefore never records this owned stage for release. In addition,
CalleeBuildsRuntimeManagedStages recognizes runtime-managed MakeClosure/capture facts but does
not necessarily recognize a CopyOutClosure-owned returned closure. Changing the count gate
alone is insufficient proof that a release will be emitted.

Correct boundary: after all source arguments finish and final result normalization settles
ownership, release the proven-owned dictionary-completed source-function closure and its env.
Do not indiscriminately release earlier dictionaries or erase dropper provenance. Partial or
escaping function results require their normal transfer/ownership contract rather than this
terminal scalar-result release. This diagnosis adds no speculative release.
