#!/usr/bin/env perl
use strict;
use warnings;
use File::Path qw(make_path);
use File::Copy qw(copy);
sub read_text { open my $f, '<', $_[0] or die $!; local $/; return <$f>; }
sub block {
    my ($source, $name) = @_;
    $source =~ /^(?:let(?: recursive)?|type) \Q$name\E(?=[ (\n=]).*?(?=^(?:let(?: recursive)?|type|export) |\z)/ms or die "Missing declaration $name";
    return $&;
}
my $out = 'work/logical-not-lifetime';
my $base = 'selfhost/tests/logical-not-inference';
make_path($out);
my $core = read_text('selfhost/packages/semantics/src/AshesCompiler/Semantics/CoreLowering.ash');
my $source = read_text("$base/LifetimePrelude.ash.inc");
$source .= block($core, $_) for qw(clearAdmissibilityAnswers withSubstitution resolveType failure bindType statePendingTraitGoals withStatePendingTraitGoals stateProvenTraitGoals withStateProvenTraitGoals stateTraitGoalsPinned withStateTraitGoalsPinned traitGoalKey pinProvenTraitGoal unprovenTraitGoalKey deferTraitGoal reserveCoreTemps finishCoreLogicalNot finishOrDeferCoreLogicalNot resolvedTraitGoals traitGoalMap);
$source .= read_text("$base/LifetimeAssertions.ash.inc");
open my $main, '>', "$out/Main.ash" or die $!;
print $main $source;
open my $manifest, '>', "$out/ashes.json" or die $!;
print $manifest qq({"name":"logical-not-lifetime","entry":"Main.ash","sourceRoots":["."],"outDir":"out","devDependencies":{"semantics":{"path":"../../selfhost/packages/semantics"}},"overrides":{"AshesCompiler.Frontend":{"path":"../../selfhost/packages/frontend"}}}\n);
copy('selfhost/tests/semantics/ashes.lock', "$out/ashes.lock") or die $!;
