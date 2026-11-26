# Unified Cross-Repo ProjectReference Build Layer

Downstream host repositories normally consume `IntoChat.OS.*` and `DigitalBrain.*` as lockstep NuGet packages pinned in `Directory.Packages.props`. During local development or unified CI runs, those 37+ internal packages can instead bind directly to an IntoChat OS checkout via `ProjectReference` without packing or downloading `.nupkg` artifacts.

## Opting In from a Downstream Repository

Add the following two lines to the downstream repository's `Directory.Build.props` and `Directory.Build.targets`:

```xml
<!-- Directory.Build.props -->
<PropertyGroup>
  <IntoChatOSRepoRoot Condition="'$(IntoChatOSRepoRoot)' == ''">$([MSBuild]::NormalizeDirectory('$(MSBuildThisFileDirectory)../intochat'))</IntoChatOSRepoRoot>
  <IntoChatOSUseProjectReferences Condition="'$(IntoChatOSUseProjectReferences)' == ''">false</IntoChatOSUseProjectReferences>
</PropertyGroup>
<Import Project="$(IntoChatOSRepoRoot)eng/IntoChat.OS.Projects.props"
        Condition="Exists('$(IntoChatOSRepoRoot)eng/IntoChat.OS.Projects.props')" />
```

```xml
<!-- Directory.Build.targets -->
<Import Project="$(IntoChatOSRepoRoot)eng/IntoChat.OS.ProjectReferences.targets"
        Condition="'$(IntoChatOSUseProjectReferences)' == 'true' And Exists('$(IntoChatOSRepoRoot)eng/IntoChat.OS.ProjectReferences.targets')" />
```

Then build with `-p:IntoChatOSUseProjectReferences=true` (or set `IntoChatOSUseProjectReferences=auto` in `Directory.Build.local.props`):

```sh
dotnet build -p:IntoChatOSUseProjectReferences=true -p:IntoChatOSRepoRoot=/path/to/intochat
```

Or build both repositories in one pass using the traversal project:

```sh
dotnet build eng/IntoChat.OS.Unified.proj -p:UnifiedProjects=/path/to/downstream/Host.slnx
```

## Preserving NuGet Release Builds

When `IntoChatOSUseProjectReferences` is unset or `false` (the default), `eng/IntoChat.OS.ProjectReferences.targets` is a no-op and `dotnet pack Intochat.OS.slnx` continues producing all versioned `.nupkg` packages from `VERSION`.
