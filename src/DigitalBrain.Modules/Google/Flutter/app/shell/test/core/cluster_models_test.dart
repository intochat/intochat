import 'package:digitalbrain_flutter/digitalbrain_flutter.dart';
import 'package:flutter_test/flutter_test.dart';

void main() {
  test('a ClusterChanged payload decodes its stage, build and silos', () {
    final cluster = ClusterChanged.fromJson({
      'stage': 'rolling',
      'sha': 'abc',
      'silos': [
        {'id': 's1', 'sha': 'abc', 'state': 'Running', 'health': 'Healthy'},
        {'id': 's2', 'health': ''},
      ],
    });
    expect(cluster.stage, 'rolling');
    expect(
      cluster.silos.first,
      const SiloState(
        id: 's1',
        sha: 'abc',
        state: 'Running',
        health: 'Healthy',
      ),
    );
    expect(cluster.silos.last.state, 'Unknown');
    expect(cluster.silos.last.health, isNull);
  });

  test('a live envelope keeps the type, publisher and payload', () {
    final envelope = LiveEnvelope.fromJson({
      'type': 'AppActivity',
      'publisher': 'b/apps/x',
      'payload': {'app': 'x', 'state': 'working'},
    });
    expect(envelope.type, 'AppActivity');
    expect(envelope.payload['state'], 'working');
  });

  test('the ready event is a marker, not a signal', () {
    expect(LiveEnvelope.fromJson({'type': 'ready'}).isReady, isTrue);
    expect(
      LiveEnvelope.fromJson({'type': 'AppActivity', 'payload': {}}).isReady,
      isFalse,
    );
  });

  test('an AppActivity payload decodes the app, its state and detail', () {
    final activity = AppActivity.fromJson({
      'app': 'assistant',
      'state': 'working',
      'detail': 'Reading your mail',
    });
    expect(activity.app, 'assistant');
    expect(activity.state, 'working');
    expect(activity.detail, 'Reading your mail');
    expect(AppActivity.fromJson({}).state, 'idle');
  });
}
