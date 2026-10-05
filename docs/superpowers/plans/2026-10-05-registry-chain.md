# Registry removal implementation plan

**Goal:** Execute #146, #147, #148 as three reviewed commits on `os`.
**Spec:** GitHub issues intochat/digitalbrain#146–148 and the user's pasted request, amended by the explicit dev-only data-loss decision.
**Architecture:** Framework owns one reflected contract catalog and embedding search beside ModuleInventory. CSharp projects that catalog as its vocabulary; Apps and MCP consume search directly. Framework also owns capability provider contracts. Assistant owns derived capability federation and a stateless grain keyed by BrainScope, preserving trusted-caller checks.

## Decisions and constraints

- All environments are dev-only. Skip the live Discovered sweep entirely. A dev brain whose Apps index predates self-tracking will not list its legacy installs; reinstalling in dev is acceptable. Record this in #146's commit.
- Work on the requested existing `os` branch. Preserve its six pre-existing commits.
- Reserve InstalledAppsState IDs 1 and 2; never renumber or reuse them.
- Choose Framework over CSharp for the catalog: non-CSharp modules should not depend on a language runtime to discover platform contracts. Exactly one exported-type walk supplies catalog metadata and vocabulary.
- For #148, provider seam in Framework and federation in Assistant follow ownership. A Framework-owned discovery grain would retain an unnecessary platform service; an in-process-only service would not satisfy the requested per-brain grain boundary. Use Assistant's grain with its key checked against the ambient brain; no persisted discovery state.
- Run project tests only, never the solution. Existing aliases of moved DTOs remain stable. Retired persisted registry blobs are orphaned, not actively deleted.

## Tasks

- [ ] #146: Remove Apps backfill and ledger, replace legacy-backfill test with index tracking/reactivation coverage, reserve state IDs, retire registry state manifest entries. Run Registry, Apps, Assistant; fresh review; commit.
- [ ] #147: Move catalog/search and DTOs into Framework, have ContractVocabulary consume the catalog, repoint CSharp/Builder/MCP, preserve filtering/aliases/descriptions/signal shapes and embedding results. Compare one Builder query before/after. Run CSharp, Apps, MCP, Registry; fresh review; commit.
- [ ] #148: Move provider DTOs/interface to Framework and federation to Assistant; implement and test caller/key brain isolation. Repoint tools/providers, remove Registry projects/references/catalog/solution entries, relocate tests and update manifests. Run Assistant, Apps, CSharp, Postgres, Supabase, MCP, OS; fresh review; commit.
- [ ] Finish: OS unit, Framework and OS E2E suites, Aspire smoke with all resources healthy. Record evidence and retired-state migration notes; close the three issues only after successful verification.

## Review focus

- Cross-brain direct grain calls cannot enumerate another brain's resources.
- App/untrusted callers remain denied even with a valid brain key.
- Platform-only types and platform assemblies never leak through either catalog consumer.
- Catalog search retains deterministic fixture results and no grain hop.
- Existing dev index state reactivates with Packages at ID 0 and retired IDs untouched.

## Evidence and progress

Baseline: 8fa6f7603221a35fbe2534207926424aaf339b15. Implementation authorized by the user's explicit full-chain instruction; seam choices above resolve the delegated placement decisions.
`n#146 evidence: replacement Apps test failed on ledger repopulation before removal. Registry 12 passed/1 live Qdrant skip; Apps 114 passed; Assistant 71 passed; Architecture 35 passed. Fresh reviewer found retired-ID baseline and obsolete collection fixtures; both repaired and architecture suite passed. Local Aspire stopped to release DLL locks.
