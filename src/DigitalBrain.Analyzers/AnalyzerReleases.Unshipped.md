; Unshipped analyzer release
; https://github.com/dotnet/roslyn-analyzers/blob/main/src/Microsoft.CodeAnalysis.Analyzers/ReleaseTrackingAnalyzers.Help.md

### New Rules

Rule ID | Category | Severity | Notes
--------|----------|----------|-------
DB0001 | DigitalBrain | Error | Module class needs [Module] and a public static ModuleManifest Manifest
DB0002 | DigitalBrain | Error | Scheduling attribute inside an App class
DB0003 | DigitalBrain | Warning | Orleans attribute on a DigitalBrain identity type
DB0013 | DigitalBrain | Error | Signal loop inside a Neuron or App
DB0014 | DigitalBrain | Error | Malformed [On] reaction
