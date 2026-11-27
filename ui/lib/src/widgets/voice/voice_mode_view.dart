import 'dart:async';
import 'dart:math' as math;
import 'dart:typed_data';

import 'package:flutter/material.dart';
import 'package:flutter/services.dart';
import 'package:intochat_os_flutter_shell/intochat_os_flutter.dart'
    show PresenceState;
import 'package:lucide_icons_flutter/lucide_icons.dart';

import '../../theme/ui_colors.dart';
import '../../theme/ui_shadows.dart';
import '../../theme/ui_theme.dart';
import '../../theme/ui_tokens.dart';
import '../ino/ino_orb_v2.dart';
import 'audio_out.dart';
import 'mic_stream.dart';
import 'talk_socket.dart';

/// Ambient docked co-presence voice HUD with an interactive reactive orb.
///
/// Renders inline above the conversation composer so the thread, cards,
/// approvals, and surrounding workspace stay visible and interactive while
/// talking. Users can toggle between the compact bar and an expanded focus
/// card without disconnecting the `/ai/talk` WebSocket.
class VoiceModeView extends StatefulWidget {
  const VoiceModeView({
    super.key,
    required this.connect,
    required this.mic,
    required this.out,
    this.onClose,
    this.initialExpanded = false,
  });

  final Future<TalkSocket> Function() connect;
  final MicStream mic;
  final AudioOut out;

  /// Invoked when the user closes the voice HUD (close button or `Esc`).
  /// Falls back to `Navigator.of(context).maybePop()` when omitted.
  final VoidCallback? onClose;

  /// Whether the HUD starts in its expanded focus layout.
  final bool initialExpanded;

  @override
  State<VoiceModeView> createState() => _VoiceModeViewState();
}

final class _ActiveToolCall {
  const _ActiveToolCall({
    required this.callId,
    required this.name,
    required this.status,
    this.label,
  });

  final String callId;
  final String name;
  final TalkToolStatus status;
  final String? label;

  String get keyId => callId.isNotEmpty ? callId : name;

  String get displayText {
    final trimmed = label?.trim();
    if (trimmed != null && trimmed.isNotEmpty) return trimmed;
    if (name.isNotEmpty) return name;
    return callId;
  }
}

class _VoiceModeViewState extends State<VoiceModeView> {
  static const int _replySampleRate = 24000;
  static const int _maxToolChips = 8;

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
  bool _expanded = false;
  String _heard = '';
  String _reply = '';
  String? _error;
  final List<_ActiveToolCall> _tools = <_ActiveToolCall>[];
  bool _closing = false;
  bool _tornDown = false;

