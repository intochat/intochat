import 'dart:convert';

import 'package:digitalbrain_flutter/digitalbrain_flutter.dart';
import 'package:digitalbrain_flutter/src/generated/neurons.pb.dart' as pb;
import 'package:fixnum/fixnum.dart';
import 'package:test/test.dart';

void main() {
  test('an envelope decodes into a NeuronSignal with its JSON payload', () {
    final envelope = pb.SignalEnvelope(
      source: 'owner/apps/calculator/surface',
      type: 'SurfaceChanged',
      publisher: 'owner/apps/calculator/surface',
      sequence: Int64(3),
      payload: utf8.encode('{"name":"x","revision":3}'),
      serverTimeMs: Int64(1700000000000),
    );
    final signal = NeuronSignal.fromEnvelope(envelope);
    expect(signal.type, 'SurfaceChanged');
    expect(signal.sequence, 3);
    expect(signal.payload['revision'], 3);
    expect(signal.serverTimeMs, 1700000000000);
  });

  test('a call request carries the arguments as a JSON array', () {
    final request = NeuronChannel.callRequest('owner', 'A.IB', 'k', 'Read', [
      1,
      'two',
    ]);
    expect(utf8.decode(request.arguments), '[1,"two"]');
    expect(request.brainId, 'owner');
  });
}
