#!/usr/bin/env perl
use strict;
use warnings;
use File::Path qw(make_path);
use File::Copy qw(copy);
my $out = 'work/persistent-dropper-context';
make_path($out);
sub read_text { my ($path)=@_; open my $in,'<',$path or die $!; local $/; return <$in>; }
sub write_text { my ($path,$text)=@_; open my $out,'>',$path or die $!; print {$out} $text; }
my $source = read_text('selfhost/packages/semantics/src/AshesCompiler/Semantics/StructuralDroppers.ash');
$source =~ s/export \(/export (\n    value generalRcAdmissible,/ or die 'export block not found';
write_text("$out/ProbeDroppers.ash",$source);
for my $test (qw(StructuralDroppersTests DropperViewCacheTests DropperAdmissibilityProjectionTests)) {
    my $text=read_text("selfhost/tests/semantics/$test.ash");
    $text =~ s/import AshesCompiler\.Semantics\.StructuralDroppers/import ProbeDroppers/;
    write_text("$out/$test.ash",$text);
}
write_text("$out/Main.ash",read_text('selfhost/tests/persistent-dropper-context/Main.ash.inc'));
write_text("$out/ashes.json",qq({"name":"persistent-dropper-context","entry":"Main.ash","sourceRoots":["."],"outDir":"out","devDependencies":{"semantics":{"path":"../../selfhost/packages/semantics"}},"overrides":{"AshesCompiler.Frontend":{"path":"../../selfhost/packages/frontend"}}}\n));
copy('selfhost/tests/dropper-view-check/ashes.lock',"$out/ashes.lock") or die $!;
