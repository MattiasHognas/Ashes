// expect: Alice
// Calls a field accessor (getName) 200000 times on a Person sourced from a genuinely RC-placed
// List(Person), exercising the retain of a borrowed string field out of a provably
// reference-counted aggregate rather than a full per-call byte copy.

type Person =
    | name: Str
    | age: Int

let getName (p: Person) = p.name

let recursive build count acc =
    if count == 0
    then acc
    else build(count - 1)(Person(name = "Alice", age = count) :: acc)

let recursive headOf xs =
    match xs with
        | h :: _ -> h
        | [] -> Person(name = "", age = 0)

let recursive callMany i result p =
    if i == 0
    then result
    else callMany(i - 1)(getName(p))(p)

let people = build(3)([])

let last = callMany(200000)("")(headOf(people))

Ashes.IO.print(last)
