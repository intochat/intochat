import 'dart:math' as math;
import 'dart:ui';

import 'package:flutter/material.dart';
import 'package:intochat_os_flutter_shell/intochat_os_ui.dart';

import 'os_bar.dart';

part 'desktop_bar_popovers.dart';

enum DesktopPopoverKind { none, clock, tokens, notifications, dockMenu }

enum DesktopSystemSheetKind {
  none,
  secure,
  profile,
  install,
  reconsent,
  conflict,
  twoFactor,
}

@immutable
class DesktopWindowSummary {
  const DesktopWindowSummary({
    required this.windowId,
    required this.numericId,
    required this.appId,
    required this.title,
    this.minimized = false,
    this.isPinned = true,
  });

  final String windowId;
  final int numericId;
  final String appId;
  final String title;
  final bool minimized;
  final bool isPinned;
}

/// `Alt+\`` (`⌥\``) visual MRU window thumbnail switcher with `IcAppIcon` previews.
class AltBacktickWindowSwitcher extends StatelessWidget {
  const AltBacktickWindowSwitcher({
    super.key,
    required this.windows,
    required this.selectedIndex,
    required this.onSelectWindow,
    required this.onCancel,
  });

  final List<DesktopWindowSummary> windows;
  final int selectedIndex;
  final ValueChanged<DesktopWindowSummary> onSelectWindow;
  final VoidCallback onCancel;

  @override
  Widget build(BuildContext context) {
    if (windows.isEmpty) return const SizedBox.shrink();
    final c = IcTheme.of(context);

    return GestureDetector(
      key: const ValueKey('desktop-window-switcher'),
      behavior: HitTestBehavior.opaque,
      onTap: onCancel,
      child: ColoredBox(
        color: Color.fromRGBO(0, 0, 0, c.isDark ? 0.42 : 0.22),
        child: Center(
          child: ClipRRect(
            borderRadius: BorderRadius.circular(IcRadius.window),
            child: BackdropFilter(
              filter: ImageFilter.blur(sigmaX: 20, sigmaY: 20),
              child: Container(
                padding: const EdgeInsets.all(14),
                decoration: BoxDecoration(
                  color: c.surfaceElevated.withValues(alpha: 0.94),
                  borderRadius: BorderRadius.circular(IcRadius.window),
                  border: Border.all(
                    color: c.isGlass ? c.specularBorder : c.borderHairline,
                  ),
                  boxShadow: IcShadow.xl(c),
                ),
                child: Column(
                  mainAxisSize: MainAxisSize.min,
                  children: [
                    Row(
                      mainAxisSize: MainAxisSize.min,
                      children: [
                        for (var i = 0; i < windows.length; i++) ...[
                          if (i > 0) const SizedBox(width: 10),
                          _WindowThumb(
                            window: windows[i],
                            selected: i == selectedIndex,
                            onTap: () => onSelectWindow(windows[i]),
                          ),
                        ],
                      ],
                    ),
                    const SizedBox(height: 10),
                    Text(
                      '⌥`   Release ⌥ to switch  ·  Esc to cancel',
                      style: IcType.text(12, color: c.textMuted),
                    ),
                  ],
                ),
              ),
            ),
          ),
        ),
      ),
    );
  }
}

class _WindowThumb extends StatefulWidget {
  const _WindowThumb({
    required this.window,
    required this.selected,
    required this.onTap,
  });

  final DesktopWindowSummary window;
  final bool selected;
  final VoidCallback onTap;

  @override
  State<_WindowThumb> createState() => _WindowThumbState();
}

class _WindowThumbState extends State<_WindowThumb> {
  bool _hover = false;

