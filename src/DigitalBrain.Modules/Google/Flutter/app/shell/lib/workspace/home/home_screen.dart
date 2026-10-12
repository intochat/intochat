import 'dart:async';

import 'package:digitalbrain_flutter/digitalbrain_flutter.dart';
import 'package:flutter/material.dart';

import '../apps/app_screen.dart';
import '../apps/apps_request.dart';
import 'app_detail_screen.dart';
import 'app_icon.dart';
import 'brain_graph_panel.dart';

// An app the shell runs itself rather than a brain app (C# files in developer mode).
class LocalApp {
  const LocalApp({required this.id, required this.title, required this.icon});
  final String id, title;
  final IconData icon;
}

// A window of an app neuron, open in this brain: what Home shows as running.
class RunningApp {
  const RunningApp({
    required this.windowId,
    required this.app,
    required this.title,
  });
  final String windowId, app, title;
}

// The start view: this brain's apps, then everything published to the Marketplace.
class HomeScreen extends StatefulWidget {
  const HomeScreen({
    super.key,
    required this.workspaceId,
    this.request,
    required this.onOpen,
    this.onLaunchLocal,
    this.localApps = const [],
    this.loadIcon,
    this.onAppsChanged,
    this.running = const [],
    this.onFocus,
    this.onStop,
    this.surfaceBuilder,
    this.infrastructure,
    this.watchBrain,
    this.loadSignals,
    this.decorateIcon,
  });

  final String workspaceId;
  final AppsRequest? request;
  final Future<void> Function(String name) onOpen;
  final ValueChanged<String>? onLaunchLocal;
  final List<LocalApp> localApps;
  final IconLoader? loadIcon;
  final VoidCallback? onAppsChanged;
  final List<RunningApp> running;
  final ValueChanged<String>? onFocus, onStop;
  // A small, non-interactive render of the app's surface for its tile.
  final Widget Function(RunningApp app)? surfaceBuilder;
  final Widget? infrastructure;
  // The brain snapshot feed and its signal journal; Home draws the graph when the feed is given.
  final Stream<BrainSnapshot> Function()? watchBrain;
  final SignalsLoader? loadSignals;
  // Lets the shell dress an installed app's icon, e.g. with a badge.
  final Widget Function(InstalledAppSummary app, Widget icon)? decorateIcon;

  @override
  State<HomeScreen> createState() => _HomeScreenState();
}

class _HomeScreenState extends State<HomeScreen> {
  List<InstalledAppSummary> _apps = const [];
  List<MarketplaceEntry> _market = const [];
  String? _error;
  bool _loading = false;

  String get _brain => Uri.encodeComponent(widget.workspaceId);

  static List<Map<String, dynamic>> _maps(dynamic value) => value is List
      ? [for (final item in value) Map<String, dynamic>.from(item as Map)]
      : const [];

  @override
  void initState() {
    super.initState();
    unawaited(_load());
  }

  Future<void> _load() async {
    final request = widget.request;
    if (request == null) return;
    setState(() {
      _loading = true;
      _error = null;
    });
    try {
      final results = await Future.wait([
        request('GET', '/brains/$_brain/apps'),
        request('GET', '/brains/$_brain/marketplace'),
      ]);
      if (!mounted) return;
      setState(() {
        _apps = _maps(results[0]).map(InstalledAppSummary.fromJson).toList();
        _market = _maps(results[1]).map(MarketplaceEntry.fromJson).toList();
      });
    } catch (error) {
      if (mounted) setState(() => _error = error.toString());
    } finally {
      if (mounted) setState(() => _loading = false);
    }
  }

  Future<void> _afterChange() async {
    await _load();
    widget.onAppsChanged?.call();
  }

  Future<void> _details({
    MarketplaceListing? listing,
    String? installed,
  }) async {
    await Navigator.of(context).push(
      MaterialPageRoute<void>(
        builder: (_) => AppDetailScreen(
          workspaceId: widget.workspaceId,
          request: widget.request!,
          listing: listing,
          installedName: installed,
          onOpen: widget.onOpen,
          loadIcon: widget.loadIcon,
        ),
      ),
    );
    if (mounted) await _afterChange();
  }

  Future<void> _create() async {
    await Navigator.of(context).push(
      MaterialPageRoute<void>(
        builder: (_) => AppScreen(
          workspaceId: widget.workspaceId,
          request: widget.request!,
        ),
      ),
    );
    if (mounted) await _afterChange();
  }

  Widget _dressed(InstalledAppSummary app, Widget icon) =>
      widget.decorateIcon?.call(app, icon) ?? icon;

