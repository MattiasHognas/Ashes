#!/usr/bin/env perl
use strict;
use warnings;
use File::Path qw(make_path);
my $out = 'work/recursive-replay';
make_path($out);
open my $source, '<', 'selfhost/packages/semantics/src/AshesCompiler/Semantics/CoreLowering.ash' or die $!;
my $core = do { local $/; <$source> };
$core =~ /^let lowerRecursiveMembersPinningSiblingResults .*?(?=^let recursive addRecursiveGroupContinuationBindings)/ms or die 'missing recursive replay helper';
my $helper = $&;
open my $main, '>', "$out/Main.ash" or die $!;
print $main <<'ASH';
// The orchestration helper below is extracted unchanged from production Core.
// Its member lowerer is a callback that reports every invocation and models
// discovery of a trait goal or a settled sibling result.
type CoreLoweringState =
    | pinned: Bool
    | pending: List(Int)
    | siblings: List(Int)
    | proven: Int
    | mode: Int

type LoweredCoreValue =
    | state: CoreLoweringState
    | error: Maybe(Int)

let stateTraitGoalsPinned state = state.pinned
let withStateTraitGoalsPinned value (state: CoreLoweringState) = state with pinned = value
let statePendingTraitGoals state = state.pending
let statePendingSiblingCallResults state = state.siblings
let stateProvenSiblingCallResults state = state.proven
let withStateProvenSiblingCallResults value (state: CoreLoweringState) = state with proven = value
let coreListLength values = Ashes.Collection.List.length(values)
let settledSiblingCallResults pending count state proven settled =
    if count > 0 then (1, true) else (proven, settled)
let lowerPreparedRecursiveMembers members selfBindings captures environmentTemp lower state = lower(state)
let memberLower (state: CoreLoweringState) =
    Ashes.IO.print("member")
    |> (given (_) ->
        match state.mode with
            | 1 ->
                if state.pinned
                then LoweredCoreValue(state = state, error = Some(1))
                else LoweredCoreValue(state = (state with pending = 1 :: state.pending), error = None)
            | 2 ->
                if state.proven == 0
                then LoweredCoreValue(state = (state with siblings = 1 :: state.siblings), error = None)
                else LoweredCoreValue(state = state, error = None)
            | _ -> LoweredCoreValue(state = state, error = None))
ASH
print $main $helper;
print $main <<'ASH';
let check (label: Str) pinned pending mode expectedError =
    Ashes.IO.print(label)
    |> (given (_) ->
        match lowerRecursiveMembersPinningSiblingResults([])([])([])(0)(memberLower)(CoreLoweringState(pinned = pinned, pending = pending, siblings = [], proven = 0, mode = mode)) with
            | LoweredCoreValue { state = finalState, error = error } ->
                if finalState.pinned == pinned && error == expectedError
                then Ashes.IO.print("passed")
                else Ashes.IO.panic("recursive replay lost pinned boundary"))
Unit
|> (given (_) -> check("pinned-empty")(true)([])(0)(None))
|> (given (_) -> check("unpinned-empty")(false)([])(0)(None))
|> (given (_) -> check("pinned-new-goal")(true)([])(1)(Some(1)))
|> (given (_) -> check("unpinned-new-goal")(false)([])(1)(None))
|> (given (_) -> check("pinned-prior-goal")(true)([9])(0)(None))
|> (given (_) -> check("pinned-settled-sibling")(true)([])(2)(None))
|> (given (_) -> check("unpinned-settled-sibling")(false)([])(2)(None))
ASH
open my $manifest, '>', "$out/ashes.json" or die $!;
print $manifest qq({"name":"recursive-replay","entry":"Main.ash","sourceRoots":["."],"outDir":"out"}\n);
open my $expected, '>', "$out/expected.txt" or die $!;
print $expected <<'EXPECTED';
pinned-empty
member
passed
unpinned-empty
member
passed
pinned-new-goal
member
member
passed
unpinned-new-goal
member
passed
pinned-prior-goal
member
member
passed
pinned-settled-sibling
member
member
passed
unpinned-settled-sibling
member
member
passed
EXPECTED
