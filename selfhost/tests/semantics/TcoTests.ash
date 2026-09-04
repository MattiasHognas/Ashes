// Pure-Ashes tests for tail-call optimization, mutual recursion dispatch, and TCO placement cost signals.

import Ashes.IO
import Ashes.Test as test
import AshesCompiler.Frontend.Syntax
import AshesCompiler.Semantics.TcoAnalysis
import AshesCompiler.Semantics.TcoPromotionCostSignal
import AshesCompiler.Semantics.Types
export (
    value runTcoTests,
)

let dummyFacts ordinal name slot =
    TcoParamStaticFacts(
        paramOrdinal = ordinal,
        paramName = name,
        slot = slot,
        hasVisibleBinding = true,
        loopInvariant = false,
        freshRebuiltList = false,
        freshClosureRebuild = false,
        bytesProvenanceSafeListRebuild = false,
        affineConsList = false,
        consumedListTail = false,
        borrowInspectOnly = false,
        affineSelfAppendOnly = false
    )

let testOrdinaryTailCallDetection unit =
    (let _ = test.assertEqual(true)(isTailPosition(InTailPos))
    in
        let _ = test.assertEqual(false)(isTailPosition(NotInTailPos))
        in
            let unaryLam = ExprLambda("x")(ExprVar("x"))(None)
            in
                let binaryLam = ExprLambda("x")(ExprLambda("y")(ExprVar("x"))(None))(None)
                in
                    let _ = test.assertEqual(1)(countLambdaArity(0)(unaryLam))
                    in
                        let _ = test.assertEqual(2)(countLambdaArity(0)(binaryLam))
                        in
                            let params = collectLambdaParamNames([])(binaryLam)
                            in
                                let _ = test.assertEqual(2)(Ashes.Collection.List.length(params))
                                in
                                    let tailCallExpr =
                                        ExprIf(
                                            ExprVar("cond"),
                                            ExprCall(
                                                ExprCall(
                                                    ExprVar("myFunc"),
                                                    ExprVar("a"),
                                                    false,
                                                    callArgumentsInline
                                                ),
                                                ExprVar("b"),
                                                false,
                                                callArgumentsInline
                                            ),
                                            ExprVar("c")
                                        )
                                    in
                                        let nonTailCallExpr =
                                            ExprIf(
                                                ExprCall(
                                                    ExprVar("myFunc"),
                                                    ExprVar("a"),
                                                    false,
                                                    callArgumentsInline
                                                ),
                                                ExprVar("b"),
                                                ExprVar("c")
                                            )
                                        in
                                            let _ = test.assertEqual(true)(hasTailSelfCalls(tailCallExpr)("myFunc")(2))
                                            in
                                                let _ = test.assertEqual(false)(hasTailSelfCalls(nonTailCallExpr)("myFunc")(1))
                                                in Unit)

let testMutualRecursionTcoPlanning unit =
    (let member1 =
        MutualMemberInfo(
            name = "isEven",
            paramNames = "n" :: [],
            paramTypes = SemInt :: [],
            returnType = SemBool,
            arity = 1
        )
    in
        let member2 =
            MutualMemberInfo(
                name = "isOdd",
                paramNames = "n" :: [],
                paramTypes = SemInt :: [],
                returnType = SemBool,
                arity = 1
            )
        in
            let planOpt = tryPlanMutualRecursionTco("even_odd")(member1 :: member2 :: [])
            in
                match planOpt with
                    | None -> test.fail("tryPlanMutualRecursionTco should succeed for matching even/odd group")
                    | Some(plan) ->
                        let _ = test.assertEqual("__recgroup_dispatch_even_odd")(plan.dispatchName)
                        in
                            let _ = test.assertEqual("_recgroup_dispatch_even_odd")(plan.dispatchLabel)
                            in
                                let _ = test.assertEqual(1)(plan.arity)
                                in
                                    let dispatchParams = buildMutualDispatchParamNames(1)(plan.sharedParamNames)
                                    in
                                        let _ = test.assertEqual(2)(Ashes.Collection.List.length(dispatchParams))
                                        in
                                            let _ = test.assertEqual("_recgroup_wrapper_isEven")(formatMutualWrapperLabel("isEven"))
                                            in Unit)

