#!/usr/bin/env perl
use strict;
use warnings;
use File::Path qw(make_path);
use File::Copy qw(copy);
my $out = 'work/logical-not-inference';
make_path($out);
copy('selfhost/tests/semantics/CoreLogicalNotInferenceTests.ash', "$out/CoreLogicalNotInferenceTests.ash") or die $!;
open my $main, '>', "$out/Main.ash" or die $!;
print $main <<'ASH';
import CoreLogicalNotInferenceTests
import AshesCompiler.Frontend.Parser
import AshesCompiler.Semantics.CoreLowering
let report result =
    match result with
        | CoreLoweringResult { error = Some(error) } -> Ashes.IO.print("ERROR " + Ashes.Trait.Show.show(error))
        | CoreLoweringResult { semanticType = semanticType, error = None } -> Ashes.IO.print("OK " + Ashes.Trait.Show.show(semanticType))
let read path =
    match Ashes.IO.File.readText(path) with
        | Ok(source) -> source
        | Error(message) -> Ashes.IO.panic(message)
match Ashes.IO.args with
    | [] -> Unit |> runCoreLogicalNotInferenceTests |> (given (_) -> Ashes.IO.print("logical-not inference passed"))
    | "program" :: path :: [] ->
        match parseProgram(read(path)) with
            | ProgramParseResult { program = program, diagnostics = [] } -> report(lowerCoreProgram(program))
            | _ -> Ashes.IO.panic("program did not parse")
    | "expression" :: path :: [] ->
        match parseExpression(read(path)) with
            | ExpressionParseResult { expression = expression, diagnostics = [] } -> report(lowerCoreExpression(expression))
            | _ -> Ashes.IO.panic("expression did not parse")
    | _ -> Ashes.IO.panic("expected no arguments or program/expression path")
ASH

open my $manifest, '>', "$out/ashes.json" or die $!;
print $manifest qq({"name":"logical-not-inference","entry":"Main.ash","sourceRoots":["."],"outDir":"out","devDependencies":{"semantics":{"path":"../../selfhost/packages/semantics"}},"overrides":{"AshesCompiler.Frontend":{"path":"../../selfhost/packages/frontend"}}}\n);
copy('selfhost/tests/semantics/ashes.lock', "$out/ashes.lock") or die $!;
