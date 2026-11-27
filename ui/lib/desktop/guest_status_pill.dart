import 'dart:ui';

import 'package:flutter/material.dart';
import 'package:flutter/services.dart';
import 'package:intochat_os_flutter_shell/intochat_os_ui.dart';

const String kReleaseDownloadBase =
    'https://github.com/intochat/intochat/releases/latest/download';

/// Formats the remaining lifetime for a Day-0 guest brain status pill.
/// Falls back to `24h` when no explicit duration is provided.
String formatGuestRemaining(Duration? remaining) {
  if (remaining == null) return '24h';
  if (remaining <= Duration.zero) return 'Expiring';
  final totalMinutes = remaining.inMinutes;
  if (totalMinutes < 60) {
    return '${totalMinutes.clamp(1, 59)}m';
  }
  final hours = remaining.inHours;
  final minutes = totalMinutes % 60;
  if (minutes == 0 || hours >= 24) {
    return '${hours}h';
  }
  return '${hours}h ${minutes}m';
}

@immutable
class GuestDownloadAsset {
  const GuestDownloadAsset({
    required this.filename,
    required this.label,
    this.primary = false,
  });

  final String filename;
  final String label;
  final bool primary;

  Uri get uri => Uri.parse('$kReleaseDownloadBase/$filename');
}

@immutable
class GuestPlatformDownload {
  const GuestPlatformDownload({
    required this.id,
    required this.name,
    required this.subtitle,
    required this.icon,
    required this.assets,
    this.commandLabel,
    this.command,
    this.pwaHint,
  });

  final String id;
  final String name;
  final String subtitle;
  final IconData icon;
  final List<GuestDownloadAsset> assets;
  final String? commandLabel;
  final String? command;
  final String? pwaHint;
}

const List<GuestPlatformDownload> kGuestPlatformDownloads = [
  GuestPlatformDownload(
    id: 'windows',
    name: 'Windows',
    subtitle: 'Windows 10/11 · x64',
    icon: LucideIcons.monitor,
    assets: [
      GuestDownloadAsset(
        filename: 'IntoChat-Setup-win-x64.exe',
        label: 'Installer',
        primary: true,
      ),
      GuestDownloadAsset(
        filename: 'intochat-win-x64.zip',
        label: 'Portable ZIP',
      ),
    ],
    commandLabel: '1-line PowerShell',
    command: 'irm $kReleaseDownloadBase/install.ps1 | iex',
  ),
  GuestPlatformDownload(
    id: 'macos',
    name: 'macOS',
    subtitle: 'Apple Silicon · arm64',
    icon: LucideIcons.laptop,
    assets: [
      GuestDownloadAsset(
        filename: 'IntoChat-macos-arm64.dmg',
        label: 'Apple Silicon DMG',
        primary: true,
      ),
      GuestDownloadAsset(
        filename: 'intochat-osx-arm64.tar.gz',
        label: 'Archive',
      ),
    ],
    commandLabel: '1-line Terminal',
    command: 'curl -fsSL $kReleaseDownloadBase/install.sh | sh',
  ),
  GuestPlatformDownload(
    id: 'linux',
    name: 'Linux',
    subtitle: 'Desktop & Server · x64',
    icon: LucideIcons.terminal,
    assets: [
      GuestDownloadAsset(
        filename: 'IntoChat-linux-x86_64.AppImage',
        label: 'AppImage',
        primary: true,
      ),
      GuestDownloadAsset(
        filename: 'intochat-linux-amd64.deb',
        label: 'Debian / Ubuntu',
      ),
      GuestDownloadAsset(
        filename: 'intochat-linux-x64.tar.gz',
        label: 'Tarball',
      ),
    ],
  ),
  GuestPlatformDownload(
    id: 'android',
    name: 'Android',
    subtitle: 'Direct APK & Chrome PWA',
    icon: LucideIcons.smartphone,
    assets: [
      GuestDownloadAsset(
        filename: 'intochat-android.apk',
        label: 'Android APK',
        primary: true,
      ),
    ],
    pwaHint: 'Or install in Chrome: open menu → Add to Home screen for fullscreen PWA.',
  ),
  GuestPlatformDownload(
    id: 'ios',
    name: 'iOS',
    subtitle: '2-Tap PWA & Unsigned IPA',
    icon: LucideIcons.tabletSmartphone,
    assets: [
      GuestDownloadAsset(
        filename: 'intochat-ios-unsigned.ipa',
        label: 'Unsigned IPA',
      ),
    ],
    pwaHint: '2-tap Safari PWA: tap Share (↑) → Add to Home Screen. Runs fullscreen with zero App Store friction.',
  ),
];

