import 'dart:convert';

import 'package:digitalbrain_flutter/digitalbrain_flutter.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:http/http.dart' as http;
import 'package:http/testing.dart';

void main() {
  test('a manifest carries its toolbar with kinds and slider bounds', () {
    final manifest = AppManifest.fromJson({
      'title': 'Player',
      'description': 'plays',
      'icon': 'icon.svg',
      'operations': [
        {'name': 'play', 'description': ''},
        {'name': 'volume', 'description': ''},
      ],
      'toolbar': [
        {'operation': 'play', 'icon': 'play', 'label': 'Play'},
        {
          'operation': 'volume',
          'kind': 'slider',
          'icon': 'volume',
          'min': 0,
          'max': 100,
        },
      ],
    });
    expect(manifest.operations, ['play', 'volume']);
    expect(manifest.toolbar.map((entry) => entry.kind), [
      AppToolbarKind.button,
      AppToolbarKind.slider,
    ]);
    expect(manifest.toolbar.last.max, 100);
    expect(manifest.toolbar.first.label, 'Play');
  });

  test('a manifest without a toolbar has none', () {
    expect(
      AppManifest.fromJson({'title': 'Echo', 'operations': [], 'toolbar': null})
          .toolbar,
      isEmpty,
    );
    expect(AppToolbarKind.parse('knob'), AppToolbarKind.button);
  });

  test('the client reads a manifest from the app view', () async {
    final client = DigitalBrainUiClient(
      baseUri: Uri.parse('http://test'),
      httpClient: MockClient((request) async {
        expect(request.url.path, '/brains/b1/apps/player');
        return http.Response(
          jsonEncode({
            'name': 'player',
            'manifest': {
              'title': 'Player',
              'description': '',
              'operations': [
                {'name': 'play', 'description': ''},
              ],
              'toolbar': [
                {'operation': 'play'},
              ],
            },
          }),
          200,
        );
      }),
    );
    final manifest = await client.readAppManifest('b1', 'player');
    expect(manifest.toolbar.single.operation, 'play');
    client.close();
  });

  test('the client reads infrastructure rows', () async {
    final client = DigitalBrainUiClient(
      baseUri: Uri.parse('http://test'),
      httpClient: MockClient((request) async {
        expect(request.url.path, '/brains/b1/infrastructure');
        return http.Response(
          jsonEncode([
            {
              'module': 'postgres',
              'resource': 'Modules-postgres-server',
              'state': 'Running',
              'health': 'Healthy',
            },
            {
              'module': 'aspire',
              'resource': 'apphost',
              'state': 'Unknown',
              'health': null,
            },
          ]),
          200,
        );
      }),
    );
    final rows = await client.readInfrastructure('b1');
    expect(rows.map((row) => row.module), ['postgres', 'aspire']);
    expect(rows.last.health, isNull);
    client.close();
  });
}
