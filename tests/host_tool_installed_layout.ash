// expect: project-ok|sources-ok|lib-ok|runtime-ok|output-ok
// executable-directory: install/bin
// working-directory: workspace/project/src
// file: workspace/project/ashes.json = {"entry":"src/Main.ash","sourceRoots":["src"]}
// file: workspace/project/src/Main.ash = Ashes.IO.print("fixture")
// file: install/lib/Ashes/Core.ash = let marker = "shipped-library"
// file: install/runtimes/payload.marker = native-runtime

import Ashes.IO.Path as path
import Ashes.Text as text
let ensure condition message =
    if condition
    then Ok(Unit)
    else Error(message)

let styleFor value =
    if text.contains(value)("\\")
    then path.Windows
    else path.Unix

let recursive findProject style directory =
    (let manifest = path.join(style)(directory)("ashes.json")
    in
        match Ashes.IO.File.exists(manifest) with
            | Error(message) -> Error(message)
            | Ok(true) -> Ok(directory)
            | Ok(false) ->
                let parent = path.parent(style)(directory)
                in
                    if parent == directory
                    then Error("project not found")
                    else findProject(style)(parent))

let run unit =
    (let? currentDirectory = Ashes.IO.Environment.currentDirectory(Unit)
    in
        let style = styleFor(currentDirectory)
        in
            let? projectRoot = findProject(style)(currentDirectory)
            in
                let manifestPath = path.join(style)(projectRoot)("ashes.json")
                in
                    let? manifest = Ashes.IO.File.readText(manifestPath)
                    in
                        let? projectChecked = ensure(text.contains(manifest)("\"sourceRoots\":[\"src\"]"))("project manifest mismatch")
                        in
                            let sourceRoot = path.join(style)(projectRoot)("src")
                            in
                                let? sourceNames = Ashes.IO.Directory.entries(sourceRoot)
                                in
                                    let? sourcesChecked = ensure(text.join("|")(sourceNames) == "Main.ash")("source discovery mismatch")
                                    in
                                        let? executableDirectory = Ashes.IO.Environment.executableDirectory(Unit)
                                        in
                                            let installRoot = path.parent(style)(executableDirectory)
                                            in
                                                let libraryPath = path.join(style)(installRoot)("lib/Ashes/Core.ash")
                                                in
                                                    let runtimePath = path.join(style)(installRoot)("runtimes/payload.marker")
                                                    in
                                                        let? library = Ashes.IO.File.readText(libraryPath)
                                                        in
                                                            let? libraryChecked = ensure(text.contains(library)("shipped-library"))("library asset mismatch")
                                                            in
                                                                let? runtime = Ashes.IO.File.readText(runtimePath)
                                                                in
                                                                    let? runtimeChecked = ensure(runtime == "native-runtime")("runtime asset mismatch")
                                                                    in
                                                                        let outputDirectory = path.join(style)(projectRoot)("build")
                                                                        in
                                                                            let outputRelative = path.relativeTo(style)(currentDirectory)(outputDirectory)
                                                                            in
                                                                                let temporaryOutput = path.join(style)(outputRelative)("compiler.tmp")
                                                                                in
                                                                                    let finalOutput = path.join(style)(outputRelative)("compiler-output")
                                                                                    in
                                                                                        let? outputDirectoryCreated = Ashes.IO.Directory.createAll(outputRelative)
                                                                                        in
                                                                                            let? temporaryWritten = Ashes.IO.File.writeText(temporaryOutput)("native-image")
                                                                                            in
                                                                                                let? outputReplaced = Ashes.IO.File.replace(temporaryOutput)(finalOutput)
                                                                                                in
                                                                                                    let? outputExecutable = Ashes.IO.File.makeExecutable(finalOutput)
                                                                                                    in
                                                                                                        let? output = Ashes.IO.File.readText(finalOutput)
                                                                                                        in
                                                                                                            let? outputChecked = ensure(output == "native-image")("output mismatch")
                                                                                                            in Ok("project-ok|sources-ok|lib-ok|runtime-ok|output-ok"))

match run(Unit) with
    | Ok(message) -> Ashes.IO.print(message)
    | Error(message) ->
        let _ = Ashes.IO.writeErrorLine(message)
        in Ashes.IO.exit(1)
