#!/usr/bin/env perl
use strict; use warnings; use File::Path qw(make_path); use File::Copy qw(copy);
sub readtext {open my $f,'<',$_[0] or die $!; local $/; <$f>}
sub block {my ($s,$name)=@_; $s =~ /^(?:let(?: recursive)?|type) \Q$name\E(?=[ (\n=]).*?(?=^(?:let(?: recursive)?|type|export) |\z)/ms or die "Missing declaration $name"; return $&;}
my $base='selfhost/tests/core-query-context'; my $out='work/core-query-context'; make_path($out);
my $path='selfhost/packages/semantics/src/AshesCompiler/Semantics/CoreLowering.ash'; my $source=readtext($path);
my $text=readtext("$base/Prelude.ash.inc");
$text.=block($source,$_) for qw(CoreHeapQueryFacts CoreHeapQueryContext emptyCoreHeapQueryFacts emptyCoreHeapQueryContext CoreConstructorLayout);
$text.=readtext("$base/State.ash.inc");
$text.=block($source,$_) for qw(stateCoverageTypes withStateCoverageTypes stateHeapQueryContext withStateHeapQueryContext coreHeapQueryFacts queryCoreCopyPlan queryCoreAdmissibility queryCoreShallowCopy queryCoreChildren queryCoreChildTypes stateDropperTypes withStateDropperTypes withStateDropperContext restoreHeapQueryContexts);
$text.="let coverageEnvironment state = stateCoverageTypes(state)\n\n";
$text.=block($source,$_) for qw(admissibilityChildOf heapShallowCopyOfIndexed heapAdmissibilityOfIndexed heapCopyPlanFactsOfIndexed heapChildrenOfNamedIndexed heapChildTypesOfNamedIndexed ScopeCopyOut ArgumentCopyPlan GeneralRcAdmissibilityWork prependAdmissibilityTypes prependAdmissibilityChildren admissibilityVisitStatus originalGeneralRcAdmissibleIndexed searchGeneralRcAdmissibilityIndexed queryGeneralRcAdmissibilityIndexed isGeneralRcAdmissibleIndexed runtimeManagedCopyPlanLayout ownedCopyPlanChildren anyCopyPlanChildSurvivesNoReset copyPlanOfIndexed argumentCopyPlanOfIndexed generalCopyPlanOfIndexed anyChildSurvivesNoReset isGeneralRcNamedTypeIndexed reachesGeneralRcNamedTypeIndexed scopeCopyOutOfIndexed callCopyOutOfIndexed isGeneralRcValueTypeIndexed);
my $old=readtext("$base/Oracle.ash.inc"); $text.=$old;
$text.=readtext("$base/Assertions.ash.inc");
open my $main,'>',"$out/Main.ash" or die $!; print $main $text;
open my $manifest,'>',"$out/ashes.json" or die $!; print $manifest qq({"name":"core-query-context","entry":"Main.ash","sourceRoots":["."],"outDir":"out","devDependencies":{"semantics":{"path":"../../selfhost/packages/semantics"}},"overrides":{"AshesCompiler.Frontend":{"path":"../../selfhost/packages/frontend"}}}\n);
copy('selfhost/tests/dropper-view-check/ashes.lock',"$out/ashes.lock") or die $!;
