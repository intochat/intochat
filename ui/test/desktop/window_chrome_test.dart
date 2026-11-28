import 'package:flutter/material.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:intochat_os_flutter_shell/desktop/window_frame.dart';
import 'package:intochat_os_flutter_shell/intochat_os_ui.dart';
import 'package:intochat_os_flutter_shell/workspace/spatial_app_canvas.dart';

DesktopWindowState _makeWindow({
  int id = 1,
  String appId = 'files',
  String title = 'Files',
  Rect bounds = const Rect.fromLTWH(40, 40, 760, 520),
}) => DesktopWindowState(
  id: id,
  appId: appId,
  title: title,
  neuronUri: 'neuron://brain-1/apps/$appId',
  bounds: bounds,
);

Widget _wrap(Widget child) => MaterialApp(
  theme: UiTheme.dark('fluent-acrylic'),
  home: Scaffold(body: child),
);

void main() {
  group('DesktopWindowActions', () {
    test('owns DesktopWindowState mutations with single reloadEpoch increment and consistent toast feedback', () {
      final state = _makeWindow(id: 3, appId: 'files', title: 'Files');
      addTearDown(state.dispose);

      final calls = <String>[];
      final toasts = <String>[];
      var stateMutations = 0;

      final actions = DesktopWindowActions(
        window: state,
        onFocus: () => calls.add('focus'),
        onClose: () => calls.add('close'),
        onMinimize: () => calls.add('minimize'),
        onPin: () => calls.add('pin'),
        onReload: () => calls.add('reload'),
        onHardRestartSandbox: () => calls.add('hard-restart'),
        onSwitchExecutionMode: (m) => calls.add('mode:$m'),
        onInspectSandbox: () => calls.add('inspect'),
        onOpenAppSettings: () => calls.add('settings'),
        onStateMutated: () => stateMutations++,
        onToast: toasts.add,
      );

      actions.reload();
      expect(state.reloadEpoch, 1);
      expect(state.sandboxStatusBadge, 'Reloaded');
      expect(calls, ['reload']);
      expect(stateMutations, 1);

      actions.hardRestartSandbox();
      expect(state.reloadEpoch, 2);
      expect(state.sandboxStatusBadge, 'Sandbox Restarted');
      expect(calls.last, 'hard-restart');
      expect(toasts, ['Hard-restarted isolated sandbox for Files']);
      expect(stateMutations, 2);

      actions.switchExecutionMode('mcp');
      expect(state.executionMode, 'mcp');
      expect(state.sandboxStatusBadge, 'Mode: MCP Server');
      expect(calls.last, 'mode:mcp');
      expect(toasts.last, 'Switched execution mode to MCP Server');
      expect(stateMutations, 3);

      actions.togglePin();
      expect(state.isPinned, isTrue);
      expect(calls.last, 'pin');
      expect(stateMutations, 4);

      actions.toggleZoom();
      expect(state.zoomed, isTrue);

      actions.focus();
      actions.minimize();
      actions.inspectSandbox();
      actions.openAppSettings();
      actions.close();
      expect(calls.sublist(calls.length - 5), [
        'focus',
        'minimize',
        'inspect',
        'settings',
        'close',
      ]);
    });
  });

  group('WindowFrame (floating chrome variant)', () {
    testWidgets(
      'dispatches actions with single reloadEpoch increment, renders tabs, and toggles/dismisses testing menu popover',
      (tester) async {
        await tester.binding.setSurfaceSize(const Size(1280, 800));
        addTearDown(() => tester.binding.setSurfaceSize(null));

        final state = _makeWindow(id: 11, appId: 'files', title: 'Files');
        addTearDown(state.dispose);

        final calls = <String>[];
        final toasts = <String>[];
        var selectedTab = 0;

        final actions = DesktopWindowActions(
          window: state,
          onFocus: () => calls.add('focus'),
          onClose: () => calls.add('close'),
          onMinimize: () => calls.add('minimize'),
          onPin: () => calls.add('pin'),
          onReload: () => calls.add('reload'),
          onHardRestartSandbox: () => calls.add('hard-restart'),
          onSwitchExecutionMode: (m) => calls.add('mode:$m'),
          onInspectSandbox: () => calls.add('inspect'),
          onOpenAppSettings: () => calls.add('settings'),
          onToast: toasts.add,
        );

        await tester.pumpWidget(
          _wrap(
            Stack(
              children: [
                WindowFrame(
                  window: state,
                  focused: true,
                  forceZoom: false,
                  desk: const Size(1280, 760),
                  actions: actions,
                  tabs: [
                    DesktopTabSpec(
                      key: 't0',
                      label: 'Home',
                      selected: selectedTab == 0,
                      onTap: () => selectedTab = 0,
                    ),
                    DesktopTabSpec(
                      key: 't1',
                      label: 'Recent',
                      selected: selectedTab == 1,
                      onTap: () => selectedTab = 1,
                    ),
                  ],
                  child: const Center(
                    child: Text('Window Body', key: ValueKey('win-body')),
                  ),
                ),
              ],
            ),
          ),
        );
        await tester.pump();

        // Tab strip rendered below floating title bar
        expect(find.byType(WindowTabStrip), findsOneWidget);
        expect(find.text('Home'), findsOneWidget);
        expect(find.text('Recent'), findsOneWidget);

        // Short tap on reload increments reloadEpoch exactly once
        await tester.tap(find.byKey(const ValueKey('window-reload-btn-11')));
        await tester.pump();
        expect(state.reloadEpoch, 1);
        expect(calls.where((c) => c == 'reload').length, 1);

        // Long press on reload increments reloadEpoch once more and emits toast
        final reloadCenter = tester.getCenter(
          find.byKey(const ValueKey('window-reload-btn-11')),
        );
        final gesture = await tester.startGesture(reloadCenter);
        await tester.pump(const Duration(milliseconds: 550));
        await gesture.up();
        await tester.pump();
        expect(state.reloadEpoch, 2);
        expect(calls.where((c) => c == 'hard-restart').length, 1);
        expect(toasts, ['Hard-restarted isolated sandbox for Files']);

        // Open popover via settings button and dismiss by tapping window body
        await tester.tap(find.byKey(const ValueKey('window-settings-btn-11')));
        await tester.pump();
        expect(
          find.byKey(const ValueKey('window-settings-popover-11')),
          findsOneWidget,
        );

        await tester.tap(find.byKey(const ValueKey('win-body')));
        await tester.pump();
        expect(
          find.byKey(const ValueKey('window-settings-popover-11')),
          findsNothing,
        );

        // Re-open popover and switch execution mode -> updates state, emits toast, dismisses
        await tester.tap(find.byKey(const ValueKey('window-settings-btn-11')));
        await tester.pump();
        await tester.tap(find.byKey(const ValueKey('exec-mode-native-linux')));
        await tester.pump();
        expect(state.executionMode, 'native-linux');
        expect(calls.contains('mode:native-linux'), isTrue);
        expect(toasts.last, 'Switched execution mode to Native Linux CLI');
        expect(
          find.byKey(const ValueKey('window-settings-popover-11')),
          findsNothing,
        );

        // Re-open popover and click Reset Sandbox -> single increment
        await tester.tap(find.byKey(const ValueKey('window-settings-btn-11')));
        await tester.pump();
        await tester.tap(
          find.byKey(const ValueKey('window-sandbox-reset-btn')),
        );
        await tester.pump();
        expect(state.reloadEpoch, 3);
        expect(
          find.byKey(const ValueKey('window-settings-popover-11')),
          findsNothing,
        );

        // Traffic lights & pin button dispatch through actions
        await tester.tap(find.byKey(const ValueKey('window-pin-btn-11')));
        await tester.pump();
        expect(state.isPinned, isTrue);

        await tester.tap(find.byKey(const ValueKey('window-minimize-btn-11')));
        await tester.pump(const Duration(milliseconds: 350));
        await tester.tap(find.byKey(const ValueKey('window-close-btn-11')));
        await tester.pump(const Duration(milliseconds: 350));
        expect(calls.contains('minimize'), isTrue);
        expect(calls.contains('close'), isTrue);
      },
    );
  });

  group('SpatialAppCanvas (spatial card chrome variant)', () {
    testWidgets(
      'uses unified WindowHeaderBar & WindowTestingMenuHost with single reloadEpoch increment and popover dismissal',
      (tester) async {
        await tester.binding.setSurfaceSize(const Size(1280, 800));
        addTearDown(() => tester.binding.setSurfaceSize(null));

        final state = _makeWindow(
          id: 22,
          appId: 'creator',
          title: 'Creator',
          bounds: const Rect.fromLTWH(80, 60, 640, 440),
        );
        addTearDown(state.dispose);

        final calls = <String>[];
        final toasts = <String>[];
        final actions = DesktopWindowActions(
          window: state,
          onFocus: () => calls.add('focus'),
          onClose: () => calls.add('close'),
          onPin: () => calls.add('pin'),
          onReload: () => calls.add('reload'),
          onHardRestartSandbox: () => calls.add('hard-restart'),
          onSwitchExecutionMode: (m) => calls.add('mode:$m'),
          onInspectSandbox: () => calls.add('inspect'),
          onOpenAppSettings: () => calls.add('settings'),
          onToast: toasts.add,
        );

        await tester.pumpWidget(
          _wrap(
            SpatialAppCanvas(
              windows: const ['w-22'],
              windowStates: {'w-22': state},
              windowActions: {'w-22': actions},
              topWindowId: 'w-22',
              tabs: {
                'w-22': [
                  const DesktopTabSpec(
                    key: 't0',
                    label: 'Graph',
                    selected: true,
                  ),
                  const DesktopTabSpec(
                    key: 't1',
                    label: 'Inspector',
                    selected: false,
                  ),
                ],
              },
              windowBuilders: {
                'w-22': (_) => const Align(
                  alignment: Alignment.bottomRight,
                  child: Padding(
                    padding: EdgeInsets.all(16),
                    child: Text('Card Body', key: ValueKey('card-body-22')),
                  ),
                ),
              },
            ),
          ),
        );
        await tester.pump();

        expect(find.byType(WindowHeaderBar), findsOneWidget);
        expect(find.byType(WindowTabStrip), findsOneWidget);
        expect(find.text('Graph'), findsOneWidget);
        expect(find.text('Inspector'), findsOneWidget);

        // Short tap reload -> exactly +1 reloadEpoch (never double incremented!)
        await tester.tap(find.byKey(const ValueKey('window-reload-btn-22')));
        await tester.pump();
        expect(state.reloadEpoch, 1);
        expect(calls.where((c) => c == 'reload').length, 1);

        // Pin button mutates DesktopWindowState.isPinned AND dispatches host pin
        await tester.tap(find.byKey(const ValueKey('window-pin-btn-22')));
        await tester.pump();
        expect(state.isPinned, isTrue);
        expect(calls.where((c) => c == 'pin').length, 1);

        // Open popover from spatial card identity cluster
        await tester.tap(find.byKey(const ValueKey('window-settings-btn-22')));
        await tester.pump();
        expect(
          find.byKey(const ValueKey('window-settings-popover-22')),
          findsOneWidget,
        );

        // Select execution mode -> mutates state, dispatches, emits consistent toast, dismisses popover
        await tester.tap(find.byKey(const ValueKey('exec-mode-mcp')));
        await tester.pump();
        expect(state.executionMode, 'mcp');
        expect(calls.contains('mode:mcp'), isTrue);
        expect(toasts, ['Switched execution mode to MCP Server']);
        expect(
          find.byKey(const ValueKey('window-settings-popover-22')),
          findsNothing,
        );

        // Open popover again and dismiss by tapping card body
        await tester.tap(find.byKey(const ValueKey('window-settings-btn-22')));
        await tester.pump();
        expect(
          find.byKey(const ValueKey('window-settings-popover-22')),
          findsOneWidget,
        );
        await tester.tap(find.byKey(const ValueKey('card-body-22')));
        await tester.pump();
        expect(
          find.byKey(const ValueKey('window-settings-popover-22')),
          findsNothing,
        );

        // Open popover and invoke Reset Sandbox -> single increment (+1) & consistent toast
        await tester.tap(find.byKey(const ValueKey('window-settings-btn-22')));
        await tester.pump();
        await tester.tap(
          find.byKey(const ValueKey('window-sandbox-reset-btn')),
        );
        await tester.pump();
        expect(state.reloadEpoch, 2);
        expect(toasts.last, 'Hard-restarted isolated sandbox for Creator');
        expect(
          find.byKey(const ValueKey('window-settings-popover-22')),
          findsNothing,
        );
      },
    );
  });
}
