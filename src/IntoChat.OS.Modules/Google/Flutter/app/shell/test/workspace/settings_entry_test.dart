import 'dart:convert' show jsonEncode;

import 'package:intochat_os_flutter_shell/workspace/workspace_settings.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:http/http.dart' as http;

import '../application_fixture.dart';
import '../workspace_root_test.dart' show ActivationServer;

void main() {
  testWidgets('settings_action_opens_native_settings', (tester) async {
    final fixture = await ApplicationFixture.start(
      tester,
      server: SettingsActivationServer(),
    );
    await tester.tap(find.text('Settings'));
    await tester.pumpAndSettle();
    final settings = tester.widget<WorkspaceSettings>(
      find.byType(WorkspaceSettings),
    );
    expect(settings.initialSection, 'general');
    expect(
      fixture.server.requests.where((r) => r.path.endsWith('/request')),
      isEmpty,
    );
    await fixture.dispose();
  });

  testWidgets('settings_action_respects_tab', (tester) async {
    final fixture = await ApplicationFixture.start(
      tester,
      server: SettingsActivationServer(tab: 'models'),
    );
    await tester.tap(find.text('Settings'));
    await tester.pumpAndSettle();
    expect(
      tester
          .widget<WorkspaceSettings>(find.byType(WorkspaceSettings))
          .initialSection,
      'models',
    );
    await fixture.dispose();
  });

  testWidgets('onSettings_does_not_show_feature_app', (tester) async {
    final fixture = await ApplicationFixture.start(tester);
    final space = fixture.space;
    await tester.tap(find.byTooltip('Settings'));
    await tester.pumpAndSettle();
    expect(find.byType(WorkspaceSettings), findsOneWidget);
    expect(space.view.value.focused, isNot('${fixture.brainId}/apps/settings'));
    await fixture.dispose();
  });
}

class SettingsActivationServer extends ActivationServer {
  SettingsActivationServer({this.tab}) : super('brain-1');
  final String? tab;
  @override
  Future<http.StreamedResponse> send(http.BaseRequest request) async {
    if (request.url.path.endsWith('/apps/node')) {
      return json({
        'label': 'Settings',
        'enabled': true,
        'activation': jsonEncode({
          'kind': 'settings',
          'brainId': 'brain-1',
          'tab': ?tab,
        }),
      });
    }
    return super.send(request);
  }
}
