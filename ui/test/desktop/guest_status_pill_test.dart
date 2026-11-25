import 'dart:convert';
import 'dart:io';

import 'package:flutter/material.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:http/http.dart' as http;
import 'package:http/testing.dart';
import 'package:intochat_os_flutter_shell/desktop/guest_status_pill.dart';
import 'package:intochat_os_flutter_shell/desktop/live_desktop_shell.dart';
import 'package:intochat_os_flutter_shell/intochat_os_flutter.dart';
import 'package:intochat_os_flutter_shell/intochat_os_ui.dart';

import 'live_desktop_shell_test.dart' show desktopViewJson, viewportJson;

void main() {
  group('formatGuestRemaining', () {
    test('formats fallback, hours, minutes, and expiring lifetimes', () {
      expect(formatGuestRemaining(null), '24h');
      expect(formatGuestRemaining(const Duration(hours: 24)), '24h');
      expect(
        formatGuestRemaining(const Duration(hours: 11, minutes: 45)),
        '11h 45m',
      );
      expect(formatGuestRemaining(const Duration(hours: 3)), '3h');
      expect(formatGuestRemaining(const Duration(minutes: 42)), '42m');
      expect(formatGuestRemaining(const Duration(seconds: 15)), '1m');
      expect(formatGuestRemaining(Duration.zero), 'Expiring');
      expect(formatGuestRemaining(const Duration(minutes: -5)), 'Expiring');
    });
  });

  group('IntoChatOSUiClient guest session detection', () {
    Future<IntoChatOSUiClient> clientWithSession(
      Map<String, dynamic> payload,
    ) async {
      final mock = MockClient((request) async {
        if (request.url.path == '/identity/session') {
          return http.Response(jsonEncode(payload), 200);
        }
        return http.Response('', 404);
      });
      final client = IntoChatOSUiClient(
        baseUri: Uri.parse('http://localhost:5000'),
        httpClient: mock,
      );
      await client.readSession();
      return client;
    }

    test('regular member session is not a guest session', () async {
      final client = await clientWithSession({
        'principalId': 'alice',
        'accountId': 'alice',
        'brainId': 'brain-1',
        'appId': null,
      });
      expect(client.isGuestSession, isFalse);
      expect(client.guestRemaining, isNull);
    });

    test('detects guest via appId=guest', () async {
      final client = await clientWithSession({
        'principalId': 'visitor-1',
        'accountId': 'visitor-1',
        'brainId': 'visitor-1',
        'appId': 'guest',
      });
      expect(client.isGuestSession, isTrue);
    });

    test('detects guest via try- prefix', () async {
      final client = await clientWithSession({
        'principalId': 'try-0123456789abcdef',
        'accountId': 'try-0123456789abcdef',
        'brainId': 'try-0123456789abcdef',
      });
      expect(client.isGuestSession, isTrue);
    });

    test('detects explicit isEphemeral flag and parses expiresAt', () async {
      final future = DateTime.now().toUtc().add(const Duration(hours: 6));
      final client = await clientWithSession({
        'principalId': 'ephemeral-user',
        'accountId': 'ephemeral-user',
        'brainId': 'temp-brain',
        'isEphemeral': true,
        'expiresAt': future.toIso8601String(),
      });
      expect(client.isGuestSession, isTrue);
      expect(client.guestRemaining, isNotNull);
      expect(client.guestRemaining!.inHours, greaterThanOrEqualTo(5));
    });
  });

  group('GuestStatusPill widget', () {
    Widget wrap(Widget child, {bool dark = false}) => MaterialApp(
      theme: dark ? UiTheme.dark('warm-paper') : UiTheme.light('warm-paper'),
      builder: (context, inner) => IcTheme(
        colors: IcColors.forPreset(IcStylePreset.warmPaper, dark: dark),
        child: inner!,
      ),
      home: Scaffold(body: Center(child: child)),
    );

    testWidgets(
      'renders default 24h status and triggers primary & secondary actions',
      (tester) async {
        var saveTapped = false;
        var downloadTapped = false;

        await tester.pumpWidget(
          wrap(
            GuestStatusPill(
              onSaveBrain: () => saveTapped = true,
              onDownloadApp: () => downloadTapped = true,
            ),
          ),
        );

        expect(find.byKey(const ValueKey('guest-status-pill')), findsOneWidget);
        expect(find.text('Guest Brain · Temp RAM · 24h'), findsOneWidget);
        expect(find.text('Save My Brain →'), findsOneWidget);
        expect(find.text('Download App ↓'), findsOneWidget);

        await tester.tap(find.byKey(const ValueKey('guest-pill-save-brain')));
        await tester.pump();
        expect(saveTapped, isTrue);

        await tester.tap(find.byKey(const ValueKey('guest-pill-download-app')));
        await tester.pump();
        expect(downloadTapped, isTrue);
      },
    );

    testWidgets(
      'displays remaining time and fits narrow mobile viewport in dark mode',
      (tester) async {
        await tester.binding.setSurfaceSize(const Size(360, 640));
        addTearDown(() => tester.binding.setSurfaceSize(null));

        await tester.pumpWidget(
          wrap(
            GuestStatusPill(
              remaining: const Duration(hours: 11, minutes: 30),
              onSaveBrain: () {},
              onDownloadApp: () {},
            ),
            dark: true,
          ),
        );

        expect(find.text('Guest Brain · Temp RAM · 11h 30m'), findsOneWidget);
        expect(tester.takeException(), isNull);
      },
    );
  });

  group('DownloadAppModal widget', () {
    testWidgets(
      'renders all 5 platforms, fires release asset & /download hub links, and closes',
      (tester) async {
        await tester.binding.setSurfaceSize(const Size(1200, 860));
        addTearDown(() => tester.binding.setSurfaceSize(null));

        final opened = <Uri>[];
        var closed = false;

        await tester.pumpWidget(
          MaterialApp(
            theme: UiTheme.light('warm-paper'),
            builder: (context, child) => IcTheme(
              colors: IcColors.forPreset(IcStylePreset.warmPaper, dark: false),
              child: child!,
            ),
            home: Scaffold(
              body: DownloadAppModal(
                onClose: () => closed = true,
                onOpenUrl: opened.add,
                downloadHubUri: Uri.parse('https://os.intochat.dev/download'),
              ),
            ),
          ),
        );

        expect(
          find.byKey(const ValueKey('download-app-modal')),
          findsOneWidget,
        );
        expect(
          find.byKey(const ValueKey('download-platform-windows')),
          findsOneWidget,
        );
        expect(
          find.byKey(const ValueKey('download-platform-macos')),
          findsOneWidget,
        );
        expect(
          find.byKey(const ValueKey('download-platform-linux')),
          findsOneWidget,
        );
        expect(
          find.byKey(const ValueKey('download-platform-android')),
          findsOneWidget,
        );
        expect(
          find.byKey(const ValueKey('download-platform-ios')),
          findsOneWidget,
        );

        await tester.tap(
          find.byKey(
            const ValueKey('download-asset-IntoChat-Setup-win-x64.exe'),
          ),
        );
        await tester.pump();
        expect(
          opened.last.toString(),
          'https://github.com/intochat/intochat/releases/latest/download/IntoChat-Setup-win-x64.exe',
        );

        await tester.tap(find.byKey(const ValueKey('download-modal-open-hub')));
        await tester.pump();
        expect(opened.last.toString(), 'https://os.intochat.dev/download');

        await tester.tap(find.byKey(const ValueKey('download-modal-close')));
        await tester.pump();
        expect(closed, isTrue);
      },
    );
  });

  group('LiveDesktopShell guest pill integration', () {
    Widget buildShell({
      required String brain,
      bool isGuest = false,
      bool isEphemeral = false,
      Duration? guestRemaining,
      VoidCallback? onSaveBrain,
      void Function(Uri)? onOpenUrl,
    }) {
      return MaterialApp(
        theme: UiTheme.light('warm-paper'),
        builder: (context, child) => IcTheme(
          colors: IcColors.forPreset(IcStylePreset.warmPaper, dark: false),
          child: child!,
        ),
        home: Scaffold(
          body: LiveDesktopShell(
            knowsApp: (app) => app == 'ino',
            brain: brain,
            name: '$brain/apps/desktop/view',
            load: (kind, name) async {
              if (kind == 'viewport') return viewportJson(name);
              if (name == '$brain/apps/desktop/view') {
                return desktopViewJson(windows: const {'w-1': 'Ino'});
              }
              return {
                'generation': 1,
                'view': {
                  'title': name,
                  'root': {'kind': 'text', 'value': 'Viewport: $name'},
                },
              };
            },
            watch: (kind, name) => const Stream.empty(),
            onAction: (_) async => null,
            onActivate: (_) {},
            stylePreset: IcStylePreset.warmPaper,
            barLayout: BarLayout.floatingGlass,
            paletteOpen: false,
            onTogglePalette: () {},
            onSelectPreset: (_) {},
            onCycleBarLayout: () {},
            isGuest: isGuest,
            isEphemeral: isEphemeral,
            guestRemaining: guestRemaining,
            onSaveBrain: onSaveBrain,
            onOpenUrl: onOpenUrl,
            downloadHubUri: Uri.parse('http://localhost:5000/download'),
          ),
        ),
      );
    }

    testWidgets(
      'shows GuestStatusPill for guest sessions, triggers Save My Brain, and opens/closes DownloadAppModal',
      (tester) async {
        await tester.binding.setSurfaceSize(const Size(1440, 900));
        addTearDown(() => tester.binding.setSurfaceSize(null));

        var saveClicked = false;
        await tester.pumpWidget(
          buildShell(
            brain: 'try-guest-1',
            guestRemaining: const Duration(hours: 23, minutes: 59),
            onSaveBrain: () => saveClicked = true,
          ),
        );
        await tester.pump();
        await tester.pump(const Duration(milliseconds: 50));

        expect(find.byType(GuestStatusPill), findsOneWidget);
        expect(find.text('Guest Brain · Temp RAM · 23h 59m'), findsOneWidget);

        await tester.tap(find.byKey(const ValueKey('guest-pill-save-brain')));
        await tester.pump();
        expect(saveClicked, isTrue);

        await tester.tap(find.byKey(const ValueKey('guest-pill-download-app')));
        await tester.pump();
        expect(find.byType(DownloadAppModal), findsOneWidget);

        await tester.tap(find.byKey(const ValueKey('download-modal-close')));
        await tester.pump();
        expect(find.byType(DownloadAppModal), findsNothing);
      },
    );

    testWidgets('hides GuestStatusPill for non-guest sessions', (tester) async {
      await tester.binding.setSurfaceSize(const Size(1440, 900));
      addTearDown(() => tester.binding.setSurfaceSize(null));

      await tester.pumpWidget(buildShell(brain: 'brain-1', isGuest: false));
      await tester.pump();
      await tester.pump(const Duration(milliseconds: 50));

      expect(find.byType(GuestStatusPill), findsNothing);
      expect(find.byType(DownloadAppModal), findsNothing);
    });
  });

  group('iOS & Mobile Web-Clip PWA assets', () {
    test('web/manifest.json declares standalone display, warm tokens, and existing icons', () {
      final manifestFile = File('web/manifest.json');
      expect(manifestFile.existsSync(), isTrue);
      final manifest =
          jsonDecode(manifestFile.readAsStringSync()) as Map<String, dynamic>;

      expect(manifest['display'], 'standalone');
      expect(manifest['background_color'], '#FBFAF7');
      expect(manifest['theme_color'], '#FBFAF7');

      final icons = (manifest['icons'] as List<dynamic>)
          .cast<Map<String, dynamic>>();
      expect(icons.length, greaterThanOrEqualTo(4));
      for (final icon in icons) {
        final src = icon['src'] as String;
        expect(
          File('web/$src').existsSync(),
          isTrue,
          reason: 'Expected icon web/$src to exist',
        );
      }
    });

    test('web/index.html configures iOS & Android PWA meta tags and light/dark theme colors', () {
      final indexFile = File('web/index.html');
      expect(indexFile.existsSync(), isTrue);
      final html = indexFile.readAsStringSync();

      expect(
        html,
        contains('<meta name="apple-mobile-web-app-capable" content="yes">'),
      );
      expect(
        html,
        contains('<meta name="mobile-web-app-capable" content="yes">'),
      );
      expect(
        html,
        contains(
          '<meta name="apple-mobile-web-app-status-bar-style" content="black-translucent">',
        ),
      );
      expect(html, contains('viewport-fit=cover'));
      expect(html, contains('rel="apple-touch-icon"'));
      expect(html, contains('href="icons/Icon-192.png"'));
      expect(
        html,
        contains(
          '<meta name="theme-color" content="#FBFAF7" media="(prefers-color-scheme: light)">',
        ),
      );
      expect(
        html,
        contains(
          '<meta name="theme-color" content="#17140F" media="(prefers-color-scheme: dark)">',
        ),
      );
    });
  });
}
