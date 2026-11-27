import 'dart:async';
import 'dart:typed_data';

import 'package:flutter_soloud/flutter_soloud.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:intochat_os_flutter_shell/src/widgets/voice/audio_out.dart';

final class _FakeSoLoudBackend implements SoLoudBackend {
  @override
  bool isInitialized = false;

  final List<String> calls = <String>[];
  final List<({Object handle, double to, Duration time})> fades = [];
  final List<({Object handle, Duration time})> scheduledStops = [];
  final List<Object> resetSources = [];
  final List<Object> stoppedHandles = [];
  final List<Object> disposedSources = [];

  int _sourceCounter = 0;
  int _handleCounter = 0;
  bool throwOnReset = false;

  @override
  Future<void> init() async {
    calls.add('init');
    isInitialized = true;
  }

  @override
  Object setBufferStream({
    required BufferingType bufferingType,
    required double bufferingTimeNeeds,
    required int sampleRate,
    required Channels channels,
    required BufferType format,
  }) {
    final source = 'stream-source-${++_sourceCounter}';
    calls.add('setBufferStream($sampleRate)->$source');
    return source;
  }

  @override
  void addAudioDataStream(Object source, Uint8List pcm16) {
    calls.add('addAudioDataStream($source,${pcm16.length})');
  }

  @override
  void resetBufferStream(Object source) {
    calls.add('resetBufferStream($source)');
    if (throwOnReset) {
      throw StateError('Simulated reset failure');
    }
    resetSources.add(source);
  }

  @override
  Object play(Object source) {
    final handle = 'handle-${++_handleCounter}';
    calls.add('play($source)->$handle');
    return handle;
  }

  @override
  void fadeVolume(Object handle, double to, Duration time) {
    calls.add('fadeVolume($handle,$to,${time.inMilliseconds}ms)');
    fades.add((handle: handle, to: to, time: time));
  }

  @override
  void scheduleStop(Object handle, Duration time) {
    calls.add('scheduleStop($handle,${time.inMilliseconds}ms)');
    scheduledStops.add((handle: handle, time: time));
  }

  @override
  Future<void> stop(Object handle) async {
    calls.add('stop($handle)');
    stoppedHandles.add(handle);
  }

  @override
  Future<void> disposeSource(Object source) async {
    calls.add('disposeSource($source)');
    disposedSources.add(source);
  }

  @override
  Future<({Object source, Stream<void> allInstancesFinished})> loadMem(
    String path,
    Uint8List bytes,
  ) async {
    final source = 'clip-source-${++_sourceCounter}';
    calls.add('loadMem($path,${bytes.length})->$source');
    return (source: source, allInstancesFinished: const Stream<void>.empty());
  }
}

void main() {
  group('SoLoudAudioOut (flutter_soloud 5.4.0)', () {
    test('flush performs click-free fadeVolume(25ms) + scheduleStop(30ms) and resets buffer stream in place without recreating source', () async {
      final backend = _FakeSoLoudBackend();
      final out = SoLoudAudioOut(backend: backend);

      await out.start(24000);
      out.push(Uint8List.fromList([1, 2, 3, 4]));

      backend.calls.clear();
      out.flush();

      // Click-free fade & scheduled stop on old handle — never hard-cut stop().
      expect(backend.fades, [
        (handle: 'handle-1', to: 0.0, time: const Duration(milliseconds: 25)),
      ]);
      expect(backend.scheduledStops, [
        (handle: 'handle-1', time: const Duration(milliseconds: 30)),
      ]);
      expect(backend.stoppedHandles, isEmpty);

      // Buffer stream source is reset in place and reused without dispose or setBufferStream.
      expect(backend.resetSources, ['stream-source-1']);
      expect(backend.disposedSources, isEmpty);
      expect(backend.calls, [
        'fadeVolume(handle-1,0.0,25ms)',
        'scheduleStop(handle-1,30ms)',
        'resetBufferStream(stream-source-1)',
        'play(stream-source-1)->handle-2',
      ]);

      // Subsequent pushes continue targeting the same reset source.
      out.push(Uint8List.fromList([5, 6]));
      expect(backend.calls.last, 'addAudioDataStream(stream-source-1,2)');
    });

    test(
      'flush falls back to reopening buffer stream if resetBufferStream throws',
      () async {
        final backend = _FakeSoLoudBackend();
        final out = SoLoudAudioOut(backend: backend);

        await out.start(16000);
        backend.calls.clear();
        backend.throwOnReset = true;

        out.flush();

        expect(backend.fades.single.handle, 'handle-1');
        expect(backend.scheduledStops.single.handle, 'handle-1');
        expect(backend.disposedSources, ['stream-source-1']);
        expect(
          backend.calls,
          containsAllInOrder([
            'fadeVolume(handle-1,0.0,25ms)',
            'scheduleStop(handle-1,30ms)',
            'resetBufferStream(stream-source-1)',
            'disposeSource(stream-source-1)',
            'setBufferStream(16000)->stream-source-2',
            'play(stream-source-2)->handle-2',
          ]),
        );
      },
    );

    test('stop tears down active buffer stream cleanly', () async {
      final backend = _FakeSoLoudBackend();
      final out = SoLoudAudioOut(backend: backend);

      await out.start(24000);
      await out.stop();

      expect(backend.stoppedHandles, ['handle-1']);
      expect(backend.disposedSources, ['stream-source-1']);
    });
  });
}
