import 'dart:async';
import 'dart:convert';
import 'dart:math' as math;
import 'dart:ui' as ui;

import 'package:flame/components.dart';
import 'package:flame/effects.dart';
import 'package:flame/events.dart';
import 'package:flame_behavior_tree/flame_behavior_tree.dart';
import 'package:flutter/foundation.dart';
import 'package:flutter/painting.dart';
import 'package:flutter_svg/flutter_svg.dart' as svg;
import 'package:intochat_os_flutter_shell/intochat_os_flutter.dart';

import '../theme/neuron_icon.dart';
import '../theme/shell_icon.dart';
import '../theme/ui_tokens.dart' show IcBrainShaderMode;
import 'flame_behavior.dart';
import 'flame_nodes.dart';

// --- src/flame/prefabs/activity_ring.dart ---
final class RingEntry {
  const RingEntry(this.id, this.label, this.kind, this.progress);
  final String id, label, kind;
  final double progress;

  /// `items` as JSON; malformed input and entries without an id are skipped, at most 24 kept.
  static List<RingEntry> parse(String? json) {
    if (json == null || json.isEmpty) return const [];
    Object? decoded;
    try {
      decoded = jsonDecode(json);
    } on FormatException {
      return const [];
    }
    if (decoded is! List) return const [];
    final entries = <RingEntry>[];
    final seen = <String>{};
    for (final item in decoded) {
      if (item is! Map) continue;
      final id = item['id'];
      if (id is! String || !seen.add(id)) continue;
      final label = item['label'];
      final kind = item['kind'];
      final progress = item['progress'];
      entries.add(
        RingEntry(
          id,
          label is String ? label : '',
          kind is String ? kind : '',
          progress is num ? progress.toDouble().clamp(0, 1).toDouble() : 0,
        ),
      );
      if (entries.length == 24) break;
    }
    return entries;
  }
}

/// Live activity as pills on a circle; each pill is tappable and reports its item id.
final class ActivityRing extends NodeComponent {
  ActivityRing(super.node);

  final _items = <String, RingItem>{};
  List<RingItem> get items => _items.values.where((i) => !i.leaving).toList();

  double get radius => param('radius', 120).clamp(8, 4000).toDouble();

  @override
  void changed() {
    // Pills centre on the circle, so the box grows by one pill to hold them whole.
    size.setValues(
      radius * 2 + RingItem.pillSize.x,
      radius * 2 + RingItem.pillSize.y,
    );
    super.changed();
    final entries = RingEntry.parse(params['items']);
    final ids = entries.map((e) => e.id).toSet();
    for (final item in _items.values) {
      if (!ids.contains(item.entry.id)) item.leaving = true;
    }
    for (final (index, entry) in entries.indexed) {
      final angle = 2 * math.pi * index / entries.length - math.pi / 2;
      final home =
          size / 2 + Vector2(math.cos(angle), math.sin(angle)) * radius;
      final item = _items[entry.id];
      if (item == null || item.leaving) {
        item?.removeFromParent();
        final created = RingItem(this, entry, home)..position = size / 2;
        _items[entry.id] = created;
        add(created);
      } else {
        item
          ..entry = entry
          ..home = home;
      }
    }
    if (isMounted) game.wake();
  }

  void forget(RingItem item) {
    if (_items[item.entry.id] == item) _items.remove(item.entry.id);
  }

  void tapped(RingItem item) {
    game.wake();
    if (!inputs.contains('tap')) return;
    game.report(
      this,
      'tap',
      position + item.position - size / 2,
      item.entry.id,
    );
  }

  @visibleForTesting
  void simulateTap(String id) {
    final item = _items[id];
    if (item != null && !item.leaving) tapped(item);
  }

  // Only pills report; a tap between them passes through.
  @override
  void onTapUp(TapUpEvent event) => event.continuePropagation = true;

  @override
  void render(ui.Canvas canvas) {}
}

/// One pill: eases in from the ring's centre, fades out when its item leaves.
final class RingItem extends PositionComponent with TapCallbacks {
  RingItem(this.ring, this.entry, this.home)
    : super(size: pillSize.clone(), anchor: Anchor.center);

  static final pillSize = Vector2(96, 28);

  final ActivityRing ring;
  RingEntry entry;
  Vector2 home;
  bool leaving = false;
  double _alpha = 0;

