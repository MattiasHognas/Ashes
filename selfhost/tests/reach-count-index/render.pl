#!/usr/bin/env perl
use strict; use warnings;
use FindBin qw($Bin); use Cwd qw(abs_path); use File::Path qw(make_path);
my $root = abs_path("$Bin/../../.."); my $out = "$root/work/reach-count-index"; make_path($out);
my $path = 'selfhost/packages/semantics/src/AshesCompiler/Semantics/ResultReachSummaries.ash';
sub read_file { my ($p)=@_; open my $f,'<',$p or die "$p: $!"; local $/; return <$f>; }
sub write_file { my ($p,$s)=@_; open my $f,'>',$p or die "$p: $!"; print {$f}$s; close $f or die $!; }
open my $git,'-|','git','-C',$root,'show',"beaa9a30:$path" or die $!; local $/; my $old=<$git>; close $git or die 'git show failed';
write_file("$out/ReferenceResultReachSummaries.ash.inc",$old);
my $reference=$old;
my @types=($reference =~ /^type (?:alias )?(\w+)/mg, $reference =~ /^    \| ([A-Z]\w*)/mg); my %types=map {$_=>1} @types;
for my $name(sort {length($b)<=>length($a)} keys %types){$reference =~ s/\b\Q$name\E\b/Ref$name/g;}
my @exports=$reference =~ /^    value (\w+)/mg;
for my $name(@exports){$reference =~ s/\b\Q$name\E\b/ref_$name/g;}
write_file("$out/ReferenceResultReachSummaries.ash",$reference);
my $current=read_file("$root/$path");
sub function_text {my($source,$name)=@_; $source =~ /(^let (?:recursive )?\Q$name\E\b.*?)(?=^let |^type |\z)/msg or die "Missing $name"; return $1;}
my $helpers="import AshesCompiler.Semantics.OwnershipSummary\nimport Ashes.Collection.Map.MapTree\nexport (value indexedSum, value indexedMax, value indexedRepeat, value oldSum, value oldMax, value oldRepeat,)\nlet reachCap = 2\n";
my @currentNames=qw(lookupCount replaceCount holdsCount setCount capped lookupCountOr linear_namesRepeat linear_summedOntoLeft linear_summedBesideLeft linear_maximizedOntoLeft linear_absentFromLeft hasCountIndexSize indexReachCounts lookupIndexedCountOr namesRepeatIndexed namesRepeat sumCountsEntryByEntry summedOntoLeft summedBesideLeft sumCounts maxCountsEntryByEntry maximizedOntoLeft absentFromLeft maxCounts);
for my $name(@currentNames){$helpers.="\n".function_text($current,$name);}
my @oldNames=qw(lookupCount replaceCount holdsCount setCount capped lookupCountOr namesRepeat sumCountsEntryByEntry summedOntoLeft summedBesideLeft sumCounts maxCountsEntryByEntry maximizedOntoLeft absentFromLeft maxCounts);
my $oldHelpers=join("\n",map {function_text($old,$_)} @oldNames);
for my $name(@oldNames){$oldHelpers=~s/\b\Q$name\E\b/old_$name/g;}
$helpers.="\n$oldHelpers\nlet indexedSum left right = sumCounts(left)(right)\nlet indexedMax left right = maxCounts(left)(right)\nlet indexedRepeat entries = namesRepeat(entries)\nlet oldSum left right = old_sumCounts(left)(right)\nlet oldMax left right = old_maxCounts(left)(right)\nlet oldRepeat entries = old_namesRepeat(entries)\n";
write_file("$out/CountQueries.ash",$helpers);
write_file("$out/Main.ash",read_file("$Bin/Main.ash.inc"));
write_file("$out/ashes.json",'{"name":"reach-count-index","entry":"Main.ash","sourceRoots":["."],"devDependencies":{"semantics":{"path":"../../selfhost/packages/semantics"}},"overrides":{"AshesCompiler.Frontend":{"path":"../../selfhost/packages/frontend"}}}');
write_file("$out/ashes.lock",read_file("$root/selfhost/tests/type-graph-path-cut/ashes.lock"));
print "$out/ashes.json\n";
