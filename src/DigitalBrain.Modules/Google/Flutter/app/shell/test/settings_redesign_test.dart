import 'package:digitalbrain_flutter/digitalbrain_flutter.dart';
import 'package:digitalbrain_flutter_shell/workspace/model_settings.dart';
import 'package:digitalbrain_ui/digitalbrain_ui.dart';
import 'package:digitalbrain_flutter_shell/workspace/workspace_settings.dart';
import 'package:digitalbrain_flutter_shell/workspace/workspace_store.dart';
import 'package:flutter/material.dart';
import 'package:flutter_test/flutter_test.dart';

const catalog = ChatModelCatalog(
  automatic: ChatModelOption(label: 'Automatic (workspace default)'),
  models: [
    ChatModelOption(
      id: 'profile:fast',
      label: 'Fast',
      provider: 'Provider',
      model: 'model',
      capabilities: ['Tools'],
    ),
  ],
);

void main() {
  paletteTests();
  testWidgets('narrow settings switches Models and UI Kit without overflow', (
    tester,
  ) async {
    tester.view.physicalSize = const Size(390, 780);
    tester.view.devicePixelRatio = 1;
    addTearDown(tester.view.resetPhysicalSize);
    addTearDown(tester.view.resetDevicePixelRatio);
    final store = WorkspaceStore();
    await tester.pumpWidget(
      MaterialApp(
        home: WorkspaceSettings(
          store: store,
          onClose: () {},
          loadModels: () async => catalog,
        ),
      ),
    );
    await tester.tap(find.byType(DropdownButtonFormField<String>).first);
    await tester.pumpAndSettle();
    await tester.tap(find.text('Models').last);
    await tester.pumpAndSettle();
    await tester.tap(find.text('Fast'));
    await tester.pumpAndSettle();
    expect(store.settings.defaultModelProfile, 'profile:fast');
    await tester.tap(find.byType(DropdownButtonFormField<String>).first);
    await tester.pumpAndSettle();
    await tester.tap(find.text('UI Kit').last);
    await tester.pumpAndSettle();
    expect(find.byKey(const Key('ui_gallery_screen')), findsOneWidget);
    expect(tester.takeException(), isNull);
    await tester.pumpWidget(const SizedBox());
    store.dispose();
  });

  testWidgets('model load retries and preserves unavailable saved choice', (
    tester,
  ) async {
    var attempts = 0;
    String? selected = 'profile:removed';
    await tester.pumpWidget(
      MaterialApp(
        home: Scaffold(
          body: SingleChildScrollView(
            child: ModelSettings(
              selectedId: selected,
              onSelected: (id) => selected = id,
              load: () async {
                if (++attempts == 1) throw StateError('offline');
                return catalog;
              },
            ),
          ),
        ),
      ),
    );
    await tester.pumpAndSettle();
    expect(find.text('Models could not be loaded.'), findsOneWidget);
    await tester.tap(find.text('Retry'));
    await tester.pumpAndSettle();
    expect(find.textContaining('no longer available'), findsOneWidget);
    expect(selected, 'profile:removed');
    await tester.tap(find.text('Automatic (workspace default)'));
    expect(selected, isNull);
  });

  testWidgets('declared select options send selection to their neuron', (
    tester,
  ) async {
    final events = <Map<String, dynamic>>[];
    await tester.pumpWidget(
      MaterialApp(
        home: Scaffold(
          body: NeuronView(
            kind: 'select',
            name: 'model',
            load: (_, _) async => {
              'label': 'Model',
              'selected': null,
              'options': [
                {'id': 'profile:fast', 'label': 'Fast', 'enabled': true},
              ],
            },
            onAction: (event) async => events.add(event),
          ),
        ),
      ),
    );
    await tester.pumpAndSettle();
    await tester.tap(find.byTooltip('Model'));
    await tester.pumpAndSettle();
    await tester.tap(find.text('Fast'));
    await tester.pumpAndSettle();
    expect(events.single, {
      'kind': 'select',
      'name': 'model',
      'value': 'profile:fast',
    });
    await tester.pumpWidget(const SizedBox());
  });
}

void paletteTests() {
  test('old theme values load as palette names and palettes round-trip', () {
    expect(WorkspacePreferences.fromJson({'theme': 'dark'}).theme, 'midnight');
    expect(WorkspacePreferences.fromJson({'theme': 'light'}).theme, 'paper');
    expect(WorkspacePreferences.fromJson({}).theme, 'midnight');
    expect(WorkspacePreferences.fromJson({'theme': 'lumen'}).theme, 'lumen');
    expect(WorkspacePreferences().toJson()['theme'], 'midnight');
  });

  testWidgets(
    'settings picker offers the three palettes and saves the choice',
    (tester) async {
      final store = WorkspaceStore();
      await tester.pumpWidget(
        MaterialApp(
          home: WorkspaceSettings(
            store: store,
            onClose: () {},
            loadModels: () async => catalog,
          ),
        ),
      );
      await tester.scrollUntilVisible(
        find.byKey(const Key('theme_picker')),
        200,
        scrollable: find.byType(Scrollable).last,
      );
      await tester.tap(find.byKey(const Key('theme_picker')));
      await tester.pumpAndSettle();
      expect(find.text('Midnight').hitTestable(), findsWidgets);
      expect(find.text('Paper').hitTestable(), findsWidgets);
      await tester.tap(find.text('Lumen').last);
      await tester.pumpAndSettle();
      expect(store.settings.theme, 'lumen');
    },
  );
}