  @override
  void update(double dt) {
    super.update(dt);
    final k = 1 - math.exp(-dt * 8);
    position += (home - position) * k;
    _alpha += ((leaving ? 0 : 1) - _alpha) * k;
    if (leaving && _alpha < 0.02) {
      ring.forget(this);
      removeFromParent();
      return;
    }
    if (leaving || _alpha < 0.999 || position.distanceTo(home) > 0.1) {
      ring.game.wake();
    }
  }

  @override
  void onTapUp(TapUpEvent event) => ring.tapped(this);

  static const _glyphs = {
    'route': '→',
    'behavior': '⚡',
    'app': '◆',
    'signal': '•',
  };

  @override
  void render(ui.Canvas canvas) {
    final alpha = _alpha.clamp(0.0, 1.0);
    canvas.drawRRect(
      ui.RRect.fromRectAndRadius(size.toRect(), const ui.Radius.circular(14)),
      ui.Paint()..color = const ui.Color(0xFFE4EEE5).withValues(alpha: alpha),
    );
    canvas.drawArc(
      ui.Rect.fromCircle(center: ui.Offset(14, size.y / 2), radius: 9),
      -math.pi / 2,
      2 * math.pi * entry.progress,
      false,
      ui.Paint()
        ..style = ui.PaintingStyle.stroke
        ..strokeWidth = 2
        ..color = const ui.Color(0xFF397B63).withValues(alpha: alpha),
    );
    final text = TextPainter(
      text: TextSpan(
        text: '${_glyphs[entry.kind] ?? '·'} ${entry.label}',
        style: TextStyle(
          fontSize: 11,
          color: const ui.Color(0xFF263934).withValues(alpha: alpha),
        ),
      ),
      textDirection: TextDirection.ltr,
      maxLines: 1,
      ellipsis: '…',
    )..layout(maxWidth: size.x - 30);
    text.paint(canvas, ui.Offset(26, (size.y - text.height) / 2));
  }
}

// --- src/flame/prefabs/glass_sphere.dart ---
const _accent = Color(0xFF3D7EFF);
const _grey = Color(0xFF9AA0A6);
const _red = Color(0xFFE5484D);
const _warm = Color(0xFFFF7A45);

/// The colour a presence state shows: the OS bar's dot uses it too.
Color presenceColor(PresenceState state) => switch (state) {
  PresenceState.attention => _warm,
  PresenceState.paused => _red,
  PresenceState.disconnected => _grey,
  PresenceState.waiting => const Color(0xFFF59E0B),
  _ => _accent,
};

/// The colour of a focus tone: info is the accent.
Color focusToneColor(FocusTone tone) => switch (tone) {
  FocusTone.info => _accent,
  FocusTone.success => const Color(0xFF10B981),
  FocusTone.warning => const Color(0xFFF59E0B),
  FocusTone.critical => _red,
};

/// Ino's sphere: a volumetric glass ball (radial specular body, rim light, blurred glow) whose mood follows
/// [state]. Inside it draws the face while listening or searching, else the glyph of [neuronKind].
/// Only [IcBrainShaderMode.volumetricGlass] has its own look; the other modes draw the same glass.
final class GlassSphere extends PositionComponent {
  GlassSphere({
    this.state = PresenceState.idle,
    this.neuronKind,
    this.mode = IcBrainShaderMode.volumetricGlass,
    double radius = 28,
  }) : super(anchor: Anchor.center, size: Vector2.all(radius * 2));

  PresenceState state;
  String? neuronKind;
  IcBrainShaderMode mode;

  /// Off under reduced motion: the mood is drawn at its rest frame.
  bool animations = true;
  double time = 0;

  double get radius => size.x / 2;

  @override
  void update(double dt) {
    if (animations) time += dt;
  }

  double get _phase => animations ? (math.sin(time * math.pi) + 1) / 2 : 0;

