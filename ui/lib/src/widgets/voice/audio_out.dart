import 'dart:async';

import 'package:flutter/foundation.dart';
import 'package:flutter_soloud/flutter_soloud.dart';

/// Audio output sink supporting low-latency streaming PCM16 playback (voice
/// mode) and one-shot clip playback (read-aloud).
abstract interface class AudioOut {
  Future<void> start(int sampleRate);
  void push(Uint8List pcm16);
  void flush();
  Future<void> playClip(Uint8List wav);
  Future<void> stop();
}

/// Injectable facade over [SoLoud] so [SoLoudAudioOut] can be unit-tested
/// without loading the native FFI audio library.
@visibleForTesting
abstract interface class SoLoudBackend {
  bool get isInitialized;
  Future<void> init();
  Object setBufferStream({
    required BufferingType bufferingType,
    required double bufferingTimeNeeds,
    required int sampleRate,
    required Channels channels,
    required BufferType format,
  });
  void addAudioDataStream(Object source, Uint8List pcm16);
  void resetBufferStream(Object source);
  Object play(Object source);
  void fadeVolume(Object handle, double to, Duration time);
  void scheduleStop(Object handle, Duration time);
  Future<void> stop(Object handle);
  Future<void> disposeSource(Object source);
  Future<({Object source, Stream<void> allInstancesFinished})> loadMem(
    String path,
    Uint8List bytes,
  );
}

final class _LiveSoLoudBackend implements SoLoudBackend {
  const _LiveSoLoudBackend();

  @override
  bool get isInitialized => SoLoud.instance.isInitialized;

  @override
  Future<void> init() => SoLoud.instance.init();

  @override
  Object setBufferStream({
    required BufferingType bufferingType,
    required double bufferingTimeNeeds,
    required int sampleRate,
    required Channels channels,
    required BufferType format,
  }) => SoLoud.instance.setBufferStream(
    bufferingType: bufferingType,
    bufferingTimeNeeds: bufferingTimeNeeds,
    sampleRate: sampleRate,
    channels: channels,
    format: format,
  );

  @override
  void addAudioDataStream(Object source, Uint8List pcm16) {
    SoLoud.instance.addAudioDataStream(source as AudioSource, pcm16);
  }

  @override
  void resetBufferStream(Object source) {
    SoLoud.instance.resetBufferStream(source as AudioSource);
  }

  @override
  Object play(Object source) =>
      SoLoud.instance.play(source as AudioSource) as Object;

  @override
  void fadeVolume(Object handle, double to, Duration time) {
    SoLoud.instance.fadeVolume(handle as SoundHandle, to, time);
  }

  @override
  void scheduleStop(Object handle, Duration time) {
    SoLoud.instance.scheduleStop(handle as SoundHandle, time);
  }

  @override
  Future<void> stop(Object handle) =>
      SoLoud.instance.stop(handle as SoundHandle);

  @override
  Future<void> disposeSource(Object source) =>
      SoLoud.instance.disposeSource(source as AudioSource);

  @override
  Future<({Object source, Stream<void> allInstancesFinished})> loadMem(
    String path,
    Uint8List bytes,
  ) async {
    final source = await SoLoud.instance.loadMem(path, bytes);
    return (
      source: source as Object,
      allInstancesFinished: source.allInstancesFinished,
    );
  }
}

/// SoLoud-backed [AudioOut] using a mono s16le buffer stream for voice mode
/// and `loadMem` for read-aloud WAV clips.
final class SoLoudAudioOut implements AudioOut {
  SoLoudAudioOut({@visibleForTesting SoLoudBackend? backend})
    : _backend = backend ?? const _LiveSoLoudBackend();

  /// Short linear fade-out applied to the interrupted voice handle on barge-in
  /// to prevent an audible click/pop.
  static const bargeInFadeDuration = Duration(milliseconds: 25);

