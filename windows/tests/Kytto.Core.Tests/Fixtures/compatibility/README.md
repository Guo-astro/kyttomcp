# macOS-generated compatibility fixtures

Generated on 2026-10-12 by the current compiled KyttoCore implementation.
The generator called PortableSetupService.export, inspect and contractDigest;
these are real implementation outputs, not a hand-written imitation.

- portable-v1-macos-export.json: 2 servers (memory stdio + remote HTTP), 1 Research
  profile, native required inputs and one optional reviewed-contract reference.
  UUID requirement IDs are opaque. The input fixture contained sentinel credentials
  and a private example path; assertions verified those values were not exported.
- contract-digest-v1-vectors.json: 5 ToolSummary inputs with expected SHA-256 and,
  when possible, the exact canonical schema string. Vectors 2 and 3 must produce
  the same digest despite schema key/whitespace order and tokenCount differences.
  Cases cover omitted nils, Unicode, slash/HTML characters, booleans, numeric
  defaults, ordered arrays and malformed-schema raw fallback.
- generate-golden.swift records how these were produced using @testable import
  KyttoCore. It was compiled with existing tools; Windows need not install Swift.

Windows acceptance: inspect/import the provided portable file using replacement
values in a temp home; imported servers must be disabled. Match every expected
contract digest and canonical schema exactly. Produce equivalent Windows exports
and compare their structure/validation rules against the reference; do not compare
opaque IDs or JSON property presentation order as product semantics.

No external network calls or real user configs/credentials were involved.
