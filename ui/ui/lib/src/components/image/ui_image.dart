import 'dart:typed_data';

import 'package:flutter/material.dart';
import 'package:flutter_svg/flutter_svg.dart';
import 'package:intochat_os_flutter/intochat_os_flutter.dart';

import '../../theme/ui_colors.dart';
import '../../theme/ui_theme.dart';

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

  Uri? get _remote {
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

  @override
  Widget build(BuildContext context) {
    final c = UiColors.of(context);
    final type = UiType.of(context);
    final remote = _remote;
    final isSvg = (url?.toLowerCase().contains('.svg') ?? false) ||
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
        image = SvgPicture.memory(
          bytes!,
          fit: BoxFit.contain,
        );
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
