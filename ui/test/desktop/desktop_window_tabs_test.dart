import 'package:flutter_test/flutter_test.dart';
import 'package:intochat_os_flutter_shell/desktop/live_desktop_shell.dart';
import 'package:intochat_os_flutter_shell/intochat_os_flutter.dart';

// A desktop window's tabs come from its tab strip child (a slot reference with
// the strip's props); a pick acts on that child through POST /act.
void main() {
  const strip = NeuronRefNode(
    refKind: 'child',
    neuronUri: 'brain-1/control.tabStrip/brain-1/app.desktop/default/tabs-w-1',
    props: {
      'items': [
        {'id': 'w-1/0', 'label': 'Calculator'},
        {'id': 'w-1/1', 'label': 'Creator'},
      ],
      'selectedId': 'w-1/1',
    },
  );
  const viewport = NeuronRefNode(
    refKind: 'viewport',
    neuronUri: 'brain-1/apps/desktop/window/w-1',
    props: null,
  );

  test('the tabs are the strip child props', () {
    expect(desktopWindowTabs(const [strip, viewport]), [
      {
        'key': 'w-1/0',
        'text': 'Calculator',
        'selected': false,
        'enabled': true,
        'child': 'tabs-w-1',
      },
      {
        'key': 'w-1/1',
        'text': 'Creator',
        'selected': true,
        'enabled': true,
        'child': 'tabs-w-1',
      },
    ]);
  });

  test('a frame without a strip child has no tabs', () {
    expect(desktopWindowTabs(const [viewport]), isEmpty);
  });

  test('a pick sends Select to the strip child of the desktop screen', () {
    final tabs = desktopWindowTabs(const [strip]);

    expect(desktopTabSelectEvent(tabs.first, 'brain-1/apps/desktop/view'), {
      'kind': 'act',
      'neuron': 'brain-1/apps/desktop',
      'target': 'tabs-w-1',
      'signal': 'control.select',
      'args': {'id': 'w-1/0'},
    });
  });

  test('picking the selected or a disabled tab sends nothing', () {
    final tabs = desktopWindowTabs(const [strip]);

    expect(
      desktopTabSelectEvent(tabs.last, 'brain-1/apps/desktop/view'),
      isNull,
    );
    expect(
      desktopTabSelectEvent({
        ...tabs.first,
        'enabled': false,
      }, 'brain-1/apps/desktop/view'),
      isNull,
    );
  });
}
