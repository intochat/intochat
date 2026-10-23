import 'package:flutter/material.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:intochat_os_flutter/intochat_os_flutter.dart';
import 'package:intochat_os_flutter_shell/workspace/model_settings.dart';

const catalog = ChatModelCatalog(
  automatic: ChatModelOption(label: 'Automatic (workspace default)'),
  models: [
    ChatModelOption(
      id: 'preset:IGpt54Nano',
      label: 'gpt-5.4-nano',
      provider: 'OpenAI',
      model: 'gpt-5.4-nano',
      capabilities: ['Tools'],
    ),
    ChatModelOption(
      id: 'preset:IHaiku45',
      label: 'claude-haiku-4-5',
      provider: 'Anthropic',
      model: 'claude-haiku-4-5',
      available: false,
      capabilities: ['Tools'],
    ),
  ],
);

Future<List<String?>> openPicker(WidgetTester tester) async {
  final picked = <String?>[];
  await tester.pumpWidget(
    MaterialApp(
      home: Scaffold(
        body: Center(
          child: ConversationModelPicker(
            load: () async => catalog,
            selectedId: 'preset:IGpt54Nano',
            onSelected: picked.add,
          ),
        ),
      ),
    ),
  );
  await tester.tap(find.byKey(const Key('conversation-model-picker')));
  await tester.pumpAndSettle();
  return picked;
}

void main() {
  testWidgets('auto_is_first', (tester) async {
    await openPicker(tester);
    final tiles = tester.widgetList<ListTile>(find.byType(ListTile)).toList();
    expect(tiles, hasLength(3));
    final first = find.descendant(
      of: find.byWidget(tiles.first),
      matching: find.text('Auto, cheapest model with tools'),
    );
    expect(first, findsOneWidget);
    expect(find.text('Automatic (workspace default)'), findsNothing);
  });

  testWidgets('unavailable_model_not_selectable', (tester) async {
    final picked = await openPicker(tester);
    final row = find.ancestor(
      of: find.text('claude-haiku-4-5').first,
      matching: find.byType(Opacity),
    );
    expect(row, findsOneWidget);
    expect(tester.widget<Opacity>(row).opacity, 0.5);
    expect(
      find.descendant(
        of: row,
        matching: find.textContaining('Add a key in API keys'),
      ),
      findsOneWidget,
    );

    await tester.tap(find.text('claude-haiku-4-5').first, warnIfMissed: false);
    await tester.pumpAndSettle();
    expect(picked, isEmpty);
    expect(find.byType(Dialog), findsOneWidget);

    await tester.tap(find.text('Auto, cheapest model with tools'));
    await tester.pumpAndSettle();
    expect(picked, [null]);
  });

  testWidgets('unavailable_model_shows_its_server_reason', (tester) async {
    await tester.pumpWidget(
      MaterialApp(
        home: Scaffold(
          body: ModelSettings(
            load: () async => const ChatModelCatalog(
              automatic: ChatModelOption(label: 'Automatic'),
              models: [
                ChatModelOption(
                  id: 'preset:IGemma4',
                  label: 'gemma4',
                  available: false,
                  reason: 'Provider Ollama is not configured.',
                ),
              ],
            ),
            onSelected: (_) {},
          ),
        ),
      ),
    );
    await tester.pumpAndSettle();
    expect(
      find.textContaining('Provider Ollama is not configured.'),
      findsOneWidget,
    );
    expect(find.textContaining('Add a key in API keys'), findsNothing);
  });

  testWidgets('auto_unavailable_links_to_api_keys', (tester) async {
    final picked = <String?>[];
    var keys = 0;
    await tester.pumpWidget(
      MaterialApp(
        home: Scaffold(
          body: ModelSettings(
            load: () async => const ChatModelCatalog(
              automatic: ChatModelOption(label: 'Automatic', available: false),
              models: [],
            ),
            onSelected: picked.add,
            onAddKey: () => keys++,
          ),
        ),
      ),
    );
    await tester.pumpAndSettle();
    await tester.tap(
      find.text('Auto, cheapest model with tools'),
      warnIfMissed: false,
    );
    await tester.pumpAndSettle();
    expect(picked, isEmpty);
    await tester.tap(find.byKey(const Key('model-add-api-key')));
    expect(keys, 1);
  });
}
