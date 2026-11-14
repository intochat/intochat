---
name: helloworld
title: Hello World
description: Interactive micro-app with a toggle button ("Say Hello" / "Hide Hello") that reveals and hides "Hello, IntoChat OS!".
capabilities: []
schedule: none
---

# Hello World (`helloworld`)

A pure interactive UI micro-app requiring zero external connectors or secrets (`Needs = []`).

## Interactive Reactions & View
- **Initial View (`Visible == false`):** Renders `"Click the button below"` and button `"toggle-hello"` labeled `"Say Hello"`.
- **Toggle (`helloworld.toggle`):** Flips `Visible`, increments `Clicks`, and journals `helloworld.toggled`.
- **Active View (`Visible == true`):** Renders `"Hello, IntoChat OS!"` and updates button `"toggle-hello"` to `"Hide Hello"`.

## Ino Commands
- Publish `helloworld.toggle` with `{}` (or click `"toggle-hello"` in `/brains/{brain}/apps/helloworld/view`) to toggle the greeting visibility.
