import 'package:flutter/material.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:intochat_os_flutter_shell/intochat_os_flutter.dart';
import 'package:intochat_os_flutter_shell/intochat_os_ui.dart';

const _forbiddenMonospaceFamilies = <String>[
  'monospace',
  'courier',
  'consolas',
  'menlo',
  'monaco',
  'jetbrains',
  'fira code',
  'source code',
  'roboto mono',
];

void _assertZeroMonospaceFonts(WidgetTester tester) {
  for (final widget in tester.allWidgets) {
    TextStyle? style;
    if (widget is Text) {
      style = widget.style;
    } else if (widget is SelectableText) {
      style = widget.style;
    } else if (widget is RichText) {
      style = widget.text.style;
    }
    final family = style?.fontFamily?.toLowerCase();
    if (family == null) continue;
    for (final forbidden in _forbiddenMonospaceFamilies) {
      expect(
        family.contains(forbidden),
        isFalse,
        reason: 'Forbidden monospace font "$family" found on $widget',
      );
    }
  }
}

void main() {
  group('Item #350 Wave 2A — Ino Inline Drawer, Context Chip & Job/Choice Cards', () {
    for (final brightness in Brightness.values) {
      testWidgets(
        'Inline SplitPane drawer renders side-by-side at 1280x800 and collapses cleanly to overlay at 900x700 and 480x700 with zero overflow or monospace (${brightness.name})',
        (tester) async {
          final dispatched = <String>[];
          final ctx = NodeContext(
            view: 'local/apps/ino/view',
            send: (action) async {
              if (action is ActAction) {
                dispatched.add(action.signal);
              }
              return null;
            },
          );

          const splitNode = SplitPaneNode(
            leadingExtent: 520,
            gap: 16,
            trailingDrawer: true,
            collapseBreakpoint: 1024,
            trailingFraction: 0.58,
            leading: ConversationNode(
              key: 'conversation',
              messages: [
                MessageNode(
                  id: 'm1',
                  role: 'assistant',
                  text: 'Your Tip Calculator is ready.',
                  cards: [
                    JobCardNode(
                      title: 'Tip Calculator',
                      steps: [
                        JobStepNode(
                          label: 'Compile & sanitize script',
                          status: 'done',
                          detail: null,
                        ),
                        JobStepNode(
                          label: 'Run in sandbox',
                          status: 'done',
                          detail: 'sandbox',
                        ),
                      ],
                      progress: 1.0,
                      status: 'done',
                      resultText: 'Ready to preview.',
                      cancel: null,
                      openArtifact: ButtonNode(
                        key: 'open-artifact:run-1',
                        label: 'Open artifact',
                        command: Command(
                          target: '.',
                          signal: 'ino.open-drawer',
                          args: null,
                          props: null,
                        ),
                        intent: null,
                        enabled: true,
                        tone: 'neutral',
                        tooltip: null,
                        visible: true,
                        automationName: 'Open artifact',
                        style: '',
                        icon: 'external-link',
                        small: true,
                      ),
                    ),
                  ],
                  activities: null,
                  status: 'completed',
                  model: null,
                  retry: null,
                  modelLabel: null,
                  providerIcon: null,
                  error: null,
                  attachments: null,
                  sender: null,
                ),
              ],
              composer: ComposerNode(
                key: 'composer',
                onSubmit: Command(
                  target: '.',
                  signal: 'control.submitText',
                  args: null,
                  props: null,
                ),
                placeholder: 'Ask anything.',
                enabled: true,
                activeModelLabel: null,
                activeProviderIcon: null,
                dictation: false,
                activeContextLabel: 'Tip Calculator',
                onClearContext: Command(
                  target: '.',
                  signal: 'control.clearContext',
                  args: null,
                  props: null,
                ),
              ),
              waiting: null,
              status: 'ready',
              error: null,
              stop: null,
              empty: null,
              automationName: 'Conversation',
            ),
            trailing: SurfaceNode(
              surfaceKind: 'elevated',
              title: null,
              subtitle: null,
              padding: 12,
              style: null,
              children: [
                ColumnNode(
                  gap: 8,
                  padding: 0,
                  extents: null,
                  children: [
                    RowNode(
                      gap: 8,
                      padding: 0,
                      extents: null,
                      children: [
                        TextNode(
                          value: 'Tip Calculator',
                          tone: 'strong',
                          size: 15,
                          mono: false,
                          tabular: false,
                        ),
                        BadgeNode(label: 'Done', tone: 'done', icon: 'check'),
                      ],
                    ),
                    RowNode(
                      gap: 6,
                      padding: 0,
                      extents: null,
                      children: [
                        ButtonNode(
                          key: 'ino:drawer-tab-preview',
                          label: 'Preview',
                          command: null,
                          intent: null,
                          enabled: true,
                          tone: 'accent',
                          tooltip: null,
                          visible: true,
                          automationName: 'Preview',
                          style: '',
                          icon: null,
                          small: true,
                        ),
                        ButtonNode(
                          key: 'ino:drawer-tab-diff',
                          label: 'Diff',
                          command: null,
                          intent: null,
                          enabled: true,
                          tone: 'neutral',
                          tooltip: null,
                          visible: true,
                          automationName: 'Diff',
                          style: '',
                          icon: null,
                          small: true,
                        ),
                        ButtonNode(
                          key: 'ino:drawer-tab-logs',
                          label: 'Logs',
                          command: null,
                          intent: null,
                          enabled: true,
                          tone: 'neutral',
                          tooltip: null,
                          visible: true,
                          automationName: 'Logs',
                          style: '',
                          icon: null,
                          small: true,
                        ),
                        ButtonNode(
                          key: 'ino:drawer-pin',
                          label: 'Pin as App',
                          command: null,
                          intent: null,
                          enabled: true,
                          tone: 'accent',
                          tooltip: null,
                          visible: true,
                          automationName: 'Pin as App',
                          style: '',
                          icon: 'pin',
                          small: true,
                        ),
                        ButtonNode(
                          key: 'ino:drawer-popout',
                          label: 'Pop out',
                          command: null,
                          intent: null,
                          enabled: false,
                          tone: 'neutral',
                          tooltip: 'Pin as App to pop out',
                          visible: true,
                          automationName: 'Pop out',
                          style: '',
                          icon: 'external-link',
                          small: true,
                        ),
                        ButtonNode(
                          key: 'ino:drawer-close',
                          label: 'Close',
                          command: null,
                          intent: null,
                          enabled: true,
                          tone: 'neutral',
                          tooltip: null,
                          visible: true,
                          automationName: 'Close',
                          style: '',
                          icon: 'x',
                          small: true,
                        ),
                      ],
                    ),
                    TextNode(
                      value: 'Status: Completed · Scoped contracts: files',
                      tone: 'muted',
                      size: 13,
                      mono: false,
                      tabular: true,
                    ),
                  ],
                ),
              ],
            ),
          );

          Widget buildAt(Size size) => MaterialApp(
            theme: UiTheme.intochat(brightness),
            home: Scaffold(
              body: SizedBox(
                width: size.width,
                height: size.height,
                child: NodeRenderer.render(splitNode, ctx),
              ),
            ),
          );

          // 1. Desktop 1280x800: side-by-side split + 28px Active Context Chip.
          tester.view.physicalSize = const Size(1280, 800);
          tester.view.devicePixelRatio = 1.0;
          addTearDown(tester.view.reset);

          await tester.pumpWidget(buildAt(const Size(1280, 800)));
          await tester.pumpAndSettle();
          expect(tester.takeException(), isNull);
          expect(find.byKey(const ValueKey('split_pane_sash')), findsOneWidget);
          expect(
            find.byKey(const ValueKey('composer-active-context-chip')),
            findsOneWidget,
          );
          expect(find.text('Editing: Tip Calculator'), findsOneWidget);
          expect(
            tester
                .getSize(
                  find.byKey(const ValueKey('composer-active-context-chip')),
                )
                .height,
            28.0,
          );
          _assertZeroMonospaceFonts(tester);

          // Tapping × clears active context.
          await tester.tap(
            find.byKey(const ValueKey('composer-clear-context')),
          );
          await tester.pump();
          expect(dispatched, contains('control.clearContext'));

          // 2. Focus Mode 900x700 (<1024px): collapses to slide-over overlay.
          tester.view.physicalSize = const Size(900, 700);
          await tester.pumpWidget(buildAt(const Size(900, 700)));
          await tester.pumpAndSettle();
          expect(tester.takeException(), isNull);
          expect(
            find.byKey(const ValueKey('split_pane_trailing_overlay')),
            findsOneWidget,
          );
          _assertZeroMonospaceFonts(tester);

          // 3. Narrow 480x700: zero RenderFlex overflow.
          tester.view.physicalSize = const Size(480, 700);
          await tester.pumpWidget(buildAt(const Size(480, 700)));
          await tester.pumpAndSettle();
          expect(tester.takeException(), isNull);
          expect(
            find.byKey(const ValueKey('split_pane_trailing_overlay')),
            findsOneWidget,
          );
          _assertZeroMonospaceFonts(tester);
        },
      );
    }

    testWidgets(
      'Docked ChoiceCard inside ConversationNode.waiting renders options and dispatches pick',
      (tester) async {
        final dispatched = <String>[];
        final ctx = NodeContext(
          view: 'local/apps/ino/view',
          send: (action) async {
            if (action is ActAction) {
              dispatched.add(action.signal);
            }
            return null;
          },
        );

        const conversation = ConversationNode(
          key: 'conversation',
          messages: [],
          composer: ComposerNode(
            key: 'composer',
            onSubmit: Command(
              target: '.',
              signal: 'control.submitText',
              args: null,
              props: null,
            ),
            placeholder: 'Ask anything.',
            enabled: true,
            activeModelLabel: null,
            activeProviderIcon: null,
            dictation: false,
            activeContextLabel: null,
            onClearContext: null,
          ),
          waiting: ChoiceCardNode(
            id: 'ask-1',
            question: 'Which environment should I deploy to?',
            pickedIndex: null,
            options: [
              ChoiceOptionNode(
                title: 'Sandbox',
                detail: 'Isolated test brain',
                icon: null,
                pick: ButtonNode(
                  key: 'pick-sandbox',
                  label: 'Sandbox',
                  command: Command(
                    target: '.',
                    signal: 'task.answer',
                    args: null,
                    props: null,
                  ),
                  intent: null,
                  enabled: true,
                  tone: 'accent',
                  tooltip: null,
                  visible: true,
                  automationName: 'Sandbox',
                  style: '',
                  icon: null,
                  small: false,
                ),
              ),
              ChoiceOptionNode(
                title: 'Production',
                detail: 'Live brain',
                icon: null,
                pick: ButtonNode(
                  key: 'pick-prod',
                  label: 'Production',
                  command: Command(
                    target: '.',
                    signal: 'task.answer',
                    args: null,
                    props: null,
                  ),
                  intent: null,
                  enabled: true,
                  tone: 'neutral',
                  tooltip: null,
                  visible: true,
                  automationName: 'Production',
                  style: '',
                  icon: null,
                  small: false,
                ),
              ),
            ],
          ),
          status: 'paused',
          error: null,
          stop: null,
          empty: null,
          automationName: 'Conversation',
        );

        await tester.pumpWidget(
          MaterialApp(
            theme: UiTheme.intochat(Brightness.light),
            home: Scaffold(body: NodeRenderer.render(conversation, ctx)),
          ),
        );
        await tester.pumpAndSettle();
        expect(tester.takeException(), isNull);
        expect(
          find.text('Which environment should I deploy to?'),
          findsOneWidget,
        );
        expect(find.text('Sandbox'), findsOneWidget);
        expect(find.text('Production'), findsOneWidget);

        await tester.tap(find.text('Sandbox'));
        await tester.pump();
        expect(dispatched, contains('task.answer'));
      },
    );

    testWidgets(
      'Child-mounted ConversationNode (childPath: conversation) routes / commands to root (.) and . commands to conversation',
      (tester) async {
        final actions = <ActAction>[];
        final ctx = NodeContext(
          view: 'local/apps/ino/view',
          childPath: 'conversation',
          send: (action) async {
            if (action is ActAction) actions.add(action);
            return null;
          },
        );

        const conversation = ConversationNode(
          key: 'conversation',
          messages: [
            MessageNode(
              id: 'm1',
              role: 'assistant',
              text: 'Built artifact.',
              cards: [
                JobCardNode(
                  title: 'Expense Splitter',
                  steps: [
                    JobStepNode(label: 'Run', status: 'done', detail: null),
                  ],
                  progress: 1.0,
                  status: 'done',
                  resultText: 'Done',
                  cancel: null,
                  openArtifact: ButtonNode(
                    key: 'open-artifact:1',
                    label: 'Open artifact',
                    command: Command(
                      target: '/',
                      signal: 'ino.open-drawer',
                      args: null,
                      props: null,
                    ),
                    intent: null,
                    enabled: true,
                    tone: 'neutral',
                    tooltip: null,
                    visible: true,
                    automationName: 'Open artifact',
                    style: '',
                    icon: 'external-link',
                    small: true,
                  ),
                ),
              ],
              activities: null,
              status: 'completed',
              model: null,
              retry: null,
              modelLabel: null,
              providerIcon: null,
              error: null,
              attachments: null,
              sender: null,
            ),
          ],
          composer: ComposerNode(
            key: 'composer',
            onSubmit: Command(
              target: '.',
              signal: 'control.submitText',
              args: null,
              props: null,
            ),
            placeholder: 'Ask anything.',
            enabled: true,
            activeModelLabel: null,
            activeProviderIcon: null,
            dictation: false,
            activeContextLabel: 'Expense Splitter',
            onClearContext: Command(
              target: '.',
              signal: 'control.clearContext',
              args: null,
              props: null,
            ),
          ),
          waiting: ChoiceCardNode(
            id: 'ask-child',
            question: 'Deploy target?',
            pickedIndex: null,
            options: [
              ChoiceOptionNode(
                title: 'Sandbox',
                detail: 'Recommended',
                icon: null,
                pick: ButtonNode(
                  key: 'task-ask:1:0',
                  label: 'Sandbox',
                  command: Command(
                    target: '/',
                    signal: 'ino.answer-task-ask',
                    args: null,
                    props: null,
                  ),
                  intent: null,
                  enabled: true,
                  tone: 'accent',
                  tooltip: null,
                  visible: true,
                  automationName: 'Sandbox',
                  style: '',
                  icon: null,
                  small: false,
                ),
              ),
              ChoiceOptionNode(
                title: 'Production',
                detail: null,
                icon: null,
                pick: ButtonNode(
                  key: 'task-ask:1:1',
                  label: 'Production',
                  command: Command(
                    target: '/',
                    signal: 'ino.answer-task-ask',
                    args: null,
                    props: null,
                  ),
                  intent: null,
                  enabled: true,
                  tone: 'neutral',
                  tooltip: null,
                  visible: true,
                  automationName: 'Production',
                  style: '',
                  icon: null,
                  small: false,
                ),
              ),
            ],
          ),
          status: 'paused',
          error: null,
          stop: null,
          empty: null,
          automationName: 'Conversation',
        );

        await tester.pumpWidget(
          MaterialApp(
            theme: UiTheme.intochat(Brightness.light),
            home: Scaffold(body: NodeRenderer.render(conversation, ctx)),
          ),
        );
        await tester.pumpAndSettle();

        await tester.tap(find.text('Sandbox'));
        await tester.pump();
        expect(
          actions.any(
            (a) => a.signal == 'ino.answer-task-ask' && a.target == '.',
          ),
          isTrue,
        );
        expect(ctx.actTarget('/'), '.');
        expect(ctx.actTarget('.'), 'conversation');
        expect(ctx.actTarget('/other'), 'other');
      },
    );

    testWidgets(
      'Focus Mode 900x700 scrim dismiss hides overlay and updating trailing drawer reopens it automatically',
      (tester) async {
        tester.view.physicalSize = const Size(900, 700);
        tester.view.devicePixelRatio = 1.0;
        addTearDown(tester.view.reset);

        final ctx = NodeContext(
          view: 'local/apps/ino/view',
          send: (_) async => null,
        );

        SplitPaneNode buildSplit(String title) => SplitPaneNode(
          leadingExtent: 520,
          gap: 16,
          trailingDrawer: true,
          collapseBreakpoint: 1024,
          trailingFraction: 0.58,
          leading: const TextNode(
            value: 'Conversation',
            tone: 'strong',
            size: 14,
            mono: false,
            tabular: false,
          ),
          trailing: TextNode(
            value: title,
            tone: 'strong',
            size: 15,
            mono: false,
            tabular: false,
          ),
        );

        await tester.pumpWidget(
          MaterialApp(
            theme: UiTheme.intochat(Brightness.light),
            home: Scaffold(
              body: SizedBox(
                width: 900,
                height: 700,
                child: NodeRenderer.render(buildSplit('Artifact A'), ctx),
              ),
            ),
          ),
        );
        await tester.pumpAndSettle();
        expect(
          find.byKey(const ValueKey('split_pane_trailing_overlay')),
          findsOneWidget,
        );

        // Dismiss via exposed left scrim strip -> overlay hides, Artifact toggle pill appears.
        await tester.tapAt(const Offset(40, 350));
        await tester.pumpAndSettle();
        expect(
          find.byKey(const ValueKey('split_pane_trailing_overlay')),
          findsNothing,
        );
        expect(
          find.byKey(const ValueKey('split_pane_trailing_toggle')),
          findsOneWidget,
        );

        // Update trailing drawer to Artifact B -> overlay reopens automatically!
        await tester.pumpWidget(
          MaterialApp(
            theme: UiTheme.intochat(Brightness.light),
            home: Scaffold(
              body: SizedBox(
                width: 900,
                height: 700,
                child: NodeRenderer.render(buildSplit('Artifact B'), ctx),
              ),
            ),
          ),
        );
        await tester.pumpAndSettle();
        expect(tester.takeException(), isNull);
        expect(
          find.byKey(const ValueKey('split_pane_trailing_overlay')),
          findsOneWidget,
        );
        expect(find.text('Artifact B'), findsOneWidget);
      },
    );

    test('IntentGate accepts Pop out OpenWindow when pinned and refuses os://view/', () {
      bool knowsApp(String app) => app == 'tip-calc';
      expect(
        IntentGate.check(
          const IntentNode(
            intentKind: IntentKind.openWindow,
            arg: 'os://window/open?app=tip-calc',
          ),
          knowsApp: knowsApp,
        ),
        isNotNull,
      );
      expect(
        IntentGate.check(
          const IntentNode(
            intentKind: IntentKind.openWindow,
            arg: 'os://view/apps/unpinned/view',
          ),
          knowsApp: knowsApp,
        ),
        isNull,
      );
    });
  });
}