  @override
  Widget build(BuildContext context) {
    final c = IcTheme.of(context);
    final sel = widget.selected;
    return MouseRegion(
      cursor: SystemMouseCursors.click,
      onEnter: (_) => setState(() => _hover = true),
      onExit: (_) => setState(() => _hover = false),
      child: GestureDetector(
        onTap: widget.onTap,
        child: Container(
          width: 168,
          padding: const EdgeInsets.all(10),
          decoration: BoxDecoration(
            color: sel
                ? c.accentTint
                : (_hover ? c.surfaceSunk : c.surfaceCard),
            borderRadius: BorderRadius.circular(IcRadius.card),
            border: Border.all(
              color: sel ? c.accent : c.borderHairline,
              width: sel ? 2 : 1,
            ),
          ),
          child: Column(
            children: [
              Container(
                height: 84,
                decoration: BoxDecoration(
                  color: c.surfacePage,
                  borderRadius: BorderRadius.circular(IcRadius.sm),
                  border: Border.all(color: c.borderHairline),
                ),
                child: Column(
                  children: [
                    Container(
                      height: 16,
                      padding: const EdgeInsets.symmetric(horizontal: 6),
                      decoration: BoxDecoration(
                        color: c.titleBarFocused,
                        borderRadius: const BorderRadius.vertical(
                          top: Radius.circular(IcRadius.sm - 1),
                        ),
                      ),
                      child: Row(
                        children: [
                          IcAppIcon(appId: widget.window.appId, size: 10),
                          const SizedBox(width: 4),
                          Expanded(
                            child: Text(
                              widget.window.title,
                              overflow: TextOverflow.ellipsis,
                              style: IcType.text(9.5, color: c.textMuted),
                            ),
                          ),
                        ],
                      ),
                    ),
                    Expanded(
                      child: Center(
                        child: IcAppIcon(appId: widget.window.appId, size: 30),
                      ),
                    ),
                  ],
                ),
              ),
              const SizedBox(height: 8),
              Text(
                widget.window.title,
                overflow: TextOverflow.ellipsis,
                style: IcType.text(
                  12.5,
                  weight: FontWeight.w600,
                  color: c.textStrong,
                ),
              ),
            ],
          ),
        ),
      ),
    );
  }
}

/// Declarative `os://` System Window & Modal Sheet overlay supporting:
/// - `os://window/secure` (Secure Credentials Vault, `480×360`)
/// - `os://window/profile` (Profile & Identity, `540×620`)
/// - `os://sheet/install` (2-Step Install by Link Sheet)
/// - `os://sheet/reconsent` (Update Capability Re-Consent Sheet)
/// - `os://sheet/conflict` (Two-Version File Conflict Sheet)
/// - `os://sheet/2fa` (Hardware TOTP 2FA Sheet)
class DesktopSystemModalSheet extends StatefulWidget {
  const DesktopSystemModalSheet({
    super.key,
    required this.kind,
    required this.onClose,
    this.onOpenSheet,
  });

  final DesktopSystemSheetKind kind;
  final VoidCallback onClose;
  final ValueChanged<DesktopSystemSheetKind>? onOpenSheet;

  static DesktopSystemSheetKind parseUri(String uriString) {
    final lower = uriString.toLowerCase();
    if (lower.contains('secure')) return DesktopSystemSheetKind.secure;
    if (lower.contains('profile')) return DesktopSystemSheetKind.profile;
    if (lower.contains('install')) return DesktopSystemSheetKind.install;
    if (lower.contains('reconsent')) return DesktopSystemSheetKind.reconsent;
    if (lower.contains('conflict')) return DesktopSystemSheetKind.conflict;
    if (lower.contains('2fa')) return DesktopSystemSheetKind.twoFactor;
    return DesktopSystemSheetKind.none;
  }

  @override
  State<DesktopSystemModalSheet> createState() =>
      _DesktopSystemModalSheetState();
}

class _DesktopSystemModalSheetState extends State<DesktopSystemModalSheet> {
  int _installStep = 0;
  bool _secretSaved = false;
  final _secretController = TextEditingController(
    text: 'sk_live_nordlys_9948104a',
  );
  final _secretFocus = FocusNode();

  @override
  void dispose() {
    _secretController.dispose();
    _secretFocus.dispose();
    super.dispose();
  }

