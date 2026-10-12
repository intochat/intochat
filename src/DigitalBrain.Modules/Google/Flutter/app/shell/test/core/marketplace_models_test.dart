import 'dart:convert';

import 'package:digitalbrain_flutter/digitalbrain_flutter.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:http/http.dart' as http;
import 'package:http/testing.dart';

void main() {
  test('an installed copy carries its origin and update offer', () {
    final app = InstalledAppSummary.fromJson({
      'name': 'customer-researcher',
      'title': 'Customer Researcher',
      'description': 'Research companies',
      'status': 'Live',
      'version': '1.2.0',
      'operations': ['open'],
      'icon': 'icon.svg',
      'publishedVersion': null,
      'origin': {
        'publisher': 'intochat',
        'name': 'customer-researcher',
        'version': '1.2.0',
      },
      'edited': false,
      'updateAvailable': true,
    });
    expect(app.icon, 'icon.svg');
    expect(app.origin!.publisher, 'intochat');
    expect(app.origin!.version, '1.2.0');
    expect(app.edited, isFalse);
    expect(app.updateAvailable, isTrue);
  });

  test('an app of the brain\'s own has no origin and no update offer', () {
    final app = InstalledAppSummary.fromJson({
      'name': 'shouter',
      'status': 'Live',
      'version': '0.2.0',
      'operations': <String>[],
    });
    expect(app.origin, isNull);
    expect(app.icon, isNull);
    expect(app.edited, isFalse);
    expect(app.updateAvailable, isFalse);
  });

  test('a marketplace entry reads its listing and install state', () {
    final entry = MarketplaceEntry.fromJson({
      'listing': {
        'publisher': 'intochat',
        'source': {
          r'$source': 'brain',
          'accountId': 'acc-1',
          'brainId': 'account-acc-1',
        },
        'name': 'customer-researcher',
        'title': 'Customer Researcher',
        'description': 'Research companies',
        'version': '1.3.0',
        'icon': 'icon.svg',
      },
      'installed': true,
      'updateAvailable': true,
    });
    expect(entry.listing.title, 'Customer Researcher');
    expect(entry.listing.source.brainId, 'account-acc-1');
    expect(entry.listing.source.isShipped, isFalse);
    expect(entry.installed, isTrue);
    expect(entry.updateAvailable, isTrue);
  });

  test(
    'opening a declared scene uses workspace state without app preparation',
    () async {
      final requests = <String>[];
      Map<String, dynamic>? opened;
      final client = DigitalBrainUiClient(
        baseUri: Uri.parse('http://test'),
        httpClient: MockClient((request) async {
          requests.add('${request.method} ${request.url.path}');
          if (request.url.path.endsWith('/apps/ino')) {
            return http.Response(
              jsonEncode({
                'manifest': {
                  'title': 'Ino',
                  'entry': {'kind': 'scene', 'name': 'scenes/home'},
                },
              }),
              200,
            );
          }
          if (request.url.path.endsWith('/windows/open')) {
            opened = jsonDecode(request.body) as Map<String, dynamic>;
            return http.Response(
              jsonEncode({
                'revision': 4,
                'windows': [],
                'focusedWindow': 'b1/apps/ino/window',
              }),
              200,
            );
          }
          return http.Response(jsonEncode({'revision': 3, 'windows': []}), 200);
        }),
      );
      final state = await client.openApp('b1', 'ino');
      expect(opened!['reference'], {
        'kind': 'scene',
        'neuronId': 'b1/apps/ino/scenes/home',
      });
      expect(opened!['expectedRevision'], 3);
      expect(state.focusedWindow, 'b1/apps/ino/window');
      expect(requests.where((r) => r.endsWith('/request')), isEmpty);
      client.close();
    },
  );

  test('a missing icon reads as null', () async {
    final client = DigitalBrainUiClient(
      baseUri: Uri.parse('http://test'),
      httpClient: MockClient((request) async => http.Response('', 404)),
    );
    expect(await client.readBytes('/brains/b1/apps/x/icon'), isNull);
    client.close();
  });
}