  /// Stop scheduled immediately after [bargeInFadeDuration] completes.
  static const bargeInScheduleStopDelay = Duration(milliseconds: 30);

  final SoLoudBackend _backend;

  Object? _streamSource;
  Object? _streamHandle;
  int? _sampleRate;

  Object? _clipSource;
  Object? _clipHandle;
  Completer<void>? _clipDone;
  int _clipCounter = 0;

  Future<void> _ensureInitialized() async {
    if (!_backend.isInitialized) {
      await _backend.init();
    }
  }

  void _openBufferStream(int sampleRate) {
    final source = _backend.setBufferStream(
      bufferingType: BufferingType.released,
      bufferingTimeNeeds: 0.08,
      sampleRate: sampleRate,
      channels: Channels.mono,
      format: BufferType.s16le,
    );
    final handle = _backend.play(source);
    _streamSource = source;
    _streamHandle = handle;
  }

  Future<void> _closeBufferStream() async {
    final handle = _streamHandle;
    final source = _streamSource;
    _streamHandle = null;
    _streamSource = null;
    if (!_backend.isInitialized) return;
    if (handle != null) {
      await _backend.stop(handle).catchError((Object _) {});
    }
    if (source != null) {
      await _backend.disposeSource(source).catchError((Object _) {});
    }
  }

  Future<void> _stopClip() async {
    final handle = _clipHandle;
    final done = _clipDone;
    _clipHandle = null;
    _clipSource = null;
    _clipDone = null;
    if (handle != null && _backend.isInitialized) {
      await _backend.stop(handle).catchError((Object _) {});
    }
    if (done != null && !done.isCompleted) {
      done.complete();
    }
  }

  @override
  Future<void> start(int sampleRate) async {
    _sampleRate = sampleRate;
    await _ensureInitialized();
    await _closeBufferStream();
    _openBufferStream(sampleRate);
  }

  @override
  void push(Uint8List pcm16) {
    if (pcm16.isEmpty) return;
    final source = _streamSource;
    if (source == null || !_backend.isInitialized) return;
    _backend.addAudioDataStream(source, pcm16);
  }

  @override
  void flush() {
    if (!_backend.isInitialized) return;
    final oldHandle = _streamHandle;
    _streamHandle = null;
    if (oldHandle != null) {
      try {
        _backend.fadeVolume(oldHandle, 0.0, bargeInFadeDuration);
        _backend.scheduleStop(oldHandle, bargeInScheduleStopDelay);
      } catch (_) {
        // Handle may have already finished playing.
      }
    }

    if (_streamSource case final source?) {
      try {
        _backend.resetBufferStream(source);
        _streamHandle = _backend.play(source);
        return;
      } catch (_) {
        _streamSource = null;
        unawaited(_backend.disposeSource(source).catchError((Object _) {}));
      }
    }

    if (_sampleRate case final rate?) {
      _openBufferStream(rate);
    }
  }

  @override
  Future<void> playClip(Uint8List wav) async {
    await _stopClip();
    if (wav.isEmpty) return;
    await _ensureInitialized();
    final loaded = await _backend.loadMem(
      'read-aloud-${++_clipCounter}.wav',
      wav,
    );
    final source = loaded.source;
    final done = Completer<void>();
    _clipSource = source;
    _clipDone = done;
    final sub = loaded.allInstancesFinished.listen((_) {
      if (!done.isCompleted) done.complete();
    });
    try {
      final handle = _backend.play(source);
      _clipHandle = handle;
      await done.future;
    } finally {
      await sub.cancel();
      if (identical(_clipSource, source)) {
        _clipSource = null;
        _clipHandle = null;
        _clipDone = null;
      }
      if (_backend.isInitialized) {
        await _backend.disposeSource(source).catchError((Object _) {});
      }
    }
  }

  @override
  Future<void> stop() async {
    _sampleRate = null;
    await _closeBufferStream();
    await _stopClip();
  }
}
