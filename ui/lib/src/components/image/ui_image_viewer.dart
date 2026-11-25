import 'dart:convert';
import 'dart:typed_data';

import 'package:flutter/material.dart';
import 'package:flutter_svg/flutter_svg.dart';
import 'package:lucide_icons_flutter/lucide_icons.dart';

import '../../theme/ui_colors.dart';
import '../../theme/ui_theme.dart';
import '../../theme/ui_tokens.dart';
import 'ui_image.dart' show resolveImageUri;

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
