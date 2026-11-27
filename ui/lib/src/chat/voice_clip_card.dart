import 'dart:async';
import 'dart:typed_data';

import 'package:flutter/material.dart';
import 'package:intochat_os_flutter_shell/intochat_os_flutter.dart';
import 'package:lucide_icons_flutter/lucide_icons.dart';

import '../nodes/node_context.dart';
import '../theme/ui_colors.dart';
import '../theme/ui_theme.dart';
import '../theme/ui_tokens.dart';
import '../theme/wire_node_values.dart';
import '../widgets/voice/audio_out.dart';

/// Playable voice-note clip bubble for Ino chat messages.
///
/// Renders play/pause, duration, waveform progress bars, and an inline/expandable
/// transcript using the warm palette tokens and Inter typography.
class VoiceClipCard extends StatefulWidget {
  const VoiceClipCard({
    super.key,
    required this.attachment,
    required this.ctx,
    this.isUserBubble = false,
  });

  final AttachmentNode attachment;
  final NodeContext ctx;
  final bool isUserBubble;

  @override
  State<VoiceClipCard> createState() => _VoiceClipCardState();
}

class _VoiceClipCardState extends State<VoiceClipCard> {
  static const List<double> _waveformHeights = [
    0.35,
    0.72,
    0.50,
    0.92,
    0.64,
    0.40,
    0.85,
    0.58,
    0.78,
    0.44,
    0.96,
    0.62,
    0.48,
    0.80,
    0.54,
    0.68,
    0.38,
    0.74,
    0.52,
    0.42,
  ];

  AudioOut? _audioOut;
  Uint8List? _cachedBytes;
  bool _loading = false;
  bool _playing = false;
  bool _transcriptExpanded = false;
  double _progress = 0.0;
  String? _error;
  Timer? _progressTimer;
  int _epoch = 0;

  @override
  void dispose() {
    _epoch++;
    _progressTimer?.cancel();
    final out = _audioOut;
    _audioOut = null;
    if (out != null) {
      unawaited(out.stop().catchError((Object _) {}));
    }
    super.dispose();
  }

  Future<void> _togglePlay() async {
    if (_playing) {
      await _stop();
      return;
    }

    final fetcher = widget.ctx.fetchAttachment;
    if (fetcher == null && _cachedBytes == null) {
      setState(() => _error = 'Audio playback unavailable.');
      return;
    }

    final epoch = ++_epoch;
    setState(() {
      _loading = true;
      _error = null;
    });

    try {
      final bytes = _cachedBytes ??= await fetcher!(widget.attachment.url);
      if (!mounted || epoch != _epoch) return;
      final out = _audioOut ??=
          (widget.ctx.createAudioOut ?? SoLoudAudioOut.new)();

      final totalMs = (widget.attachment.durationMs ?? 3000).clamp(500, 300000);
      final startedAt = DateTime.now();
      _progressTimer?.cancel();
      _progressTimer = Timer.periodic(const Duration(milliseconds: 80), (_) {
        if (!mounted || epoch != _epoch || !_playing) return;
        final elapsed = DateTime.now().difference(startedAt).inMilliseconds;
        setState(() => _progress = (elapsed / totalMs).clamp(0.0, 1.0));
      });

      setState(() {
        _loading = false;
        _playing = true;
        _progress = 0.0;
      });

      await out.playClip(bytes);
    } catch (_) {
      if (mounted && epoch == _epoch) {
        setState(() => _error = 'Could not play voice clip.');
      }
    } finally {
      _progressTimer?.cancel();
      if (mounted && epoch == _epoch) {
        setState(() {
          _loading = false;
          _playing = false;
          _progress = 0.0;
        });
      }
    }
  }

  Future<void> _stop() async {
    _epoch++;
    _progressTimer?.cancel();
    final out = _audioOut;
    if (out != null) {
      await out.stop().catchError((Object _) {});
    }
    if (mounted) {
      setState(() {
        _loading = false;
        _playing = false;
        _progress = 0.0;
      });
    }
  }