/// Sleek, non-blocking top status pill rendered in the shell chrome while the
/// active session is running on an ephemeral/guest brain.
class GuestStatusPill extends StatelessWidget {
  const GuestStatusPill({
    super.key,
    this.remaining,
    this.onSaveBrain,
    this.onDownloadApp,
  });

  final Duration? remaining;
  final VoidCallback? onSaveBrain;
  final VoidCallback? onDownloadApp;

  @override
  Widget build(BuildContext context) {
    final c = UiColors.of(context);
    final ttlLabel = formatGuestRemaining(remaining);
    final statusText = 'Guest Brain · Temp RAM · $ttlLabel';

    final pillRadius = BorderRadius.circular(20);

    final content = Container(
      key: const ValueKey('guest-status-pill'),
      padding: const EdgeInsets.symmetric(horizontal: 12, vertical: 6),
      decoration: BoxDecoration(
        color: c.chrome,
        borderRadius: pillRadius,
        border: Border.all(
          color: c.isGlass ? c.specularBorder : c.chromeBorder,
        ),
        boxShadow: UiShadows.md(c),
      ),
      child: Wrap(
        alignment: WrapAlignment.center,
        crossAxisAlignment: WrapCrossAlignment.center,
        spacing: 8,
        runSpacing: 6,
        children: [
          Row(
            mainAxisSize: MainAxisSize.min,
            children: [
              Container(
                width: 22,
                height: 22,
                decoration: BoxDecoration(
                  color: c.workingBg,
                  shape: BoxShape.circle,
                ),
                alignment: Alignment.center,
                child: Icon(LucideIcons.zap, size: 12, color: c.working),
              ),
              const SizedBox(width: 7),
              Flexible(
                child: Text(
                  statusText,
                  maxLines: 1,
                  overflow: TextOverflow.ellipsis,
                  style: UiType.textStyle(
                    12,
                    weight: FontWeight.w600,
                    color: c.textStrong,
                    tnum: true,
                  ),
                ),
              ),
            ],
          ),
          Wrap(
            alignment: WrapAlignment.center,
            crossAxisAlignment: WrapCrossAlignment.center,
            spacing: 6,
            runSpacing: 6,
            children: [
              _PillActionButton(
                key: const ValueKey('guest-pill-save-brain'),
                label: 'Save My Brain →',
                primary: true,
                onTap: onSaveBrain,
              ),
              _PillActionButton(
                key: const ValueKey('guest-pill-download-app'),
                label: 'Download App ↓',
                primary: false,
                onTap: onDownloadApp,
              ),
            ],
          ),
        ],
      ),
    );

    if (c.blurSigma > 0) {
      return RepaintBoundary(
        child: ClipRRect(
          borderRadius: pillRadius,
          child: BackdropFilter(
            filter: ImageFilter.blur(sigmaX: c.blurSigma, sigmaY: c.blurSigma),
            child: content,
          ),
        ),
      );
    }

    return RepaintBoundary(child: content);
  }
}

class _PillActionButton extends StatefulWidget {
  const _PillActionButton({
    super.key,
    required this.label,
    required this.primary,
    required this.onTap,
  });

  final String label;
  final bool primary;
  final VoidCallback? onTap;

  @override
  State<_PillActionButton> createState() => _PillActionButtonState();
}

class _PillActionButtonState extends State<_PillActionButton> {
  bool _hover = false;