  Future<void> _tapInstalled(InstalledAppSummary app) async {
    if (app.status != 'Live') {
      await _details(installed: app.id);
      return;
    }
    try {
      final view = await widget.request!(
        'GET',
        '/brains/$_brain/apps/${Uri.encodeComponent(app.id)}',
      );
      if (!mounted) return;
      final manifest = AppManifest.fromJson(
        Map<String, dynamic>.from(view['manifest'] as Map),
      );
      if (manifest.entry != null) {
        await widget.onOpen(app.id);
      } else {
        await _details(installed: app.id);
      }
    } catch (error) {
      if (mounted) {
        ScaffoldMessenger.of(context).showSnackBar(
          SnackBar(content: Text('Could not open ${app.title}: $error')),
        );
      }
    }
  }

  @override
  Widget build(BuildContext context) {
    final textTheme = Theme.of(context).textTheme;
    final online = widget.request != null;
    return ListView(
      padding: const EdgeInsets.fromLTRB(24, 16, 24, 24),
      children: [
        if (_loading) const LinearProgressIndicator(),
        if (_error != null)
          Padding(
            padding: const EdgeInsets.only(bottom: 12),
            child: Text(
              _error!,
              key: const ValueKey('home-error'),
              style: TextStyle(color: Theme.of(context).colorScheme.error),
            ),
          ),
        if (widget.running.isNotEmpty) ...[
          Text(
            'Running',
            key: const ValueKey('home-running'),
            style: textTheme.titleLarge,
          ),
          const SizedBox(height: 12),
          Wrap(
            spacing: 16,
            runSpacing: 16,
            children: [
              for (final app in widget.running)
                _RunningTile(
                  key: ValueKey('running-${app.app}'),
                  app: app,
                  icon: AppIcon(
                    title: app.title,
                    path:
                        '/brains/$_brain/apps/${Uri.encodeComponent(app.app)}/icon',
                    load: widget.loadIcon,
                    size: 28,
                  ),
                  preview: widget.surfaceBuilder?.call(app),
                  onTap: () => widget.onFocus?.call(app.windowId),
                  onStop: () => widget.onStop?.call(app.windowId),
                ),
            ],
          ),
          const SizedBox(height: 32),
        ],
        if (widget.infrastructure != null) ...[
          Text(
            'Infrastructure',
            key: const ValueKey('home-infrastructure'),
            style: textTheme.titleLarge,
          ),
          const SizedBox(height: 12),
          widget.infrastructure!,
          const SizedBox(height: 32),
        ],
        if (widget.watchBrain != null) ...[
          Text(
            'Brain',
            key: const ValueKey('home-brain'),
            style: textTheme.titleLarge,
          ),
          const SizedBox(height: 12),
          BrainGraphPanel(
            watch: widget.watchBrain!,
            loadSignals: widget.loadSignals,
          ),
          const SizedBox(height: 32),
        ],
        Text(
          'My apps',
          key: const ValueKey('home-my-apps'),
          style: textTheme.titleLarge,
        ),
        const SizedBox(height: 12),
        Wrap(
          spacing: 16,
          runSpacing: 16,
          children: [
            for (final app in widget.localApps)
              _Tile(
                key: ValueKey('local-${app.id}'),
                title: app.title,
                icon: Icon(app.icon, size: 40),
                onTap: () => widget.onLaunchLocal?.call(app.id),
              ),
            for (final app in _apps)
              _Tile(
                key: ValueKey('app-${app.id}'),
                title: app.title,
                subtitle: app.status == 'Live' ? null : app.status,
                badge: app.updateAvailable ? 'Update' : null,
                icon: _dressed(
                  app,
                  AppIcon(
                    title: app.title,
                    path: app.icon == null
                        ? null
                        : '/brains/$_brain/apps/${Uri.encodeComponent(app.id)}/icon',
                    load: widget.loadIcon,
                  ),
                ),
                onTap: () => _tapInstalled(app),
                onDetails: () => _details(installed: app.id),
                detailsKey: ValueKey('details-${app.id}'),
              ),
            if (online)
              _Tile(
                key: const ValueKey('create-app'),
                title: 'Create app',
                icon: const Icon(Icons.add, size: 40),
                onTap: _create,
              ),
          ],
        ),
        if (online) ...[
          const SizedBox(height: 32),
          Text(
            'Marketplace',
            key: const ValueKey('home-marketplace'),
            style: textTheme.titleLarge,
          ),
          const SizedBox(height: 12),
          if (_market.isEmpty && !_loading)
            const Text('Nothing is published yet.')
          else
            Wrap(
              spacing: 16,
              runSpacing: 16,
              children: [
                for (final entry in _market)
                  _Tile(
                    key: ValueKey(
                      'market-${entry.listing.publisher}/${entry.listing.name}',
                    ),
                    title: entry.listing.title,
                    subtitle: entry.listing.description,
                    badge: entry.installed ? 'Installed' : null,
                    icon: AppIcon(
                      title: entry.listing.title,
                      path: entry.listing.icon == null
                          ? null
                          : '/brains/$_brain/marketplace/${Uri.encodeComponent(entry.listing.publisher)}/${Uri.encodeComponent(entry.listing.name)}/icon',
                      load: widget.loadIcon,
                    ),
                    onTap: () => _details(listing: entry.listing),
                  ),
              ],
            ),
        ],
      ],
    );
  }
}

