import 'dart:convert';
import 'dart:typed_data';

import 'package:flutter/material.dart';
import 'package:flutter_svg/flutter_svg.dart';
import 'package:intochat_os_flutter_shell/intochat_os_flutter.dart';
import 'package:lucide_icons_flutter/lucide_icons.dart';

import '../../theme/ui_colors.dart';
import '../../theme/ui_theme.dart';
import '../../theme/ui_tokens.dart';

// --- src/components/image/ui_image.dart ---

final class UiImage extends StatelessWidget {
  /// Decodes local [bytes].
  const UiImage({
    super.key,
    required Uint8List this.bytes,
    required this.caption,
  }) : url = null;

  /// Loads [url] over http/https or relative path; any empty url shows the
  /// caption only.
  const UiImage.network({
    super.key,
    required String this.url,
    required this.caption,
  }) : bytes = null;

  final Uint8List? bytes;
  final String? url;
  final String caption;

  Uri? get _remote => resolveImageUri(url);

  @override
  Widget build(BuildContext context) {
    final c = UiColors.of(context);
    final type = UiType.of(context);
    final remote = _remote;
    final isSvg =
        (url?.toLowerCase().contains('.svg') ?? false) ||
        (remote?.path.toLowerCase().endsWith('.svg') ?? false);

    Widget loading(BuildContext context, Widget child, int? frame, bool sync) =>
        sync || frame != null
        ? child
        : const Padding(
            key: Key('ui_image_loading'),
            padding: EdgeInsets.all(24),
            child: Center(child: CircularProgressIndicator(strokeWidth: 2)),
          );
    Widget failed(BuildContext context, Object error, StackTrace? stack) =>
        Padding(
          key: const Key('ui_image_error'),
          padding: const EdgeInsets.all(24),
          child: Center(
            child: Text('Image failed to load', style: type.bodyMuted),
          ),
        );

    Widget? image;
    if (isSvg) {
      if (bytes != null) {
        image = SvgPicture.memory(bytes!, fit: BoxFit.contain);
      } else if (remote != null) {
        image = SvgPicture.network(
          remote.toString(),
          fit: BoxFit.contain,
          placeholderBuilder: (ctx) => const Padding(
            key: Key('ui_image_loading'),
            padding: EdgeInsets.all(24),
            child: Center(child: CircularProgressIndicator(strokeWidth: 2)),
          ),
        );
      }
    } else if (bytes != null) {
      image = Image.memory(
        bytes!,
        fit: BoxFit.cover,
        frameBuilder: loading,
        errorBuilder: failed,
      );
    } else if (remote != null) {
      image = Image.network(
        remote.toString(),
        fit: BoxFit.cover,
        semanticLabel: caption.isEmpty ? null : caption,
        frameBuilder: loading,
        errorBuilder: failed,
      );
    }

    return DecoratedBox(
      key: Key('ui_image_$caption'),
      decoration: BoxDecoration(
        color: c.surface,
        borderRadius: BorderRadius.circular(12),
        border: Border.all(color: c.hairline),
      ),
      child: Padding(
        padding: const EdgeInsets.fromLTRB(12, 12, 12, 8),
        child: Column(
          crossAxisAlignment: CrossAxisAlignment.start,
          mainAxisSize: MainAxisSize.min,
          children: [
            if (caption.isNotEmpty) ...[
              Text(caption, style: type.title),
              if (image != null) const SizedBox(height: 10),
            ],
            if (image != null)
              ClipRRect(borderRadius: BorderRadius.circular(8), child: image),
          ],
        ),
      ),
    );
  }
}

/// The address an image [url] loads from: an http(s) URL as it is, or a path on
/// the shell's own host (`/brains/...`); null for anything else.
Uri? resolveImageUri(String? url) {
  final raw = url?.trim() ?? '';
  if (raw.isEmpty) return null;
  final uri = Uri.tryParse(raw);
  if (uri != null &&
      uri.host.isNotEmpty &&
      const {'http', 'https'}.contains(uri.scheme)) {
    return uri;
  }
  if (raw.startsWith('/')) {
    try {
      final base = IntoChatOSHostEnv.requireUiBaseUri();
      return base.resolve(raw);
    } catch (_) {
      return Uri.tryParse('http://127.0.0.1:5000$raw');
    }
  }
  return null;
}

// --- src/components/image/ui_image_viewer.dart ---
const _minZoom = 0.25;
const _maxZoom = 4.0;
const _zoomStep = 0.25;

final class _DataImage {
  const _DataImage(this.bytes, this.isSvg);

  final Uint8List bytes;
  final bool isSvg;

  static _DataImage? tryParse(String raw) {
    if (!raw.startsWith('data:image/')) return null;
    final comma = raw.indexOf(',');
    if (comma <= 0 || comma + 1 >= raw.length) return null;
    final header = raw.substring(0, comma).toLowerCase();
    try {
      final data = raw.substring(comma + 1);
      final bytes = header.endsWith(';base64')
          ? base64Decode(data)
          : Uint8List.fromList(utf8.encode(Uri.decodeComponent(data)));
      return _DataImage(bytes, header.startsWith('data:image/svg'));
    } catch (_) {
      return null;
    }
  }
}