  @override
  void render(ui.Canvas canvas) {
    final center = (size / 2).toOffset();
    final pulse = state == PresenceState.working && animations
        ? 1 + 0.06 * math.sin(time * 2 * math.pi / 1.2)
        : 1.0;
    final r = radius * 0.78 * pulse;
    final muted =
        state == PresenceState.paused || state == PresenceState.disconnected;
    final body = muted
        ? (state == PresenceState.paused ? const Color(0xFF8A93A3) : _grey)
        : _accent;
    final rim = switch (state) {
      PresenceState.attention => _warm,
      PresenceState.paused => _red,
      _ => const Color(0xCCFFFFFF),
    };

    // Glow: the painter's blurred halo, warmer when the sphere wants attention.
    final glow =
        state == PresenceState.attention || state == PresenceState.paused
        ? rim
        : body;
    canvas.drawCircle(
      center,
      r * 1.08,
      ui.Paint()
        ..color = glow.withValues(
          alpha: state == PresenceState.attention ? 0.75 : 0.4,
        )
        ..maskFilter = const ui.MaskFilter.blur(ui.BlurStyle.normal, 10),
    );

    // Body: the radial specular shader of the volumetric glass mode.
    final rect = Rect.fromCircle(center: center, radius: r);
    canvas.drawCircle(
      center,
      r,
      ui.Paint()
        ..shader = RadialGradient(
          center: const Alignment(-0.38, -0.38),
          radius: 0.95,
          colors: [
            Color.lerp(body, const Color(0xFFFFFFFF), 0.55)!,
            body.withValues(alpha: 0.92),
            Color.lerp(body, const Color(0xFF000000), 0.55)!,
          ],
          stops: const [0.0, 0.52, 1.0],
        ).createShader(rect),
    );

    // Specular highlight and rim light.
    canvas.drawCircle(
      center.translate(-r * 0.36, -r * 0.4),
      r * 0.22,
      ui.Paint()
        ..color = const Color(0x8CFFFFFF)
        ..maskFilter = const ui.MaskFilter.blur(ui.BlurStyle.normal, 3),
    );
    canvas.drawCircle(
      center,
      r,
      ui.Paint()
        ..style = ui.PaintingStyle.stroke
        ..strokeWidth =
            state == PresenceState.attention || state == PresenceState.paused
            ? 2.4
            : 1.1
        ..color = rim,
    );

    if (state == PresenceState.listening || state == PresenceState.searching) {
      final edge = r * 1.2;
      canvas.save();
      canvas.translate(center.dx - edge / 2, center.dy - edge / 2);
      paintInoFace(canvas, Size.square(edge), state, _phase);
      canvas.restore();
    } else {
      _paintGlyph(canvas, center, r);
    }

    if (state == PresenceState.waiting) {
      _paintDottedRing(canvas, center, r * 1.22);
    }
  }

  void _paintGlyph(ui.Canvas canvas, Offset center, double r) {
    final kind = neuronKind;
    final known = NeuronIconKind.fromKey(kind);
    final String text;
    String? family, package;
    if (kind == null || kind.isEmpty) {
      final data = NeuronIcon.glyphOf(NeuronIconKind.assistant);
      text = String.fromCharCode(data.codePoint);
      family = data.fontFamily;
      package = data.fontPackage;
    } else if (known == NeuronIconKind.generic) {
      text = kind[0].toUpperCase();
    } else {
      final data = NeuronIcon.glyphOf(known);
      text = String.fromCharCode(data.codePoint);
      family = data.fontFamily;
      package = data.fontPackage;
    }
    final glyph = TextPainter(
      text: TextSpan(
        text: text,
        style: TextStyle(
          fontSize: r * 0.9,
          color: const Color(0xF2FFFFFF),
          fontFamily: family,
          package: package,
        ),
      ),
      textDirection: TextDirection.ltr,
    )..layout();
    glyph.paint(canvas, center - Offset(glyph.width / 2, glyph.height / 2));
  }

  void _paintDottedRing(ui.Canvas canvas, Offset center, double ringRadius) {
    const dots = 12;
    final turn = animations ? time * 0.25 * 2 * math.pi : 0.0;
    final paint = ui.Paint()..color = const Color(0xFFF59E0B);
    for (var i = 0; i < dots; i++) {
      final angle = turn + 2 * math.pi * i / dots;
      canvas.drawCircle(
        center + Offset(math.cos(angle), math.sin(angle)) * ringRadius,
        2,
        paint,
      );
    }
  }
}

/// The presence layer's own outline around a focused rect, in the tone's colour. It draws on the layer, never
/// inside the target's view.
final class HighlightRing extends PositionComponent {
  HighlightRing({this.tone = FocusTone.info});

  FocusTone tone;
  bool visible = false;

  @override
  void render(ui.Canvas canvas) {
    if (!visible) return;
    final color = focusToneColor(tone);
    final shape = RRect.fromRectAndRadius(
      Rect.fromLTWH(0, 0, size.x, size.y),
      const Radius.circular(12),
    );
    canvas.drawRRect(
      shape,
      ui.Paint()
        ..style = ui.PaintingStyle.stroke
        ..strokeWidth = 6
        ..color = color.withValues(alpha: 0.35)
        ..maskFilter = const ui.MaskFilter.blur(ui.BlurStyle.normal, 6),
    );
    canvas.drawRRect(
      shape,
      ui.Paint()
        ..style = ui.PaintingStyle.stroke
        ..strokeWidth = 2.5
        ..color = color,
    );
  }
}

