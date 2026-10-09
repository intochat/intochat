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
    'a docked open posts a request and reads the app surface neuron',
    () async {
      Object? sent;
      final client = DigitalBrainUiClient(
        baseUri: Uri.parse('http://test'),
        httpClient: MockClient((request) async {
          sent = jsonDecode(request.body);
          expect(request.url.path, '/brains/b1/apps/assistant/request');
          return http.Response(jsonEncode({'requestId': 'request-1'}), 200);
        }),
      );
      final surface = await client.openDockedApp('b1', 'assistant');
      expect(sent, {'operation': 'open', 'input': 'docked'});
      expect(surface, {'kind': 'surface', 'name': 'b1/apps/assistant/surface'});
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