class _Tile extends StatelessWidget {
  const _Tile({
    super.key,
    required this.title,
    required this.icon,
    required this.onTap,
    this.subtitle,
    this.badge,
    this.onDetails,
    this.detailsKey,
  });

  final String title;
  final String? subtitle, badge;
  final Widget icon;
  final VoidCallback onTap;
  final VoidCallback? onDetails;
  final Key? detailsKey;

  @override
  Widget build(BuildContext context) => SizedBox(
    width: 168,
    height: 168,
    child: Card(
      clipBehavior: Clip.antiAlias,
      child: InkWell(
        onTap: onTap,
        child: Stack(
          children: [
            Padding(
              padding: const EdgeInsets.all(12),
              child: Column(
                crossAxisAlignment: CrossAxisAlignment.start,
                children: [
                  icon,
                  const Spacer(),
                  Text(
                    title,
                    maxLines: 2,
                    overflow: TextOverflow.ellipsis,
                    style: Theme.of(context).textTheme.titleSmall,
                  ),
                  if (subtitle != null && subtitle!.isNotEmpty)
                    Text(
                      subtitle!,
                      maxLines: 2,
                      overflow: TextOverflow.ellipsis,
                      style: Theme.of(context).textTheme.bodySmall,
                    ),
                ],
              ),
            ),
            if (badge != null)
              Positioned(
                top: 8,
                right: onDetails == null ? 8 : 40,
                child: Chip(
                  label: Text(badge!),
                  visualDensity: VisualDensity.compact,
                ),
              ),
            if (onDetails != null)
              Positioned(
                top: 0,
                right: 0,
                child: IconButton(
                  key: detailsKey,
                  tooltip: 'Details',
                  icon: const Icon(Icons.info_outline, size: 18),
                  onPressed: onDetails,
                ),
              ),
          ],
        ),
      ),
    ),
  );
}

class _RunningTile extends StatelessWidget {
  const _RunningTile({
    super.key,
    required this.app,
    required this.icon,
    required this.onTap,
    required this.onStop,
    this.preview,
  });

  final RunningApp app;
  final Widget icon;
  final Widget? preview;
  final VoidCallback onTap, onStop;

  @override
  Widget build(BuildContext context) => SizedBox(
    width: 260,
    height: 200,
    child: Card(
      clipBehavior: Clip.antiAlias,
      child: InkWell(
        onTap: onTap,
        child: Column(
          crossAxisAlignment: CrossAxisAlignment.stretch,
          children: [
            SizedBox(
              height: 44,
              child: Row(
                children: [
                  const SizedBox(width: 10),
                  icon,
                  const SizedBox(width: 8),
                  Expanded(
                    child: Text(
                      app.title,
                      maxLines: 1,
                      overflow: TextOverflow.ellipsis,
                      style: Theme.of(context).textTheme.titleSmall,
                    ),
                  ),
                  IconButton(
                    key: ValueKey('stop-${app.app}'),
                    tooltip: 'Stop',
                    icon: const Icon(Icons.close, size: 18),
                    onPressed: onStop,
                  ),
                ],
              ),
            ),
            Expanded(
              child: IgnorePointer(
                child: ExcludeSemantics(
                  child: preview == null
                      ? const SizedBox.shrink()
                      : FittedBox(
                          fit: BoxFit.cover,
                          alignment: Alignment.topLeft,
                          clipBehavior: Clip.hardEdge,
                          child: SizedBox(
                            width: 720,
                            height: 480,
                            child: preview,
                          ),
                        ),
                ),
              ),
            ),
          ],
        ),
      ),
    ),
  );
}
