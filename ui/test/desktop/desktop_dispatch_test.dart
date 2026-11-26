import 'package:flutter_test/flutter_test.dart';
import 'package:intochat_os_flutter_shell/intochat_os_flutter.dart';
import 'package:intochat_os_flutter_shell/intochat_os_ui.dart';

void main() {
  test('only the shell-owned (app, operation) pairs are sent', () {
    expect(desktopShellOperationAllowed('desktop', 'widget-open'), isTrue);
    expect(desktopShellOperationAllowed('window', 'sandbox-restart'), isTrue);
    expect(desktopShellOperationAllowed('desktop', 'delete-brain'), isFalse);
    expect(desktopShellOperationAllowed('identity', 'open'), isFalse);
  });

  test(
    'typed desktop commands dispatch structured signals over /act',
    () async {
      final sent = <Map<String, dynamic>>[];
      final controller = BrainSessionController(
        brainId: 'brain-1',
        loadProjection: (_) async => const DesktopShellProjection(
          windows: [],
          pins: [],
          tiles: [],
          openWidgets: [],
        ),
        onAction: (event) async {
          sent.add(event);
          return const {'delivered': true};
        },
      );
      addTearDown(controller.dispose);

      await controller.openApp('ino');
      await controller.focusWindow('w-1');
      await controller.selectTab('w-1', 1);
      await controller.newTab('w-1');
      await controller.toggleFavourite('w-1');
      await controller.pinApp('files');
      await controller.unpinApp('files');
      await controller.closeWindow('w-1', tabIndex: 0);
      await controller.openWidget('launcher');
      await controller.closeWidget('launcher');

      expect(sent, [
        {
          'kind': 'act',
          'neuron': 'brain-1/apps/desktop',
          'target': '.',
          'signal': 'desktop.open',
          'args': {'app': 'ino'},
        },
        {
          'kind': 'act',
          'neuron': 'brain-1/apps/desktop',
          'target': '.',
          'signal': 'desktop.focus',
          'args': {'windowId': 'w-1'},
        },
        {
          'kind': 'act',
          'neuron': 'brain-1/apps/desktop',
          'target': '.',
          'signal': 'desktop.tab',
          'args': {'target': 'w-1/1'},
        },
        {
          'kind': 'act',
          'neuron': 'brain-1/apps/desktop',
          'target': '.',
          'signal': 'desktop.newtab',
          'args': {'windowId': 'w-1'},
        },
        {
          'kind': 'act',
          'neuron': 'brain-1/apps/desktop',
          'target': '.',
          'signal': 'desktop.favourite',
          'args': {'windowId': 'w-1'},
        },
        {
          'kind': 'act',
          'neuron': 'brain-1/apps/desktop',
          'target': '.',
          'signal': 'desktop.pin',
          'args': {'appId': 'files', 'pinned': true},
        },
        {
          'kind': 'act',
          'neuron': 'brain-1/apps/desktop',
          'target': '.',
          'signal': 'desktop.unpin',
          'args': {'appId': 'files'},
        },
        {
          'kind': 'act',
          'neuron': 'brain-1/apps/desktop',
          'target': '.',
          'signal': 'desktop.close',
          'args': {'target': 'w-1/0'},
        },
        {
          'kind': 'act',
          'neuron': 'brain-1/apps/desktop',
          'target': '.',
          'signal': 'desktop.widget-open',
          'args': {'widget': 'launcher'},
        },
        {
          'kind': 'act',
          'neuron': 'brain-1/apps/desktop',
          'target': '.',
          'signal': 'desktop.widget-close',
          'args': {'widget': 'launcher'},
        },
      ]);
    },
  );
}
