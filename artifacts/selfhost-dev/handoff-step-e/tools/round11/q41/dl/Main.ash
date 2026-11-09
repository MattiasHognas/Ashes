import AshesCompiler.Frontend.Parser
import AshesCompiler.Frontend.Syntax
import AshesCompiler.Semantics.CoreLowering
import AshesCompiler.Semantics.Ir
import AshesCompiler.Semantics.IrText
import AshesCompiler.Semantics.ProjectSyntaxStitching
import AshesCompiler.Semantics.ShippedModuleStitching
let isAshSourceName name = Ashes.Text.length(name) > 4 && Ashes.Text.substring(name)(Ashes.Text.length(name) - 4)(4) == ".ash"

// Every `<Module.Path>.ash` under the shipped standard-library root, as the texts the program's
// `import Ashes.*` header is resolved against; only the modules the program reaches are parsed.
let recursive readShippedModules root names =
    match names with
        | [] -> []
        | name :: rest ->
            if isAshSourceName(name) == false
            then readShippedModules(root)(rest)
            else
                let path = root + "/" + name
                in
                    match Ashes.IO.File.readText(path) with
                        | Error(message) -> Ashes.IO.panic("could not read shipped module " + path + ": " + message)
                        | Ok(source) ->
                            ShippedModuleText(
                                moduleName = "Ashes." + Ashes.Text.substring(name)(0)(Ashes.Text.length(name) - 4),
                                sourcePath = path,
                                source = source
                            ) :: readShippedModules(root)(rest)

let loadShippedModules root =
    match Ashes.IO.Directory.entries(root) with
        | Error(message) -> Ashes.IO.panic("could not list shipped modules under " + root + ": " + message)
        | Ok(names) -> readShippedModules(root)(names)

let formatLowered path source program =
    match lowerCoreProgramWithSource(path)(source)(program) with
        | CoreLoweringResult { program = Some(lowered), error = None } ->
            None
            |> formatIr(lowered)(LoweredIr)
            |> Ashes.Text.join("\n")
        | CoreLoweringResult { error = Some(message) } -> "lowering failed: " + Ashes.Trait.Show.show(message)
        | _ -> "lowering produced no program"

// The program alone, as the parity fixtures are lowered.
let dumpBare (path: Str) source =
    match parseProgram(source) with
        | ProgramParseResult { program = program, diagnostics = [] } -> formatLowered(path)(source)(program)
        | ProgramParseResult { diagnostics = _ } -> "parse failed"

// The program with the shipped modules it imports stitched in first, as the compiler lowers it.
let dumpStitched (path: Str) source shippedRoot =
    match shippedRoot
    |> loadShippedModules
    |> stitchWithShippedModules("main")(path)(source) with
        | Error(error) -> "stitching failed: " + Ashes.Trait.Show.show(error)
        | Ok(StitchedSyntaxProject { program = program }) -> formatLowered(path)(source)(program)

let dump (path: Str) (shippedRoot: Maybe(Str)) =
    match Ashes.IO.File.readText(path) with
        | Error(message) -> "could not read " + path + ": " + message
        | Ok(source) ->
            match shippedRoot with
                | None -> dumpBare(path)(source)
                | Some(root) -> dumpStitched(path)(source)(root)

match Ashes.IO.args with
    | path :: [] ->
        None
        |> dump(path)
        |> Ashes.IO.print
    | path :: shippedRoot :: [] ->
        shippedRoot
        |> Some
        |> dump(path)
        |> Ashes.IO.print
    | _ -> Ashes.IO.print("usage: dumpir <source> [shipped-library-root]")
