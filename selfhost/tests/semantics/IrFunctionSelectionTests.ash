import Ashes.Test as test
import AshesCompiler.Semantics.IrFunctionSelection
import AshesCompiler.Semantics.IrOrigins
export (
    value runIrFunctionSelectionTests,
)

let sourceOrigin =
    SourceFunctionOrigin(
        functionSourceName = "build",
        functionQualifiedName = Some("App.Work.build"),
        declarationLocation = None,
        declarationOffset = 100
    )

let generatedOrigin =
    IrFunctionOrigin(
        generatedLabel = "reuse_build_1",
        originKind = ReuseSpecializationOrigin,
        sourceOrigin = Some(sourceOrigin),
        parentGeneratedLabel = Some("build_lambda_0"),
        compilerOwner = None,
        stableDiscriminator = Some("reuse:1"),
        generationLocation = None
    )

let expectFunctionSelection unit =
    unit
    |> (given (_) ->
        None
        |> matchesIrFunction(Some(generatedOrigin))("reuse_build_1")
        |> test.assertEqual(true))
    |> (given (_) ->
        Some("BUILD")
        |> matchesIrFunction(Some(generatedOrigin))("reuse_build_1")
        |> test.assertEqual(true))
    |> (given (_) ->
        Some("app.work")
        |> matchesIrFunction(Some(generatedOrigin))("reuse_build_1")
        |> test.assertEqual(true))
    |> (given (_) ->
        Some("lambda_0")
        |> matchesIrFunction(Some(generatedOrigin))("reuse_build_1")
        |> test.assertEqual(true))
    |> (given (_) ->
        Some("missing")
        |> matchesIrFunction(Some(generatedOrigin))("reuse_build_1")
        |> test.assertEqual(false))
    |> (given (_) ->
        Some("WORK.BUILD")
        |> matchesSourceFunction(Some(sourceOrigin))("build")
        |> test.assertEqual(true))

let runIrFunctionSelectionTests unit =
    unit
    |> expectFunctionSelection
    |> (given (_) -> Ashes.IO.print("all self-hosted IR function selection tests passed"))