// --- src/flame/prefabs/signal_pulse.dart ---

/// One drawn edge a pulse can travel: [from] is null for a `*` source (any publisher of [alias]).
final class PulseEdge {
  const PulseEdge({
    required this.key,
    required this.fromId,
    required this.toId,
    required this.alias,
    required this.start,
    required this.end,
  });
  final String key, fromId, toId, alias;
  final Vector2 start, end;
  Vector2 get midpoint => (start + end)..scale(.5);
}

/// One step of a scripted replay: the signal [alias] of [traceId] at journal [seq], from the publisher [from] towards [to]
/// (empty: any edge of the alias). [index] is its place in the sequence.
final class ReplayStep {
  const ReplayStep({
    required this.index,
    required this.seq,
    required this.alias,
    required this.from,
    required this.to,
    required this.traceId,
  });
  final int index, seq;
  final String alias, from, to, traceId;
}

/// One pulse in flight: it starts at [start], reaches [end] after [SignalPulses.travel].
final class SignalPulse {
  SignalPulse({
    required this.edge,
    required this.traceId,
    required this.start,
    required this.end,
  });
  final PulseEdge edge;
  final String traceId;
  final Vector2 start, end;
  double elapsed = 0;
  String get key => 'pulse:$traceId';
  ui.Color get color => pulseColor(traceId);
}

/// The colour of a trace: its id hashed onto the hue wheel, so one trace keeps one colour everywhere.
ui.Color pulseColor(String traceId) {
  var hash = 0x811C9DC5;
  for (final unit in traceId.codeUnits) {
    hash = ((hash ^ unit) * 0x01000193) & 0xFFFFFFFF;
  }
  return HSVColor.fromAHSV(1, (hash % 360).toDouble(), .7, 1).toColor();
}

/// The journal tail turned into pulses: each signal travels every edge whose alias it carries into a drawn node. Bounded:
/// at most [cap] pulses fly at once, a signal whose trace already crosses the same edge joins it instead of spawning, and a
/// pulse older than twice [travel] is dropped by [advance] even if its arrival was never reported.
final class SignalPulses {
  SignalPulses({this.cap = 64});

  static const travel = Duration(milliseconds: 600);
  static final double _expiry =
      2 * travel.inMicroseconds / Duration.microsecondsPerSecond;
  final int cap;
  final _byAlias = <String, List<PulseEdge>>{};
  final _positions = <String, Vector2>{};
  final pulses = <SignalPulse>[];
  int? _replayAt;

  /// True while a replay step is current: live signals are not drawn until [replay] is called with no step.
  bool get replaying => _replayAt != null;

  /// Plays the replay of [steps] at step [at], one pulse per edge of that step's signal, in order as the cursor moves. Entering
  /// a replay drops the live pulses in flight; a null [at] ends it, and live signals are drawn again. Returns the pulses added.
  List<SignalPulse> replay(List<ReplayStep> steps, int? at) {
    final wasReplaying = replaying;
    if (at == null || at < 0 || at >= steps.length) {
      _replayAt = null;
      if (wasReplaying) pulses.clear();
      return const [];
    }
    if (!wasReplaying) pulses.clear();
    if (_replayAt == at) return const [];
    _replayAt = at;
    final step = steps[at];
    final added = <SignalPulse>[];
    for (final edge in _byAlias[step.alias] ?? const <PulseEdge>[]) {
      if (step.to.isNotEmpty && edge.toId != step.to) continue;
      if (pulses.length >= cap) break;
      final start = edge.fromId == '*'
          ? (_positions[step.from]?.clone() ?? edge.midpoint)
          : edge.start.clone();
      final pulse = SignalPulse(
        edge: edge,
        traceId: step.traceId,
        start: start,
        end: edge.end.clone(),
      );
      pulses.add(pulse);
      added.add(pulse);
    }
    return added;
  }

  /// Replaces the drawn edges and node positions (the scene changed); pulses in flight are dropped.
  void layout(Iterable<PulseEdge> edges, Map<String, Vector2> positions) {
    pulses.clear();
    _replayAt = null;
    _byAlias.clear();
    _positions
      ..clear()
      ..addAll(positions);
    for (final edge in edges) {
      if (!_positions.containsKey(edge.toId)) continue;
      (_byAlias[edge.alias] ??= []).add(edge);
    }
  }

