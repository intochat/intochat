# Declarative Pulumi Infrastructure-as-Code Assets

This directory holds the declarative Pulumi YAML templates embedded by `IntoChat.OS.Kernel` (`PulumiStackSynthesizer`):

- [`Pulumi.yaml`](Pulumi.yaml): Canonical Pulumi YAML stack skeleton (`name`, `runtime: yaml`, Azure `fn::invoke` variables, resource insertion anchor, and stack outputs).
- [`Pulumi.stack.yaml`](Pulumi.stack.yaml): Per-environment Pulumi stack config template (`Pulumi.<env>.yaml`).
- [`command-stop.yaml`](command-stop.yaml) & [`command-start.yaml`](command-start.yaml): Declarative `command:local:Command` stop-before-update / start-after-update handover templates for single-silo Container Apps.

The reusable POSIX `/bin/sh` retry and revision-handover helper lives in [`../scripts/az-retry.sh`](../scripts/az-retry.sh) and is embedded directly into `IntoChat.OS.Kernel` as well as written into synthesized workspaces (`WriteWorkspaceAsync`).

## Direct Shell Usage

```sh
# Source helper functions into a POSIX shell
. scripts/az-retry.sh
az_retry az containerapp show -n intochat-test-os -g intochat-test --query name -o tsv

# Or invoke directly
sh scripts/az-retry.sh stop-revision intochat-test-os intochat-test 24
sh scripts/az-retry.sh start-revision intochat-test-os intochat-test
```
