import 'package:flutter/material.dart';

import '../../models/ui_part.dart';
import '../../theme/ui_theme.dart';
import 'embedded_browser_stub.dart'
    if (dart.library.io) 'embedded_browser_windows.dart';

final class UiWebBrowser extends StatefulWidget {
  const UiWebBrowser({
    super.key,
    required this.part,
    this.height = 160,
    this.onConnect,
    this.onDisconnect,
    this.onUnavailable,
    this.connection = '',
    this.error = '',
    this.actualUrl = '',
  });

  final UiWebBrowserPart part;
  final double? height;
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
    final connection = widget.connection.toLowerCase();
    final hasError = widget.error.isNotEmpty;
    final url = widget.actualUrl.isNotEmpty
        ? widget.actualUrl
        : widget.part.uri;
    final connectionLabel = widget.connection.isEmpty
        ? ''
        : '${widget.connection[0].toUpperCase()}${widget.connection.substring(1)}';
    final placeholder = hasError
        ? widget.error
        : connection == 'waiting'
        ? 'Waiting for the page...'
        : null;

    return Semantics(
      label: 'browser ${widget.part.title}',
      child: DecoratedBox(
        key: Key('ui_webbrowser_${widget.part.name}'),
        decoration: BoxDecoration(
          color: scheme.surfaceContainerHighest,
          borderRadius: BorderRadius.circular(12),
          border: Border.all(color: scheme.outlineVariant),
        ),
        child: SizedBox(
          height: widget.height,
          child: Padding(
            padding: const EdgeInsets.all(12),
            child: Column(
              crossAxisAlignment: CrossAxisAlignment.start,
              children: [
                Row(
                  children: [
                    Expanded(
                      child: Text(
                        widget.part.title,
                        maxLines: 2,
                        overflow: TextOverflow.ellipsis,
                        style: TextStyle(
                          fontFamily: UiType.bodyFamily,
                          fontWeight: FontWeight.w600,
                          color: scheme.onSurface,
                        ),
                      ),
                    ),
                    if (widget.onConnect != null)
                      IconButton(
                        tooltip: 'Reconnect browser',
                        onPressed: () => setState(() => _session++),
                        icon: const Icon(Icons.refresh),
                      ),
                  ],
                ),
                const SizedBox(height: 8),
                if (connectionLabel.isNotEmpty)
                  Text(
                    connectionLabel,
                    maxLines: 1,
                    style: TextStyle(
                      fontFamily: UiType.bodyFamily,
                      fontSize: 11,
                      fontWeight: FontWeight.w600,
                      color: scheme.onSurfaceVariant,
                      letterSpacing: 0.5,
                    ),
                  ),
                if (url.isNotEmpty) ...[
                  const SizedBox(height: 4),
                  Text(
                    url,
                    maxLines: 2,
                    overflow: TextOverflow.ellipsis,
                    style: TextStyle(
                      fontFamily: UiType.bodyFamily,
                      color: scheme.onSurfaceVariant,
                    ),
                  ),
                ],
                Expanded(
                  child: Stack(
                    fit: StackFit.expand,
                    children: [
                      EmbeddedBrowser(
                        // Reconnect replaces the native page and its marked connector session.
                        key: ValueKey(_session),
                        onConnect: widget.onConnect,
                        onDisconnect: widget.onDisconnect,
                        onUnavailable: widget.onUnavailable,
                      ),
                      if (placeholder != null)
                        ColoredBox(
                          color: scheme.surface,
                          child: Center(
                            child: Padding(
                              padding: const EdgeInsets.all(16),
                              child: SelectableText(
                                placeholder,
                                textAlign: TextAlign.center,
                                style: TextStyle(
                                  color: scheme.onSurfaceVariant,
                                ),
                              ),
                            ),
                          ),
                        ),
                    ],
                  ),
                ),
              ],
            ),
          ),
        ),
      ),
    );
  }
}
