// expect: dictionary positions passed
// Pattern fields introduce their type variables in a different order from the
// function arguments. All three Eq requirements must keep their declared slots.
let recursive hasEntry category operation operand entries =
    match entries with
        | [] -> false
        | (entryCategory, entryOperand, entryOperation, _label) :: rest -> entryCategory == category && entryOperation == operation && entryOperand == operand || hasEntry(category)(operation)(operand)(rest)

let entries = [("Ord", 71, "compare", "ordering"), ("Eq", 71, "equal", "equality")]

let alias = hasEntry

let found = hasEntry("Ord")("compare")(71)(entries) && hasEntry("Eq")("equal")(71)(entries)

let absent = hasEntry("Ord")("compare")(72)(entries) || hasEntry("Eq")("missing")(71)(entries)

let viaAlias = alias("Eq")("equal")(71)(entries)

if found && absent == false && viaAlias
then Ashes.IO.print("dictionary positions passed")
else Ashes.IO.panic("a dictionary was applied to the wrong argument type")