let testMutualRecursionMismatches unit =
    (let member1 =
        MutualMemberInfo(
            name = "f",
            paramNames = "x" :: [],
            paramTypes = SemInt :: [],
            returnType = SemBool,
            arity = 1
        )
    in
        let memberMismatchArity =
            MutualMemberInfo(
                name = "g",
                paramNames = "x" :: "y" :: [],
                paramTypes = SemInt :: SemInt :: [],
                returnType = SemBool,
                arity = 2
            )
        in
            let memberMismatchType =
                MutualMemberInfo(
                    name = "h",
                    paramNames = "x" :: [],
                    paramTypes = SemString :: [],
                    returnType = SemBool,
                    arity = 1
                )
            in
                let _ = test.assertEqual(true)(tryPlanMutualRecursionTco("group1")(member1 :: memberMismatchArity :: []) == None)
                in
                    let _ = test.assertEqual(true)(tryPlanMutualRecursionTco("group2")(member1 :: memberMismatchType :: []) == None)
                    in
                        let _ = test.assertEqual(true)(tryPlanMutualRecursionTco("group3")(member1 :: []) == None)
                        in Unit)

let testTcoRcEligibilityEvaluation unit =
    (let facts = dummyFacts(0)("acc")(0)
    in
        let eligInt = evaluateTcoRcEligibility(facts)(Some(SemInt))(false)
        in
            let _ = test.assertEqual(true)(eligInt.ownershipShapeEligible)
            in
                let _ = test.assertEqual(true)(eligInt.resolvedLayoutEligible)
                in
                    let eligUnresolved = evaluateTcoRcEligibility(facts)(Some(SemVariable(0)))(false)
                    in
                        let _ = test.assertEqual(false)(eligUnresolved.ownershipShapeEligible)
                        in
                            let factsFreshList =
                                TcoParamStaticFacts(
                                    paramOrdinal = 0,
                                    paramName = "list",
                                    slot = 0,
                                    hasVisibleBinding = true,
                                    loopInvariant = false,
                                    freshRebuiltList = true,
                                    freshClosureRebuild = false,
                                    bytesProvenanceSafeListRebuild = false,
                                    affineConsList = false,
                                    consumedListTail = false,
                                    borrowInspectOnly = false,
                                    affineSelfAppendOnly = false
                                )
                            in
                                let eligFreshList = evaluateTcoRcEligibility(factsFreshList)(Some(SemList(SemInt)))(false)
                                in
                                    let _ = test.assertEqual(true)(eligFreshList.ownershipShapeEligible)
                                    in
                                        let _ = test.assertEqual(true)(eligFreshList.resolvedLayoutEligible)
                                        in
                                            let factsFreshClosure =
                                                TcoParamStaticFacts(
                                                    paramOrdinal = 0,
                                                    paramName = "fn",
                                                    slot = 0,
                                                    hasVisibleBinding = true,
                                                    loopInvariant = false,
                                                    freshRebuiltList = false,
                                                    freshClosureRebuild = true,
                                                    bytesProvenanceSafeListRebuild = false,
                                                    affineConsList = false,
                                                    consumedListTail = false,
                                                    borrowInspectOnly = false,
                                                    affineSelfAppendOnly = false
                                                )
                                            in
                                                let fnType = SemFunction(SemInt)(SemInt)(None)
                                                in
                                                    let eligClosureDeferred = evaluateTcoRcEligibility(factsFreshClosure)(Some(fnType))(false)
                                                    in
                                                        let _ = test.assertEqual(false)(eligClosureDeferred.ownershipShapeEligible)
                                                        in
                                                            let eligClosureIncluded = evaluateTcoRcEligibility(factsFreshClosure)(Some(fnType))(true)
                                                            in
                                                                let _ = test.assertEqual(true)(eligClosureIncluded.ownershipShapeEligible)
                                                                in Unit)

