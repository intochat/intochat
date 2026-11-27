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
  });
}