  @override
  void initState() {
    super.initState();
    _expanded = widget.initialExpanded;
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
          _tools.clear();
        });
      case TalkReplyEvent(:final text):
        setState(() => _reply = '$_reply$text');
      case TalkToolEvent():
        setState(() => _upsertTool(event));
      case TalkCancelEvent():
        widget.out.flush();
        setState(() {
          _phase = TalkPhase.listening;
          _tools.removeWhere((t) => t.status == TalkToolStatus.running);
        });
      case TalkErrorEvent(:final message):
        widget.out.flush();
        setState(() => _error = message);
    }
  }

  void _upsertTool(TalkToolEvent event) {
    final existingIndex = event.callId.isNotEmpty
        ? _tools.indexWhere((t) => t.callId == event.callId)
        : _tools.lastIndexWhere(
            (t) => t.name == event.name && t.status == TalkToolStatus.running,
          );
    final next = _ActiveToolCall(
      callId: event.callId,
      name: event.name.isNotEmpty
          ? event.name
          : (existingIndex >= 0 ? _tools[existingIndex].name : ''),
      status: event.status,
      label:
          event.label ??
          (existingIndex >= 0 ? _tools[existingIndex].label : null),
    );
    if (existingIndex >= 0) {
      _tools[existingIndex] = next;
    } else {
      _tools.add(next);
      while (_tools.length > _maxToolChips) {
        final finishedIdx = _tools.indexWhere(
          (t) => t.status != TalkToolStatus.running,
        );
        _tools.removeAt(finishedIdx >= 0 ? finishedIdx : 0);
      }
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

  void _toggleExpanded() {
    if (_closing || !mounted) return;
    setState(() => _expanded = !_expanded);
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
    _tools.clear();

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
    final onClose = widget.onClose;
    if (onClose != null) {
      onClose();
    } else if (mounted) {
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

  String get _statusLabel => _error != null
      ? 'Error'
      : (_pushToTalk && !_pttHeld && _phase == TalkPhase.listening
            ? 'Hold orb to speak'
            : _phaseLabel);

  PresenceState get _presenceState {
    if (_error != null) return PresenceState.disconnected;
    if (_tools.any((t) => t.status == TalkToolStatus.running)) {
      return PresenceState.working;
    }
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

  Widget _buildOrb({required double size}) {
    return Semantics(
      container: true,
      button: true,
      label: _error != null ? 'Voice error' : '$_phaseLabel. Tap to interrupt.',
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
            size: size,
            audioLevel: _activeLevel,
            semanticsId: 'voice-mode:orb',
          ),
        ),
      ),
    );
  }

  Widget _buildModeChips(UiColors c) {
    return Wrap(
      spacing: 6,
      runSpacing: 6,
      children: [
        FilterChip(
          key: const ValueKey('voice-ptt-toggle'),
          visualDensity: VisualDensity.compact,
          materialTapTargetSize: MaterialTapTargetSize.shrinkWrap,
          label: Text(
            _pushToTalk ? 'Push-to-talk' : 'Hands-free VAD',
            style: UiType.textStyle(
              12,
              weight: FontWeight.w500,
              color: _pushToTalk ? c.accent : c.textMuted,
            ),
          ),
          selected: _pushToTalk,
          backgroundColor: c.surfaceSunk,
          selectedColor: c.accentTint,
          side: BorderSide(
            color: _pushToTalk ? c.accentBorder : c.borderHairline,
          ),
          checkmarkColor: c.accent,
          onSelected: (v) => setState(() {
            _pushToTalk = v;
            _pttHeld = false;
          }),
        ),
        FilterChip(
          key: const ValueKey('voice-echo-gate-toggle'),
          visualDensity: VisualDensity.compact,
          materialTapTargetSize: MaterialTapTargetSize.shrinkWrap,
          label: Text(
            'Speaker safe',
            style: UiType.textStyle(
              12,
              weight: FontWeight.w500,
              color: _muteWhileSpeaking ? c.accent : c.textMuted,
            ),
          ),
          selected: _muteWhileSpeaking,
          backgroundColor: c.surfaceSunk,
          selectedColor: c.accentTint,
          side: BorderSide(
            color: _muteWhileSpeaking ? c.accentBorder : c.borderHairline,
          ),
          checkmarkColor: c.accent,
          onSelected: (v) => setState(() => _muteWhileSpeaking = v),
        ),
      ],
    );
  }

  Widget _buildActionButtons(UiColors c) {
    final expandLabel = _expanded ? 'Compact voice HUD' : 'Expand voice HUD';
    return Row(
      mainAxisSize: MainAxisSize.min,
      children: [
        Semantics(
          container: true,
          button: true,
          label: expandLabel,
          child: IconButton(
            key: const ValueKey('voice-expand-toggle'),
            tooltip: expandLabel,
            visualDensity: VisualDensity.compact,
            constraints: const BoxConstraints.tightFor(width: 32, height: 32),
            padding: EdgeInsets.zero,
            onPressed: _toggleExpanded,
            icon: Icon(
              _expanded ? LucideIcons.minimize2 : LucideIcons.maximize2,
              size: 16,
              color: c.textMuted,
            ),
          ),
        ),
        const SizedBox(width: 2),
        Semantics(
          container: true,
          button: true,
          label: 'Close voice mode',
          child: IconButton(
            key: const ValueKey('voice-close'),
            tooltip: 'Close',
            visualDensity: VisualDensity.compact,
            constraints: const BoxConstraints.tightFor(width: 32, height: 32),
            padding: EdgeInsets.zero,
            onPressed: _close,
            icon: Icon(LucideIcons.x, size: 16, color: c.textStrong),
          ),
        ),
      ],
    );
  }

  Widget _buildToolChips(
    UiColors c,
    UiTokens tokens, {
    WrapAlignment alignment = WrapAlignment.start,
  }) {
    return Wrap(
      alignment: alignment,
      spacing: 6,
      runSpacing: 6,
      children: [
        for (final tool in _tools)
          _VoiceToolChip(tool: tool, colors: c, tokens: tokens),
      ],
    );
  }

  Widget _buildCompactBody(UiColors c, UiTokens tokens) {
    return Padding(
      key: const ValueKey('voice-hud-compact'),
      padding: const EdgeInsets.symmetric(horizontal: 12, vertical: 10),
      child: Column(
        mainAxisSize: MainAxisSize.min,
        crossAxisAlignment: CrossAxisAlignment.stretch,
        children: [
          Row(
            crossAxisAlignment: CrossAxisAlignment.center,
            children: [
              _buildOrb(size: 42),
              const SizedBox(width: 12),
              Expanded(
                child: Column(
                  mainAxisSize: MainAxisSize.min,
                  crossAxisAlignment: CrossAxisAlignment.start,
                  children: [
                    Text(
                      _statusLabel,
                      maxLines: 1,
                      overflow: TextOverflow.ellipsis,
                      style: UiType.textStyle(
                        13,
                        weight: FontWeight.w600,
                        color: _error != null ? c.error : c.textStrong,
                      ),
                    ),
                    const SizedBox(height: 2),
                    if (_error case final error?)
                      Text(
                        error,
                        maxLines: 2,
                        overflow: TextOverflow.ellipsis,
                        style: UiType.textStyle(
                          12,
                          color: c.error,
                          height: 1.35,
                        ),
                      )
                    else ...[
                      if (_heard.isNotEmpty)
                        Text(
                          _heard,
                          maxLines: 1,
                          overflow: TextOverflow.ellipsis,
                          style: UiType.textStyle(
                            12,
                            color: c.textMuted,
                            height: 1.35,
                          ),
                        ),
                      if (_reply.isNotEmpty)
                        Text(
                          _reply,
                          maxLines: 2,
                          overflow: TextOverflow.ellipsis,
                          style: UiType.textStyle(
                            13,
                            weight: FontWeight.w500,
                            color: c.textStrong,
                            height: 1.4,
                          ),
                        ),
                      if (_heard.isEmpty && _reply.isEmpty)
                        Text(
                          'Tap the orb to interrupt',
                          maxLines: 1,
                          overflow: TextOverflow.ellipsis,
                          style: UiType.textStyle(12, color: c.textSubtle),
                        ),
                    ],
                  ],
                ),
              ),
              const SizedBox(width: 6),
              _buildActionButtons(c),
            ],
          ),
          if (_tools.isNotEmpty) ...[
            const SizedBox(height: 8),
            _buildToolChips(c, tokens),
          ],
          const SizedBox(height: 8),
          _buildModeChips(c),
        ],
      ),
    );
  }

  Widget _buildExpandedBody(UiColors c, UiTokens tokens) {
    return ConstrainedBox(
      key: const ValueKey('voice-hud-expanded'),
      constraints: const BoxConstraints(maxHeight: 360),
      child: SingleChildScrollView(
        padding: const EdgeInsets.fromLTRB(16, 12, 16, 18),
        child: Column(
          mainAxisSize: MainAxisSize.min,
          crossAxisAlignment: CrossAxisAlignment.stretch,
          children: [
            Row(
              crossAxisAlignment: CrossAxisAlignment.start,
              children: [
                Expanded(child: _buildModeChips(c)),
                const SizedBox(width: 8),
                _buildActionButtons(c),
              ],
            ),
            const SizedBox(height: 14),
            Center(
              child: ConstrainedBox(
                constraints: const BoxConstraints(maxWidth: 480),
                child: Column(
                  mainAxisSize: MainAxisSize.min,
                  children: [
                    _buildOrb(size: 132),
                    const SizedBox(height: 12),
                    Text(
                      _statusLabel,
                      textAlign: TextAlign.center,
                      style: UiType.textStyle(
                        15,
                        weight: FontWeight.w600,
                        color: _error != null ? c.error : c.textStrong,
                      ),
                    ),
                    if (_tools.isNotEmpty) ...[
                      const SizedBox(height: 12),
                      _buildToolChips(
                        c,
                        tokens,
                        alignment: WrapAlignment.center,
                      ),
                    ],
                    const SizedBox(height: 14),
                    if (_error case final error?)
                      Container(
                        padding: const EdgeInsets.symmetric(
                          horizontal: 14,
                          vertical: 10,
                        ),
                        decoration: BoxDecoration(
                          color: c.errorBg,
                          borderRadius: BorderRadius.circular(tokens.card),
                          border: Border.all(
                            color: c.error.withValues(alpha: 0.3),
                          ),
                        ),
                        child: Text(
                          error,
                          textAlign: TextAlign.center,
                          style: UiType.textStyle(
                            13,
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
                            13,
                            color: c.textMuted,
                            height: 1.45,
                          ),
                        ),
                      if (_heard.isNotEmpty && _reply.isNotEmpty)
                        const SizedBox(height: 8),
                      if (_reply.isNotEmpty)
                        Text(
                          _reply,
                          textAlign: TextAlign.center,
                          style: UiType.textStyle(
                            15,
                            weight: FontWeight.w500,
                            color: c.textStrong,
                            height: 1.5,
                          ),
                        ),
                      if (_heard.isEmpty && _reply.isEmpty)
                        Text(
                          'Tap the orb to interrupt',
                          textAlign: TextAlign.center,
                          style: UiType.textStyle(13, color: c.textSubtle),
                        ),
                    ],
                  ],
                ),
              ),
            ),
          ],
        ),
      ),
    );
  }

  @override
  Widget build(BuildContext context) {
    final c = UiColors.of(context);
    final tokens = UiTokens.of(context);

    return CallbackShortcuts(
      bindings: {const SingleActivator(LogicalKeyboardKey.escape): _close},
      child: Focus(
        autofocus: true,
        child: Container(
          key: const ValueKey('voice-hud'),
          decoration: BoxDecoration(
            color: c.surfaceCard,
            borderRadius: BorderRadius.circular(tokens.panel),
            border: Border.all(
              color: _error != null
                  ? c.error.withValues(alpha: 0.45)
                  : c.borderInput,
            ),
            boxShadow: UiShadows.sm(c),
          ),
          child: Material(
            type: MaterialType.transparency,
            borderRadius: BorderRadius.circular(tokens.panel),
            clipBehavior: Clip.antiAlias,
            child: AnimatedSize(
              duration: tokens.base,
              curve: tokens.ease,
              alignment: Alignment.topCenter,
              child: _expanded
                  ? _buildExpandedBody(c, tokens)
                  : _buildCompactBody(c, tokens),
            ),
          ),
        ),
      ),
    );
  }
}

final class _VoiceToolChip extends StatelessWidget {
  const _VoiceToolChip({
    required this.tool,
    required this.colors,
    required this.tokens,
  });

  final _ActiveToolCall tool;
  final UiColors colors;
  final UiTokens tokens;

  @override
  Widget build(BuildContext context) {
    final (Color fg, Color bg, Color border) = switch (tool.status) {
      TalkToolStatus.running => (
        colors.working,
        colors.workingBg,
        colors.working.withValues(alpha: 0.28),
      ),
      TalkToolStatus.done => (
        colors.done,
        colors.doneBg,
        colors.done.withValues(alpha: 0.28),
      ),
      TalkToolStatus.failed => (
        colors.error,
        colors.errorBg,
        colors.error.withValues(alpha: 0.3),
      ),
    };

    final Widget indicator = switch (tool.status) {
      TalkToolStatus.running => SizedBox(
        width: 11,
        height: 11,
        child: CircularProgressIndicator(
          strokeWidth: 1.75,
          valueColor: AlwaysStoppedAnimation<Color>(fg),
        ),
      ),
      TalkToolStatus.done => Icon(LucideIcons.check, size: 12, color: fg),
      TalkToolStatus.failed => Icon(
        LucideIcons.circleAlert,
        size: 12,
        color: fg,
      ),
    };

    return Semantics(
      container: true,
      label: '${tool.displayText}, ${tool.status.name}',
      child: Container(
        key: ValueKey('voice-tool-chip:${tool.keyId}'),
        padding: const EdgeInsets.symmetric(horizontal: 10, vertical: 4),
        decoration: BoxDecoration(
          color: bg,
          borderRadius: BorderRadius.circular(tokens.pill),
          border: Border.all(color: border),
        ),
        child: Row(
          mainAxisSize: MainAxisSize.min,
          children: [
            indicator,
            const SizedBox(width: 6),
            Flexible(
              child: Text(
                tool.displayText,
                maxLines: 1,
                overflow: TextOverflow.ellipsis,
                style: UiType.textStyle(12, weight: FontWeight.w500, color: fg),
              ),
            ),
          ],
        ),
      ),
    );
  }
}
