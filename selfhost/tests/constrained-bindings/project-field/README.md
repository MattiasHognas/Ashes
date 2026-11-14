# Constrained binding reading a field of an annotated record parameter

`lookupTemp` in the backend code generator takes an annotated record parameter, reads one of
its fields, and calls an imported `Eq`-constrained helper. In a stitched project the record's
type and constructor are spelled differently, so field inference must find the record by the
type its constructor builds. Expected output: `30`.

```bash
<compiler> compile --project selfhost/tests/constrained-bindings/project-field/ashes.json -o <out>
```
