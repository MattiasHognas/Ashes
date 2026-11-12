use strict; use warnings; use File::Path qw(make_path); use File::Copy qw(copy);
sub readtext { open my $f,'<',$_[0] or die $!; local $/; <$f> }
sub writefile { open my $f,'>',$_[0] or die $!; print $f $_[1] }
for my $flavor (qw(baseline candidate)) {
 my $out="work/persistent-dropper-$flavor"; make_path($out);
 my $path='selfhost/packages/semantics/src/AshesCompiler/Semantics/StructuralDroppers.ash';
 my $source=$flavor eq 'baseline' ? qx(git show 5804df05:$path) : readtext($path);
 writefile("$out/ProbeDroppers.ash",$source);
 my $main=readtext('selfhost/tests/persistent-dropper-context/Synthesis.ash.inc');
 my $carry=$flavor eq 'baseline' ? 'types with graphIndex = result.graphIndex' : 'withDropperQueryContext(result.queryContext)(types)';
 $main =~ s/CONTEXT_CARRY/$carry/;
 writefile("$out/Main.ash",$main);
 writefile("$out/ashes.json",qq({"name":"persistent-dropper-$flavor","entry":"Main.ash","sourceRoots":["."],"outDir":"out","devDependencies":{"semantics":{"path":"../../selfhost/packages/semantics"}},"overrides":{"AshesCompiler.Frontend":{"path":"../../selfhost/packages/frontend"}}}\n));
 copy('selfhost/tests/dropper-view-check/ashes.lock',"$out/ashes.lock") or die $!;
}
