import 'package:flutter_test/flutter_test.dart';
import 'package:intochat_os_flutter_shell/intochat_os_flutter.dart';
import 'package:intochat_os_flutter_shell/intochat_os_ui.dart';

void main() {
  test('the circles icon spec is removed while friends remains', () {
    expect(IcAppIcon.builtInSpecs.containsKey('circles'), isFalse);
    expect(IcAppIcon.builtInSpecs.containsKey('friends'), isTrue);
    expect(IcAppIcon.normalizeAppId('friend'), 'friends');
  });

  test('SmartAppTileSpec.fromNode carries server launch, dock, and category metadata', () {
    const node = AppTileNode(
      key: 'catalog-vectorcraft',
      appId: 'vectorcraft',
      title: 'VectorCraft',
      icon: 'vectorcraft',
      category: 'ArtCraft Studio',
      launch: AppLaunch.maximized,
      dock: null,
      appKind: 'compiled',
      description: 'Vector illustration studio',
      command: null,
      automationName: '',
    );

    final spec = SmartAppTileSpec.fromNode(node, brain: 'brain-1');
    expect(spec.id, 'vectorcraft');
    expect(spec.name, 'VectorCraft');
    expect(spec.category, 'ArtCraft Studio');
    expect(spec.launch, AppLaunch.maximized);
    expect(spec.dock, isNull);
    expect(spec.appKind, 'compiled');
    expect(spec.description, 'Vector illustration studio');
    expect(spec.iconSlug, 'vectorcraft');
    expect(spec.neuronUri, 'neuron://brain-1/apps/vectorcraft');
  });
}