  @override
  Widget build(BuildContext context) {
    final c = UiColors.of(context);
    final bg = widget.primary
        ? (_hover ? c.accentHover : c.accent)
        : (_hover ? c.barHoverOverlay : c.surfaceSunk);
    final fg = widget.primary ? c.onAccent : c.textStrong;

    return Semantics(
      button: true,
      label: widget.label,
      child: MouseRegion(
        cursor: widget.onTap != null
            ? SystemMouseCursors.click
            : SystemMouseCursors.basic,
        onEnter: (_) => setState(() => _hover = true),
        onExit: (_) => setState(() => _hover = false),
        child: GestureDetector(
          behavior: HitTestBehavior.opaque,
          onTap: widget.onTap,
          child: AnimatedContainer(
            duration: UiTokens.of(context).fast,
            padding: const EdgeInsets.symmetric(horizontal: 10, vertical: 5),
            decoration: BoxDecoration(
              color: bg,
              borderRadius: BorderRadius.circular(UiTokens.of(context).pill),
              border: widget.primary
                  ? null
                  : Border.all(color: c.borderHairline),
            ),
            child: Text(
              widget.label,
              style: UiType.textStyle(11.5, weight: FontWeight.w600, color: fg),
            ),
          ),
        ),
      ),
    );
  }
}

/// Clean in-shell modal presenting all 5 IntoChat OS platform downloads,
/// 1-line install commands, 2-tap iOS PWA guidance, and a `/download` hub link.
class DownloadAppModal extends StatefulWidget {
  const DownloadAppModal({
    super.key,
    required this.onClose,
    this.onOpenUrl,
    this.downloadHubUri,
  });

  final VoidCallback onClose;
  final void Function(Uri url)? onOpenUrl;
  final Uri? downloadHubUri;

  @override
  State<DownloadAppModal> createState() => _DownloadAppModalState();
}

class _DownloadAppModalState extends State<DownloadAppModal> {
  String? _copiedPlatformId;

  String? _detectedPlatformId(TargetPlatform platform) => switch (platform) {
    TargetPlatform.windows => 'windows',
    TargetPlatform.macOS => 'macos',
    TargetPlatform.linux => 'linux',
    TargetPlatform.android => 'android',
    TargetPlatform.iOS => 'ios',
    TargetPlatform.fuchsia => null,
  };

  Future<void> _copyCommand(String platformId, String command) async {
    await Clipboard.setData(ClipboardData(text: command));
    if (!mounted) return;
    setState(() => _copiedPlatformId = platformId);
    Future<void>.delayed(const Duration(milliseconds: 1600), () {
      if (mounted && _copiedPlatformId == platformId) {
        setState(() => _copiedPlatformId = null);
      }
    });
  }

