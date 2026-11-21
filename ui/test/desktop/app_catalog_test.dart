import 'package:flutter_test/flutter_test.dart';
import 'package:intochat_os_flutter_shell/desktop/app_catalog_launcher.dart';
import 'package:intochat_os_flutter_shell/intochat_os_ui.dart';

void main() {
  test('the circles tile and icon spec are removed while friends remains', () {
    final defaultIds = defaultSmartAppSpecs.map((spec) => spec.id).toList();
    final allIds = allSmartAppSpecs.map((spec) => spec.id).toList();

    expect(defaultIds, isNot(contains('circles')));
    expect(allIds, isNot(contains('circles')));
    expect(defaultIds, contains('friends'));
    expect(allIds, contains('friends'));

    expect(IcAppIcon.builtInSpecs.containsKey('circles'), isFalse);
    expect(IcAppIcon.builtInSpecs.containsKey('friends'), isTrue);
    expect(IcAppIcon.normalizeAppId('friend'), 'friends');
  });
}
