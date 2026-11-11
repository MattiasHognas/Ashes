#!/usr/bin/env perl
use strict; use warnings; use FindBin qw($Bin); use Cwd qw(abs_path); use File::Path qw(make_path);
my $root=abs_path("$Bin/../../.."); my $out="$root/work/handoff-ownership-index"; make_path($out);
sub readfile {open my$f,'<',$_[0]or die$!;local$/;return<$f>;}
sub writefile {open my$f,'>',$_[0]or die$!;print{$f}$_[1];close$f or die$!;}
my$core=readfile("$root/selfhost/packages/semantics/src/AshesCompiler/Semantics/CoreLowering.ash");
my$ownership=readfile("$root/selfhost/packages/semantics/src/AshesCompiler/Semantics/OwnershipInference.ash");
sub function {my($text,$name)=@_;$text=~/(^let (?:recursive )?\Q$name\E\b.*?)(?=^let |^type |\z)/msg or die"missing $name";return$1;}
my$text=readfile("$Bin/Prelude.ash.inc");
$text.=function($ownership,'lookupProgramParameterOwnership');
for my$name(qw(overlayProvenBorrows sameParameterNames registeredTopLevelName provenParameterOwnership)){$text.=function($core,$name);}
open my$git,'-|','git','-C',$root,'show','ce3fbd27:selfhost/packages/semantics/src/AshesCompiler/Semantics/CoreLowering.ash' or die$!;
my$baseline=do{local$/;<$git>};close$git or die 'baseline failed';
my$old=function($baseline,'provenParameterOwnership');$old=~s/\bprovenParameterOwnership\b/oldProvenParameterOwnership/g;$old=~s/stateProgramParameterOwnership/(given (state: CoreLoweringState) -> state.oldTable)/g;
$text.=$old.readfile("$Bin/Assertions.ash.inc");
$text=~s/\(table: ProgramParameterOwnership\)/(table: List((Str, List((Str, ParameterOwnership)))))/g;
writefile("$out/Main.ash",$text);
writefile("$out/ashes.json",'{"name":"handoff-ownership-index","entry":"Main.ash","sourceRoots":["."],"devDependencies":{"semantics":{"path":"../../selfhost/packages/semantics"}},"overrides":{"AshesCompiler.Frontend":{"path":"../../selfhost/packages/frontend"}}}');
writefile("$out/ashes.lock",readfile("$root/selfhost/tests/type-graph-path-cut/ashes.lock"));