  @override
  Widget build(BuildContext context) {
    final c = UiColors.of(context);
    final detectedId = _detectedPlatformId(Theme.of(context).platform);
    final ordered = [...kGuestPlatformDownloads];
    if (detectedId != null) {
      final idx = ordered.indexWhere((p) => p.id == detectedId);
      if (idx > 0) {
        final match = ordered.removeAt(idx);
        ordered.insert(0, match);
      }
    }
    final hubUri =
        widget.downloadHubUri ?? Uri.parse('https://intochat.io/download');

    return GestureDetector(
      key: const ValueKey('download-app-modal'),
      behavior: HitTestBehavior.opaque,
      onTap: widget.onClose,
      child: ColoredBox(
        color: c.scrim,
        child: Center(
          child: GestureDetector(
            onTap: () {},
            child: ConstrainedBox(
              constraints: const BoxConstraints(maxWidth: 720, maxHeight: 620),
              child: Padding(
                padding: const EdgeInsets.all(16),
                child: Material(
                  color: c.surfaceCard,
                  shape: RoundedRectangleBorder(
                    borderRadius: BorderRadius.circular(
                      UiTokens.of(context).panel,
                    ),
                    side: BorderSide(color: c.borderHairline),
                  ),
                  elevation: 0,
                  child: Container(
                    decoration: BoxDecoration(
                      color: c.surfaceCard,
                      borderRadius: BorderRadius.circular(
                        UiTokens.of(context).panel,
                      ),
                      boxShadow: UiShadows.xl(c),
                    ),
                    child: Column(
                      mainAxisSize: MainAxisSize.min,
                      children: [
                        Padding(
                          padding: const EdgeInsets.fromLTRB(20, 18, 14, 14),
                          child: Row(
                            children: [
                              Container(
                                width: 34,
                                height: 34,
                                decoration: BoxDecoration(
                                  color: c.accentTint,
                                  borderRadius: BorderRadius.circular(
                                    UiTokens.of(context).sm,
                                  ),
                                ),
                                alignment: Alignment.center,
                                child: Icon(
                                  LucideIcons.download,
                                  size: 18,
                                  color: c.accent,
                                ),
                              ),
                              const SizedBox(width: 12),
                              Expanded(
                                child: Column(
                                  crossAxisAlignment: CrossAxisAlignment.start,
                                  children: [
                                    Text(
                                      'Download IntoChat OS',
                                      style: UiType.textStyle(
                                        16,
                                        weight: FontWeight.w700,
                                        color: c.textStrong,
                                      ),
                                    ),
                                    const SizedBox(height: 2),
                                    Text(
                                      'Native builds for all 5 platforms · Official GitHub Releases',
                                      style: UiType.textStyle(
                                        12,
                                        color: c.textMuted,
                                      ),
                                    ),
                                  ],
                                ),
                              ),
                              IconButton(
                                key: const ValueKey('download-modal-close'),
                                tooltip: 'Close',
                                onPressed: widget.onClose,
                                icon: Icon(
                                  LucideIcons.x,
                                  size: 18,
                                  color: c.textMuted,
                                ),
                              ),
                            ],
                          ),
                        ),
                        Divider(height: 1, color: c.borderHairline),
                        Flexible(
                          child: SingleChildScrollView(
                            padding: const EdgeInsets.all(16),
                            child: Column(
                              children: [
                                for (var i = 0; i < ordered.length; i++) ...[
                                  if (i > 0) const SizedBox(height: 10),
                                  _PlatformDownloadCard(
                                    platform: ordered[i],
                                    detected: ordered[i].id == detectedId,
                                    copied: _copiedPlatformId == ordered[i].id,
                                    onOpenUrl: widget.onOpenUrl,
                                    onCopyCommand: ordered[i].command == null
                                        ? null
                                        : () => _copyCommand(
                                            ordered[i].id,
                                            ordered[i].command!,
                                          ),
                                  ),
                                ],
                              ],
                            ),
                          ),
                        ),
                        Divider(height: 1, color: c.borderHairline),
                        Padding(
                          padding: const EdgeInsets.symmetric(
                            horizontal: 20,
                            vertical: 12,
                          ),
                          child: Row(
                            children: [
                              Expanded(
                                child: Text(
                                  'Want to share this page? Open /download in your browser.',
                                  style: UiType.textStyle(
                                    12,
                                    color: c.textMuted,
                                  ),
                                ),
                              ),
                              const SizedBox(width: 12),
                              _PillActionButton(
                                key: const ValueKey('download-modal-open-hub'),
                                label: 'Open /download Hub ↗',
                                primary: true,
                                onTap: widget.onOpenUrl == null
                                    ? null
                                    : () => widget.onOpenUrl!(hubUri),
                              ),
                            ],
                          ),
                        ),
                      ],
                    ),
                  ),
                ),
              ),
            ),
          ),
        ),
      ),
    );
  }
}

class _PlatformDownloadCard extends StatelessWidget {
  const _PlatformDownloadCard({
    required this.platform,
    required this.detected,
    required this.copied,
    required this.onOpenUrl,
    required this.onCopyCommand,
  });

  final GuestPlatformDownload platform;
  final bool detected;
  final bool copied;
  final void Function(Uri url)? onOpenUrl;
  final VoidCallback? onCopyCommand;

