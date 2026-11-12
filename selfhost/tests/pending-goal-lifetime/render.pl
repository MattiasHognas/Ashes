#!/usr/bin/env perl
use strict;
use warnings;
use File::Path qw(make_path);
use File::Copy qw(copy);
my $mode = shift // 'state';
die "expected state or literal\n" unless $mode eq 'state' || $mode eq 'literal';
my $out = "work/pending-goal-lifetime-$mode";
make_path($out);
my $source = $mode eq 'state' ? 'Main.ash.inc' : 'LiteralControl.ash.inc';
copy("selfhost/tests/pending-goal-lifetime/$source", "$out/Main.ash") or die $!;
open my $manifest, '>', "$out/ashes.json" or die $!;
print $manifest qq({"name":"logical-not-lifetime","entry":"Main.ash","sourceRoots":["."],"outDir":"out","devDependencies":{"semantics":{"path":"../../selfhost/packages/semantics"}},"overrides":{"AshesCompiler.Frontend":{"path":"../../selfhost/packages/frontend"}}}\n);
copy('selfhost/tests/semantics/ashes.lock', "$out/ashes.lock") or die $!;
print "$out\n";
