import 'package:flutter/material.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:intochat_os_flutter_shell/intochat_os_flutter.dart';
import 'package:intochat_os_flutter_shell/intochat_os_ui.dart';

void main() {
  Future<void> pumpShellSurface(
    WidgetTester tester,
    Widget child, {
    double width = 1440,
    double height = 900,
    bool disableAnimations = false,
  }) async {
    tester.view.physicalSize = Size(width, height);
    tester.view.devicePixelRatio = 1.0;
    addTearDown(() {
      tester.view.resetPhysicalSize();
      tester.view.resetDevicePixelRatio();
    });
    await tester.pumpWidget(
      MediaQuery(
        data: MediaQueryData(
          size: Size(width, height),
          disableAnimations: disableAnimations,
        ),
        child: MaterialApp(
          theme: UiTheme.light('intochat'),
          home: Scaffold(
            body: SizedBox(width: width, height: height, child: child),
          ),
        ),
      ),
    );
  }

  group('NodeRenderer.fills & full-height viewport stretching', () {
    test('BrainSceneNode, WebBrowserNode, full-viewport NeuronRef, and single-viewport containers fill bounded height', () {
      final brain = Node.fromJson({
        'kind': 'brainScene',
        'nodes': const <Map<String, dynamic>>[],
        'synapses': const <Map<String, dynamic>>[],
      });
      final browser = Node.fromJson({
        'kind': 'webBrowser',
        'key': 'aspire-browser',
        'url': 'https://example.test',
      });
      expect(NodeRenderer.fills(brain), isTrue);
      expect(NodeRenderer.fills(browser), isTrue);

      for (final refKind in [
        'ui.code-editor',
        'ui.chat',
        'ui.property-grid',
        'ui.tree-view',
        'ui.table',
        'viewport',
        'webbrowser',
      ]) {
        final refNode = Node.fromJson({
          'kind': 'neuronRef',
          'refKind': refKind,
          'neuronUri': 'engineer/editor/code',
        });
        expect(NodeRenderer.fills(refNode), isTrue, reason: refKind);
      }

      // Single filling viewport wrapped inside Column / Surface / Card fills:
      final columnWithEditor = Node.fromJson({
        'kind': 'column',
        'children': [
          {'kind': 'heading', 'value': 'Changes', 'level': 4},
          {
            'kind': 'neuronRef',
            'refKind': 'ui.code-editor',
            'neuronUri': 'engineer/editor/code',
          },
        ],
      });
      expect(NodeRenderer.fills(columnWithEditor), isTrue);

      final surfaceWithScene = Node.fromJson({
        'kind': 'surface',
        'surfaceKind': 'recessed',
        'children': [
          {
            'kind': 'brainScene',
            'nodes': const <Map<String, dynamic>>[],
            'synapses': const <Map<String, dynamic>>[],
          },
        ],
      });
      expect(NodeRenderer.fills(surfaceWithScene), isTrue);

      // Ordinary multi-card scrolling Column stays false so scroll views remain safe:
      final ordinaryColumn = Node.fromJson({
        'kind': 'column',
        'children': [
          {
            'kind': 'card',
            'title': 'One',
            'body': 'A',
            'children': const <Map<String, dynamic>>[],
          },
          {
            'kind': 'card',
            'title': 'Two',
            'body': 'B',
            'children': const <Map<String, dynamic>>[],
          },
        ],
      });
      expect(NodeRenderer.fills(ordinaryColumn), isFalse);
    });

    test('resolveEmbeddedBrowserWebUrl resolves root-relative co-hosted paths and preserves security checks', () {
      expect(
        resolveEmbeddedBrowserWebUrl(
          '/orleans#/grains',
          uiBaseOrigin: 'http://127.0.0.1:19440',
        ),
        'http://127.0.0.1:19440/orleans#/grains',
      );
      expect(
        resolveEmbeddedBrowserWebUrl(
          '/orleans#/overview',
          fallbackBase: Uri.parse('https://studio.intochat.test:8443/app'),
        ),
        'https://studio.intochat.test:8443/orleans#/overview',
      );
      expect(
        resolveEmbeddedBrowserWebUrl(
          'javascript:alert(1)',
          uiBaseOrigin: 'http://127.0.0.1:19440',
        ),
        'javascript:alert(1)',
      );
      expect(
        resolveEmbeddedBrowserWebUrl(
          '//evil.example/phish',
          uiBaseOrigin: 'http://127.0.0.1:19440',
        ),
        '//evil.example/phish',
      );
    });

    testWidgets(
      'WebBrowserNode stretches to >= 80% of 900px viewport inside AppShell and falls back cleanly when unbounded',
      (tester) async {
        const ctx = NodeContext(view: 'local/apps/aspire/view');
        final shellWithBrowser = Node.fromJson({
          'kind': 'appShell',
          'key': 'aspire-shell',
          'toolbar': {
            'kind': 'surface',
            'surfaceKind': 'toolbar',
            'children': [
              {
                'kind': 'button',
                'key': 'tab-traces',
                'label': 'Traces',
                'small': true,
              },
            ],
          },
          'body': {
            'kind': 'webBrowser',
            'key': 'aspire-browser',
            'url': 'https://aspire.example.test/traces',
            'title': 'Aspire Dashboard',
            'height': 520,
          },
        });

        await pumpShellSurface(
          tester,
          NodeRenderer.render(shellWithBrowser, ctx),
          width: 1440,
          height: 900,
        );
        await tester.pumpAndSettle();

        final headerRect = tester.getRect(
          find.byKey(const ValueKey('shell-header-bar')),
        );
        final browserRect = tester.getRect(find.byType(UiWebBrowser));
        expect(headerRect.height, lessThanOrEqualTo(52));
        expect(browserRect.height, greaterThanOrEqualTo(900 * 0.80));
        expect(browserRect.width, greaterThanOrEqualTo(1400));

        // Unbounded scroll container falls back to declared node.height (520):
        await pumpShellSurface(
          tester,
          SingleChildScrollView(
            child: NodeRenderer.render(
              Node.fromJson({
                'kind': 'webBrowser',
                'key': 'unbounded-browser',
                'url': 'https://aspire.example.test',
                'title': 'Unbounded',
                'height': 520,
              }),
              ctx,
            ),
          ),
        );
        await tester.pumpAndSettle();
        expect(tester.getSize(find.byType(UiWebBrowser)).height, 520);
      },
    );
  });

  group('Studio-grade AppShellNode resize, collapse & ViewTransition', () {
    Map<String, dynamic> buildStudioShell({
      String bodyText = 'Stage Flow',
      String transition = '',
    }) => {
      'kind': 'appShell',
      'key': 'creator-shell',
      'transition': transition,
      'toolbar': {
        'kind': 'surface',
        'surfaceKind': 'toolbar',
        'children': [
          {
            'kind': 'button',
            'key': 'mode-brain',
            'label': 'Brain Graph',
            'small': true,
          },
        ],
      },
      'sidebar': {
        'kind': 'column',
        'children': [
          {'kind': 'text', 'value': 'Sidebar Explorer'},
        ],
      },
      'body': {
        'kind': 'surface',
        'surfaceKind': 'recessed',
        'title': bodyText,
        'children': [
          {'kind': 'text', 'value': bodyText},
        ],
      },
      'inspector': {
        'kind': 'column',
        'children': [
          {'kind': 'text', 'value': 'Inspector Properties'},
        ],
      },
    };

    testWidgets(
      'AppShellNode supports 180-360px sidebar sash, 260-640px inspector sash, and 1-click collapse/re-expand to 100% width',
      (tester) async {
        const ctx = NodeContext(view: 'local/apps/creator/view');
        await pumpShellSurface(
          tester,
          NodeRenderer.render(Node.fromJson(buildStudioShell()), ctx),
          width: 1440,
          height: 900,
        );
        await tester.pumpAndSettle();

        final sidebarPane = find.byKey(const ValueKey('shell-sidebar-pane'));
        final inspectorPane = find.byKey(
          const ValueKey('shell-inspector-pane'),
        );
        expect(tester.getSize(sidebarPane).width, 248);
        expect(tester.getSize(inspectorPane).width, 320);

        // Drag sidebar sash right (+80px -> widens past 300px) and clamp at 360px:
        final sideSash = find.byKey(const ValueKey('shell_sidebar_sash'));
        await tester.drag(sideSash, const Offset(80, 0));
        await tester.pumpAndSettle();
        expect(tester.getSize(sidebarPane).width, greaterThan(300));
        expect(tester.getSize(sidebarPane).width, lessThanOrEqualTo(360));

        await tester.drag(sideSash, const Offset(200, 0));
        await tester.pumpAndSettle();
        expect(tester.getSize(sidebarPane).width, 360);

        // Drag inspector sash left (-120px -> widens right inspector past 410px, clamped <= 640px):
        final inspSash = find.byKey(const ValueKey('shell_inspector_sash'));
        await tester.drag(inspSash, const Offset(-120, 0));
        await tester.pumpAndSettle();
        expect(tester.getSize(inspectorPane).width, greaterThan(410));
        expect(tester.getSize(inspectorPane).width, lessThanOrEqualTo(640));

        // 1-click collapse inspector via key:shell-toggle-inspector semantics/key:
        final toggleInspector = find.byKey(
          const ValueKey('shell-toggle-inspector'),
        );
        expect(
          tester.getSemantics(toggleInspector).identifier,
          'key:shell-toggle-inspector',
        );
        await tester.tap(toggleInspector);
        await tester.pumpAndSettle();
        expect(inspectorPane, findsNothing);

        // 1-click collapse sidebar via key:shell-toggle-sidebar -> body gets ~100% width!
        final toggleSidebar = find.byKey(
          const ValueKey('shell-toggle-sidebar'),
        );
        expect(
          tester.getSemantics(toggleSidebar).identifier,
          'key:shell-toggle-sidebar',
        );
        await tester.tap(toggleSidebar);
        await tester.pumpAndSettle();
        expect(sidebarPane, findsNothing);

        final fullBodyRect = tester.getRect(find.byType(UiMixSurface));
        expect(fullBodyRect.width, greaterThanOrEqualTo(1420));

        // Re-expand both panes:
        await tester.tap(toggleSidebar);
        await tester.tap(toggleInspector);
        await tester.pumpAndSettle();
        expect(sidebarPane, findsOneWidget);
        expect(inspectorPane, findsOneWidget);
      },
    );

    testWidgets(
      'AppShellNode animates fade-through, shared-axis-x, and shared-axis-z smoothly in 180ms with zero pending timers',
      (tester) async {
        const ctx = NodeContext(view: 'local/apps/engineer/view');

        for (final mode in ['fade-through', 'shared-axis-x', 'shared-axis-z']) {
          await pumpShellSurface(
            tester,
            NodeRenderer.render(
              Node.fromJson(
                buildStudioShell(bodyText: 'Lens A', transition: mode),
              ),
              ctx,
            ),
          );
          await tester.pump();

          // Update body target -> triggers 180ms ticker animation:
          await pumpShellSurface(
            tester,
            NodeRenderer.render(
              Node.fromJson(
                buildStudioShell(bodyText: 'Lens B ($mode)', transition: mode),
              ),
              ctx,
            ),
          );
          await tester.pump(const Duration(milliseconds: 90));
          expect(find.text('Lens B ($mode)'), findsNWidgets(2));
          await tester.pump(const Duration(milliseconds: 120));
          expect(tester.takeException(), isNull);
        }

        // Reduced motion (`disableAnimations: true`) settles immediately in 1 frame:
        await pumpShellSurface(
          tester,
          NodeRenderer.render(
            Node.fromJson(
              buildStudioShell(
                bodyText: 'Instant Cut',
                transition: 'shared-axis-x',
              ),
            ),
            ctx,
          ),
          disableAnimations: true,
        );
        await tester.pump();
        expect(find.text('Instant Cut'), findsNWidgets(2));
      },
    );
  });
}