  @override
  Widget build(BuildContext context) {
    final c = UiColors.of(context);
    final t = UiTokens.of(context);
    final transcript = widget.attachment.transcript?.trim();
    final hasTranscript = transcript != null && transcript.isNotEmpty;
    final durationLabel = _formatDuration(widget.attachment.durationMs);

    final cardBg = widget.isUserBubble ? c.surfaceCard : c.surfaceSunk;
    final accentColor = c.accent;

    return Semantics(
      container: true,
      label: 'Voice note, $durationLabel',
      child: Container(
        key: ValueKey('voice-clip-card:${widget.attachment.id}'),
        constraints: const BoxConstraints(minWidth: 230, maxWidth: 360),
        padding: const EdgeInsets.symmetric(horizontal: 12, vertical: 10),
        decoration: BoxDecoration(
          color: cardBg,
          borderRadius: BorderRadius.circular(t.card),
          border: Border.all(color: c.borderHairline),
        ),
        child: Column(
          mainAxisSize: MainAxisSize.min,
          crossAxisAlignment: CrossAxisAlignment.stretch,
          children: [
            Row(
              children: [
                Semantics(
                  button: true,
                  label: _playing ? 'Pause voice note' : 'Play voice note',
                  child: GestureDetector(
                    key: ValueKey('voice-clip-play:${widget.attachment.id}'),
                    onTap: _loading ? null : () => unawaited(_togglePlay()),
                    child: Container(
                      width: 34,
                      height: 34,
                      decoration: BoxDecoration(
                        color: accentColor,
                        shape: BoxShape.circle,
                      ),
                      child: Center(
                        child: _loading
                            ? SizedBox.square(
                                dimension: 14,
                                child: CircularProgressIndicator(
                                  strokeWidth: 2,
                                  color: c.onAccent,
                                ),
                              )
                            : Icon(
                                _playing ? LucideIcons.pause : LucideIcons.play,
                                size: 15,
                                color: c.onAccent,
                              ),
                      ),
                    ),
                  ),
                ),
                const SizedBox(width: 10),
                Expanded(
                  child: Column(
                    crossAxisAlignment: CrossAxisAlignment.stretch,
                    children: [
                      SizedBox(
                        height: 22,
                        child: Row(
                          mainAxisAlignment: MainAxisAlignment.spaceBetween,
                          crossAxisAlignment: CrossAxisAlignment.center,
                          children: [
                            for (var i = 0; i < _waveformHeights.length; i++)
                              Container(
                                width: 3,
                                height: 22 * _waveformHeights[i],
                                decoration: BoxDecoration(
                                  color:
                                      (i / _waveformHeights.length) <=
                                              _progress &&
                                          _playing
                                      ? accentColor
                                      : c.textSubtle.withValues(alpha: 0.45),
                                  borderRadius: BorderRadius.circular(2),
                                ),
                              ),
                          ],
                        ),
                      ),
                      const SizedBox(height: 4),
                      Row(
                        children: [
                          Text(
                            durationLabel,
                            style: UiType.textStyle(
                              11.5,
                              weight: FontWeight.w500,
                              color: c.textMuted,
                            ),
                          ),
                          const Spacer(),
                          if (hasTranscript)
                            GestureDetector(
                              key: ValueKey(
                                'voice-clip-transcript-toggle:${widget.attachment.id}',
                              ),
                              onTap: () => setState(
                                () =>
                                    _transcriptExpanded = !_transcriptExpanded,
                              ),
                              child: Text(
                                _transcriptExpanded
                                    ? 'Hide transcript'
                                    : 'Transcript',
                                style: UiType.textStyle(
                                  11.5,
                                  weight: FontWeight.w600,
                                  color: accentColor,
                                ),
                              ),
                            ),
                        ],
                      ),
                    ],
                  ),
                ),
              ],
            ),
            if (_error != null) ...[
              const SizedBox(height: 6),
              Text(_error!, style: UiType.textStyle(12, color: c.error)),
            ],
            if (hasTranscript && _transcriptExpanded) ...[
              const SizedBox(height: 8),
              Container(
                key: ValueKey('voice-clip-transcript:${widget.attachment.id}'),
                padding: const EdgeInsets.all(8),
                decoration: BoxDecoration(
                  color: c.surfacePage,
                  borderRadius: BorderRadius.circular(t.sm),
                  border: Border.all(color: c.borderHairline),
                ),
                child: Text(
                  transcript,
                  style: UiType.textStyle(
                    12.5,
                    color: c.textBody,
                    height: 1.45,
                  ),
                ),
              ),
            ],
          ],
        ),
      ),
    );
  }
}

/// Image or file attachment preview card for Ino chat messages.
class AttachmentPreviewCard extends StatefulWidget {
  const AttachmentPreviewCard({
    super.key,
    required this.attachment,
    required this.ctx,
  });