  /// Spawns the pulses of [signals]; returns those it added.
  List<SignalPulse> feed(Iterable<JournalTailSignal> signals) {
    final added = <SignalPulse>[];
    if (replaying) return added;
    for (final signal in signals) {
      final trace = signal.traceId ?? 'seq:${signal.seq}';
      for (final edge in _byAlias[signal.alias] ?? const <PulseEdge>[]) {
        if (pulses.length >= cap) return added;
        if (pulses.any((p) => p.edge.key == edge.key && p.traceId == trace)) {
          continue;
        }
        final start = edge.fromId == '*'
            ? (_positions[signal.publisher]?.clone() ?? edge.midpoint)
            : edge.start.clone();
        final pulse = SignalPulse(
          edge: edge,
          traceId: trace,
          start: start,
          end: edge.end.clone(),
        );
        pulses.add(pulse);
        added.add(pulse);
      }
    }
    return added;
  }

  /// Ages every pulse by [dt] seconds and drops those past the expiry.
  void advance(double dt) {
    for (final pulse in pulses) {
      pulse.elapsed += dt;
    }
    pulses.removeWhere((pulse) => pulse.elapsed >= _expiry);
  }

  /// Forgets a pulse that arrived.
  void arrived(SignalPulse pulse) => pulses.remove(pulse);

  void clear() => pulses.clear();
}

/// Ages a [SignalPulses] with the game clock.
final class SignalPulseClock extends Component {
  SignalPulseClock(this.pulses);
  final SignalPulses pulses;

  @override
  void update(double dt) => pulses.advance(dt);
}

/// A pulse on screen: a dot in its trace's colour moving along its edge over [SignalPulses.travel]; a tap reports its key.
final class SignalPulseComponent extends CircleComponent with TapCallbacks {
  SignalPulseComponent(this.pulse, {this.onTap, this.onArrived})
    : super(
        radius: 6,
        anchor: Anchor.center,
        position: pulse.start.clone(),
        paint: ui.Paint()..color = pulse.color,
        priority: 10,
      );

  final SignalPulse pulse;
  final void Function(String key)? onTap;
  final void Function(SignalPulse pulse)? onArrived;

  @override
  Future<void> onLoad() async {
    await super.onLoad();
    final seconds =
        SignalPulses.travel.inMicroseconds / Duration.microsecondsPerSecond;
    await add(
      MoveEffect.to(
        pulse.end,
        EffectController(duration: seconds),
        onComplete: () {
          onArrived?.call(pulse);
          removeFromParent();
        },
      ),
    );
  }

  @override
  void onTapDown(TapDownEvent event) => onTap?.call(pulse.key);
}

// --- src/flame/prefabs/sphere.dart ---

typedef SphereIconLoader = Future<Uint8List?> Function(
  String neuron,
  String icon,
);

