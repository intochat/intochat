import 'dart:async';
import 'dart:math' as math;
import 'dart:typed_data';

import 'package:flutter/material.dart';
import 'package:flutter/services.dart';
import 'package:lucide_icons_flutter/lucide_icons.dart';

import 'package:intochat_os_flutter_shell/intochat_os_flutter.dart'
    show PresenceState;

import '../../theme/ui_colors.dart';
import '../../theme/ui_theme.dart';
import '../../theme/ui_tokens.dart';
import '../ino/ino_orb_v2.dart';
import 'audio_out.dart';
import 'mic_stream.dart';
import 'talk_socket.dart';

/// Full-screen full-duplex voice-mode surface with an interactive orb.
///
/// Mic PCM16 frames stream continuously to the Kernel so server-side VAD can
/// detect barge-in; tapping the orb also sends an immediate `interrupt` and
/// flushes queued playback.
class VoiceModeView extends StatefulWidget {
  const VoiceModeView({
    super.key,
    required this.connect,
    required this.mic,
    required this.out,
  });

  final Future<TalkSocket> Function() connect;
  final MicStream mic;
  final AudioOut out;

  @override
  State<VoiceModeView> createState() => _VoiceModeViewState();
}

class _VoiceModeViewState extends State<VoiceModeView> {
  static const int _replySampleRate = 24000;

  TalkSocket? _socket;
  StreamSubscription<TalkEvent>? _eventsSub;
  StreamSubscription<Uint8List>? _audioSub;
  StreamSubscription<Uint8List>? _micSub;

  TalkPhase _phase = TalkPhase.listening;
  double _micLevel = 0.0;
  double _ttsLevel = 0.0;
  bool _pushToTalk = false;
  bool _pttHeld = false;
  bool _muteWhileSpeaking = false;
  String _heard = '';
  String _reply = '';
  String? _error;
  bool _closing = false;
  bool _tornDown = false;

  @override
  void initState() {
    super.initState();
    unawaited(_start());
  }

  Future<void> _start() async {
    try {
      await widget.out.start(_replySampleRate);
      if (_closing || !mounted) {
        await widget.out.stop();
        return;
      }

      final socket = await widget.connect();
      if (_closing || !mounted) {
        await socket.close();
        await widget.out.stop();
        return;
      }

      _socket = socket;
      _eventsSub = socket.events.listen(_onEvent, onError: _onError);
      _audioSub = socket.audio.listen(_onAudio, onError: _onError);

      final micStream = await widget.mic.start();
      if (_closing || !mounted) {
        await widget.mic.stop();
        await _teardown();
        return;
      }
      _micSub = micStream.listen(_onMicFrame, onError: _onError);
    } catch (error) {
      if (mounted && !_closing) {
        setState(() => _error = '$error');
      }
    }
  }

  void _onMicFrame(Uint8List pcm16) {
    if (_closing) return;
    final gatedByPtt = _pushToTalk && !_pttHeld;
    final gatedByEcho = _muteWhileSpeaking && _phase == TalkPhase.speaking;
    if (!gatedByPtt && !gatedByEcho) {
      _socket?.send(pcm16);
    }
    final rms = (gatedByPtt || gatedByEcho) ? 0.0 : _pcm16Rms(pcm16);
    final smoothed = (_micLevel * 0.55 + rms * 0.45).clamp(0.0, 1.0);
    if ((smoothed - _micLevel).abs() > 0.01 && mounted) {
      setState(() => _micLevel = smoothed);
    }
  }

  void _onAudio(Uint8List pcm16) {
    if (_closing) return;
    widget.out.push(pcm16);
    final rms = _pcm16Rms(pcm16);
    final smoothed = (_ttsLevel * 0.55 + rms * 0.45).clamp(0.0, 1.0);
    if ((smoothed - _ttsLevel).abs() > 0.01 && mounted) {
      setState(() => _ttsLevel = smoothed);
    }
  }

