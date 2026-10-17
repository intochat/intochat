import 'package:digitalbrain_flutter/digitalbrain_flutter.dart';
import 'package:test/test.dart';

void main() {
  test(
    'friend slots watch the viewers space rather than foreign neurons',
    () async {
      final link = RecordingLink();
      final client = DigitalBrainUiClient(
        baseUri: Uri.parse('http://localhost'),
      )..link = link;
      for (final slot in [
        ('person', 'bob/profile'),
        ('card', 'bob/posts/p1'),
        ('collection', 'bob/posts'),
      ]) {
        await client.watchNode('alice', slot.$1, slot.$2).toList();
        expect(link.brain, 'alice');
        expect(link.source!.key, 'alice/space');
        expect(link.source!.contract, 'DigitalBrain.Flame.Scene.IScene');
        expect(link.source!.types, ['SceneChanged']);
      }
    },
  );
}

class RecordingLink implements NeuronLink {
  String? brain;
  WatchSourceSpec? source;
  @override
  Stream<NeuronSignal> watch(String brainId, List<WatchSourceSpec> sources) {
    brain = brainId;
    source = sources.single;
    return const Stream.empty();
  }

  @override
  Future<Map<String, dynamic>?> call(
    String brainId,
    String contract,
    String key,
    String method,
    List<Object?> arguments,
  ) async => null;
  @override
  Future<void> shutdown() async {}
}
