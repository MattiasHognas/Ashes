// expect: manifest|Main.ash
// working-directory: workspace/project
// file: workspace/project/ashes.json = manifest
// file: workspace/project/src/Main.ash = source

import Ashes.IO.Path as path
import Ashes.Text as text
let inspect unit =
    (let recursive climb style directory count =
        if count <= 0
        then Ok(directory)
        else climb(style)(path.parent(style)(directory))(count - 1)
    in
        let? current = Ashes.IO.Environment.currentDirectory(Unit)
        in
            let style =
                if text.contains(current)("\\")
                then path.Windows
                else path.Unix
            in
                let? root = climb(style)(current)(0)
                in
                    let manifest = path.join(style)(root)("ashes.json")
                    in
                        let? contents = Ashes.IO.File.readText(manifest)
                        in
                            let source = path.join(style)(root)("src")
                            in
                                let? names = Ashes.IO.Directory.entries(source)
                                in Ok(contents + "|" + text.join("|")(names)))

match inspect(Unit) with
    | Error(message) -> Ashes.IO.print(message)
    | Ok(result) -> Ashes.IO.print(result)