  void _onEvent(TalkEvent event) {
    if (_closing || !mounted) return;
    switch (event) {
      case TalkStateEvent(:final state):
        setState(() => _phase = state);
      case TalkHeardEvent(:final text):
        setState(() {
          _heard = text;
          _reply = '';
        });
      case TalkReplyEvent(:final text):
        setState(() => _reply = '$_reply$text');
      case TalkCancelEvent():
        widget.out.flush();
        setState(() => _phase = TalkPhase.listening);
      case TalkErrorEvent(:final message):
        widget.out.flush();
        setState(() => _error = message);
    }
  }

  void _onError(Object error) {
    if (_closing || !mounted) return;
    widget.out.flush();
    setState(() => _error = '$error');
  }

  void _interrupt() {
    if (_closing) return;
    _socket?.interrupt();
    widget.out.flush();
    if (mounted) {
      setState(() => _phase = TalkPhase.listening);
    }
  }

  Future<void> _teardown() async {
    if (_tornDown) return;
    _tornDown = true;
    final micSub = _micSub;
    final eventsSub = _eventsSub;
    final audioSub = _audioSub;
    final socket = _socket;
    _micSub = null;
    _eventsSub = null;
    _audioSub = null;
    _socket = null;

    // Kick off cancellations and stops immediately in the current turn so
    // `end`, mic stop, and audio stop run before any async suspension.
    final futures = <Future<void>>[
      if (micSub != null) micSub.cancel().catchError((Object _) {}),
      if (eventsSub != null) eventsSub.cancel().catchError((Object _) {}),
      if (audioSub != null) audioSub.cancel().catchError((Object _) {}),
      Future<void>.sync(widget.mic.stop).catchError((Object _) {}),
      if (socket != null) socket.close().catchError((Object _) {}),
      Future<void>.sync(widget.out.stop).catchError((Object _) {}),
    ];
    await Future.wait(futures);
  }

  void _close() {
    if (_closing) return;
    _closing = true;
    unawaited(_teardown());
    if (mounted) {
      unawaited(Navigator.of(context).maybePop());
    }
  }

  @override
  void dispose() {
    _closing = true;
    unawaited(_teardown());
    super.dispose();
  }

  static double _pcm16Rms(Uint8List bytes) {
    final samples = bytes.length ~/ 2;
    if (samples == 0) return 0.0;
    final view = ByteData.sublistView(bytes);
    var sumSquares = 0.0;
    for (var i = 0; i < samples; i++) {
      final sample = view.getInt16(i * 2, Endian.little) / 32768.0;
      sumSquares += sample * sample;
    }
    return math.sqrt(sumSquares / samples).clamp(0.0, 1.0);
  }

  String get _phaseLabel => switch (_phase) {
    TalkPhase.listening => 'Listening',
    TalkPhase.thinking => 'Thinking',
    TalkPhase.speaking => 'Speaking',
  };

  PresenceState get _presenceState {
    if (_error != null) return PresenceState.disconnected;
    return switch (_phase) {
      TalkPhase.listening => PresenceState.listening,
      TalkPhase.thinking => PresenceState.working,
      TalkPhase.speaking => PresenceState.idle,
    };
  }

  double get _activeLevel {
    if (_error != null) return 0.0;
    return math.max(_micLevel, _ttsLevel);
  }

