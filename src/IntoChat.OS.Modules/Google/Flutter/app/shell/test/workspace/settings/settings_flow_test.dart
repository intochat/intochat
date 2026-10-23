import 'package:flutter/material.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:http/http.dart' as http;

import '../../application_fixture.dart';
import '../settings_entry_test.dart' show SettingsActivationServer;

const _secret = 'sk-openai-flow-secret-424242';

/// Answers the entries and model routes the way the kernel does: OpenAI
/// models are available once `provider:openai` is stored.
class _FlowServer extends SettingsActivationServer {
  final secrets = <String, String>{};
  @override
  Future<http.StreamedResponse> send(http.BaseRequest request) async {
    final path = request.url.path;
    if (path.endsWith('/ai/models')) {
      return json({
        'automatic': {'label': 'Automatic'},
        'models': [
          {
            'id': 'preset:IGpt54Nano',
            'label': 'gpt-5.4-nano',
            'provider': 'OpenAI',
            'available': secrets.containsKey('provider:openai'),
            'capabilities': ['Tools'],
            'reason': 'Add a key in API keys',
          },
        ],
      });
    }
    if (path.contains('/entries')) {
      if (request.method == 'PUT') {
        requests.add(RecordedAppRequest(path, body(request)));
        secrets[Uri.decodeComponent(path.split('/').last)] =
            body(request)['value'] as String;
        return json({});
      }
      return json([
        for (final e in secrets.entries)
          {'name': e.key, 'kind': 'secret', 'isSet': true, 'tail': '4242'},
      ]);
    }
    return super.send(request);
  }
}

void main() {
  testWidgets('settings_flow_key_unlocks_models_and_sets_default', (
    tester,
  ) async {
    final server = _FlowServer();
    final fixture = await ApplicationFixture.start(tester, server: server);
    await tester.tap(find.text('Settings'));
    await tester.pumpAndSettle();

    await tester.tap(find.text('API keys'));
    await tester.pumpAndSettle();
    await tester.tap(find.byKey(const Key('settings-key-add-openai')));
    await tester.pumpAndSettle();
    await tester.enterText(
      find.byKey(const Key('settings-key-field')),
      _secret,
    );
    await tester.tap(find.byKey(const Key('settings-key-save')));
    await tester.pumpAndSettle();
    expect(server.secrets['provider:openai'], _secret);
    expect(_anyText(tester, _secret), isFalse);

    await tester.tap(find.text('AI models'));
    await tester.pumpAndSettle();
    expect(find.text('gpt-5.4-nano'), findsWidgets);
    await tester.tap(find.byKey(const Key('default-model-select')));
    await tester.pumpAndSettle();
    await tester.tap(find.text('gpt-5.4-nano').first);
    await tester.pumpAndSettle();
    expect(fixture.store.settings.defaultModelProfile, 'preset:IGpt54Nano');
    expect(_anyText(tester, _secret), isFalse);
    await fixture.dispose();
  });
}

/// Whether any Text or EditableText shows [secret] (the entry field is
/// cleared after save, so none should).
bool _anyText(WidgetTester tester, String secret) =>
    find.textContaining(secret).evaluate().isNotEmpty ||
    find
        .byWidgetPredicate(
          (w) => w is EditableText && w.controller.text.contains(secret),
        )
        .evaluate()
        .isNotEmpty;
