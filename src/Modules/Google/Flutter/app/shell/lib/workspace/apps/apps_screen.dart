import 'dart:async';

import 'package:flutter/material.dart';

import 'app_screen.dart';

typedef AppsRequest = Future<dynamic> Function(
  String method,
  String path, [
  Object? body,
]);

// This brain's apps: one card per app neuron, created in the Creator, driven through its
// own operations, deleted in one step. Sharing is export/import of a verified version.
class AppsScreen extends StatefulWidget {
  const AppsScreen({
    super.key,
    required this.workspaceId,
    required this.request,
    required this.onClose,
    this.onOpen,
  });

  final String workspaceId;
  final AppsRequest request;
  final VoidCallback onClose;
  final Future<void> Function(String id)? onOpen;

  @override
  State<AppsScreen> createState() => _AppsScreenState();
}

class _AppsScreenState extends State<AppsScreen> {
  List<Map<String, dynamic>> _apps = [];
  String _search = '';
  bool _busy = false;
  String? _error;
  String? _notice;

  String get _appsPath =>
      '/brains/${Uri.encodeComponent(widget.workspaceId)}/apps';

  static Map<String, dynamic> _map(dynamic value) =>
      value is Map ? Map<String, dynamic>.from(value) : <String, dynamic>{};

  @override
  void initState() {
    super.initState();
    unawaited(_refresh());
  }

  Future<void> _perform(Future<void> Function() action) async {
    if (_busy) return;
    setState(() {
      _busy = true;
      _error = null;
      _notice = null;
    });
    try {
      await action();
    } catch (error) {
      if (mounted) setState(() => _error = error.toString());
    } finally {
      if (mounted) setState(() => _busy = false);
    }
  }

  Future<void> _refresh() => _perform(_load);

  Future<void> _load() async {
    final listed = await widget.request('GET', _appsPath);
    if (!mounted) return;
    setState(() {
      _apps = listed is List ? listed.map(_map).toList() : [];
    });
  }

  Future<void> _delete(String name) => _perform(() async {
    await widget.request('DELETE', '$_appsPath/${Uri.encodeComponent(name)}');
    await _load();
    if (mounted) setState(() => _notice = 'Deleted $name.');
  });

  Future<void> _openCreator([String? name]) async {
    await Navigator.of(context).push(
      MaterialPageRoute<void>(
        builder: (_) => AppScreen(
          workspaceId: widget.workspaceId,
          request: widget.request,
          name: name,
        ),
      ),
    );
    if (mounted) await _refresh();
  }

  @override
  Widget build(BuildContext context) {
    return Scaffold(
      appBar: AppBar(
        title: const Text('Apps'),
        leading: IconButton(
          tooltip: 'Close',
          icon: const Icon(Icons.close),
          onPressed: widget.onClose,
        ),
        actions: [
          TextButton.icon(
            key: const ValueKey('create-app'),
            onPressed: _busy ? null : () => _openCreator(),
            icon: const Icon(Icons.add),
            label: const Text('Create app'),
          ),
          IconButton(
            tooltip: 'Refresh',
            icon: const Icon(Icons.refresh),
            onPressed: _busy ? null : _refresh,
          ),
        ],
      ),
      body: Column(
        crossAxisAlignment: CrossAxisAlignment.stretch,
        children: [
          if (_busy) const LinearProgressIndicator(),
          if (_error != null)
            Padding(
              padding: const EdgeInsets.all(12),
              child: Text(
                _error!,
                style: TextStyle(color: Theme.of(context).colorScheme.error),
              ),
            ),
          if (_notice != null)
            Padding(padding: const EdgeInsets.all(12), child: Text(_notice!)),
          Padding(
            padding: const EdgeInsets.all(12),
            child: TextField(
              key: const ValueKey('search-apps'),
              decoration: const InputDecoration(
                labelText: 'Search apps by name or description',
                prefixIcon: Icon(Icons.search),
              ),
              onChanged: (value) =>
                  setState(() => _search = value.trim().toLowerCase()),
            ),
          ),
          Expanded(
            child: _apps.isEmpty && !_busy
                ? const Center(child: Text('No apps yet. Create one.'))
                : ListView(
                    padding: const EdgeInsets.all(12),
                    children: [
                      for (final app in _apps)
                        if ('${app['name']} ${app['title'] ?? ''} ${app['description'] ?? ''}'
                            .toLowerCase()
                            .contains(_search))
                          _card(app),
                    ],
                  ),
          ),
        ],
      ),
    );
  }

  Widget _card(Map<String, dynamic> app) {
    final name = '${app['name']}';
    final operations = app['operations'] is List
        ? (app['operations'] as List).map((operation) => '$operation').toList()
        : <String>[];
    return Card(
      key: ValueKey('app-$name'),
      child: ListTile(
        title: Text('${app['title'] ?? name}'),
        subtitle: Text(
          '$name · v${app['version']} · ${app['status']}'
          '${'${app['description'] ?? ''}'.isEmpty ? '' : '\n${app['description']}'}',
        ),
        isThreeLine: '${app['description'] ?? ''}'.isNotEmpty,
        onTap: () => _openCreator(name),
        trailing: Wrap(
          spacing: 4,
          children: [
            if (widget.onOpen != null &&
                app['status'] == 'Live' &&
                operations.contains('open'))
              FilledButton(
                key: ValueKey('open-$name'),
                onPressed: _busy ? null : () => widget.onOpen!(name),
                child: const Text('Open'),
              ),
            IconButton(
              key: ValueKey('delete-$name'),
              tooltip: 'Delete',
              icon: const Icon(Icons.delete_outline),
              onPressed: _busy ? null : () => _delete(name),
            ),
          ],
        ),
      ),
    );
  }
}
