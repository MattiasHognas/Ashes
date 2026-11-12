#!/usr/bin/env perl
use strict; use warnings; use File::Path qw(make_path); use File::Copy qw(copy); use Digest::SHA qw(sha256_hex);
sub readtext { open my $f,'<',$_[0] or die $!; local $/; <$f> }
sub writefile { open my $f,'>',$_[0] or die $!; print $f $_[1] }
my $module='selfhost/packages/semantics/src/AshesCompiler/Semantics/StructuralDroppers.ash';
my $candidate=$ARGV[0] // $module;
my $baseline=qx(git show c0e23905:$module); die 'baseline missing' if !$baseline;
for my $flavor (qw(baseline candidate)) {
    my $out="work/dropper-worklist-ir-$flavor"; make_path($out);
    my $dropper=$flavor eq 'baseline' ? $baseline : readtext($candidate);
    writefile("$out/ProbeDroppers.ash",$dropper);
    for my $name (qw(StructuralCopiers ToSpaceCopiers)) {
        my $text=readtext("selfhost/packages/semantics/src/AshesCompiler/Semantics/$name.ash");
        $text =~ s/import AshesCompiler\.Semantics\.StructuralDroppers/import ProbeDroppers/g;
        writefile("$out/Probe$name.ash",$text);
    }
    for my $name (qw(StructuralDroppersTests DropperViewCacheTests DropperAdmissibilityProjectionTests)) {
        my $text=readtext("selfhost/tests/semantics/$name.ash");
        $text =~ s/import AshesCompiler\.Semantics\.StructuralDroppers/import ProbeDroppers/g;
        writefile("$out/$name.ash",$text);
    }
    my $copier=readtext('selfhost/tests/dropper-view-check/CopierFixture.ash');
    $copier =~ s/import AshesCompiler\.Semantics\.StructuralDroppers/import ProbeDroppers/g;
    $copier =~ s/import AshesCompiler\.Semantics\.(StructuralCopiers|ToSpaceCopiers)/"import Probe".$1/ge;
    writefile("$out/CopierFixture.ash",$copier);
    writefile("$out/Main.ash",readtext('selfhost/tests/dropper-worklist-ir/Main.ash.inc'));
    writefile("$out/ashes.json",qq({"name":"dropper-worklist-ir-$flavor","entry":"Main.ash","sourceRoots":["."],"outDir":"out","devDependencies":{"semantics":{"path":"../../selfhost/packages/semantics"}},"overrides":{"AshesCompiler.Frontend":{"path":"../../selfhost/packages/frontend"}}}\n));
    copy('selfhost/tests/dropper-view-check/ashes.lock',"$out/ashes.lock") or die $!;
    writefile("$out/dropper-sha256.txt",sha256_hex($dropper)."\n");
}