  @override
  Widget build(BuildContext context) {
    final c = UiColors.of(context);

    return CallbackShortcuts(
      bindings: {const SingleActivator(LogicalKeyboardKey.escape): _close},
      child: Focus(
        autofocus: true,
        child: Scaffold(
          backgroundColor: c.surfacePage,
          body: SafeArea(
            child: Padding(
              padding: const EdgeInsets.symmetric(horizontal: 24, vertical: 20),
              child: Column(
                children: [
                  Row(
                    mainAxisAlignment: MainAxisAlignment.spaceBetween,
                    children: [
                      Wrap(
                        spacing: 8,
                        children: [
                          FilterChip(
                            key: const ValueKey('voice-ptt-toggle'),
                            label: Text(
                              _pushToTalk ? 'Push-to-talk' : 'Hands-free VAD',
                            ),
                            selected: _pushToTalk,
                            onSelected: (v) => setState(() {
                              _pushToTalk = v;
                              _pttHeld = false;
                            }),
                          ),
                          FilterChip(
                            key: const ValueKey('voice-echo-gate-toggle'),
                            label: const Text('Speaker safe'),
                            selected: _muteWhileSpeaking,
                            onSelected: (v) =>
                                setState(() => _muteWhileSpeaking = v),
                          ),
                        ],
                      ),
                      Semantics(
                        container: true,
                        button: true,
                        label: 'Close voice mode',
                        child: IconButton(
                          key: const ValueKey('voice-close'),
                          tooltip: 'Close',
                          onPressed: _close,
                          icon: Icon(LucideIcons.x, color: c.textStrong),
                        ),
                      ),
                    ],
                  ),
                  Expanded(
                    child: Center(
                      child: ConstrainedBox(
                        constraints: const BoxConstraints(maxWidth: 520),
                        child: Column(
                          mainAxisAlignment: MainAxisAlignment.center,
                          mainAxisSize: MainAxisSize.min,
                          children: [
                            Semantics(
                              container: true,
                              button: true,
                              label: _error != null
                                  ? 'Voice error'
                                  : '$_phaseLabel. Tap to interrupt.',
                              child: GestureDetector(
                                key: const ValueKey('voice-orb'),
                                onTap: _interrupt,
                                onLongPressStart: _pushToTalk
                                    ? (_) => setState(() => _pttHeld = true)
                                    : null,
                                onLongPressEnd: _pushToTalk
                                    ? (_) => setState(() => _pttHeld = false)
                                    : null,
                                child: MouseRegion(
                                  cursor: SystemMouseCursors.click,
                                  child: InoOrbV2(
                                    state: _presenceState,
                                    size: 176,
                                    audioLevel: _activeLevel,
                                    semanticsId: 'voice-mode:orb',
                                  ),
                                ),
                              ),
                            ),
                            const SizedBox(height: 12),
                            Text(
                              _error != null
                                  ? 'Error'
                                  : (_pushToTalk &&
                                            !_pttHeld &&
                                            _phase == TalkPhase.listening
                                        ? 'Hold orb to speak'
                                        : _phaseLabel),
                              style: UiType.textStyle(
                                15,
                                weight: FontWeight.w600,
                                color: _error != null ? c.error : c.textStrong,
                              ),
                            ),
                            const SizedBox(height: 20),
                            if (_error case final error?)
                              Container(
                                padding: const EdgeInsets.symmetric(
                                  horizontal: 16,
                                  vertical: 12,
                                ),
                                decoration: BoxDecoration(
                                  color: c.errorBg,
                                  borderRadius: BorderRadius.circular(
                                    UiTokens.of(context).panel,
                                  ),
                                  border: Border.all(
                                    color: c.error.withValues(alpha: 0.3),
                                  ),
                                ),
                                child: Text(
                                  error,
                                  textAlign: TextAlign.center,
                                  style: UiType.textStyle(
                                    14,
                                    color: c.error,
                                    height: 1.45,
                                  ),
                                ),
                              )
                            else ...[
                              if (_heard.isNotEmpty)
                                Text(
                                  _heard,
                                  textAlign: TextAlign.center,
                                  style: UiType.textStyle(
                                    14,
                                    color: c.textMuted,
                                    height: 1.5,
                                  ),
                                ),
                              if (_heard.isNotEmpty && _reply.isNotEmpty)
                                const SizedBox(height: 12),
                              if (_reply.isNotEmpty)
                                Text(
                                  _reply,
                                  textAlign: TextAlign.center,
                                  style: UiType.textStyle(
                                    16,
                                    weight: FontWeight.w500,
                                    color: c.textStrong,
                                    height: 1.55,
                                  ),
                                ),
                              if (_heard.isEmpty && _reply.isEmpty)
                                Text(
                                  'Tap the orb to interrupt',
                                  textAlign: TextAlign.center,
                                  style: UiType.textStyle(
                                    13,
                                    color: c.textSubtle,
                                  ),
                                ),
                            ],
                          ],
                        ),
                      ),
                    ),
                  ),
                ],
              ),
            ),
          ),
        ),
      ),
    );
  }
}
