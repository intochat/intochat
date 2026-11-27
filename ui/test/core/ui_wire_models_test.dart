import 'dart:convert';

import 'package:http/http.dart' as http;
import 'package:http/testing.dart';
import 'package:intochat_os_flutter_shell/intochat_os_flutter.dart';
import 'package:test/test.dart';

void main() {
  group('UiAction wire parity', () {
    test('ActAction serializes cleanly into AppUiEndpoints.ActRequest', () {
      const action = ActAction(
        neuron: 'local/apps/tasks',
        target: 'items/a',
        signal: 'tasks.rename',
        args: {'id': 42},
        value: 'Buy milk',
      );
      expect(action.toRequestJson(), {
        'neuron': 'local/apps/tasks',
        'target': 'items/a',
        'signal': 'tasks.rename',
        'args': {'id': 42},
        'value': 'Buy milk',
      });
      final roundTrip = UiAction.fromJson(
        Map<String, dynamic>.from(
          jsonDecode(jsonEncode(action.toJson())) as Map,
        ),
      );
      expect(roundTrip, isA<ActAction>());
      final decoded = roundTrip as ActAction;
      expect(decoded.neuron, action.neuron);
      expect(decoded.target, action.target);
      expect(decoded.signal, action.signal);
      expect(decoded.args, action.args);
      expect(decoded.value, action.value);
    });

    test('ActAction omits null value from request JSON', () {
      const action = ActAction(
        neuron: 'local/apps/desktop',
        target: '.',
        signal: 'desktop.open',
        args: {'app': 'settings'},
      );
      final req = action.toRequestJson();
      expect(req.containsKey('value'), isFalse);
      expect(req['args'], {'app': 'settings'});
    });

    test('ConfirmationAction round-trips and matches request body', () {
      const action = ConfirmationAction(planId: 'plan/run-1', approve: true);
      expect(action.toRequestJson(), {'approve': true});
      final decoded = UiAction.fromJson(
        Map<String, dynamic>.from(
          jsonDecode(jsonEncode(action.toJson())) as Map,
        ),
      );
      expect(decoded, equals(action));
    });

    test('WebBrowserAction variants match WebBrowserEndpoints records', () {
      const connect = WebBrowserConnectAction(
        name: 'local/apps/browser/main',
        port: 9222,
        sessionId: 's-1',
      );
      const disconnect = WebBrowserDisconnectAction(
        name: 'local/apps/browser/main',
        sessionId: 's-1',
      );
      const unavailable = WebBrowserUnavailableAction(
        name: 'local/apps/browser/main',
        reason: 'WebView2 missing',
      );

      expect(connect.toRequestJson(), {
        'name': 'local/apps/browser/main',
        'port': 9222,
        'sessionId': 's-1',
      });
      expect(disconnect.toRequestJson(), {
        'name': 'local/apps/browser/main',
        'sessionId': 's-1',
      });
      expect(unavailable.toRequestJson(), {
        'name': 'local/apps/browser/main',
        'reason': 'WebView2 missing',
      });

      for (final action in <UiAction>[connect, disconnect, unavailable]) {
        final decoded = UiAction.fromJson(
          Map<String, dynamic>.from(
            jsonDecode(jsonEncode(action.toJson())) as Map,
          ),
        );
        expect(decoded, equals(action));
      }
    });

    test('UiAction.fromJson rejects unknown kind and browser action', () {
      expect(
        () => UiAction.fromJson({'kind': 'view', 'name': 'x'}),
        throwsArgumentError,
      );
      expect(
        () => UiAction.fromJson({
          'kind': 'webbrowser',
          'action': 'navigate',
          'name': 'local/apps/browser/main',
        }),
        throwsArgumentError,
      );
    });
  });

  group('UiNodePayload ShellUiJsonContext parity', () {
    test('decodes PublishedView envelope', () {
      final json = <String, dynamic>{
        'alias': 'app.tasks',
        'generation': 7,
        'view': {
          'title': 'Tasks',
          'acts': 'act',
          'root': {'kind': 'text', 'value': 'Hello'},
        },
      };
      final payload = UiNodePayload.fromJson('view', json);
      expect(payload, isA<PublishedViewPayload>());
      final published = payload as PublishedViewPayload;
      expect(published.alias, 'app.tasks');
      expect(published.generation, 7);
      expect(published.isScreenView, isTrue);
      expect(published.hasView, isTrue);
      expect(published.view!.title, 'Tasks');
      expect((published.view!.root as TextNode).value, 'Hello');
    });

    test('decodes direct DigitalBrain.UI.View (confirmation inbox)', () {
      final json = <String, dynamic>{
        'title': 'Confirmations',
        'root': {'kind': 'text', 'value': 'Pending approval'},
      };
      final payload = UiNodePayload.fromJson('confirmation', json);
      expect(payload, isA<PublishedViewPayload>());
      final published = payload as PublishedViewPayload;
      expect(published.generation, 0);
      expect(published.hasView, isTrue);
      expect(published.view!.title, 'Confirmations');
    });

    test('decodes null or empty preparing view safely', () {
      final fromNull = UiNodePayload.fromDynamic('view', null);
      expect(fromNull, isA<PublishedViewPayload>());
      expect((fromNull as PublishedViewPayload).hasView, isFalse);

      final fromEmpty = UiNodePayload.fromJson('view', const {});
      expect(fromEmpty, isA<PublishedViewPayload>());
      expect((fromEmpty as PublishedViewPayload).hasView, isFalse);
    });

    test('decodes UnitViewportResponse', () {
      final json = <String, dynamic>{
        'generation': 12,
        'target': null,
        'root': {'kind': 'view', 'name': 'local/bank/bank'},
        'error': 'Poll failed: 503',
      };
      final payload = UiNodePayload.fromJson('viewport', json);
      expect(payload, isA<ViewportPayload>());
      final vp = payload as ViewportPayload;
      expect(vp.generation, 12);
      expect(vp.revision, isNull);
      expect(vp.target, isNull);
      expect(vp.root, const UiViewRef(kind: 'view', name: 'local/bank/bank'));
      expect(vp.error, 'Poll failed: 503');
    });

    test('decodes EmptyViewportResponse', () {
      final json = <String, dynamic>{
        'revision': 3,
        'root': null,
        'error': null,
      };
      final payload = UiNodePayload.fromJson('viewport', json);
      expect(payload, isA<ViewportPayload>());
      final vp = payload as ViewportPayload;
      expect(vp.revision, 3);
      expect(vp.root, isNull);
      expect(vp.error, isNull);
    });

    test('decodes TargetedViewportResponse', () {
      final json = <String, dynamic>{
        'revision': 9,
        'target': {
          'id': 'win-1',
          'app': 'local/apps/notes',
          'variant': 'default',
          'context': '',
        },
        'root': {'kind': 'view', 'name': 'local/apps/notes/view'},
        'error': null,
      };
      final payload = UiNodePayload.fromJson('viewport', json);
      expect(payload, isA<ViewportPayload>());
      final vp = payload as ViewportPayload;
      expect(vp.revision, 9);
      expect(
        vp.target,
        const WorkspaceViewReference(
          id: 'win-1',
          app: 'local/apps/notes',
          variant: 'default',
          context: '',
        ),
      );
      expect(
        vp.root,
        const UiViewRef(kind: 'view', name: 'local/apps/notes/view'),
      );
      expect(vp.toJson()['root'], {
        'kind': 'view',
        'name': 'local/apps/notes/view',
      });
    });

    test('decodes WebBrowserView (flat and definition-wrapped)', () {
      final flat = UiNodePayload.fromJson('webbrowser', {
        'name': 'local/apps/researcher/browser',
        'version': 4,
        'uri': 'https://example.com',
        'title': 'Researcher',
        'connection': 'Attached',
        'error': '',
        'actualUrl': 'https://example.com/docs',
      }) as WebBrowserPayload;
      expect(flat.name, 'local/apps/researcher/browser');
      expect(flat.version, 4);
      expect(flat.uri, 'https://example.com');
      expect(flat.title, 'Researcher');
      expect(flat.connection, 'Attached');
      expect(flat.actualUrl, 'https://example.com/docs');

      final wrapped = UiNodePayload.fromJson('webbrowser', {
        'definition': {'uri': 'https://legacy.test', 'title': 'Legacy'},
        'connection': 'Waiting',
        'error': 'none',
        'actualUrl': '',
      }) as WebBrowserPayload;
      expect(wrapped.uri, 'https://legacy.test');
      expect(wrapped.title, 'Legacy');
      expect(wrapped.connection, 'Waiting');
    });

    test('decodes ConnectResponse and UiErrorResponse', () {
      final connect = UiNodePayload.fromJson('connect', {
        'root': {'kind': 'view', 'name': 'local/apps/desktop/view'},
        'activationApp': 'desktop',
      }) as ConnectPayload;
      expect(
        connect.root,
        const UiViewRef(kind: 'view', name: 'local/apps/desktop/view'),
      );
      expect(connect.activationApp, 'desktop');

      final error = UiNodePayload.fromJson('error', {
        'error': 'Not loaded',
        'reason': 'NotLoaded',
      }) as ErrorPayload;
      expect(error.error, 'Not loaded');
      expect(error.reason, 'NotLoaded');
    });
  });

  group('IntoChatOSUiClient.readNode', () {
    test(
      'routes view, child, viewport, webbrowser, and null responses',
      () async {
        final paths = <String>[];
        final client = IntoChatOSUiClient(
          baseUri: Uri.parse('http://127.0.0.1:5080'),
          httpClient: MockClient((request) async {
            paths.add(request.url.toString());
            if (request.url.path.endsWith('/children/panel/item')) {
              return http.Response(
                jsonEncode({
                  'generation': 2,
                  'view': {
                    'title': 'Child',
                    'root': {'kind': 'text', 'value': 'Nested'},
                  },
                }),
                200,
                headers: {'content-type': 'application/json'},
              );
            }
            final kind = request.url.queryParameters['kind'];
            if (kind == 'viewport') {
              return http.Response(
                jsonEncode({
                  'revision': 5,
                  'root': {'kind': 'view', 'name': 'owner/apps/notes/view'},
                  'error': null,
                }),
                200,
                headers: {'content-type': 'application/json'},
              );
            }
            if (kind == 'webbrowser') {
              return http.Response(
                jsonEncode({
                  'name': 'owner/apps/web/browser',
                  'version': 1,
                  'uri': 'https://intochat.ai',
                  'title': 'IntoChat',
                  'connection': 'Attached',
                  'error': '',
                  'actualUrl': 'https://intochat.ai',
                }),
                200,
                headers: {'content-type': 'application/json'},
              );
            }
            return http.Response(
              'null',
              200,
              headers: {'content-type': 'application/json'},
            );
          }),
        );
        addTearDown(client.close);

        final child = await client.readNode(
          'owner',
          'child',
          'owner/apps/notes/children/panel/item',
        );
        expect(child, isA<PublishedViewPayload>());
        expect((child as PublishedViewPayload).generation, 2);

        final vp = await client.readNode('owner', 'viewport', 'owner/vp/1');
        expect(vp, isA<ViewportPayload>());
        expect((vp as ViewportPayload).root?.name, 'owner/apps/notes/view');

        final browser = await client.readNode(
          'owner',
          'webbrowser',
          'owner/apps/web/browser',
        );
        expect(browser, isA<WebBrowserPayload>());
        expect((browser as WebBrowserPayload).title, 'IntoChat');

        final unloaded = await client.readNode(
          'owner',
          'view',
          'owner/apps/pending/view',
        );
        expect(unloaded, isA<PublishedViewPayload>());
        expect((unloaded as PublishedViewPayload).hasView, isFalse);

        expect(paths, [
          'http://127.0.0.1:5080/brains/owner/apps/notes/children/panel/item',
          'http://127.0.0.1:5080/brains/owner/apps/node?kind=viewport&name=owner%2Fvp%2F1',
          'http://127.0.0.1:5080/brains/owner/apps/node?kind=webbrowser&name=owner%2Fapps%2Fweb%2Fbrowser',
          'http://127.0.0.1:5080/brains/owner/apps/node?kind=view&name=owner%2Fapps%2Fpending%2Fview',
        ]);
      },
    );
  });
}
