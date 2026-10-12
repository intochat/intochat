import 'dart:async';

import 'package:flutter/material.dart';

import 'connect_window.dart';

class WindowsStorageSetup extends StatefulWidget {
  const WindowsStorageSetup({
    super.key,
    required this.request,
    required this.windowsRequest,
    this.onChanged,
  });

  final ConnectionsRequest request;
  final ConnectionsRequest windowsRequest;
  final Future<void> Function()? onChanged;

  @override
  State<WindowsStorageSetup> createState() => _WindowsStorageSetupState();
}

class _WindowsStorageSetupState extends State<WindowsStorageSetup> {
  final _path = TextEditingController();
  String? _machineId, _machineName, _error;
  List<String> _roots = [];
  bool _connected = false, _busy = true;
  int _generation = 0;

  @override
  void initState() {
    super.initState();
    unawaited(_load());
  }

  @override
  void didUpdateWidget(WindowsStorageSetup oldWidget) {
    super.didUpdateWidget(oldWidget);
    if (widget.request != oldWidget.request ||
        widget.windowsRequest != oldWidget.windowsRequest) {
      _machineId = null;
      _machineName = null;
      _roots = [];
      _connected = false;
      _path.clear();
      unawaited(_load());
    }
  }

  @override
  void dispose() {
    _path.dispose();
    super.dispose();
  }

  Future<void> _load() async {
    final generation = ++_generation;
    final request = widget.request;
    final windows = widget.windowsRequest;
    setState(() {
      _busy = true;
      _error = null;
    });
    try {
      final device = Map<String, dynamic>.from(await windows('device') as Map);
      final id = device['machineId'] as String;
      final name = device['machineName'] as String;
      final connections = await request('') as List;
      final connected = connections.any(
        (row) =>
            row['connectorId'] == 'windows' &&
            row['connectionId'] == id &&
            row['status'] == 'Connected',
      );
      final roots = connected
          ? List<String>.from(
              await windows('${Uri.encodeComponent(id)}/roots') as List,
            )
          : <String>[];
      if (!mounted || generation != _generation) return;
      setState(() {
        _machineId = id;
        _machineName = name;
        _connected = connected;
        _roots = roots;
      });
    } catch (_) {
      if (mounted && generation == _generation) {
        setState(
          () =>
              _error = 'Windows storage is unavailable. Refresh to try again.',
        );
      }
    } finally {
      if (mounted && generation == _generation) setState(() => _busy = false);
    }
  }

  Future<void> _change(String action, [String? path]) async {
    final generation = _generation;
    final id = _machineId!;
    final request = widget.request;
    final windows = widget.windowsRequest;
    final changed = widget.onChanged;
    setState(() {
      _busy = true;
      _error = null;
    });
    try {
      if (action == 'connect') {
        final result = await request(
          '${Uri.encodeComponent(id)}/connect',
          body: {
            'connectorId': 'windows',
            'authMethodId': 'device',
            'value': id,
          },
        ) as Map;
        if (result['status'] != 'Connected') {
          throw StateError('Connection failed');
        }
      } else {
        await windows(
          '${Uri.encodeComponent(id)}/roots/$action',
          body: {'path': path},
        );
      }
      if (!mounted || generation != _generation) return;
      _path.clear();
      await _load();
      if (mounted && _generation == generation + 1) await changed?.call();
    } catch (_) {
      if (mounted && generation == _generation) {
        setState(() {
          _busy = false;
          _error = action == 'connect'
              ? 'The computer could not connect. Please try again.'
              : 'The folder change failed. Use an existing full folder path on $_machineName.';
        });
      }
    }
  }

  @override
  Widget build(BuildContext context) => Column(
    crossAxisAlignment: CrossAxisAlignment.stretch,
    children: [
      Row(
        children: [
          Expanded(
            child: Text(
              'Windows storage',
              style: Theme.of(context).textTheme.titleMedium,
            ),
          ),
          IconButton(
            tooltip: 'Refresh Windows storage',
            onPressed: _busy ? null : _load,
            icon: const Icon(Icons.refresh),
          ),
        ],
      ),
      if (_busy) const LinearProgressIndicator(),
      if (_error != null)
        Text(
          _error!,
          style: TextStyle(color: Theme.of(context).colorScheme.error),
        ),
      if (_machineName != null) ...[
        Text('Computer: $_machineName'),
        const Text(
          'Folder paths refer to this workspace host, which may be a different computer from yours.',
        ),
        const SizedBox(height: 8),
        if (!_connected)
          FilledButton.icon(
            onPressed: _busy ? null : () => _change('connect'),
            icon: const Icon(Icons.computer),
            label: Text('Connect $_machineName'),
          )
        else ...[
          Text(
            'Allowed folders on $_machineName',
            style: Theme.of(context).textTheme.titleSmall,
          ),
          const Text(
            'Only folders you allow are available to Files and console commands.',
          ),
          if (_roots.isEmpty) const Text('No folders allowed yet.'),
          for (final root in _roots)
            Row(
              children: [
                Expanded(child: Text(root)),
                IconButton(
                  tooltip: 'Revoke $root',
                  onPressed: _busy ? null : () => _change('revoke', root),
                  icon: const Icon(Icons.remove_circle_outline),
                ),
              ],
            ),
          const SizedBox(height: 8),
          TextField(
            key: const Key('windows_folder_path'),
            controller: _path,
            enabled: !_busy,
            decoration: InputDecoration(
              labelText: 'Full folder path on $_machineName',
              hintText: r'C:\Folder',
              border: const OutlineInputBorder(),
            ),
          ),
          const SizedBox(height: 8),
          FilledButton.icon(
            onPressed: _busy
                ? null
                : () {
                    final path = _path.text.trim();
                    if (path.isEmpty) {
                      setState(
                        () => _error =
                            'Enter a full folder path on $_machineName.',
                      );
                      return;
                    }
                    unawaited(_change('allow', path));
                  },
            icon: const Icon(Icons.create_new_folder_outlined),
            label: const Text('Allow folder'),
          ),
        ],
      ],
    ],
  );
}
