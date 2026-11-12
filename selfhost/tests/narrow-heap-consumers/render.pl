#!/usr/bin/env perl
# Render exact candidate query boundaries beside an immutable historical classifier.
use strict;
use warnings;
use FindBin qw($Bin);
use Cwd qw(abs_path);
use File::Path qw(make_path);

my $fixture = $Bin;
my $root = abs_path("$fixture/../../..");
my $out = "$root/work/narrow-consumers";
make_path($out);
my $source_path = 'selfhost/packages/semantics/src/AshesCompiler/Semantics/';
sub read_file {
    my ($path) = @_;
    open my $file, '<', $path or die "$path: $!";
    local $/;
    return <$file>;
}
sub write_file {
    my ($path, $contents) = @_;
    open my $file, '>', $path or die "$path: $!";
    print {$file} $contents;
    close $file or die "$path: $!";
}
sub frozen_source {
    my ($name) = @_;
    open my $git, '-|', 'git', '-C', $root, 'show', "b19daa03:$source_path$name" or die "git show: $!";
    local $/;
    my $contents = <$git>;
    close $git or die "git show failed for $name";
    return $contents;
}
my $reference = frozen_source('HeapLayoutClassification.ash');
write_file("$out/ReferenceHeapLayoutClassification.ash.inc", $reference);
my @types = ($reference =~ /^type (\w+)/mg, $reference =~ /^    \| ([A-Z]\w*)/mg);
my %types = map { $_ => 1 } @types;
for my $name (sort { length($b) <=> length($a) } keys %types) {
    $reference =~ s/\b\Q$name\E\b/Ref$name/g;
}
my @exports = $reference =~ /^    value (\w+)/mg;
for my $name (@exports) {
    $reference =~ s/\b\Q$name\E\b/ref_$name/g;
}
write_file("$out/ReferenceHeapLayoutClassification.ash", $reference);
my $core = read_file("$root/${source_path}CoreLowering.ash");
sub extract {
    my ($name) = @_;
    $core =~ /(^let \Q$name\E\b.*?)(?=^let |^type |\z)/msg or die "Missing query: $name";
    return $1;
}
my $body = read_file("$fixture/Prelude.ash.inc") . "\n" . read_file("$fixture/Environment.ash.inc");
for my $name (qw(scalarCaptureText classifyClosureCaptureLayout heapClosureCaptureLayoutOf canNormalizeClosureCapture ownedChildrenDroppable)) {
    $body .= "\n" . extract($name);
}
my $old_core = frozen_source('CoreLowering.ash');
$old_core =~ /(^let canNormalizeClosureCapture\b.*?)(?=^let |^type |\z)/msg or die 'Missing old capture query';
my $old_capture = $1;
$old_capture =~ s/let canNormalizeClosureCapture/let oldCanNormalizeClosureCapture/g;
$old_capture =~ s/\QrecordAdtSupported(heapFactsOf(named)(state))\E/oldFact(named)(state)(0)/g;
$old_capture =~ s/\QownedChildAdtSupported(heapFactsOf(named)(state))\E/oldFact(named)(state)(1)/g;
my $assertions = read_file("$fixture/Assertions.ash.inc");
$assertions =~ s/let agrees /$old_capture\nlet agrees /;
$body .= "\n" . $assertions;
write_file("$out/Main.ash", $body);
write_file("$out/ashes.json", '{"name":"narrow-consumers","entry":"Main.ash","sourceRoots":["."],"devDependencies":{"semantics":{"path":"../../selfhost/packages/semantics"}},"overrides":{"AshesCompiler.Frontend":{"path":"../../selfhost/packages/frontend"}}}');
write_file("$out/ashes.lock", read_file("$root/selfhost/tests/type-graph-path-cut/ashes.lock"));
print "$out/ashes.json\n";