let testTcoPlacementProfitability unit =
    (let candidateFacts = dummyFacts(0)("candidate")(0)
    in
        let blockingFacts = dummyFacts(1)("blockingHeapVal")(1)
        in
            let unmanagedListType = SemList(SemString)
            in
                let siblings = (candidateFacts, Some(SemInt)) :: (blockingFacts, Some(unmanagedListType)) :: []
                in
                    let requestedRuntimes = true :: false :: []
                    in
                        match evaluateTcoPlacementProfitability(siblings)(requestedRuntimes)(0)(true) with
                            | (verdictOpt, blockerOpt) ->
                                match (verdictOpt, blockerOpt) with
                                    | (Some(TcoVerdictNotProfitable), Some(1)) -> Unit
                                    | _ -> test.fail("candidate should be NotProfitable due to sibling 1 blocker"))

let testTcoPlacementTransitions unit =
    (let initialTrans = getTcoPlacementTransition(None)([])(false)(true)(TcoReasonEligible)
    in
        match initialTrans with
            | TcoTransitionInitial ->
                let dummyDec =
                    TcoParamPlacementDecision(
                        parameterOrdinal = 0,
                        parameterName = "p",
                        slot = 0,
                        resolutionPoint = TcoPointEntry,
                        representation = TcoRepArena,
                        reason = TcoReasonEligible,
                        eligibility = TcoRcEligibility(
                            ownershipShapeEligible = true,
                            resolvedLayoutEligible = true,
                            kind = TcoManagedOrdinary,
                            reason = TcoRcScalarTupleOrAdtLayout
                        ),
                        resolvedType = None,
                        dynamicRestricted = false,
                        profitability = None,
                        decisiveBlocker = None,
                        transition = TcoTransitionInitial,
                        firstPromotedAt = None
                    )
                in
                    let promotedTrans = getTcoPlacementTransition(Some(dummyDec))([])(false)(true)(TcoReasonEligible)
                    in
                        match promotedTrans with
                            | TcoTransitionPromotedAfterResolution ->
                                let demotedTrans = getTcoPlacementTransition(Some(dummyDec))([])(true)(false)(TcoReasonBlockingSiblingNotProfitable)
                                in
                                    match demotedTrans with
                                        | TcoTransitionDemotedByFrameProfitability -> Unit
                                        | _ -> test.fail("demoted transition expected")
                            | _ -> test.fail("promoted transition expected")
            | _ -> test.fail("initial transition expected"))

// A self-call that is an operand of an operator (`1 + f(x)`, `f(x) + 1`, a comparison, a negated
// comparison) is never a tail call; only a self-call that is the branch's own result is.
let testOperatorOperandsAreNotTailCalls unit =
    (let selfCall = ExprCall(ExprVar("f"))(ExprVar("x"))(false)(callArgumentsInline)
    in
        let _ = test.assertEqual(true)(hasTailSelfCalls(ExprIf(ExprVar("even"))(ExprAdd(ExprInt(1))(selfCall))(selfCall))("f")(1))
        in
            let _ = test.assertEqual(false)(hasTailSelfCalls(ExprAdd(ExprInt(1))(selfCall))("f")(1))
            in
                let _ = test.assertEqual(false)(hasTailSelfCalls(ExprAdd(selfCall)(ExprInt(1)))("f")(1))
                in
                    let _ = test.assertEqual(false)(hasTailSelfCalls(ExprEqual(selfCall)(ExprInt(1)))("f")(1))
                    in
                        let _ = test.assertEqual(false)(hasTailSelfCalls(ExprLogicalNot(ExprEqual(selfCall)(ExprInt(1))))("f")(1))
                        in
                            let _ = test.assertEqual(false)(hasTailSelfCalls(ExprIf(ExprVar("even"))(ExprAdd(ExprInt(1))(selfCall))(ExprInt(0)))("f")(1))
                            in Unit)

let runTcoTests unit =
    (let _ = testOrdinaryTailCallDetection(Unit)
    in
        let _ = testMutualRecursionTcoPlanning(Unit)
        in
            let _ = testMutualRecursionMismatches(Unit)
            in
                let _ = testTcoRcEligibilityEvaluation(Unit)
                in
                    let _ = testTcoPlacementProfitability(Unit)
                    in
                        let _ = testTcoPlacementTransitions(Unit)
                        in
                            let _ = testOperatorOperandsAreNotTailCalls(Unit)
                            in
                                let _ = Ashes.IO.print("all self-hosted TCO and mutual recursion tests passed")
                                in Unit)
