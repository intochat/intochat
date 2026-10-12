import 'dart:async';

import 'package:digitalbrain_flutter/digitalbrain_flutter.dart';
import 'package:digitalbrain_flutter_shell/workspace/workspace_app.dart';
import 'package:digitalbrain_flutter_shell/workspace/workspace_store.dart';
import 'package:digitalbrain_ui/digitalbrain_ui.dart';
import 'package:flutter/material.dart';
import 'package:flutter_test/flutter_test.dart';

import 'customer_researcher_window_test.dart' show ResearchWindowClient;
import 'workspace_remote_controller_test.dart' show MemoryPersistence;

/// Records what the shell subscribes to; delivers nothing.
final class RecordingLink implements NeuronLink {
  final List<(String, List<WatchSourceSpec>)> watches = [];
  @override
  Future<Map<String, dynamic>?> call(
    String brainId,
    String contract,
    String key,
    String method,
    List<Object?> arguments,
  ) async => null;
  @override
  Stream<NeuronSignal> watch(String brainId, List<WatchSourceSpec> sources) {
    watches.add((brainId, sources));
    return const Stream.empty();
  }

  @override
  Future<void> shutdown() async {}
}

void main() {
  testWidgets(
    'with a neuron link the workspace and its nodes are watched, not polled',
    (tester) async {
      tester.view.physicalSize = const Size(1600, 1000);
      tester.view.devicePixelRatio = 1;
      addTearDown(tester.view.resetPhysicalSize);
      addTearDown(tester.view.resetDevicePixelRatio);
      final store = WorkspaceStore(persistence: MemoryPersistence());
      final api = ResearchWindowClient(store);
      final link = RecordingLink();
      final client = DigitalBrainUiClient(
        baseUri: Uri.parse('http://test'),
        httpClient: api,
      )..link = link;
      expect(client.grpcLive, isTrue);
      await tester.pumpWidget(
        WorkspaceApp(store: store, programmingClient: client),
      );
      await tester.pumpAndSettle();
      await tester.tap(find.text('Apps and brain'));
      await tester.pumpAndSettle();
      await tester.tap(find.text('Customer Researcher'));
      await tester.pumpAndSettle();
      expect(tester.takeException(), isNull);

      final view = tester.widget<NeuronView>(find.byType(NeuronView).first);
      expect(view.watch, isNotNull);
      expect(
        link.watches.any(
          (w) => w.$2.any(
            (s) => s.key == api.surface && s.types.contains('SurfaceChanged'),
          ),
        ),
        isTrue,
        reason: 'the surface node is watched through the link',
      );
      expect(
        link.watches.any(
          (w) => w.$2.any((s) => s.types.contains('WorkspaceChanged')),
        ),
        isTrue,
        reason: 'the workspace is watched through the link',
      );
      await tester.pumpWidget(const SizedBox());
      await api.events.close();
      client.close();
      store.dispose();
    },
  );
}
