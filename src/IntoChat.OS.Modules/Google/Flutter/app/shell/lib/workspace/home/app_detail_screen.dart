import 'dart:async';

import 'package:intochat_os_flutter/intochat_os_flutter.dart';
import 'package:flutter/material.dart';

import '../apps/app_screen.dart';
import '../apps/apps_request.dart';
import 'app_icon.dart';

// One app before or after install: what it is, who publishes it,
// and the single next step — Install, Open or Update. A copy you edited is yours: it keeps
// working but no longer receives the publisher's updates.
class AppDetailScreen extends StatefulWidget {
  const AppDetailScreen({
    super.key,
    required this.workspaceId,
    required this.request,
    this.listing,
    this.installedName,
    required this.onOpen,
    this.loadIcon,
  }) : assert(listing != null || installedName != null);

  final String workspaceId;
  final AppsRequest request;
  final MarketplaceListing? listing;
  final String? installedName;
  final Future<void> Function(String name) onOpen;
  final IconLoader? loadIcon;

  @override
  State<AppDetailScreen> createState() => _AppDetailScreenState();
}

class _AppDetailScreenState extends State<AppDetailScreen> {
  MarketplaceEntry? _entry;
  Map<String, dynamic>? _app;
  bool _busy = false;
  String? _error, _notice;

  String get _brain => Uri.encodeComponent(widget.workspaceId);
  String _appPath(String name) =>
      '/brains/$_brain/apps/${Uri.encodeComponent(name)}';
  String _marketPath(String publisher, String name) =>
      '/brains/$_brain/marketplace/${Uri.encodeComponent(publisher)}/${Uri.encodeComponent(name)}';

  static Map<String, dynamic> _map(dynamic value) =>
      value is Map ? Map<String, dynamic>.from(value) : <String, dynamic>{};

  MarketplaceListing? get _listing => _entry?.listing ?? widget.listing;
  String? get _installedName =>
      _app == null ? null : '${_app!['name'] ?? widget.installedName}';
  Map<String, dynamic> get _manifest => _map(_app?['manifest']);
  bool get _openable => _app?['status'] == 'Live';

  @override
  void initState() {
    super.initState();
    unawaited(_perform(_load));
  }

  Future<void> _perform(Future<void> Function() action) async {
    if (_busy) return;
    setState(() {
      _busy = true;
      _error = null;
    });
    try {
      await action();
    } catch (error) {
      if (mounted) setState(() => _error = error.toString());
    } finally {
      if (mounted) setState(() => _busy = false);
    }
  }

  Future<void> _load() async {
    MarketplaceEntry? entry;
    Map<String, dynamic>? app;
    var name = widget.installedName;
    final listing = widget.listing;
    if (listing != null) {
      entry = MarketplaceEntry.fromJson(
        _map(
          await widget.request(
            'GET',
            _marketPath(listing.publisher, listing.name),
          ),
        ),
      );
      if (entry.installed) name ??= listing.name;
    }
    if (name != null) {
      app = _map(await widget.request('GET', _appPath(name)));
      final origin = app['origin'];
      if (entry == null && origin is Map) {
        entry = MarketplaceEntry.fromJson(
          _map(
            await widget.request(
              'GET',
              _marketPath('${origin['publisher']}', '${origin['name']}'),
            ),
          ),
        );
      }
    }
    if (!mounted) return;
    setState(() {
      _entry = entry;
      _app = app;
    });
  }

  Future<void> _install() => _perform(() async {
    final listing = _listing!;
    await widget.request(
      'POST',
      '${_marketPath(listing.publisher, listing.name)}/install',
      const <String, dynamic>{},
    );
    await _load();
    if (mounted) setState(() => _notice = 'Installed.');
  });

  Future<void> _update() => _perform(() async {
    await widget.request(
      'POST',
      '${_appPath(_installedName!)}/update',
      const <String, dynamic>{},
    );
    await _load();
    if (mounted) setState(() => _notice = 'Updated to v${_app?['version']}.');
  });

  Future<void> _delete() => _perform(() async {
    await widget.request('DELETE', _appPath(_installedName!));
    if (mounted) Navigator.of(context).pop();
  });

  Future<void> _open() async {
    final name = _installedName!;
    await widget.onOpen(name);
    if (mounted && Navigator.of(context).canPop()) Navigator.of(context).pop();
  }

