import Ashes.Test as test
import AshesCompiler.Frontend.Token
import AshesCompiler.Semantics.ProjectDiagnostics
export (
    value runProjectDiagnosticsTests,
)

let diagnostic start end message code =
    DiagnosticEntry(
        span = TextSpan(start = start, end = end),
        message = message,
        code = code
    )

let recursive diagnosticLabels diagnostics =
    match diagnostics with
        | [] -> []
        | ProjectDiagnostic { sourcePath = path, diagnostic = DiagnosticEntry { message = message } } :: rest -> path + ":" + message :: diagnosticLabels(rest)

let expectDeterministicProjectDiagnosticOrder unit =
    [
        ProjectDiagnosticSource(
            sourcePath = "/deps/Values.ash",
            diagnostics = [
                diagnostic(20)(21)("later")(
                    Some("ASH003")
                ),
                diagnostic(3)(4)("first-at-three")(
                    Some("ASH001")
                ),
                diagnostic(3)(4)("second-at-three")(
                    Some("ASH002")
                )
            ]
        ),
        ProjectDiagnosticSource(
            sourcePath = "/app/Main.ash",
            diagnostics = [
                diagnostic(0)(1)("entry")(
                    None
                )
            ]
        )
    ]
    |> orderProjectDiagnostics
    |> diagnosticLabels
    |> test.assertEqual(
        [
            "/deps/Values.ash:first-at-three",
            "/deps/Values.ash:second-at-three",
            "/deps/Values.ash:later",
            "/app/Main.ash:entry"
        ]
    )

let runProjectDiagnosticsTests unit =
    unit
    |> expectDeterministicProjectDiagnosticOrder
    |> (given (_) -> Ashes.IO.print("all self-hosted project diagnostic tests passed"))
