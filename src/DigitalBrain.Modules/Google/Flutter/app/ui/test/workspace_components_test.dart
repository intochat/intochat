import 'package:digitalbrain_ui/src/theme/ui_theme.dart';
import 'package:digitalbrain_ui/src/theme/ui_tokens.dart';
import 'package:digitalbrain_ui/src/components/workspace/ui_workspace_components.dart';
import 'package:flutter/material.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:forui/forui.dart';

Widget host(Widget child, {bool reduced = false, ThemeData? theme}) =>
    MaterialApp(
      theme: theme ?? UiTheme.workspaceDark('lumen'),
      home: MediaQuery(
        data: MediaQueryData(disableAnimations: reduced),
        child: Scaffold(body: child),
      ),
    );

void main() {
  testWidgets('control bridge inherits selected palette', (tester) async {
    final theme = UiTheme.workspaceDark('lumen');
    late Color primary;
    await tester.pumpWidget(
      host(
        UiThemeScope(
          child: Builder(
            builder: (context) {
              primary = FTheme.of(context).colors.primary;
              return const Text('Control');
            },
          ),
        ),
        theme: theme,
      ),
    );
    expect(primary, theme.colorScheme.primary);
  });
  testWidgets('stable identity preserves editor state without replay', (
    tester,
  ) async {
    Widget content(String id) =>
        host(UiMotion(contentKey: id, child: const TextField()));
    await tester.pumpWidget(content('document'));
    final fade = tester.widget<FadeTransition>(
      find
          .descendant(
            of: find.byType(UiMotion),
            matching: find.byType(FadeTransition),
          )
          .first,
    );
    expect(fade.opacity.value, 1);
    expect(fade.opacity.status, AnimationStatus.completed);
    await tester.enterText(find.byType(TextField), 'draft');
    await tester.pumpWidget(content('document'));
    expect(find.text('draft'), findsOneWidget);
    await tester.pumpWidget(content('other'));
    await tester.pump(const Duration(milliseconds: 30));
    expect(tester.hasRunningAnimations, isTrue);
    await tester.pumpAndSettle();
  });
  for (final reduced in [true, false]) {
    testWidgets(
      'motion suppression for ${reduced ? 'accessibility' : 'reconnect'}',
      (tester) async {
        Widget content(String id) => host(
          UiMotion(contentKey: id, animate: reduced, child: Text(id)),
          reduced: reduced,
        );
        await tester.pumpWidget(content('before'));
        await tester.pumpWidget(content('after'));
        expect(find.text('before'), findsNothing);
        expect(find.text('after'), findsOneWidget);
        final fade = tester.widget<FadeTransition>(
          find
              .descendant(
                of: find.byType(UiMotion),
                matching: find.byType(FadeTransition),
              )
              .first,
        );
        expect(fade.opacity.value, 1);
        expect(fade.opacity.status, AnimationStatus.completed);
      },
    );
  }
  testWidgets(
    'collapsed ancestors navigate by identity and current location is inert',
    (tester) async {
      final selected = <String>[];
      await tester.pumpWidget(
        host(
          SizedBox(
            width: 260,
            child: UiBreadcrumbs(
              entries: const [
                UiBreadcrumbEntry(id: 'root', label: 'Files'),
                UiBreadcrumbEntry(id: 'a', label: 'Archive'),
                UiBreadcrumbEntry(id: 'b', label: 'Photos'),
                UiBreadcrumbEntry(id: 'c', label: 'Current'),
              ],
              maxVisible: 2,
              onSelected: selected.add,
            ),
          ),
        ),
      );
      await tester.tap(find.byTooltip('Show parent locations'));
      await tester.pumpAndSettle();
      await tester.tap(find.text('Archive'));
      await tester.pumpAndSettle();
      expect(selected, ['a']);
      await tester.tap(find.text('Current'));
      expect(selected, ['a']);
      expect(tester.takeException(), isNull);
    },
  );
  testWidgets('surface inherits custom theme spacing', (tester) async {
    final theme = UiTheme.workspace(Brightness.light)
        .copyWith(extensions: [const UiTokens(spaceLarge: 30)]);
    await tester.pumpWidget(
      host(const UiSurface(child: Text('Content')), theme: theme),
    );
    final padding = tester.widget<Padding>(
      find
          .descendant(
            of: find.byType(UiSurface),
            matching: find.byType(Padding),
          )
          .first,
    );
    expect(padding.padding, const EdgeInsets.all(30));
  });
}