  @override
  Widget build(BuildContext context) {
    final c = UiColors.of(context);

    return Container(
      key: ValueKey('download-platform-${platform.id}'),
      width: double.infinity,
      padding: const EdgeInsets.all(14),
      decoration: BoxDecoration(
        color: c.surfacePage,
        borderRadius: BorderRadius.circular(UiTokens.of(context).card),
        border: Border.all(
          color: detected ? c.accent : c.borderHairline,
          width: detected ? 1.5 : 1,
        ),
      ),
      child: Column(
        crossAxisAlignment: CrossAxisAlignment.start,
        children: [
          Row(
            children: [
              Container(
                width: 30,
                height: 30,
                decoration: BoxDecoration(
                  color: detected ? c.accentTint : c.surfaceSunk,
                  borderRadius: BorderRadius.circular(UiTokens.of(context).xs),
                ),
                alignment: Alignment.center,
                child: Icon(
                  platform.icon,
                  size: 16,
                  color: detected ? c.accent : c.textStrong,
                ),
              ),
              const SizedBox(width: 10),
              Expanded(
                child: Column(
                  crossAxisAlignment: CrossAxisAlignment.start,
                  children: [
                    Text(
                      platform.name,
                      style: UiType.textStyle(
                        13.5,
                        weight: FontWeight.w700,
                        color: c.textStrong,
                      ),
                    ),
                    Text(
                      platform.subtitle,
                      style: UiType.textStyle(11.5, color: c.textMuted),
                    ),
                  ],
                ),
              ),
              if (detected)
                Container(
                  padding: const EdgeInsets.symmetric(
                    horizontal: 8,
                    vertical: 3,
                  ),
                  decoration: BoxDecoration(
                    color: c.doneBg,
                    borderRadius: BorderRadius.circular(
                      UiTokens.of(context).pill,
                    ),
                  ),
                  child: Text(
                    'This device',
                    style: UiType.textStyle(
                      11,
                      weight: FontWeight.w600,
                      color: c.done,
                    ),
                  ),
                ),
            ],
          ),
          const SizedBox(height: 10),
          Wrap(
            spacing: 8,
            runSpacing: 8,
            children: [
              for (final asset in platform.assets)
                _AssetChip(
                  asset: asset,
                  onTap: onOpenUrl == null ? null : () => onOpenUrl!(asset.uri),
                ),
            ],
          ),
          if (platform.command != null) ...[
            const SizedBox(height: 10),
            Container(
              width: double.infinity,
              padding: const EdgeInsets.symmetric(horizontal: 10, vertical: 8),
              decoration: BoxDecoration(
                color: c.surfaceSunk,
                borderRadius: BorderRadius.circular(UiTokens.of(context).sm),
                border: Border.all(color: c.borderHairline),
              ),
              child: Row(
                children: [
                  Expanded(
                    child: Column(
                      crossAxisAlignment: CrossAxisAlignment.start,
                      children: [
                        Text(
                          platform.commandLabel ?? 'Install command',
                          style: UiType.textStyle(
                            10.5,
                            weight: FontWeight.w600,
                            color: c.textMuted,
                          ),
                        ),
                        const SizedBox(height: 2),
                        SelectableText(
                          platform.command!,
                          style: UiType.codeStyle(11.5, color: c.textStrong),
                        ),
                      ],
                    ),
                  ),
                  const SizedBox(width: 8),
                  _PillActionButton(
                    key: ValueKey('download-copy-${platform.id}'),
                    label: copied ? 'Copied' : 'Copy',
                    primary: false,
                    onTap: onCopyCommand,
                  ),
                ],
              ),
            ),
          ],
          if (platform.pwaHint != null) ...[
            const SizedBox(height: 10),
            Container(
              width: double.infinity,
              padding: const EdgeInsets.symmetric(horizontal: 10, vertical: 8),
              decoration: BoxDecoration(
                color: c.workingBg,
                borderRadius: BorderRadius.circular(UiTokens.of(context).sm),
                border: Border.all(color: c.borderHairline),
              ),
              child: Text(
                platform.pwaHint!,
                style: UiType.textStyle(11.5, color: c.textBody),
              ),
            ),
          ],
        ],
      ),
    );
  }
}

