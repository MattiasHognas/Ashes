// expect: /a/c|../../c|/root|C:\work\Main.ash|\\server\share\b|C:\override|C:\work|Main.ash|.ash|empty|..\Tests|D:\Other|/|C:\

import Ashes.IO.Path as path
let unix = path.Unix

let windows = path.Windows

let output =
    path.normalize(unix)("/a//b/../c/.") + "|" + path.relativeTo(unix)("a/b/d")("a/c") + "|" + path.join(unix)("/root/work")("/root") + "|" + path.normalize(windows)("C:\\work\\.\\src\\..\\Main.ash") + "|" + path.normalize(windows)("\\\\server\\share\\a\\..\\b") + "|" + path.join(windows)("C:\\work")("C:\\override") + "|" + path.parent(windows)("C:\\work\\Main.ash") + "|" + path.basename(windows)("C:\\work\\Main.ash") + "|" + path.extension(windows)("C:\\work\\Main.ash") + "|" + (if path.extension(unix)("/home/.profile") == ""
    then "empty"
    else "bad") + "|" + path.relativeTo(windows)("C:\\Work\\Src")("c:\\work\\Tests") + "|" + path.relativeTo(windows)("C:\\Work")("D:\\Other") + "|" + path.parent(unix)("/") + "|" + path.parent(windows)("C:\\")

Ashes.IO.writeLine(output)
