import 'package:intochat_os_ui/intochat_os_ui.dart';
import 'package:flutter/material.dart';
import 'package:flutter/services.dart';

final class LoginScreen extends StatefulWidget {
  const LoginScreen({
    super.key,
    required this.onSubmit,
    this.onRegister,
    this.initialUsername = '',
    this.errorMessage,
  });
  final Future<String?> Function(String username, String password) onSubmit;
  final Future<String?> Function(String username, String password)? onRegister;
  final String initialUsername;
  final String? errorMessage;
  @override
  State<LoginScreen> createState() => _LoginScreenState();
}

final class _LoginScreenState extends State<LoginScreen> {
  final _form = GlobalKey<FormState>();
  late final _username = TextEditingController(text: widget.initialUsername);
  final _password = TextEditingController();
  final _confirmation = TextEditingController();
  final _passwordFocus = FocusNode();
  final _confirmationFocus = FocusNode();
  late String? _error = widget.errorMessage;
  bool _busy = false;
  bool _register = false;
  bool _reveal = false;

  @override
  void dispose() {
    _username.dispose();
    _password.dispose();
    _confirmation.dispose();
    _passwordFocus.dispose();
    _confirmationFocus.dispose();
    super.dispose();
  }

  Future<void> _submit() async {
    if (_busy || !_form.currentState!.validate()) return;
    setState(() {
      _busy = true;
      _error = null;
    });
    String? failure;
    try {
      failure = await (_register ? widget.onRegister! : widget.onSubmit)(
        _username.text.trim(),
        _password.text,
      );
      if (failure == null) TextInput.finishAutofillContext();
    } on Object {
      failure = 'Unable to connect. Please try again.';
    }
    if (!mounted) return;
    setState(() {
      _busy = false;
      _error = failure;
    });
  }

  void _switchMode() {
    _form.currentState!.reset();
    _password.clear();
    _confirmation.clear();
    setState(() {
      _register = !_register;
      _error = null;
      _reveal = false;
    });
  }

