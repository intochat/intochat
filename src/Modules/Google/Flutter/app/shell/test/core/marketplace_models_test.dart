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
        'publisherAccountId': 'acc-1',
        'publisherBrainId': 'account-acc-1',
        'name': 'customer-researcher',
        'title': 'Customer Researcher',
        'description': 'Research companies',
        'version': '1.3.0',
        'icon': 'icon.svg',
        'scenarios': ['Opening the researcher composes its surface'],
      },
      'installed': true,
      'updateAvailable': true,
    });
    expect(entry.listing.title, 'Customer Researcher');
    expect(entry.listing.scenarios, [
      'Opening the researcher composes its surface',
    ]);
    expect(entry.installed, isTrue);
    expect(entry.updateAvailable, isTrue);
    expect(() => entry.listing.scenarios.add('x'), throwsUnsupportedError);
  });

  test('seeded accounts are empty when the server is not in open posture', () async {
    final client = DigitalBrainUiClient(
      baseUri: Uri.parse('http://test'),
      httpClient: MockClient((request) async => http.Response('', 404)),
    );
    expect(await client.listSeededAccounts(), isEmpty);
    client.close();
  });

  test('continuing as a seeded account adopts its session', () async {
    final paths = <String>[];
    final client = DigitalBrainUiClient(
      baseUri: Uri.parse('http://test'),
      httpClient: MockClient((request) async {
        paths.add('${request.method} ${request.url.path}');
        if (request.url.path == '/identity/seeded-accounts') {
          return http.Response(
            jsonEncode([
              {'username': 'intochat', 'displayName': 'IntoChat'},
            ]),
            200,
          );
        }
        return http.Response(
          jsonEncode({
            'principalId': 'intochat',
            'accountId': 'acc-1',
            'brainId': 'account-acc-1',
          }),
          200,
        );
      }),
    );
    final accounts = await client.listSeededAccounts();
    expect(accounts.single.displayName, 'IntoChat');
    await client.signInSeeded('intochat');
    expect(paths.last, 'POST /identity/seeded-accounts/intochat/login');
    expect(client.workspaceIdentity, 'intochat|acc-1');
    expect(client.defaultBrainId, 'account-acc-1');
    client.close();
  });

  test('a docked open asks the app for its surface reference', () async {
    Object? sent;
    final client = DigitalBrainUiClient(
      baseUri: Uri.parse('http://test'),
      httpClient: MockClient((request) async {
        sent = jsonDecode(request.body);
        expect(request.url.path, '/brains/b1/apps/assistant/ask');
        return http.Response(
          jsonEncode({
            'id': 'b1/apps/assistant/window',
            'title': 'Assistant',
            'surface': 'b1/apps/assistant/surface',
          }),
          200,
        );
      }),
    );
    final surface = await client.openDockedApp('b1', 'assistant');
    expect(sent, {'operation': 'open', 'input': 'docked'});
    expect(surface, {'kind': 'surface', 'name': 'b1/apps/assistant/surface'});
    client.close();
  });

  test('a missing icon reads as null', () async {
    final client = DigitalBrainUiClient(
      baseUri: Uri.parse('http://test'),
      httpClient: MockClient((request) async => http.Response('', 404)),
    );
    expect(await client.readBytes('/brains/b1/apps/x/icon'), isNull);
    client.close();
  });
}