/// What a sphere's behavior tree moves: draws a sleek 3D-elevated rounded icon card, status ring,
/// optional `0–100%` circular progress arc, and labels, running the tree's moveTo/scale/fade/pulse effects.
final class SphereVisual extends PositionComponent
    with HasPaint
    implements SphereBody {
  SphereVisual() : super(anchor: Anchor.center);

  @override
  double now = 0;
  @override
  int frame = 0;

  /// Off under reduced motion: tasks still run their clock but draw nothing.
  bool animations = true;
  String icon = '';
  String title = '';
  String description = '';
  String nodeStatus = '';
  Color accentColor = const Color(0xFF7FB069);
  double? progress;
  bool selected = false;
  bool badge = false;
  double presentationOpacity = 1;
  ui.Image? iconImage;

  final _effects = <String, Effect>{};
  TextPainter? _titleText, _descriptionText, _progressText;
  String _cachedProgressLabel = '';

  bool get hasEffects => _effects.values.any((effect) => !effect.isRemoved);

  void advance(double dt) {
    now += dt;
    frame++;
  }

  void setLabels(String title, String description) {
    if (title != this.title || _titleText == null) {
      this.title = title;
      _titleText = _label(
        title,
        13,
        const Color(0xFFE6EDF3),
        weight: FontWeight.w600,
      );
    }
    if (description != this.description || _descriptionText == null) {
      this.description = description;
      _descriptionText = _label(description, 11, const Color(0xFF9AA4B2));
    }
  }

  void setProgress(double? value) {
    progress = value?.clamp(0.0, 1.0);
    if (progress == null) {
      _progressText = null;
      _cachedProgressLabel = '';
      return;
    }
    final pctText = '${(progress! * 100).round()}%';
    if (pctText != _cachedProgressLabel || _progressText == null) {
      _cachedProgressLabel = pctText;
      _progressText = TextPainter(
        text: TextSpan(
          text: pctText,
          style: const TextStyle(
            color: Color(0xFFF1F5F9),
            fontSize: 9.5,
            fontWeight: FontWeight.w700,
          ),
        ),
        textDirection: TextDirection.ltr,
        maxLines: 1,
      )..layout();
    }
  }

  static TextPainter _label(
    String text,
    double size,
    Color color, {
    FontWeight weight = FontWeight.w400,
  }) => TextPainter(
    text: TextSpan(
      text: text,
      style: TextStyle(color: color, fontSize: size, fontWeight: weight),
    ),
    textDirection: TextDirection.ltr,
    maxLines: 1,
    ellipsis: '...',
  )..layout(maxWidth: 176);

  @override
  void begin(String kind, TreeNode params) {
    if (!animations) return;
    final controller = EffectController(
      duration: math.max(params.seconds, 0.001),
    );
    final (property, effect) = switch (kind) {
      'moveTo' => (
        'position',
        MoveEffect.to(
          Vector2(size.x / 2 + params.x, size.y / 2 + params.y),
          controller,
        ),
      ),
      'scale' => (
        'scale',
        ScaleEffect.to(Vector2.all(params.factor), controller),
      ),
      'fade' => ('opacity', OpacityEffect.to(params.alpha, controller)),
      'pulse' => (
        'scale',
        ScaleEffect.to(
          Vector2.all(1 + params.amplitude),
          EffectController(
            duration: math.max(params.seconds, 0.002) / 2,
            alternate: true,
          ),
        ),
      ),
      _ => (null, null),
    };
    if (property == null || effect == null) return;
    _effects.remove(property)?.removeFromParent();
    _effects[property] = effect;
    add(effect);
  }

  @override
  void render(ui.Canvas canvas) {
    // HasPaint stores effect opacity in paint.color; render must leave it intact.
    canvas.saveLayer(
      null,
      ui.Paint()
        ..color = Color.fromRGBO(255, 255, 255, opacity * presentationOpacity),
    );

    final center = (size / 2).toOffset();
    final radius = size.x / 2;
    final cardRect = Rect.fromLTWH(4, 4, size.x - 8, size.y - 8);
    const cardRadius = ui.Radius.circular(22);
    final cardRRect = ui.RRect.fromRectAndRadius(cardRect, cardRadius);

    // 1. 3D Extruded Pedestal Lip
    final pedestalRRect = ui.RRect.fromRectAndRadius(
      cardRect.shift(const Offset(0, 4.5)),
      cardRadius,
    );
    canvas.drawRRect(
      pedestalRRect,
      ui.Paint()..color = const Color(0xFF060A10),
    );
    canvas.drawRRect(
      pedestalRRect,
      ui.Paint()
        ..style = ui.PaintingStyle.stroke
        ..strokeWidth = 1.2
        ..color = Color.lerp(const Color(0xFF0E1622), accentColor, 0.30)!,
    );

    // 2. 3D Elevated Card Surface + Top Bevel Highlight
    final topSurface = Color.lerp(const Color(0xFF1C2736), accentColor, 0.22)!;
    const bottomSurface = Color(0xFF0F1620);
    canvas.drawRRect(
      cardRRect,
      ui.Paint()
        ..shader = ui.Gradient.linear(
          cardRect.topCenter,
          cardRect.bottomCenter,
          [topSurface, bottomSurface],
        ),
    );
    canvas.drawRRect(
      cardRRect,
      ui.Paint()
        ..style = ui.PaintingStyle.stroke
        ..strokeWidth = selected ? 2.6 : 1.8
        ..color = selected
            ? const Color(0xFFF8FAFC)
            : accentColor.withValues(alpha: 0.85),
    );
    canvas.drawLine(
      Offset(cardRect.left + 18, cardRect.top + 1.5),
      Offset(cardRect.right - 18, cardRect.top + 1.5),
      ui.Paint()
        ..strokeWidth = 1.1
        ..strokeCap = ui.StrokeCap.round
        ..color = const Color(0x38FFFFFF),
    );

    // 3. Recessed Icon Medallion + Status Ring + Optional 0–100% Circular Progress Ring
    final medallionRadius = radius * 0.56;
    canvas.drawCircle(
      center,
      medallionRadius,
      ui.Paint()..color = const Color(0xFF0A1018),
    );
    final statusColor = resolveGraphStatusRingColor(
      nodeStatus,
      accentColor.withValues(alpha: 0.55),
    );
    canvas.drawCircle(
      center,
      medallionRadius + 1.5,
      ui.Paint()
        ..style = ui.PaintingStyle.stroke
        ..strokeWidth = 1.8
        ..color = statusColor,
    );

    if (progress case final pct?) {
      final arcRadius = radius * 0.70;
      final arcRect = Rect.fromCircle(center: center, radius: arcRadius);
      canvas.drawCircle(
        center,
        arcRadius,
        ui.Paint()
          ..style = ui.PaintingStyle.stroke
          ..strokeWidth = 3.8
          ..color = accentColor.withValues(alpha: 0.20),
      );
      if (pct > 0.001) {
        final arcColor = pct >= 0.999 ? const Color(0xFF34D399) : accentColor;
        canvas.drawArc(
          arcRect,
          -math.pi / 2,
          2 * math.pi * pct.clamp(0.0, 1.0),
          false,
          ui.Paint()
            ..style = ui.PaintingStyle.stroke
            ..strokeWidth = 4.2
            ..strokeCap = ui.StrokeCap.round
            ..color = arcColor,
        );
      }
    }

    // 4. Crisp Icon Image or Glyph
    if (iconImage case final image?) {
      final extent = radius * 1.3;
      canvas.drawImageRect(
        image,
        Rect.fromLTWH(0, 0, image.width.toDouble(), image.height.toDouble()),
        Rect.fromCenter(center: center, width: extent, height: extent),
        ui.Paint(),
      );
    } else if (icon.isNotEmpty) {
      final data = shellIcon(icon);
      final glyph = TextPainter(
        text: TextSpan(
          text: data == null
              ? icon[0].toUpperCase()
              : String.fromCharCode(data.codePoint),
          style: TextStyle(
            fontSize: radius * 0.78,
            color: const Color(0xFFEFF6FF),
            fontFamily: data?.fontFamily,
            package: data?.fontPackage,
          ),
        ),
        textDirection: TextDirection.ltr,
      )..layout();
      glyph.paint(canvas, center - Offset(glyph.width / 2, glyph.height / 2));
    }

    // 5. Optional Percentage Pill on Card Bottom
    if (_progressText case final pctPainter?) {
      final pillW = pctPainter.width + 10;
      final pillH = pctPainter.height + 3;
      final pillRect = Rect.fromCenter(
        center: Offset(center.dx, cardRect.bottom - pillH / 2 - 3),
        width: pillW,
        height: pillH,
      );
      canvas.drawRRect(
        ui.RRect.fromRectAndRadius(pillRect, const ui.Radius.circular(8)),
        ui.Paint()..color = const Color(0xEE0B121C),
      );
      canvas.drawRRect(
        ui.RRect.fromRectAndRadius(pillRect, const ui.Radius.circular(8)),
        ui.Paint()
          ..style = ui.PaintingStyle.stroke
          ..strokeWidth = 1.0
          ..color = accentColor.withValues(alpha: 0.75),
      );
      pctPainter.paint(
        canvas,
        Offset(
          center.dx - pctPainter.width / 2,
          pillRect.center.dy - pctPainter.height / 2,
        ),
      );
    }

    // 6. Title & Subtitle Labels Above Card
    var top = -6.0;
    for (final text in [_descriptionText, _titleText]) {
      if (text == null || (text.text?.toPlainText().isEmpty ?? true)) continue;
      top -= text.height;
      text.paint(canvas, Offset(center.dx - text.width / 2, top));
    }

    // 7. Attention / Error Badge
    if (badge) {
      final badgeCenter = center.translate(radius * 0.72, -radius * 0.72);
      canvas.drawCircle(
        badgeCenter,
        7,
        ui.Paint()..color = const Color(0xFFE5484D),
      );
    }

    canvas.restore();
  }
}

