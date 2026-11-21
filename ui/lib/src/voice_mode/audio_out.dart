import 'dart:async';
import 'dart:typed_data';

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

/// SoLoud-backed [AudioOut] using a mono s16le buffer stream for voice mode
/// and `loadMem` for read-aloud WAV clips.
final class SoLoudAudioOut implements AudioOut {
  AudioSource? _streamSource;
  SoundHandle? _streamHandle;
  int? _sampleRate;

  AudioSource? _clipSource;
  SoundHandle? _clipHandle;
  Completer<void>? _clipDone;
  int _clipCounter = 0;

  Future<void> _ensureInitialized() async {
    final soloud = SoLoud.instance;
    if (!soloud.isInitialized) {
      await soloud.init();
    }
  }

  void _openBufferStream(int sampleRate) {
    final soloud = SoLoud.instance;
    final source = soloud.setBufferStream(
      bufferingType: BufferingType.released,
      bufferingTimeNeeds: 0.08,
      sampleRate: sampleRate,
      channels: Channels.mono,
      format: BufferType.s16le,
    );
    final handle = soloud.play(source);
    _streamSource = source;
    _streamHandle = handle;
  }

  Future<void> _closeBufferStream() async {
    final handle = _streamHandle;
    final source = _streamSource;
    _streamHandle = null;
    _streamSource = null;
    if (!SoLoud.instance.isInitialized) return;
    if (handle != null) {
      await SoLoud.instance.stop(handle).catchError((Object _) {});
    }
    if (source != null) {
      await SoLoud.instance.disposeSource(source).catchError((Object _) {});
    }
  }

  Future<void> _stopClip() async {
    final handle = _clipHandle;
    final done = _clipDone;
    _clipHandle = null;
    _clipSource = null;
    _clipDone = null;
    if (handle != null && SoLoud.instance.isInitialized) {
      await SoLoud.instance.stop(handle).catchError((Object _) {});
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
    if (source == null || !SoLoud.instance.isInitialized) return;
    SoLoud.instance.addAudioDataStream(source, pcm16);
  }

  @override
  void flush() {
    if (!SoLoud.instance.isInitialized) return;
    final oldHandle = _streamHandle;
    final oldSource = _streamSource;
    _streamHandle = null;
    _streamSource = null;
    if (oldHandle != null) {
      unawaited(SoLoud.instance.stop(oldHandle).catchError((Object _) {}));
    }
    if (oldSource != null) {
      unawaited(
        SoLoud.instance.disposeSource(oldSource).catchError((Object _) {}),
      );
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
    final soloud = SoLoud.instance;
    final source = await soloud.loadMem(
      'read-aloud-${++_clipCounter}.wav',
      wav,
    );
    final done = Completer<void>();
    _clipSource = source;
    _clipDone = done;
    final sub = source.allInstancesFinished.listen((_) {
      if (!done.isCompleted) done.complete();
    });
    try {
      final handle = soloud.play(source);
      _clipHandle = handle;
      await done.future;
    } finally {
      await sub.cancel();
      if (identical(_clipSource, source)) {
        _clipSource = null;
        _clipHandle = null;
        _clipDone = null;
      }
      if (soloud.isInitialized) {
        await soloud.disposeSource(source).catchError((Object _) {});
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
