# os

AI-Native Operating System. A runtime for AI apps and agents, built on .NET and Orleans.

Run it from the repository root:

```
dotnet run digitalbrain.cs
```

NuGet packages are published to the GitHub Packages feed:

```
https://nuget.pkg.github.com/intochat/index.json
```

Container images:

```
docker pull ghcr.io/intochat/os
docker pull ghcr.io/intochat/os-ui
docker pull ghcr.io/intochat/os-sandbox
```

`os` is the kernel, `os-ui` is the Flutter web shell served by nginx, and `os-sandbox` is the C# sandbox.

Documentation lives in the handbook: https://github.com/intochat/digitalbrain.docs