class _AssetChip extends StatefulWidget {
  const _AssetChip({required this.asset, required this.onTap});

  final GuestDownloadAsset asset;
  final VoidCallback? onTap;

  @override
  State<_AssetChip> createState() => _AssetChipState();
}

class _AssetChipState extends State<_AssetChip> {
  bool _hover = false;

  @override
  Widget build(BuildContext context) {
    final c = UiColors.of(context);
    final bg = widget.asset.primary
        ? (_hover ? c.accentHover : c.accent)
        : (_hover ? c.barHoverOverlay : c.surfaceCard);
    final fg = widget.asset.primary ? c.onAccent : c.textStrong;

    return MouseRegion(
      cursor: widget.onTap != null
          ? SystemMouseCursors.click
          : SystemMouseCursors.basic,
      onEnter: (_) => setState(() => _hover = true),
      onExit: (_) => setState(() => _hover = false),
      child: GestureDetector(
        key: ValueKey('download-asset-${widget.asset.filename}'),
        onTap: widget.onTap,
        child: AnimatedContainer(
          duration: UiTokens.of(context).fast,
          padding: const EdgeInsets.symmetric(horizontal: 10, vertical: 6),
          decoration: BoxDecoration(
            color: bg,
            borderRadius: BorderRadius.circular(UiTokens.of(context).sm),
            border: widget.asset.primary
                ? null
                : Border.all(color: c.borderHairline),
          ),
          child: Row(
            mainAxisSize: MainAxisSize.min,
            children: [
              Icon(LucideIcons.download, size: 12, color: fg),
              const SizedBox(width: 6),
              Text(
                widget.asset.filename,
                style: UiType.textStyle(
                  11.5,
                  weight: FontWeight.w600,
                  color: fg,
                ),
              ),
              const SizedBox(width: 6),
              Text(
                widget.asset.label,
                style: UiType.textStyle(
                  10.5,
                  color: fg.withValues(
                    alpha: widget.asset.primary ? 0.85 : 0.7,
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

/// Shared Day-0 guest status pill and download modal overlay layers used by
/// both `WorkspaceApp` (non-desktop roots) and `LiveDesktopShell` (Compact,
/// Medium, and Expanded shells).
class GuestChromeOverlay extends StatelessWidget {
  const GuestChromeOverlay({
    super.key,
    required this.showPill,
    required this.downloadModalOpen,
    required this.onDownloadApp,
    required this.onCloseDownloadModal,
    this.remaining,
    this.onSaveBrain,
    this.onOpenUrl,
    this.downloadHubUri,
    this.useSafeArea = false,
  });

  final bool showPill;
  final bool downloadModalOpen;
  final VoidCallback onDownloadApp;
  final VoidCallback onCloseDownloadModal;
  final Duration? remaining;
  final VoidCallback? onSaveBrain;
  final void Function(Uri url)? onOpenUrl;
  final Uri? downloadHubUri;
  final bool useSafeArea;

  @override
  Widget build(BuildContext context) {
    if (!showPill && !downloadModalOpen) {
      return const SizedBox.shrink();
    }
    final pill = Align(
      alignment: Alignment.topCenter,
      child: GuestStatusPill(
        remaining: remaining,
        onSaveBrain: onSaveBrain,
        onDownloadApp: onDownloadApp,
      ),
    );
    return Stack(
      clipBehavior: Clip.none,
      children: [
        if (showPill)
          Positioned(
            top: 10,
            left: 16,
            right: 16,
            child: useSafeArea ? SafeArea(child: pill) : pill,
          ),
        if (downloadModalOpen)
          Positioned.fill(
            child: DownloadAppModal(
              onClose: onCloseDownloadModal,
              onOpenUrl: onOpenUrl,
              downloadHubUri: downloadHubUri,
            ),
          ),
      ],
    );
  }
}
