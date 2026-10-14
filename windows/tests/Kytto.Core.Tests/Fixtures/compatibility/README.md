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

## Windows additions

- portable-v1-windows-export.json: this app's export of the same inputs as the
  macOS generator used (memory stdio with an `npx` package, a working directory and
  an environment value, plus the remote HTTP server with an Authorization header,
  the Research profile and vector 2's reviewed tool), with the Windows spelling of
  the paths. Same structure, requirement kinds and labels, and the same contract
  digest (`0b5dc197...`) as the macOS file; only the opaque requirement ids differ.
  `PortableCompatibilityTests` compares a fresh export with it after replacing each
  id by its position, and checks it against an independent reading of the macOS
  validator rules. Regenerate with
  `KYTTO_WRITE_WINDOWS_FIXTURE=<path> dotnet test --filter AWindowsExportOfTheGoldenInputs`.
- The macOS file is imported on Windows, and the Windows file imported again, by the
  same tests, always into a temporary profile.
