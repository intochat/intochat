import 'package:flutter/foundation.dart';
import 'package:flutter/material.dart';
import 'package:intochat_os_flutter_shell/intochat_os_flutter.dart';

import 'package:intochat_os_flutter_shell/intochat_os_ui.dart' show IcAppIcon;

import '../../theme/ui_theme.dart';
import '../../theme/ui_tokens.dart';
import 'embedded_browser_stub.dart'
    if (dart.library.io) 'embedded_browser_windows.dart'
    if (dart.library.js_interop) 'embedded_browser_web.dart';

final class UiWebBrowser extends StatefulWidget {
  const UiWebBrowser({
    super.key,
    required this.part,
    this.height = 160,
    this.frameless = false,
    this.chrome = 'full',
    this.onConnect,
    this.onDisconnect,
    this.onUnavailable,
    this.connection = '',
    this.error = '',
    this.actualUrl = '',
  });

  final UiWebBrowserPart part;
  final double? height;
  final bool frameless;
  final String chrome;
  bool get isFrameless =>
      frameless || chrome.trim().toLowerCase() == 'frameless';
  final Future<void> Function(int, String)? onConnect;
  final Future<void> Function(String)? onDisconnect;
  final Future<void> Function(String)? onUnavailable;
  final String connection;
  final String error;
  final String actualUrl;

  @override
  State<UiWebBrowser> createState() => _UiWebBrowserState();
}

class _UiWebBrowserState extends State<UiWebBrowser> {
  int _session = 0;

  @override
  Widget build(BuildContext context) {
    final scheme = Theme.of(context).colorScheme;
    final tokens = UiTokens.of(context);
    final connection = widget.connection.toLowerCase();
    final hasError = widget.error.isNotEmpty;
    var url = !kIsWeb && widget.actualUrl.isNotEmpty
        ? widget.actualUrl
        : widget.part.uri;
    if (kIsWeb) {
      final baseOrigin = IntoChatOSHostEnv.resolveUiBaseRaw();
      url = IcAppIcon.resolveArtCraftWebUrl(
        url,
        hint: '${widget.part.name} ${widget.part.title}',
        isWeb: kIsWeb,
        uiBaseOrigin: baseOrigin,
      );
      url = resolveEmbeddedBrowserWebUrl(
        url,
        uiBaseOrigin: baseOrigin,
        fallbackBase: Uri.base,
      );
    }
    final placeholder = kIsWeb
        ? null
        : hasError
        ? widget.error
        : connection == 'waiting'
        ? 'Waiting for the page...'
        : null;

    final viewport = Stack(
      fit: StackFit.expand,
      children: [
        EmbeddedBrowser(
          // Reconnect replaces the native page and its marked connector session.
          key: ValueKey(_session),
          uri: url,
          onConnect: widget.onConnect,
          onDisconnect: widget.onDisconnect,
          onUnavailable: widget.onUnavailable,
        ),
        if (placeholder != null)
          ColoredBox(
            color: scheme.surface,
            child: Center(
              child: Padding(
                padding: EdgeInsets.all(tokens.spaceMedium),
                child: SelectableText(
                  placeholder,
                  textAlign: TextAlign.center,
                  style: TextStyle(color: scheme.onSurfaceVariant),
                ),
              ),
            ),
          ),
      ],
    );

    if (widget.isFrameless) {
      return Semantics(
        label: 'browser ${widget.part.title}',
        child: SizedBox(
          key: Key('ui_webbrowser_${widget.part.name}'),
          width: double.infinity,
          height: widget.height,
          child: viewport,
        ),
      );
    }

    return Semantics(
      label: 'browser ${widget.part.title}',
      child: DecoratedBox(
        key: Key('ui_webbrowser_${widget.part.name}'),
        decoration: BoxDecoration(
          color: scheme.surfaceContainerLow,
          borderRadius: BorderRadius.circular(tokens.surfaceRadius),
          border: Border.all(color: scheme.outlineVariant),
        ),
        child: SizedBox(
          height: widget.height,
          child: Padding(
            padding: EdgeInsets.all(tokens.spaceSmall),
            child: Column(
              crossAxisAlignment: CrossAxisAlignment.start,
              children: [
                Row(
                  children: [
                    Expanded(
                      child: Text(
                        widget.part.title,
                        maxLines: 1,
                        overflow: TextOverflow.ellipsis,
                        style: TextStyle(
                          fontFamily: UiType.bodyFamily,
                          fontWeight: FontWeight.w600,
                          color: scheme.onSurface,
                        ),
                      ),
                    ),
                    if (kIsWeb)
                      Tooltip(
                        message: 'Interact with this page here. Browser automation requires the desktop app.',
                        child: Padding(
                          padding: EdgeInsets.all(tokens.spaceSmall),
                          child: Icon(
                            Icons.info_outline,
                            size: 16,
                            color: scheme.onSurfaceVariant,
                          ),
                        ),
                      ),
                    if (widget.onConnect != null || kIsWeb)
                      IconButton(
                        tooltip: 'Reconnect browser',
                        onPressed: () => setState(() => _session++),
                        icon: const Icon(Icons.refresh),
                      ),
                  ],
                ),
                SizedBox(height: tokens.spaceSmall),
                Expanded(child: viewport),
              ],
            ),
          ),
        ),
      ),
    );
  }
}

final class UiWebBrowserPart {
  const UiWebBrowserPart({
    required this.name,
    required this.uri,
    required this.title,
  });

  static const kindName = 'webbrowser';

  final String name;
  final String uri;
  final String title;

  String get kind => kindName;

  String get copyText => uri;

  factory UiWebBrowserPart.fromMetadata(Map<String, dynamic> metadata) =>
      UiWebBrowserPart(
        name: metadata['name'] as String? ?? '',
        uri: metadata['uri'] as String? ?? '',
        title: metadata['title'] as String? ?? '',
      );

  Map<String, Object?> toMetadata() => {
    'kind': kindName,
    'name': name,
    'uri': uri,
    'title': title,
  };
}

/// Resolves same-origin root-relative paths (such as `/orleans#/overview`) to an
/// absolute HTTP/HTTPS URL when hosted in a web browser frame so `EmbeddedBrowser`
/// passes authority validation cleanly while continuing to reject non-HTTP schemes
/// and protocol-relative `//` URLs.
String resolveEmbeddedBrowserWebUrl(
  String rawUrl, {
  String? uiBaseOrigin,
  Uri? fallbackBase,
}) {
  final trimmed = rawUrl.trim();
  if (!trimmed.startsWith('/') || trimmed.startsWith('//')) {
    return rawUrl;
  }
  for (final candidate in <Uri?>[
    if (uiBaseOrigin != null && uiBaseOrigin.trim().isNotEmpty)
      Uri.tryParse(uiBaseOrigin.trim()),
    fallbackBase,
  ]) {
    if (candidate != null &&
        candidate.hasAuthority &&
        candidate.host.isNotEmpty &&
        (candidate.scheme == 'http' || candidate.scheme == 'https')) {
      final portSuffix = candidate.hasPort ? ':${candidate.port}' : '';
      return '${candidate.scheme}://${candidate.host}$portSuffix$trimmed';
    }
  }
  return rawUrl;
}