/// A neuron's sphere: 3D-elevated icon card with status & progress rings, driven by the behavior tree
/// its node carries (or [defaultIdleTree]). The game rewrites [blackboard] from `blackboard` on every apply.
final class SphereComponent extends NodeComponent
    with HasBehaviorTree<BaseNode> {
  SphereComponent(super.node);

  final _board = SphereBlackboard();

  @override
  SphereBlackboard get blackboard => _board;
  final body = SphereVisual();
  String _treeKey = 'unset';
  SphereIconLoader? _loadIcon;
  int _iconGeneration = 0;
  String _iconKey = '';
  bool _removed = false;
  Future<void> _iconRead = Future.value();
  Future<void> get iconReady => _iconRead;
  SphereIconLoader? get loadIcon => _loadIcon;
  set loadIcon(SphereIconLoader? value) {
    if (value == _loadIcon) return;
    _loadIcon = value;
    _iconKey = '';
    _readIcon();
  }

  double? get progress => body.progress;
  String get nodeStatus => body.nodeStatus;

  void _readIcon() {
    final icon = params['icon'] ?? '', neuron = params['neuron'] ?? nodeId;
    final key = '$neuron:$icon';
    if (key == _iconKey) return;
    _iconKey = key;
    final generation = ++_iconGeneration;
    body.iconImage?.dispose();
    body.iconImage = null;
    final load = _loadIcon;
    if (load == null || icon.isEmpty || shellIcon(icon) != null || _removed) {
      return;
    }
    _iconRead = () async {
      ui.Image? image;
      try {
        final bytes = await load(neuron, icon);
        if (bytes == null ||
            bytes.isEmpty ||
            generation != _iconGeneration ||
            _removed) {
          return;
        }
        if (utf8
            .decode(bytes.take(256).toList(), allowMalformed: true)
            .trimLeft()
            .startsWith('<')) {
          final picture = await svg.vg.loadPicture(
            svg.SvgBytesLoader(bytes),
            null,
          );
          try {
            final recorder = ui.PictureRecorder();
            ui.Canvas(recorder)
              ..scale(96 / picture.size.width, 96 / picture.size.height)
              ..drawPicture(picture.picture);
            final scaled = recorder.endRecording();
            try {
              image = await scaled.toImage(96, 96);
            } finally {
              scaled.dispose();
            }
          } finally {
            picture.picture.dispose();
          }
        } else {
          final codec = await ui.instantiateImageCodec(bytes);
          try {
            image = (await codec.getNextFrame()).image;
          } finally {
            codec.dispose();
          }
        }
        if (generation != _iconGeneration || _removed) {
          image.dispose();
          return;
        }
        body.iconImage = image;
        if (isMounted) game.wake();
      } catch (_) {
        image?.dispose();
      }
    }();
    unawaited(_iconRead);
  }

  @override
  void onRemove() {
    _removed = true;
    _iconGeneration++;
    body.iconImage?.dispose();
    body.iconImage = null;
    super.onRemove();
  }

  bool get usesDefaultTree => treeDef == null;
  bool get attentionBadge => blackboard['attention'] == 'true';

  bool get animations => body.animations;
  set animations(bool value) => body.animations = value;

  /// Whether the scene must keep drawing frames for this sphere: a tree of its own, a flag that makes the
  /// default tree move, or an effect still running.
  bool get needsFrames =>
      animations &&
      (treeDef != null ||
          blackboard['running'] == 'true' ||
          attentionBadge ||
          body.hasEffects);

  @override
  Future<void> onLoad() async {
    await super.onLoad();
    await add(body);
  }

  @override
  void changed() {
    size.setAll(96);
    super.changed();
    _readBoard();
    final resolvedProgress = parseGraphNodeProgress(
      progressParam: params['progress'] ?? blackboard['progress'],
      status: params['nodeStatus'],
      subtitle: params['description'],
      label: params['title'],
    );
    body
      ..size.setFrom(size)
      ..position.setValues(size.x / 2, size.y / 2)
      ..accentColor = sceneColor(style) ?? const Color(0xFF7FB069)
      ..icon = params['icon'] ?? ''
      ..nodeStatus = params['nodeStatus'] ?? ''
      ..selected = params['selected'] == 'true'
      ..presentationOpacity = params['state'] == 'unready' ? .45 : 1
      ..badge =
          attentionBadge ||
          params['state'] == 'error' ||
          params['nodeStatus'] == 'error' ||
          params['nodeStatus'] == 'failed'
      ..setLabels(params['title'] ?? '', params['description'] ?? '')
      ..setProgress(resolvedProgress);
    _readIcon();
    final key = treeDef == null ? '' : jsonEncode(treeDef!.toJson());
    if (key != _treeKey) {
      _treeKey = key;
      treeRoot = buildTree(treeDef ?? defaultIdleTree(), blackboard, body);
    }
    if (isMounted) game.wake();
  }

  void _readBoard() {
    blackboard.clear();
    final raw = params['blackboard'];
    if (raw == null) return;
    try {
      final decoded = jsonDecode(raw);
      if (decoded is Map) {
        for (final entry in decoded.entries) {
          blackboard['${entry.key}'] = '${entry.value}';
        }
      }
    } on FormatException {
      // The server validates the blackboard as JSON; an old shell reading a newer shape shows no facts.
    }
  }

  @override
  void update(double dt) {
    body.advance(dt);
    super.update(dt);
    if (needsFrames) game.wake();
  }
}
