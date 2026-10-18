# DigitalBrain.Testing.Playwright

A Playwright browser against a brain on the Aspire target: sessions open on the AppHost's web endpoint, locally or in a
browser container.

## Install

```sh
dotnet add package DigitalBrain.Testing.Playwright
```

The feed and the `read:packages` token are set up as described in the `DigitalBrain.Testing` README.

Playwright needs its browser installed on the machine running the tests. After building the test project, run:

```sh
pwsh bin/<Configuration>/<TargetFramework>/playwright.ps1 install --with-deps chromium
```

## Use

```csharp
await using var brain = await TestBrain.Create()
    .On(new AspireTarget<Projects.MyAppHost>(BrowserLocation.Local))
    .StartAsync(ct);

await using var browser = await brain.OpenBrowserAsync(
    new BrowserOptions { Headless = true, AssertionTimeout = TimeSpan.FromSeconds(15) }, ct);
```

`BrainBrowser.OpenBrowserAsync` returns a `BrowserSession`: a fresh browser context on the brain's web endpoint, with the
Playwright `IPage` on `Page`. One Chromium is shared per booted AppHost. `BrowserLocation.Container` runs the browser in
a container. A headed browser runs only with `BrowserLocation.Local`.

## Tier

Browser tests start the whole AppHost and a browser. In the DigitalBrain repository they live in `*.Tests.Browser`
projects and run in the nightly workflow, not on pull requests.
