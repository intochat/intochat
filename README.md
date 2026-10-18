# DigitalBrain

Product documentation lives in the [DigitalBrain handbook](https://intochat.github.io/digitalbrain.docs/).

## Packages

The testing packages are published to the GitHub Packages feed `https://nuget.pkg.github.com/intochat/index.json`:

| Package | Use |
|---|---|
| `DigitalBrain.Testing` | In-process `TestBrain`, one host per assembly (`BrainHostFixture`), probes and fakes. |
| `DigitalBrain.Testing.Aspire` | `TestBrain` on your Aspire AppHost (`AspireTarget<TAppHost>`). |
| `DigitalBrain.Testing.Playwright` | Playwright browser sessions against the Aspire target. |
| `DigitalBrain.Aspire.Testing` | Aspire application-model checks without running anything. |

Until this repository is public, restoring from the feed needs a GitHub token with the `read:packages` scope. The
`DigitalBrain.Testing` package README shows the `nuget.config` that reads it from an environment variable.
