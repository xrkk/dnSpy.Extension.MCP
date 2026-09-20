# THROWAWAY — dnSpy structured-edit feasibility spikes

This project is not production code. It answers two questions only:

1. Can a private dnlib copy plus precomputed inverse operations provide a short,
   failure-atomic live commit against the same `ModuleDef` used by dnSpy?
2. Can dnSpy's public `ILanguageCompilerProvider` feed a bounded MCP-owned importer,
   including embedded portable debug information, without referencing AsmEditor internals?

The extension runs once at `ExtensionEvent.AppLoaded`, writes machine-readable evidence,
and does not register MCP tools or modify the production extension.