  Future<void> _edit() async {
    await Navigator.of(context).push(
      MaterialPageRoute<void>(
        builder: (_) => AppScreen(
          workspaceId: widget.workspaceId,
          request: widget.request,
          name: _installedName,
        ),
      ),
    );
    if (mounted) await _perform(_load);
  }

  @override
  Widget build(BuildContext context) {
    final listing = _listing;
    final app = _app;
    final installed = app != null;
    final title =
        _manifest['title'] as String? ??
        listing?.title ??
        widget.installedName ??
        '';
    final description =
        _manifest['description'] as String? ?? listing?.description ?? '';
    final origin = _map(app?['origin']);
    final publisher = listing?.publisher ?? origin['publisher'] as String?;
    final version = listing?.version ?? '${app?['version'] ?? ''}';
    final edited = app?['edited'] == true && origin.isNotEmpty;
    final iconPath = listing?.icon != null
        ? '${_marketPath(listing!.publisher, listing.name)}/icon'
        : _manifest['icon'] != null && _installedName != null
        ? '${_appPath(_installedName!)}/icon'
        : null;
    final textTheme = Theme.of(context).textTheme;
    return Scaffold(
      appBar: AppBar(title: Text(title)),
      body: Center(
        child: ConstrainedBox(
          constraints: const BoxConstraints(maxWidth: 820),
          child: ListView(
            padding: const EdgeInsets.all(24),
            children: [
              if (_busy) const LinearProgressIndicator(),
              Row(
                children: [
                  AppIcon(
                    title: title,
                    path: iconPath,
                    load: widget.loadIcon,
                    size: 64,
                  ),
                  const SizedBox(width: 16),
                  Expanded(
                    child: Column(
                      crossAxisAlignment: CrossAxisAlignment.start,
                      children: [
                        Text(
                          title,
                          key: const ValueKey('detail-title'),
                          style: textTheme.headlineSmall,
                        ),
                        Text(
                          publisher == null
                              ? 'Yours · v$version'
                              : 'by $publisher · v$version',
                          key: const ValueKey('detail-publisher'),
                          style: textTheme.bodyMedium,
                        ),
                      ],
                    ),
                  ),
                ],
              ),
              const SizedBox(height: 16),
              if (description.isNotEmpty)
                Text(description, key: const ValueKey('detail-description')),
              const SizedBox(height: 16),
              Wrap(
                spacing: 8,
                runSpacing: 8,
                children: [
                  if (listing != null && !installed)
                    FilledButton(
                      key: const ValueKey('install-app'),
                      onPressed: _busy ? null : _install,
                      child: const Text('Install'),
                    ),
                  if (installed && _openable)
                    FilledButton(
                      key: const ValueKey('open-app'),
                      onPressed: _busy ? null : _open,
                      child: const Text('Open'),
                    ),
                  if (installed && (_entry?.updateAvailable ?? false))
                    FilledButton.tonal(
                      key: const ValueKey('update-app'),
                      onPressed: _busy ? null : _update,
                      child: Text('Update to v${_entry!.listing.version}'),
                    ),
                  if (installed)
                    OutlinedButton(
                      key: const ValueKey('edit-app'),
                      onPressed: _busy ? null : _edit,
                      child: const Text('Edit'),
                    ),
                  if (installed)
                    TextButton(
                      key: const ValueKey('delete-app'),
                      onPressed: _busy ? null : _delete,
                      child: const Text('Remove'),
                    ),
                ],
              ),
              if (edited)
                Padding(
                  padding: const EdgeInsets.only(top: 12),
                  child: Text(
                    'You changed this copy, so it no longer receives updates from $publisher.',
                    key: const ValueKey('edited-note'),
                  ),
                ),
              if (_notice != null)
                Padding(
                  padding: const EdgeInsets.only(top: 12),
                  child: Text(_notice!, key: const ValueKey('detail-notice')),
                ),
              if (_error != null)
                Padding(
                  padding: const EdgeInsets.only(top: 12),
                  child: Text(
                    _error!,
                    key: const ValueKey('detail-error'),
                    style: TextStyle(
                      color: Theme.of(context).colorScheme.error,
                    ),
                  ),
                ),
            ],
          ),
        ),
      ),
    );
  }
}