  @override
  Widget build(BuildContext context) {
    final colors = UiColors.of(context);
    final type = UiType.of(context);
    return Scaffold(
      backgroundColor: colors.recessed,
      body: SafeArea(
        child: Center(
          child: SingleChildScrollView(
            padding: const EdgeInsets.symmetric(horizontal: 24, vertical: 32),
            child: ConstrainedBox(
              constraints: const BoxConstraints(maxWidth: 400),
              child: AutofillGroup(
                onDisposeAction: AutofillContextAction.cancel,
                child: Form(
                  key: _form,
                  autovalidateMode: AutovalidateMode.onUserInteraction,
                  child: Column(
                    mainAxisSize: MainAxisSize.min,
                    crossAxisAlignment: CrossAxisAlignment.stretch,
                    children: [
                      Row(
                        children: [
                          Container(
                            padding: const EdgeInsets.all(10),
                            decoration: BoxDecoration(
                              color: colors.done.withValues(alpha: 0.12),
                              borderRadius: BorderRadius.circular(14),
                            ),
                            child: Icon(
                              Icons.blur_on_rounded,
                              color: colors.done,
                              size: 28,
                            ),
                          ),
                          const SizedBox(width: 12),
                          Text(
                            'IntoChat',
                            style: type.title.copyWith(fontSize: 20),
                          ),
                        ],
                      ),
                      const SizedBox(height: 32),
                      Text(
                        _register ? 'Your own space.' : 'Welcome back.',
                        style: type.heading.copyWith(fontSize: 32),
                      ),
                      const SizedBox(height: 8),
                      Text(
                        _register
                            ? 'Create an account to bring your ideas to life.'
                            : 'Sign in to your apps and workspace.',
                        style: type.bodyMuted,
                      ),
                      const SizedBox(height: 28),
                      TextFormField(
                        key: const Key('login-username'),
                        controller: _username,
                        enabled: !_busy,
                        autofocus: true,
                        autofillHints: const [AutofillHints.username],
                        autocorrect: false,
                        enableSuggestions: false,
                        textInputAction: TextInputAction.next,
                        decoration: InputDecoration(
                          labelText: 'Username',
                          helperText: _register
                              ? 'Lowercase letters, numbers and hyphens. Up to 80 characters.'
                              : null,
                          helperMaxLines: 2,
                        ),
                        validator: (value) {
                          final name = value?.trim() ?? '';
                          if (name.isEmpty) return 'Enter your username.';
                          if (_register &&
                              (name.length > 80 ||
                                  !RegExp(r'^[a-z0-9][a-z0-9-]*$')
                                      .hasMatch(name))) {
                            return 'Use lowercase letters, numbers and hyphens.';
                          }
                          return null;
                        },
                        onFieldSubmitted: (_) => _passwordFocus.requestFocus(),
                      ),
                      const SizedBox(height: 16),
                      TextFormField(
                        key: const Key('login-password'),
                        controller: _password,
                        focusNode: _passwordFocus,
                        enabled: !_busy,
                        obscureText: !_reveal,
                        autocorrect: false,
                        enableSuggestions: false,
                        autofillHints: [
                          _register
                              ? AutofillHints.newPassword
                              : AutofillHints.password,
                        ],
                        textInputAction: _register
                            ? TextInputAction.next
                            : TextInputAction.done,
                        decoration: InputDecoration(
                          labelText: 'Password',
                          helperText: _register
                              ? 'Use 12–256 characters.'
                              : null,
                          suffixIcon: IconButton(
                            tooltip: _reveal
                                ? 'Hide password'
                                : 'Show password',
                            onPressed: _busy
                                ? null
                                : () => setState(() => _reveal = !_reveal),
                            icon: Icon(
                              _reveal
                                  ? Icons.visibility_off_outlined
                                  : Icons.visibility_outlined,
                            ),
                          ),
                        ),
                        validator: (value) {
                          if (value == null || value.isEmpty) {
                            return 'Enter your password.';
                          }
                          if (_register &&
                              (value.length < 12 || value.length > 256)) {
                            return 'Use 12–256 characters.';
                          }
                          return null;
                        },
                        onFieldSubmitted: (_) => _register
                            ? _confirmationFocus.requestFocus()
                            : _submit(),
                      ),
                      if (_register) ...[
                        const SizedBox(height: 16),
                        TextFormField(
                          key: const Key('login-confirm-password'),
                          controller: _confirmation,
                          focusNode: _confirmationFocus,
                          enabled: !_busy,
                          obscureText: !_reveal,
                          autocorrect: false,
                          enableSuggestions: false,
                          autofillHints: const [AutofillHints.newPassword],
                          textInputAction: TextInputAction.done,
                          decoration: const InputDecoration(
                            labelText: 'Confirm password',
                          ),
                          validator: (value) => value == null || value.isEmpty
                              ? 'Confirm your password.'
                              : value != _password.text
                              ? 'Passwords do not match.'
                              : null,
                          onFieldSubmitted: (_) => _submit(),
                        ),
                      ],
                      if (_error case final error?) ...[
                        const SizedBox(height: 16),
                        Semantics(
                          liveRegion: true,
                          child: Text(
                            error,
                            key: const Key('login-error'),
                            style: type.body.copyWith(color: colors.error),
                          ),
                        ),
                      ],
                      const SizedBox(height: 24),
                      FilledButton(
                        key: const Key('login-submit'),
                        onPressed: _busy ? null : _submit,
                        style: FilledButton.styleFrom(
                          minimumSize: const Size.fromHeight(48),
                        ),
                        child: _busy
                            ? Row(
                                mainAxisSize: MainAxisSize.min,
                                children: [
                                  const SizedBox(
                                    height: 16,
                                    width: 16,
                                    child: CircularProgressIndicator(
                                      strokeWidth: 2,
                                    ),
                                  ),
                                  const SizedBox(width: 12),
                                  Text(
                                    _register
                                        ? 'Creating account…'
                                        : 'Signing in…',
                                  ),
                                ],
                              )
                            : Text(_register ? 'Create account' : 'Sign in'),
                      ),
                      if (widget.onRegister != null) ...[
                        const SizedBox(height: 12),
                        TextButton(
                          onPressed: _busy ? null : _switchMode,
                          child: Text(
                            _register
                                ? 'Already have an account? Sign in'
                                : 'Create account',
                          ),
                        ),
                      ],
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