  final AttachmentNode attachment;
  final NodeContext ctx;

  @override
  State<AttachmentPreviewCard> createState() => _AttachmentPreviewCardState();
}

class _AttachmentPreviewCardState extends State<AttachmentPreviewCard> {
  Future<Uint8List>? _imageFuture;

  @override
  void initState() {
    super.initState();
    _maybeStartImageFetch();
  }

  @override
  void didUpdateWidget(covariant AttachmentPreviewCard oldWidget) {
    super.didUpdateWidget(oldWidget);
    if (oldWidget.attachment.url != widget.attachment.url) {
      _imageFuture = null;
      _maybeStartImageFetch();
    }
  }

  void _maybeStartImageFetch() {
    if (parseAttachmentKind(widget.attachment.kind) ==
            WireAttachmentKind.image &&
        widget.ctx.fetchAttachment != null) {
      _imageFuture = widget.ctx.fetchAttachment!(widget.attachment.url);
    }
  }

  @override
  Widget build(BuildContext context) {
    final c = UiColors.of(context);
    final t = UiTokens.of(context);
    final isImage =
        parseAttachmentKind(widget.attachment.kind) == WireAttachmentKind.image;
    final sizeLabel = _formatBytes(widget.attachment.sizeBytes);

    return Container(
      key: ValueKey('attachment-card:${widget.attachment.id}'),
      constraints: const BoxConstraints(maxWidth: 320),
      padding: const EdgeInsets.all(10),
      decoration: BoxDecoration(
        color: c.surfaceCard,
        borderRadius: BorderRadius.circular(t.card),
        border: Border.all(color: c.borderHairline),
      ),
      child: Column(
        mainAxisSize: MainAxisSize.min,
        crossAxisAlignment: CrossAxisAlignment.start,
        children: [
          if (isImage && _imageFuture != null) ...[
            ClipRRect(
              borderRadius: BorderRadius.circular(t.sm),
              child: FutureBuilder<Uint8List>(
                future: _imageFuture,
                builder: (context, snap) {
                  if (snap.hasData && snap.data!.isNotEmpty) {
                    return Image.memory(
                      snap.data!,
                      height: 150,
                      width: double.infinity,
                      fit: BoxFit.cover,
                      errorBuilder: (_, _, _) => _imagePlaceholder(c),
                    );
                  }
                  return _imagePlaceholder(c);
                },
              ),
            ),
            const SizedBox(height: 8),
          ],
          Row(
            mainAxisSize: MainAxisSize.min,
            children: [
              Icon(
                isImage ? LucideIcons.image : LucideIcons.fileText,
                size: 16,
                color: c.accent,
              ),
              const SizedBox(width: 8),
              Expanded(
                child: Column(
                  crossAxisAlignment: CrossAxisAlignment.start,
                  children: [
                    Text(
                      widget.attachment.fileName,
                      maxLines: 1,
                      overflow: TextOverflow.ellipsis,
                      style: UiType.textStyle(
                        13,
                        weight: FontWeight.w600,
                        color: c.textStrong,
                      ),
                    ),
                    Text(
                      '${widget.attachment.mediaType} · $sizeLabel',
                      maxLines: 1,
                      overflow: TextOverflow.ellipsis,
                      style: UiType.textStyle(11.5, color: c.textMuted),
                    ),
                  ],
                ),
              ),
            ],
          ),
        ],
      ),
    );
  }

  Widget _imagePlaceholder(UiColors c) => Container(
    height: 96,
    width: double.infinity,
    color: c.surfaceSunk,
    child: Center(
      child: Icon(LucideIcons.image, size: 24, color: c.textSubtle),
    ),
  );
}

String _formatDuration(int? durationMs) {
  if (durationMs == null || durationMs <= 0) return '0:00';
  final totalSeconds = (durationMs / 1000).round();
  final minutes = totalSeconds ~/ 60;
  final seconds = totalSeconds % 60;
  return '$minutes:${seconds.toString().padLeft(2, '0')}';
}

String _formatBytes(int bytes) {
  if (bytes < 1024) return '$bytes B';
  if (bytes < 1024 * 1024) return '${(bytes / 1024).toStringAsFixed(1)} KB';
  return '${(bytes / (1024 * 1024)).toStringAsFixed(1)} MB';
}
