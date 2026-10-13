# Neuron space: one UI for every neuron

Date: 2026-10-13
Status: design approved in chat on 2026-10-13, section by section. No code changed.
Supersedes: the window/desktop, Home grid, Assistant island and Welcome scene parts of
`2026-10-12-workspace-design-system.md` and `2026-10-12-presentation-hosting-design.md`. Their typed
presentation root in app state, program-revision guard and shared design tokens remain valid and are
built on here. Replaces the `StateMachine` part of `2026-10-11-flame-stage-design.md` with behavior trees.

## Intent

Every neuron the person can look at is drawn the same way: a sphere with its icon, name and
description, and when it has a UI, components in fixed places around the sphere. Ino's chat, a
folder in Files, a friend's posts are the same mechanism. There is one screen, one transition (zoom),
one bottom toolbar that shows the focused neuron's quick actions, and one stable control (Home, bottom
left). Home is the brain itself: a field of every neuron, running ones highlighted. Sign-in lands on
Ino focused with its chat open.

Decisions taken in the brainstorm:

1. Every neuron has its own sphere, not only Ino.
2. Navigation is one continuous space with zoom; Back and Home are the reverse flight. Nested neurons
   (a post inside a friend) are one level deeper in the same space.
3. Sphere behavior is a behavior tree stored as scene data authored in C#, validated by the publish
   gate, run in Dart with the `flame_behavior_tree` package (the robot and traffic examples are the
   reference shapes).
4. Components around the sphere are fixed slots filled by typed neurons: side, main, toolbar.
5. Home is the brain; the field is every neuron, running ones highlighted.
6. Friend and Post are in scope as the third reference app, built on Circle.
7. Sign-in goes straight to Ino focused, chat in main. The Welcome scene and first-run view are deleted.
8. Approach A: the brain's space is one derived scene composed by the Flutter module from workspace
   state and every app's published presentation. Apps describe themselves; they never write Home.

## The four words

- **Neuron:** an app's state holds its presentation; slot neurons hold the contents; the workspace
  neuron holds the navigation path; posts and profiles are neurons in the author's brain.
- **Signal:** presentation and workspace changes invalidate the derived scene; activity signals set
  the module-owned blackboard keys.
- **Behavior:** one synapse composes slot contents and the blackboard, then publishes the presentation.
- **Connector:** the Flutter shell renders the derived scene with Flame, runs the trees, overlays the
  slots with the shared design system and reports taps as workspace operations.

A presentation, a sphere and a behavior tree are data held by neurons, not new runtime objects.

## Section 1: the model

### Presentation

Held in app state under the program-revision guard of the hosting spec:

```
Presentation(
    Sphere Sphere,            // always present
    UiChildRef? Side,         // left column
    UiChildRef? Main)         // large area beside the sphere

Sphere(
    string Icon,              // manifest icon file or shell icon name
    string[] BlackboardKeys,  // keys the app's synapses may write, e.g. mood, unread
    BehaviorTree? Tree)       // null uses the shell's default idle tree
```

`Blackboard` values are a string map on the app's presentation state, written by the app's
synapses for its declared keys and by the Flutter module for the reserved keys `running` and
`attention`. A script write to a reserved key is refused.

Toolbar stays in the manifest (`AppManifest.Toolbar`), unchanged.

Publication rules from the hosting spec hold: null root is "not ready", error is recorded under the
same guard, identical publication is idempotent, program replacement invalidates the prior
presentation, only the executing assignment may publish.

### Workspace

The workspace neuron (`IWorkspace`, per brain) is reshaped to:

```
WorkspaceState(long Revision, string[] Path, string[] Open)

Show(string app, string operationId, long expectedRevision)   // push
Back(string operationId, long expectedRevision)               // pop one
Home(string operationId, long expectedRevision)               // pop to the brain
Close(string app, string operationId, long expectedRevision)  // remove from Open
```

`Path[0]` is always the brain's own key (Home). The focused neuron is `Path[^1]`. `Open` is every
app with a prepared presentation that is running. A fresh brain seeds `Path = [brain, ino]`.
Window ids, titles and references are gone.

### The derived scene

The Flutter module derives one `SceneDefinition` per brain and streams it on the existing scene
stream. It is never stored by a script. Contents:

- one sphere node per neuron in the focused neuron's field (for Home: every installed app, every
  friend; for a friend: their posts), with the icon, blackboard and tree from its presentation;
- the camera targeted at the focused sphere; focus scale on it;
- slot references for the focused neuron, read from its presentation at render time.

Rebuilt whole on every workspace or presentation change. No script subscribes to it.

### Flame contract

`StateMachine` is removed from `SceneDefinition`. A node may carry a `BehaviorTree`:

```
BehaviorTree(TreeNode Root)
TreeNode = Selector(children) | Sequence(children, memory) | Repeat(child)
         | Condition(key, value) | Wait(seconds)
         | MoveTo(x, y, seconds) | Scale(factor, seconds) | Fade(alpha, seconds) | Pulse(amplitude, seconds)
```

