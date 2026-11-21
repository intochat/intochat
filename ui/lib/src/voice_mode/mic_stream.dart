import 'dart:typed_data';

import 'package:record/record.dart';

/// Live microphone PCM16 stream source for full-duplex voice mode.
abstract interface class MicStream {
  Future<Stream<Uint8List>> start();
  Future<void> stop();
}

/// Captures 16 kHz mono PCM16 LE frames using `package:record` with hardware
/// echo cancellation and noise suppression enabled.
final class RecordMicStream implements MicStream {
  RecordMicStream({AudioRecorder Function()? createRecorder})
    : _createRecorder = createRecorder ?? AudioRecorder.new;

  final AudioRecorder Function() _createRecorder;
  AudioRecorder? _recorder;

  @override
  Future<Stream<Uint8List>> start() async {
    final recorder = _recorder ??= _createRecorder();
    if (!await recorder.hasPermission()) {
      throw StateError('Microphone permission was denied.');
    }
    return recorder.startStream(
      const RecordConfig(
        encoder: AudioEncoder.pcm16bits,
        sampleRate: 16000,
        numChannels: 1,
        echoCancel: true,
        noiseSuppress: true,
      ),
    );
  }

  @override
  Future<void> stop() async {
    final recorder = _recorder;
    _recorder = null;
    if (recorder == null) return;
    try {
      await recorder.stop();
    } catch (_) {}
    try {
      await recorder.dispose();
    } catch (_) {}
  }
}