/// An image from a URL or a `data:image/*` URL with zoom and rotation tools.
/// Zoom and rotation are the viewer's own: they live here and are never sent.
class UiImageViewer extends StatefulWidget {
  const UiImageViewer({
    super.key,
    required this.url,
    required this.caption,
    required this.height,
    required this.fit,
  });

  final String url;
  final String caption;
  final double height;
  final BoxFit fit;

  @override
  State<UiImageViewer> createState() => _UiImageViewerState();
}

class _UiImageViewerState extends State<UiImageViewer> {
  double _zoom = 1;
  int _rotation = 0;

  @override
  void didUpdateWidget(covariant UiImageViewer oldWidget) {
    super.didUpdateWidget(oldWidget);
    if (oldWidget.url != widget.url) _reset();
  }

  void _reset() {
    _zoom = 1;
    _rotation = 0;
  }

  void _zoomBy(double delta) => setState(
    () => _zoom = (_zoom + delta).clamp(_minZoom, _maxZoom).toDouble(),
  );

  @override
  Widget build(BuildContext context) {
    final c = UiColors.of(context);
    final tokens = UiTokens.of(context);
    final url = widget.url.trim();
    final caption = widget.caption.trim();
    final height = widget.height > 0
        ? widget.height.clamp(80.0, 1200.0)
        : 320.0;
    final fit = widget.fit;

    final picture = _picture(url, caption, fit);

    Widget tool(
      String key,
      String tooltip,
      IconData icon,
      VoidCallback onTap,
    ) => IconButton(
      key: ValueKey('image:$key'),
      tooltip: tooltip,
      icon: Icon(icon, size: 15, color: c.muted),
      visualDensity: VisualDensity.compact,
      padding: EdgeInsets.zero,
      constraints: const BoxConstraints(minWidth: 28, minHeight: 28),
      onPressed: onTap,
    );

    return Container(
      padding: const EdgeInsets.all(12),
      decoration: BoxDecoration(
        color: c.surfaceSunk,
        borderRadius: BorderRadius.circular(tokens.surfaceRadius),
        border: Border.all(color: c.borderHairline),
      ),
      child: Column(
        mainAxisSize: MainAxisSize.min,
        crossAxisAlignment: CrossAxisAlignment.stretch,
        spacing: 8,
        children: [
          Row(
            children: [
              Icon(LucideIcons.image, size: 14, color: c.muted),
              const SizedBox(width: 6),
              Expanded(
                child: Text(
                  caption,
                  maxLines: 1,
                  overflow: TextOverflow.ellipsis,
                  style: UiType.textStyle(
                    12.5,
                    weight: FontWeight.w600,
                    color: c.textStrong,
                  ),
                ),
              ),
              if (picture != null) ...[
                Text(
                  '${(_zoom * 100).round()}% · $_rotation°',
                  style: UiType.tabularStyle(11, color: c.muted),
                ),
                const SizedBox(width: 4),
                tool(
                  'zoomOut',
                  'Zoom out',
                  LucideIcons.zoomOut,
                  () => _zoomBy(-_zoomStep),
                ),
                tool(
                  'zoomIn',
                  'Zoom in',
                  LucideIcons.zoomIn,
                  () => _zoomBy(_zoomStep),
                ),
                tool(
                  'rotate',
                  'Rotate right',
                  LucideIcons.rotateCw,
                  () => setState(() => _rotation = (_rotation + 90) % 360),
                ),
                tool(
                  'reset',
                  'Reset view',
                  LucideIcons.refreshCcw,
                  () => setState(_reset),
                ),
              ],
            ],
          ),
          SizedBox(
            height: height,
            width: double.infinity,
            child: picture == null
                ? Center(
                    child: Text(
                      'No image preview available',
                      style: UiType.of(context).bodyMuted,
                    ),
                  )
                : ClipRRect(
                    borderRadius: BorderRadius.circular(8),
                    child: RotatedBox(
                      quarterTurns: _rotation ~/ 90,
                      child: Transform.scale(scale: _zoom, child: picture),
                    ),
                  ),
          ),
        ],
      ),
    );
  }

  Widget? _picture(String url, String caption, BoxFit fit) {
    if (url.isEmpty) return null;
    final c = UiColors.of(context);
    Widget failed(BuildContext context, Object error, StackTrace? stack) =>
        Center(child: Icon(LucideIcons.imageOff, size: 32, color: c.muted));
    final data = _DataImage.tryParse(url);
    if (data != null) {
      return data.isSvg
          ? SvgPicture.memory(data.bytes, fit: fit)
          : Image.memory(
              data.bytes,
              fit: fit,
              filterQuality: FilterQuality.medium,
              semanticLabel: caption.isEmpty ? null : caption,
              errorBuilder: failed,
            );
    }
    final remote = resolveImageUri(url);
    if (remote == null) return null;
    final address = remote.toString();
    return remote.path.toLowerCase().endsWith('.svg')
        ? SvgPicture.network(address, fit: fit)
        : Image.network(
            address,
            fit: fit,
            semanticLabel: caption.isEmpty ? null : caption,
            errorBuilder: failed,
          );
  }
}
