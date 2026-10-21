import 'package:intochat_os_flutter/intochat_os_flutter.dart';
import 'package:intochat_os_flutter_shell/workspace/home/app_detail_screen.dart';
import 'package:flutter/material.dart';
import 'package:flutter_test/flutter_test.dart';

const _listingJson = {
  'publisher': 'intochat',
  'source': {
    r'$source': 'brain',
    'accountId': 'acc-1',
    'brainId': 'account-acc-1',
  },
  'name': 'customer-researcher',
  'title': 'Customer Researcher',
  'description': 'Research a company and keep verified evidence.',
  'version': '1.3.0',
  'icon': null,
};

class _FakeMarket {
  _FakeMarket({
    this.installed = false,
    this.edited = false,
    this.update = false,
    this.listingJson = _listingJson,
  });
  bool installed, edited, update;
  final Map<String, dynamic> listingJson;
  final calls = <String>[];

  Map<String, dynamic> get _app => {
    'name': 'customer-researcher',
    'manifest': {
      'entry': {'kind': 'surface', 'name': 'surface'},
      'title': 'Customer Researcher',
      'description': 'Research a company and keep verified evidence.',
      'operations': [
        {'name': 'open', 'description': 'Open the window'},
      ],
    },
    'status': 'Live',
    'version': '1.2.0',
    'origin': {
      'publisher': 'intochat',
      'name': 'customer-researcher',
      'version': '1.2.0',
    },
    'edited': edited,
  };

  Future<dynamic> request(String method, String path, [Object? body]) async {
    calls.add('$method $path');
    const market = '/brains/b1/marketplace/intochat/customer-researcher';
    if (method == 'GET' && path == market) {
      return {
        'listing': listingJson,
        'installed': installed,
        'updateAvailable': update && !edited,
      };
    }
    if (method == 'POST' && path == '$market/install') {
      installed = true;
      return _app;
    }
    if (method == 'GET' && path == '/brains/b1/apps/customer-researcher') {
      return _app;
    }
    if (method == 'POST' &&
        path == '/brains/b1/apps/customer-researcher/update') {
      update = false;
      return _app;
    }
    throw StateError('Unexpected $method $path');
  }
}

Widget _host(Widget child) => MaterialApp(home: child);

void main() {
  final listing = MarketplaceListing.fromJson(_listingJson);

  testWidgets('install shows what the app requires before the one consent', (
    tester,
  ) async {
    final requiring = {
      ..._listingJson,
      'requires': {
        'semanticTypes': ['person.email'],
        'connectors': ['gmail'],
        'secrets': ['openai-key'],
      },
    };
    final server = _FakeMarket(listingJson: requiring);
    await tester.pumpWidget(
      _host(
        AppDetailScreen(
          workspaceId: 'b1',
          request: server.request,
          listing: MarketplaceListing.fromJson(requiring),
          onOpen: (name) async {},
        ),
      ),
    );
    await tester.pumpAndSettle();

    expect(find.byKey(const ValueKey('detail-requires')), findsOneWidget);
    expect(find.text('Your data: person.email'), findsOneWidget);
    expect(find.text('Connections: gmail'), findsOneWidget);
    expect(find.text('Secrets: openai-key'), findsOneWidget);
    await tester.tap(find.byKey(const ValueKey('install-app')));
    await tester.pumpAndSettle();
    expect(find.byKey(const ValueKey('detail-requires')), findsNothing);
  });

  testWidgets('a marketplace app shows what it is and installs in one step', (
    tester,
  ) async {
    final server = _FakeMarket();
    final opened = <String>[];
    await tester.pumpWidget(
      _host(
        AppDetailScreen(
          workspaceId: 'b1',
          request: server.request,
          listing: listing,
          onOpen: (name) async => opened.add(name),
        ),
      ),
    );
    await tester.pumpAndSettle();

    expect(find.byKey(const ValueKey('detail-title')), findsOneWidget);
    expect(find.text('Customer Researcher'), findsWidgets);
    expect(find.text('by intochat · v1.3.0'), findsOneWidget);
    expect(
      find.text('Research a company and keep verified evidence.'),
      findsOneWidget,
    );
    expect(find.byKey(const ValueKey('open-app')), findsNothing);

    await tester.tap(find.byKey(const ValueKey('install-app')));
    await tester.pumpAndSettle();
    expect(
      server.calls,
      contains(
        'POST /brains/b1/marketplace/intochat/customer-researcher/install',
      ),
    );
    expect(find.byKey(const ValueKey('install-app')), findsNothing);
    expect(find.text('Installed.'), findsOneWidget);

    await tester.tap(find.byKey(const ValueKey('open-app')));
    await tester.pumpAndSettle();
    expect(opened, ['customer-researcher']);
  });

  testWidgets('an installed copy offers its update', (tester) async {
    final server = _FakeMarket(installed: true, update: true);
    await tester.pumpWidget(
      _host(
        AppDetailScreen(
          workspaceId: 'b1',
          request: server.request,
          installedName: 'customer-researcher',
          onOpen: (_) async {},
        ),
      ),
    );
    await tester.pumpAndSettle();

    // Reached from My apps: the listing is found through the copy's origin.
    expect(
      server.calls,
      contains('GET /brains/b1/marketplace/intochat/customer-researcher'),
    );
    await tester.tap(find.byKey(const ValueKey('update-app')));
    await tester.pumpAndSettle();
    expect(
      server.calls,
      contains('POST /brains/b1/apps/customer-researcher/update'),
    );
    expect(find.byKey(const ValueKey('update-app')), findsNothing);
  });

  testWidgets('an edited copy says why it no longer updates', (tester) async {
    final server = _FakeMarket(installed: true, update: true, edited: true);
    await tester.pumpWidget(
      _host(
        AppDetailScreen(
          workspaceId: 'b1',
          request: server.request,
          installedName: 'customer-researcher',
          onOpen: (_) async {},
        ),
      ),
    );
    await tester.pumpAndSettle();
    expect(find.byKey(const ValueKey('update-app')), findsNothing);
    expect(find.byKey(const ValueKey('edited-note')), findsOneWidget);
  });

  testWidgets('a failed install shows the server reason and keeps Install', (
    tester,
  ) async {
    Future<dynamic> refusing(String method, String path, [Object? body]) async {
      if (method == 'GET') {
        return {
          'listing': _listingJson,
          'installed': false,
          'updateAvailable': false,
        };
      }
      throw StateError('An app named customer-researcher is already here.');
    }

    await tester.pumpWidget(
      _host(
        AppDetailScreen(
          workspaceId: 'b1',
          request: refusing,
          listing: listing,
          onOpen: (_) async {},
        ),
      ),
    );
    await tester.pumpAndSettle();
    await tester.tap(find.byKey(const ValueKey('install-app')));
    await tester.pumpAndSettle();
    expect(find.byKey(const ValueKey('detail-error')), findsOneWidget);
    expect(find.textContaining('already here'), findsOneWidget);
    expect(find.byKey(const ValueKey('install-app')), findsOneWidget);
  });
}