  @override
  Widget build(BuildContext context) {
    if (widget.kind == DesktopSystemSheetKind.none) {
      return const SizedBox.shrink();
    }
    final c = IcTheme.of(context);
    final (
      String title,
      String appId,
      double width,
      Widget body,
    ) = switch (widget.kind) {
      DesktopSystemSheetKind.secure => (
        'Secure · Zero-Leakage Credential Vault',
        'settings',
        480.0,
        Column(
          crossAxisAlignment: CrossAxisAlignment.stretch,
          mainAxisSize: MainAxisSize.min,
          children: [
            Wrap(
              spacing: 8,
              runSpacing: 4,
              crossAxisAlignment: WrapCrossAlignment.center,
              children: [
                const TagPill(
                  label: 'Zero LLM Exposure',
                  tone: TagTone.done,
                  icon: LucideIcons.shieldCheck,
                ),
                Text(
                  'neuron://local/vault/secrets',
                  style: IcType.code(11.5, color: c.textMuted),
                ),
              ],
            ),
            const SizedBox(height: 12),
            Text(
              'Secrets entered here are encrypted directly into your personal Vault grain and never appear in chat transcripts or LLM prompts.',
              style: IcType.text(12.5, color: c.textBody),
            ),
            const SizedBox(height: 12),
            Container(
              height: 36,
              padding: const EdgeInsets.symmetric(horizontal: 12),
              decoration: BoxDecoration(
                color: c.surfaceSunk,
                borderRadius: BorderRadius.circular(IcRadius.input),
                border: Border.all(color: c.borderInput),
              ),
              alignment: Alignment.centerLeft,
              child: Field(
                controller: _secretController,
                focusNode: _secretFocus,
                obscure: true,
                placeholder: 'Paste secret token…',
              ),
            ),
            const SizedBox(height: 14),
            Row(
              mainAxisAlignment: MainAxisAlignment.end,
              children: [
                Btn(label: 'Cancel', small: true, onTap: widget.onClose),
                const SizedBox(width: 8),
                Btn(
                  key: const ValueKey('sheet-secure-save'),
                  label: _secretSaved ? 'Saved to Vault ✓' : 'Seal in Vault',
                  small: true,
                  primary: true,
                  icon: LucideIcons.keyRound,
                  onTap: () => setState(() => _secretSaved = true),
                ),
              ],
            ),
          ],
        ),
      ),
      DesktopSystemSheetKind.profile => (
        'Profile · DigitalBrain Identity & Sessions',
        'circle',
        540.0,
        Column(
          crossAxisAlignment: CrossAxisAlignment.stretch,
          mainAxisSize: MainAxisSize.min,
          children: [
            Row(
              children: [
                const IcAppIcon(appId: 'circle', size: 44),
                const SizedBox(width: 12),
                Expanded(
                  child: Column(
                    crossAxisAlignment: CrossAxisAlignment.start,
                    children: [
                      Text(
                        'Jeff Tilley (@jeff)',
                        style: IcType.heading(18, color: c.textStrong),
                      ),
                      Text(
                        'neuron://nordlys/os.profile/profile · Oslo, Norway',
                        style: IcType.code(11.5, color: c.textMuted),
                      ),
                    ],
                  ),
                ),
                const TagPill(
                  label: '2FA Active',
                  tone: TagTone.done,
                  icon: LucideIcons.shieldCheck,
                ),
              ],
            ),
            const SizedBox(height: 14),
            Row(
              children: [
                Btn(
                  key: const ValueKey('sheet-profile-2fa'),
                  label: 'Configure 2FA (TOTP)',
                  small: true,
                  icon: LucideIcons.keyRound,
                  onTap: () => widget.onOpenSheet?.call(
                    DesktopSystemSheetKind.twoFactor,
                  ),
                ),
                const SizedBox(width: 8),
                Btn(
                  label: 'Close',
                  small: true,
                  primary: true,
                  onTap: widget.onClose,
                ),
              ],
            ),
          ],
        ),
      ),
      DesktopSystemSheetKind.install => (
        _installStep == 0
            ? 'Install Smart App · Step 1 of 2 (Manifest)'
            : 'Install Smart App · Step 2 of 2 (Capabilities)',
        'creator',
        500.0,
        Column(
          crossAxisAlignment: CrossAxisAlignment.stretch,
          mainAxisSize: MainAxisSize.min,
          children: [
            Row(
              children: [
                const IcAppIcon(appId: 'datavisualizer', size: 40),
                const SizedBox(width: 12),
                Expanded(
                  child: Column(
                    crossAxisAlignment: CrossAxisAlignment.start,
                    children: [
                      Text(
                        'Tripletex Accounting Sync (v1.4.0)',
                        style: IcType.text(
                          14,
                          weight: FontWeight.w700,
                          color: c.textStrong,
                        ),
                      ),
                      Text(
                        'Publisher: IntoChat OS · Verified C# App<TState>',
                        style: IcType.text(12, color: c.textMuted),
                      ),
                    ],
                  ),
                ),
              ],
            ),
            const SizedBox(height: 12),
            Text(
              _installStep == 0
                  ? 'Step 1: Verify publisher signature and BDD [Scenario] gate (14/14 passing).'
                  : 'Step 2: Grant read-only access to Files and Workspace storage.',
              style: IcType.text(12.5, color: c.textBody),
            ),
            const SizedBox(height: 14),
            Row(
              mainAxisAlignment: MainAxisAlignment.end,
              children: [
                Btn(label: 'Cancel', small: true, onTap: widget.onClose),
                const SizedBox(width: 8),
                Btn(
                  key: const ValueKey('sheet-install-next'),
                  label: _installStep == 0
                      ? 'Review Capabilities →'
                      : 'Install & Pin to Dock',
                  small: true,
                  primary: true,
                  icon: LucideIcons.packagePlus,
                  onTap: () {
                    if (_installStep == 0) {
                      setState(() => _installStep = 1);
                    } else {
                      widget.onClose();
                    }
                  },
                ),
              ],
            ),
          ],
        ),
      ),
      DesktopSystemSheetKind.reconsent => (
        'Update Re-Consent · Capability Diff',
        'settings',
        480.0,
        Column(
          crossAxisAlignment: CrossAxisAlignment.stretch,
          mainAxisSize: MainAxisSize.min,
          children: [
            const TagPill(
              label: '+1 New Capability Requested',
              tone: TagTone.working,
              icon: LucideIcons.shieldAlert,
            ),
            const SizedBox(height: 10),
            Text(
              'Connector Sync v1.5.0 requests write access to external workspace records (gated by LFP ApprovalCard).',
              style: IcType.text(12.5, color: c.textBody),
            ),
            const SizedBox(height: 14),
            Row(
              mainAxisAlignment: MainAxisAlignment.end,
              children: [
                Btn(label: 'Keep v1.4.0', small: true, onTap: widget.onClose),
                const SizedBox(width: 8),
                Btn(
                  key: const ValueKey('sheet-reconsent-approve'),
                  label: 'Approve & Update',
                  small: true,
                  primary: true,
                  onTap: widget.onClose,
                ),
              ],
            ),
          ],
        ),
      ),
      DesktopSystemSheetKind.conflict => (
        'Two-Version File Conflict · nordlys-q4-renewal.md',
        'files',
        520.0,
        Column(
          crossAxisAlignment: CrossAxisAlignment.stretch,
          mainAxisSize: MainAxisSize.min,
          children: [
            Text(
              'Both your local editor (11:40) and Ino (11:42) modified CloseDate in nordlys-q4-renewal.md.',
              style: IcType.text(12.5, color: c.textBody),
            ),
            const SizedBox(height: 14),
            Row(
              mainAxisAlignment: MainAxisAlignment.end,
              children: [
                Btn(label: 'Keep Local', small: true, onTap: widget.onClose),
                const SizedBox(width: 8),
                Btn(label: 'Save Both', small: true, onTap: widget.onClose),
                const SizedBox(width: 8),
                Btn(
                  key: const ValueKey('sheet-conflict-keep-ino'),
                  label: 'Keep Ino Version (15 Nov)',
                  small: true,
                  primary: true,
                  onTap: widget.onClose,
                ),
              ],
            ),
          ],
        ),
      ),
      DesktopSystemSheetKind.twoFactor => (
        'Two-Factor Authentication (TOTP)',
        'settings',
        440.0,
        Column(
          crossAxisAlignment: CrossAxisAlignment.stretch,
          mainAxisSize: MainAxisSize.min,
          children: [
            Text(
              'Hardware authenticator & recovery keys are active for neuron://nordlys/os.profile.',
              style: IcType.text(12.5, color: c.textBody),
            ),
            const SizedBox(height: 14),
            Align(
              alignment: Alignment.centerRight,
              child: Btn(
                label: 'Done',
                small: true,
                primary: true,
                onTap: widget.onClose,
              ),
            ),
          ],
        ),
      ),
      DesktopSystemSheetKind.none => (
        '',
        'settings',
        400.0,
        const SizedBox.shrink(),
      ),
    };

    return GestureDetector(
      key: ValueKey('desktop-system-sheet-${widget.kind.name}'),
      behavior: HitTestBehavior.opaque,
      onTap: widget.onClose,
      child: ColoredBox(
        color: const Color(0x66000000),
        child: Center(
          child: GestureDetector(
            onTap: () {},
            child: ClipRRect(
              borderRadius: BorderRadius.circular(IcRadius.window),
              child: BackdropFilter(
                filter: ImageFilter.blur(sigmaX: 24, sigmaY: 24),
                child: Container(
                  width: width,
                  padding: const EdgeInsets.all(20),
                  decoration: BoxDecoration(
                    color: c.surfaceElevated.withValues(alpha: 0.96),
                    borderRadius: BorderRadius.circular(IcRadius.window),
                    border: Border.all(
                      color: c.isGlass ? c.specularBorder : c.borderHairline,
                    ),
                    boxShadow: IcShadow.xl(c),
                  ),
                  child: Column(
                    mainAxisSize: MainAxisSize.min,
                    crossAxisAlignment: CrossAxisAlignment.stretch,
                    children: [
                      Row(
                        children: [
                          IcAppIcon(appId: appId, size: 24),
                          const SizedBox(width: 10),
                          Expanded(
                            child: Text(
                              title,
                              style: IcType.text(
                                14,
                                weight: FontWeight.w700,
                                color: c.textStrong,
                              ),
                            ),
                          ),
                          IconButton(
                            key: const ValueKey('desktop-system-sheet-close'),
                            icon: Icon(
                              LucideIcons.x,
                              size: 16,
                              color: c.textMuted,
                            ),
                            onPressed: widget.onClose,
                          ),
                        ],
                      ),
                      const Divider(height: 20),
                      body,
                    ],
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