`SceneValidation.Check` validates tree shape, that every `Condition.key` is a declared or reserved
blackboard key, and task parameter ranges, per `FlameSchema` (Option A of the Flame redesign
proposal). The schema is emitted by a Flame-module fact and asserted by a Flutter test so both sides
cannot drift. Unknown node types still render as placeholders.

## Section 2: the shell

- **One screen.** A full-bleed Flame layer draws the field and camera. Over it, four fixed regions:
  left column (side slot), centre area (main slot; the focused sphere sits left of centre when main
  is present and dead centre otherwise), bottom toolbar, bottom-left Home button. The focused
  neuron's icon, title and description sit above its sphere.
- **Zoom is the only transition.** `Show` flies the camera to the sphere, scales it to focus size,
  fades and slides the slots in. `Back` and `Home` reverse it. Reduced motion collapses the flight to
  a cut. Arrival animations never replay on reconnect or identical rebuild.
- **Toolbar** is the focused app's manifest toolbar drawn with shared tokens. Home is the only
  stable control. Ino's island is gone; Ino is a sphere whose mood is visible from anywhere.
- **Running and attention** are reserved blackboard keys set by the module from the app's activity;
  the default tree turns `running` into a pulse and `attention` into a badge.
- **Deleted:** `WorkspaceDesktop` and window title bars, the `HomeScreen` tile grid,
  `AssistantIsland`, the Stage navigation stack, the Welcome scene, the first-run view. The Creator
  and app details become a neuron in the field with its surface in main.

## Section 3: the three reference apps

- **Ino.** Sphere with the presence tree (idle, listening, thinking, acting, speaking, error) read
  from blackboard `mood`; the presence synapses write `mood` instead of firing a state machine.
  Main is the assistant's selected conversation rendered as chat with voice input. No side. Toolbar:
  new conversation, voice. Ino is the default focused app.
- **Files.** Sphere with the folder icon. Side is storages and allowed roots. Main is the current
  folder listing with filter and text preview as composed today. Toolbar is breadcrumb navigation
  plus Storages. Folders and files are content, not spheres; Files is one level deep.
- **Friends and posts.** New OS feature app `features/os/Friends` with operations `post` and `open`.
  A friend is a member of a circle the person is in; the Friend sphere is read from the circle brain's
  membership and the member's profile neuron `{brain}/profile`. Focusing a friend shows the profile in
  main and their posts in side. A post is a neuron `{brain}/posts/{id}` in the author's brain with its
  own presentation (sphere plus main showing the post). `post` publishes it into a circle through the
  existing circle publication path; the circle brain keeps the index every member reads. Posts are
  spheres in the friend's field, so opening one is the same zoom.

## Section 4: data flow, failures, security

- **Flow.** Synapse writes slot neurons and its blackboard keys, then publishes the presentation.
  Workspace changes the path on `Show`, `Back`, `Home`. The module subscribes to workspace and
  presentation changes and rebuilds the derived scene whole. The shell renders, runs trees, and posts
  taps as `Show`. Nothing waits on a script.
- **Not ready and failed.** Null root: dimmed sphere with the icon, no slots. Error: sphere with the
  attention badge and the error text in main. Null tree: the shell's default idle tree. Invalid tree:
  refused at publish.
- **Cross-brain reads.** Friend and post presentations are read from the circle brain and the author's
  brain through the person's membership and the existing `/brains/{circle}` authorization. The module
  holds references, never copies. A member who leaves disappears on the next rebuild.
- **Provenance.** Only the executing assignment publishes a presentation. Reserved blackboard keys
  are stamped by the module.
- **State.** Concrete arrays, whole rebuilds, no self-calls on the workspace neuron, no migration:
  old window state is dropped.

## Section 5: verification

- **Contract facts** (`TestBrain.Create().WithModule<FlutterModule>()`): a published presentation
  appears in the derived scene with the right sphere and slots; `Show` and `Back` move the camera
  target; a stale-program publish is refused; an invalid tree fails the publish gate; `running`
  follows activity and a script write to it is refused; a circle member's presentation appears in the
  Friend field and vanishes when they leave.
- **Flame facts:** tree validation against the schema; the schema parity emission.
- **Flutter tests:** the tree interpreter on the robot and traffic shapes; zoom moves the camera and
  swaps slots; reduced motion cuts; each slot kind renders in its region; Home pops to the brain.
- **Live loop:** `dotnet run digitalbrain.cs`, all resources Healthy; drive `Show`, `Back`, `post`,
  `open` through `digitalbrain-mcp`; read workspace and derived scene back; matching spans and logs in
  Aspire. Fresh brain lands on Ino with chat; Files shows its folder; a Friend shows a post.
- **Delivery order:** Flame trees and schema; workspace path and derived scene; shell regions and
  zoom; Ino; Files; Friends; old screens deleted as each lands.

## Out of scope

Cross-app subscription scopes, renderable palette / UiPart, run-token tightening (open fronts, as in
CLAUDE.md). Profile editing beyond name and avatar. Post comments and reactions.
