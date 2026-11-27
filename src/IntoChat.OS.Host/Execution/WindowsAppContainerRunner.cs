// os-only: Windows Job Object + AppContainer / LPAC process confinement runner (Paper 19 invariants).
using System.Collections.Concurrent;
using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using Microsoft.Extensions.Logging;
using Microsoft.Win32.SafeHandles;

namespace IntoChat.OS.Host.Execution;

/// <summary>
/// Confines Windows child processes inside a Job Object (<c>KILL_ON_JOB_CLOSE</c> + active process cap + RAM cap +
/// <c>0xFF</c> UI restrictions), explicit stdio handle allowlist (<c>PROC_THREAD_ATTRIBUTE_HANDLE_LIST</c>),
/// <c>CREATE_NO_WINDOW</c>, sanitized Unicode environment block (zero <c>MasterKey</c> or host secrets), and optional
/// AppContainer / Less-Privileged AppContainer token with automatic Job-Object fallback when unprivileged.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed partial class WindowsAppContainerRunner : IConfinedProcessRunner
{
    private static readonly string[] SafeSystemEnvironmentNames =
    [
        "SystemRoot",
        "SystemDrive",
        "windir",
        "ComSpec",
        "PATHEXT",
        "NUMBER_OF_PROCESSORS",
        "PROCESSOR_ARCHITECTURE",
        "ProgramFiles",
        "ProgramFiles(x86)",
        "ProgramData",
    ];

    internal delegate int CreateAppContainerProfileDelegate(
        string pszAppContainerName,
        string pszDisplayName,
        string pszDescription,
        IntPtr pCapabilities,
        uint dwCapabilityCount,
        out IntPtr ppSidAppContainerSid);

    internal readonly record struct WindowsJobLimitSummary(
        uint LimitFlags,
        uint ActiveProcessLimit,
        ulong JobMemoryLimitBytes,
        uint UiRestrictionFlags);

    // Instance-scoped cache (never static, respecting StaticCacheFacts) to skip redundant ReadAndExecute grants.
    private readonly ConcurrentDictionary<string, byte> _aclCache = new(StringComparer.OrdinalIgnoreCase);
    private readonly ILogger<WindowsAppContainerRunner>? _logger;
    private readonly CreateAppContainerProfileDelegate _createAppContainerProfile;

    public WindowsAppContainerRunner(ILogger<WindowsAppContainerRunner>? logger = null)
        : this(logger, Native.CreateAppContainerProfile)
    {
    }

    internal WindowsAppContainerRunner(
        ILogger<WindowsAppContainerRunner>? logger,
        CreateAppContainerProfileDelegate createAppContainerProfile)
    {
        _logger = logger;
        _createAppContainerProfile = createAppContainerProfile ?? Native.CreateAppContainerProfile;
    }

    public static IReadOnlyDictionary<string, string> BuildCleanEnvironmentDictionary(ConfinementSpec spec, string tempDirectory)
    {
        ArgumentNullException.ThrowIfNull(spec);
        ArgumentException.ThrowIfNullOrWhiteSpace(tempDirectory);

        var variables = new SortedDictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var name in SafeSystemEnvironmentNames)
        {
            if (Environment.GetEnvironmentVariable(name) is { Length: > 0 } value)
            {
                variables[name] = value;
            }
        }

        var systemDir = Environment.GetFolderPath(Environment.SpecialFolder.System);
        var windowsDir = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        var programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        variables["PATH"] = string.Join(
            ';',
            new[]
            {
                systemDir,
                windowsDir,
                Path.Combine(systemDir, "WindowsPowerShell", "v1.0"),
                Path.Combine(programFiles, "dotnet"),
                Path.Combine(programFiles, "Git", "cmd"),
                Path.Combine(programFiles, "GitHub CLI"),
            }.Where(static p => !string.IsNullOrWhiteSpace(p)));

        variables["TEMP"] = tempDirectory;
        variables["TMP"] = tempDirectory;
        variables["USERPROFILE"] = spec.WorkingDirectory;
        variables["APPDATA"] = Path.Combine(tempDirectory, "appdata");
        variables["LOCALAPPDATA"] = Path.Combine(tempDirectory, "localappdata");
        variables["DOTNET_CLI_TELEMETRY_OPTOUT"] = "1";
        variables["DOTNET_SKIP_FIRST_TIME_EXPERIENCE"] = "1";
        variables["DOTNET_NOLOGO"] = "1";
        // AppContainer tokens cannot call CreateFileMapping(PAGE_EXECUTE_READWRITE) used by CoreCLR W^X double-mapping
        // (causes coreclr.dll DllMain ERROR_DLL_INIT_FAILED 0x8007045A); disable W^X double-mapping for confined .NET hosts.
        variables["DOTNET_EnableWriteXorExecute"] = "0";
        variables["DOTNET_EnableDiagnostics"] = "0";
        variables["SPIKE_WINDOWS"] = "0";

        foreach (var (key, value) in spec.CleanEnvironment)
        {
            if (!ChildEnvironmentPolicy.IsBlockedSecretVariableName(key))
            {
                variables[key] = value;
            }
        }

        return variables;
    }

    public static string BuildUnicodeEnvironmentBlock(IReadOnlyDictionary<string, string> variables)
    {
        ArgumentNullException.ThrowIfNull(variables);
        var sorted = new SortedDictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (k, v) in variables)
        {
            if (!string.IsNullOrWhiteSpace(k) && !k.Contains('=', StringComparison.Ordinal) && !k.Contains('\0', StringComparison.Ordinal))
            {
                sorted[k] = (v ?? string.Empty).Replace("\0", string.Empty, StringComparison.Ordinal);
            }
        }

        var sb = new StringBuilder();
        foreach (var (name, value) in sorted)
        {
            sb.Append(name).Append('=').Append(value).Append('\0');
        }

        sb.Append('\0');
        return sb.ToString();
    }

    public static string FormatCommandLine(string executablePath, IReadOnlyList<string> arguments)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(executablePath);
        ArgumentNullException.ThrowIfNull(arguments);

        var sb = new StringBuilder();
        AppendQuotedArgument(sb, executablePath);
        foreach (var arg in arguments)
        {
            sb.Append(' ');
            AppendQuotedArgument(sb, arg ?? string.Empty);
        }

        return sb.ToString();
    }

    public ConfinedProcessSession StartSession(ConfinementSpec spec)
    {
        ArgumentNullException.ThrowIfNull(spec);
        ArgumentException.ThrowIfNullOrWhiteSpace(spec.ExecutablePath);
        ArgumentException.ThrowIfNullOrWhiteSpace(spec.WorkingDirectory);

        if (!OperatingSystem.IsWindows())
        {
            var posix = new PosixConfinedProcessRunner();
            return posix.StartSession(spec);
        }

        var workDir = Path.GetFullPath(spec.WorkingDirectory);
        var tempDir = CreateSessionTempDirectory(spec.SandboxId);
        Directory.CreateDirectory(workDir);

        var cleanDict = BuildCleanEnvironmentDictionary(spec, tempDir);
        var unicodeEnvBlock = BuildUnicodeEnvironmentBlock(cleanDict);
        var commandLineText = FormatCommandLine(spec.ExecutablePath, spec.Arguments);

        var job = CreateConfiguredJobObject(spec, out var iocp);
        var grantedWorkDirs = new List<(string Folder, SecurityIdentifier Sid)>();

        IntPtr outRead = IntPtr.Zero, outWrite = IntPtr.Zero;
        IntPtr errRead = IntPtr.Zero, errWrite = IntPtr.Zero;
        IntPtr inRead = IntPtr.Zero, inWrite = IntPtr.Zero;

        try
        {
            var sa = new Native.SECURITY_ATTRIBUTES
            {
                nLength = Marshal.SizeOf<Native.SECURITY_ATTRIBUTES>(),
                bInheritHandle = 1,
            };

            CreateInheritableChildPipe(ref sa, childReads: false, out outRead, out outWrite);
            CreateInheritableChildPipe(ref sa, childReads: false, out errRead, out errWrite);
            CreateInheritableChildPipe(ref sa, childReads: true, out inRead, out inWrite);

            var procInfo = LaunchWithFallback(
                spec,
                workDir,
                tempDir,
                commandLineText,
                unicodeEnvBlock,
                job,
                inRead,
                outWrite,
                errWrite,
                grantedWorkDirs,
                out var effectiveMode);

            // Parent must close child ends of stdio pipes immediately so EOF propagates cleanly when child exits.
            _ = Native.CloseHandle(inRead);
            inRead = IntPtr.Zero;
            _ = Native.CloseHandle(outWrite);
            outWrite = IntPtr.Zero;
            _ = Native.CloseHandle(errWrite);
            errWrite = IntPtr.Zero;
            _ = Native.CloseHandle(procInfo.hThread);

            var processHandle = new SafeProcessHandle(procInfo.hProcess, ownsHandle: true);
            var stdinWriter = new StreamWriter(
                new FileStream(new SafeFileHandle(inWrite, ownsHandle: true), FileAccess.Write),
                new UTF8Encoding(encoderShouldEmitUTF8Identifier: false))
            {
                AutoFlush = false,
            };
            inWrite = IntPtr.Zero;
            var stdoutReader = new StreamReader(
                new FileStream(new SafeFileHandle(outRead, ownsHandle: true), FileAccess.Read),
                Encoding.UTF8);
            outRead = IntPtr.Zero;
            var stderrReader = new StreamReader(
                new FileStream(new SafeFileHandle(errRead, ownsHandle: true), FileAccess.Read),
                Encoding.UTF8);
            errRead = IntPtr.Zero;

            return new WindowsConfinedProcessSession(
                this,
                procInfo.dwProcessId,
                processHandle,
                job,
                iocp,
                effectiveMode,
                tempDir,
                stdinWriter,
                stdoutReader,
                stderrReader,
                grantedWorkDirs);
        }
        catch
        {
            if (inRead != IntPtr.Zero) { _ = Native.CloseHandle(inRead); }
            if (inWrite != IntPtr.Zero) { _ = Native.CloseHandle(inWrite); }
            if (outRead != IntPtr.Zero) { _ = Native.CloseHandle(outRead); }
            if (outWrite != IntPtr.Zero) { _ = Native.CloseHandle(outWrite); }
            if (errRead != IntPtr.Zero) { _ = Native.CloseHandle(errRead); }
            if (errWrite != IntPtr.Zero) { _ = Native.CloseHandle(errWrite); }

            TerminateAndDrainJob(job, iocp);
            if (job != IntPtr.Zero) { _ = Native.CloseHandle(job); }
            if (iocp != IntPtr.Zero) { _ = Native.CloseHandle(iocp); }

            RevokeGrantedAccess(grantedWorkDirs);
            TryPurgeDirectory(tempDir, _logger);
            throw;
        }
    }

    public async Task<ConfinedProcessResult> RunAsync(ConfinementSpec spec, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(spec);
        ArgumentException.ThrowIfNullOrWhiteSpace(spec.ExecutablePath);
        ArgumentException.ThrowIfNullOrWhiteSpace(spec.WorkingDirectory);

        if (!OperatingSystem.IsWindows())
        {
            var posix = new PosixConfinedProcessRunner();
            return await posix.RunAsync(spec, cancellationToken).ConfigureAwait(false);
        }

        var workDir = Path.GetFullPath(spec.WorkingDirectory);
        var tempDir = CreateSessionTempDirectory(spec.SandboxId);
        Directory.CreateDirectory(workDir);

        var cleanDict = BuildCleanEnvironmentDictionary(spec, tempDir);
        var unicodeEnvBlock = BuildUnicodeEnvironmentBlock(cleanDict);
        var commandLineText = FormatCommandLine(spec.ExecutablePath, spec.Arguments);

        var sw = Stopwatch.StartNew();
        var job = CreateConfiguredJobObject(spec, out var iocp);
        var grantedWorkDirs = new List<(string Folder, SecurityIdentifier Sid)>();

        IntPtr outRead = IntPtr.Zero, outWrite = IntPtr.Zero;
        IntPtr errRead = IntPtr.Zero, errWrite = IntPtr.Zero;
        IntPtr inRead = IntPtr.Zero, inWrite = IntPtr.Zero;

        try
        {
            var sa = new Native.SECURITY_ATTRIBUTES
            {
                nLength = Marshal.SizeOf<Native.SECURITY_ATTRIBUTES>(),
                bInheritHandle = 1,
            };

            CreateInheritableChildPipe(ref sa, childReads: false, out outRead, out outWrite);
            CreateInheritableChildPipe(ref sa, childReads: false, out errRead, out errWrite);
            CreateInheritableChildPipe(ref sa, childReads: true, out inRead, out inWrite);

            // Close stdin write end immediately so interactive prompts inside confined tools receive EOF instead of hanging.
            _ = Native.CloseHandle(inWrite);
            inWrite = IntPtr.Zero;

            var procInfo = LaunchWithFallback(
                spec,
                workDir,
                tempDir,
                commandLineText,
                unicodeEnvBlock,
                job,
                inRead,
                outWrite,
                errWrite,
                grantedWorkDirs,
                out var effectiveMode);

            // Parent must close child ends of stdio pipes immediately so EOF propagates cleanly when child exits.
            _ = Native.CloseHandle(inRead);
            inRead = IntPtr.Zero;
            _ = Native.CloseHandle(outWrite);
            outWrite = IntPtr.Zero;
            _ = Native.CloseHandle(errWrite);
            errWrite = IntPtr.Zero;
            _ = Native.CloseHandle(procInfo.hThread);

            using var processHandle = new SafeProcessHandle(procInfo.hProcess, ownsHandle: true);
            using var stdoutReader = new StreamReader(new FileStream(new SafeFileHandle(outRead, ownsHandle: true), FileAccess.Read), Encoding.UTF8);
            outRead = IntPtr.Zero;
            using var stderrReader = new StreamReader(new FileStream(new SafeFileHandle(errRead, ownsHandle: true), FileAccess.Read), Encoding.UTF8);
            errRead = IntPtr.Zero;

            var stdoutTask = stdoutReader.ReadToEndAsync(CancellationToken.None);
            var stderrTask = stderrReader.ReadToEndAsync(CancellationToken.None);

            var timedOut = false;
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeoutCts.CancelAfter(spec.EffectiveTimeout);

            try
            {
                await WaitForProcessHandleAsync(processHandle, timeoutCts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                timedOut = true;
            }
            catch (OperationCanceledException)
            {
                TerminateAndDrainJob(job, iocp);
                throw;
            }

            // Paper 19 invariant: always terminate & drain the entire Job Object BEFORE revoking any temporary ACLs.
            TerminateAndDrainJob(job, iocp);

            _ = Native.GetExitCodeProcess(processHandle.DangerousGetHandle(), out var rawExitCode);

            var stdout = await stdoutTask.ConfigureAwait(false);
            var stderr = await stderrTask.ConfigureAwait(false);
            sw.Stop();

            return new ConfinedProcessResult(
                ExitCode: timedOut ? -1 : unchecked((int)rawExitCode),
                StandardOutput: stdout,
                StandardError: stderr,
                TimedOut: timedOut,
                Duration: sw.Elapsed,
                EffectiveMode: effectiveMode);
        }
        finally
        {
            if (inRead != IntPtr.Zero) { _ = Native.CloseHandle(inRead); }
            if (inWrite != IntPtr.Zero) { _ = Native.CloseHandle(inWrite); }
            if (outRead != IntPtr.Zero) { _ = Native.CloseHandle(outRead); }
            if (outWrite != IntPtr.Zero) { _ = Native.CloseHandle(outWrite); }
            if (errRead != IntPtr.Zero) { _ = Native.CloseHandle(errRead); }
            if (errWrite != IntPtr.Zero) { _ = Native.CloseHandle(errWrite); }

            TerminateAndDrainJob(job, iocp);
            if (job != IntPtr.Zero) { _ = Native.CloseHandle(job); }
            if (iocp != IntPtr.Zero) { _ = Native.CloseHandle(iocp); }

            RevokeGrantedAccess(grantedWorkDirs);
            TryPurgeDirectory(tempDir, _logger);
        }
    }

    private Native.PROCESS_INFORMATION LaunchWithFallback(
        ConfinementSpec spec,
        string workDir,
        string tempDir,
        string commandLineText,
        string unicodeEnvBlock,
        IntPtr job,
        IntPtr inRead,
        IntPtr outWrite,
        IntPtr errWrite,
        List<(string Folder, SecurityIdentifier Sid)> grantedWorkDirs,
        out ConfinementMode effectiveMode)
    {
        var wantAppContainer = spec.PreferAppContainer || spec.UseLessPrivilegedAppContainer;
        effectiveMode = ConfinementMode.JobObject;

        if (wantAppContainer)
        {
            try
            {
                return LaunchNativeProcess(
                    spec,
                    workDir,
                    tempDir,
                    commandLineText,
                    unicodeEnvBlock,
                    job,
                    inRead,
                    outWrite,
                    errWrite,
                    useAppContainer: true,
                    grantedWorkDirs,
                    out effectiveMode);
            }
            catch (Exception ex) when (ex is Win32Exception or InvalidOperationException or UnauthorizedAccessException)
            {
                // Revoke any partial ACLs granted during the failed AppContainer attempt before falling back.
                RevokeGrantedAccess(grantedWorkDirs);
                grantedWorkDirs.Clear();

                var limits = ComputeJobLimits(spec);
                if (_logger is not null)
                {
                    _logger.LogWarning(
                        ex,
                        "AppContainer isolation unavailable for sandbox {SandboxId} (LPAC={UseLpac}); falling back to degraded JobObject confinement while unconditionally enforcing Win32 Job Object limits (MemoryBytes={MaxMemoryBytes}, ActiveProcesses={MaxProcesses}, UIRestrictions=0x{UiRestrictions:X}, KillOnClose=true), 3-handle stdio allowlist, sanitized environment, and temp-dir purge.",
                        spec.SandboxId,
                        spec.UseLessPrivilegedAppContainer,
                        limits.JobMemoryLimitBytes,
                        limits.ActiveProcessLimit,
                        limits.UiRestrictionFlags);
                }
                else
                {
                    Trace.TraceWarning(
                        "AppContainer isolation unavailable for sandbox '{0}' (LPAC={1}); falling back to degraded JobObject confinement with unconditional Win32 Job Object limits (MemoryBytes={2}, ActiveProcesses={3}, UIRestrictions=0x{4:X}): {5}",
                        spec.SandboxId,
                        spec.UseLessPrivilegedAppContainer,
                        limits.JobMemoryLimitBytes,
                        limits.ActiveProcessLimit,
                        limits.UiRestrictionFlags,
                        ex.Message);
                }
            }
        }

        return LaunchNativeProcess(
            spec,
            workDir,
            tempDir,
            commandLineText,
            unicodeEnvBlock,
            job,
            inRead,
            outWrite,
            errWrite,
            useAppContainer: false,
            grantedWorkDirs,
            out effectiveMode);
    }

    private Native.PROCESS_INFORMATION LaunchNativeProcess(
        ConfinementSpec spec,
        string workDir,
        string tempDir,
        string commandLineText,
        string unicodeEnvBlock,
        IntPtr job,
        IntPtr childStdIn,
        IntPtr childStdOut,
        IntPtr childStdErr,
        bool useAppContainer,
        List<(string Folder, SecurityIdentifier Sid)> grantedWorkDirs,
        out ConfinementMode effectiveMode)
    {
        var pinned = new List<IntPtr>();
        var attrList = IntPtr.Zero;
        var envBlockPtr = IntPtr.Zero;
        var appContainerSid = IntPtr.Zero;
        var capabilitySid = IntPtr.Zero;

        try
        {
            var isLpac = useAppContainer && spec.UseLessPrivilegedAppContainer;
            // Attributes: (1) HANDLE_LIST, (2) JOB_LIST, + optional (3) SECURITY_CAPABILITIES, + optional (4) ALL_APPLICATION_PACKAGES_POLICY
            var attributeCount = 2 + (useAppContainer ? 1 : 0) + (isLpac ? 1 : 0);

            var attrSize = IntPtr.Zero;
            _ = Native.InitializeProcThreadAttributeList(IntPtr.Zero, attributeCount, 0, ref attrSize);
            attrList = Marshal.AllocHGlobal(attrSize);
            CheckWin32(Native.InitializeProcThreadAttributeList(attrList, attributeCount, 0, ref attrSize), "InitializeProcThreadAttributeList");

            // 1. Explicit handle allowlist (Paper 19 invariant: prevent inheriting parent sockets, files, or tokens).
            var handleArray = Marshal.AllocHGlobal(IntPtr.Size * 3);
            pinned.Add(handleArray);
            Marshal.WriteIntPtr(handleArray, 0, childStdIn);
            Marshal.WriteIntPtr(handleArray, IntPtr.Size, childStdOut);
            Marshal.WriteIntPtr(handleArray, IntPtr.Size * 2, childStdErr);
            CheckWin32(
                Native.UpdateProcThreadAttribute(
                    attrList,
                    0,
                    Native.PROC_THREAD_ATTRIBUTE_HANDLE_LIST,
                    handleArray,
                    IntPtr.Size * 3,
                    IntPtr.Zero,
                    IntPtr.Zero),
                "UpdateProcThreadAttribute(HANDLE_LIST)");

            // 2. Atomic Job Object assignment at CreateProcess time.
            var jobPtr = Marshal.AllocHGlobal(IntPtr.Size);
            pinned.Add(jobPtr);
            Marshal.WriteIntPtr(jobPtr, job);
            CheckWin32(
                Native.UpdateProcThreadAttribute(
                    attrList,
                    0,
                    Native.PROC_THREAD_ATTRIBUTE_JOB_LIST,
                    jobPtr,
                    IntPtr.Size,
                    IntPtr.Zero,
                    IntPtr.Zero),
                "UpdateProcThreadAttribute(JOB_LIST)");

            if (useAppContainer)
            {
                var hashHex = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(spec.SandboxId + "|" + workDir.ToLowerInvariant())))[..16];
                var profileName = "intochat.sbx." + hashHex.ToLowerInvariant();
                var hr = _createAppContainerProfile(
                    profileName,
                    "IntoChat OS Sandbox",
                    "Confined IntoChat OS child process",
                    IntPtr.Zero,
                    0,
                    out appContainerSid);

                if (hr == unchecked((int)0x800700B7)) // ERROR_ALREADY_EXISTS
                {
                    hr = Native.DeriveAppContainerSidFromAppContainerName(profileName, out appContainerSid);
                }

                if (hr != 0 || appContainerSid == IntPtr.Zero)
                {
                    throw new InvalidOperationException($"CreateAppContainerProfile failed with HRESULT 0x{hr:X8}.");
                }

                var securityId = new SecurityIdentifier(appContainerSid);
                GrantDirectoryAccess(tempDir, securityId, FileSystemRights.Modify, cacheable: false);
                grantedWorkDirs.Add((tempDir, securityId));

                if (!string.Equals(workDir, tempDir, StringComparison.OrdinalIgnoreCase))
                {
                    GrantDirectoryAccess(workDir, securityId, FileSystemRights.Modify, cacheable: false);
                    grantedWorkDirs.Add((workDir, securityId));
                }

                foreach (var rw in spec.ReadWritePaths)
                {
                    var fullRw = Path.GetFullPath(rw);
                    if (Directory.Exists(fullRw)
                        && !string.Equals(fullRw, workDir, StringComparison.OrdinalIgnoreCase)
                        && !string.Equals(fullRw, tempDir, StringComparison.OrdinalIgnoreCase))
                    {
                        GrantDirectoryAccess(fullRw, securityId, FileSystemRights.Modify, cacheable: false);
                        grantedWorkDirs.Add((fullRw, securityId));
                    }
                }

                foreach (var ro in spec.ReadOnlyPaths)
                {
                    var fullRo = Path.GetFullPath(ro);
                    if (Directory.Exists(fullRo))
                    {
                        GrantDirectoryAccess(fullRo, securityId, FileSystemRights.ReadAndExecute, cacheable: true);
                    }
                }

                var exeDir = Path.GetDirectoryName(Path.GetFullPath(spec.ExecutablePath));
                if (!string.IsNullOrEmpty(exeDir) && Directory.Exists(exeDir) && !IsStandardWindowsPackageReadableFolder(exeDir))
                {
                    GrantDirectoryAccess(exeDir, securityId, FileSystemRights.ReadAndExecute, cacheable: true);
                }

                var capPtr = IntPtr.Zero;
                uint capCount = 0;
                if (spec.Network == NetworkPolicy.PublicInternetOnly)
                {
                    // S-1-15-3-1 = internetClient (outbound public internet, still blocks loopback & private LAN).
                    CheckWin32(Native.ConvertStringSidToSidW("S-1-15-3-1", out capabilitySid), "ConvertStringSidToSidW(internetClient)");
                    var sidAndAttributesSize = IntPtr.Size * 2;
                    capPtr = Marshal.AllocHGlobal(sidAndAttributesSize);
                    pinned.Add(capPtr);
                    Marshal.WriteIntPtr(capPtr, 0, capabilitySid);
                    Marshal.WriteInt32(capPtr, IntPtr.Size, 0x4 /* SE_GROUP_ENABLED */);
                    capCount = 1;
                }

                var secCaps = new Native.SECURITY_CAPABILITIES
                {
                    AppContainerSid = appContainerSid,
                    Capabilities = capPtr,
                    CapabilityCount = capCount,
                };
                var secCapsPtr = Marshal.AllocHGlobal(Marshal.SizeOf<Native.SECURITY_CAPABILITIES>());
                pinned.Add(secCapsPtr);
                Marshal.StructureToPtr(secCaps, secCapsPtr, fDeleteOld: false);

                CheckWin32(
                    Native.UpdateProcThreadAttribute(
                        attrList,
                        0,
                        Native.PROC_THREAD_ATTRIBUTE_SECURITY_CAPABILITIES,
                        secCapsPtr,
                        Marshal.SizeOf<Native.SECURITY_CAPABILITIES>(),
                        IntPtr.Zero,
                        IntPtr.Zero),
                    "UpdateProcThreadAttribute(SECURITY_CAPABILITIES)");

                if (isLpac)
                {
                    var lpacPolicy = Marshal.AllocHGlobal(sizeof(int));
                    pinned.Add(lpacPolicy);
                    Marshal.WriteInt32(lpacPolicy, Native.PROCESS_CREATION_ALL_APPLICATION_PACKAGES_OPT_OUT);
                    CheckWin32(
                        Native.UpdateProcThreadAttribute(
                            attrList,
                            0,
                            Native.PROC_THREAD_ATTRIBUTE_ALL_APPLICATION_PACKAGES_POLICY,
                            lpacPolicy,
                            sizeof(int),
                            IntPtr.Zero,
                            IntPtr.Zero),
                        "UpdateProcThreadAttribute(ALL_APPLICATION_PACKAGES_POLICY)");
                }
            }

            var startup = new Native.STARTUPINFOEX();
            startup.StartupInfo.cb = Marshal.SizeOf<Native.STARTUPINFOEX>();
            startup.StartupInfo.dwFlags = Native.STARTF_USESTDHANDLES;
            startup.StartupInfo.hStdInput = childStdIn;
            startup.StartupInfo.hStdOutput = childStdOut;
            startup.StartupInfo.hStdError = childStdErr;
            startup.lpAttributeList = attrList;

            envBlockPtr = Marshal.StringToHGlobalUni(unicodeEnvBlock);
            var mutableCmd = new StringBuilder(commandLineText);
            var creationFlags = Native.EXTENDED_STARTUPINFO_PRESENT | Native.CREATE_UNICODE_ENVIRONMENT | Native.CREATE_NO_WINDOW;

            CheckWin32(
                Native.CreateProcessW(
                    null,
                    mutableCmd,
                    IntPtr.Zero,
                    IntPtr.Zero,
                    bInheritHandles: true,
                    creationFlags,
                    envBlockPtr,
                    workDir,
                    ref startup,
                    out var processInfo),
                "CreateProcessW");

            effectiveMode = useAppContainer
                ? (spec.UseLessPrivilegedAppContainer ? ConfinementMode.LessPrivilegedAppContainer : ConfinementMode.AppContainer)
                : ConfinementMode.JobObject;

            return processInfo;
        }
        finally
        {
            if (envBlockPtr != IntPtr.Zero)
            {
                Marshal.FreeHGlobal(envBlockPtr);
            }

            if (attrList != IntPtr.Zero)
            {
                Native.DeleteProcThreadAttributeList(attrList);
                Marshal.FreeHGlobal(attrList);
            }

            foreach (var ptr in pinned)
            {
                Marshal.FreeHGlobal(ptr);
            }

            if (capabilitySid != IntPtr.Zero)
            {
                _ = Native.LocalFree(capabilitySid);
            }

            if (appContainerSid != IntPtr.Zero)
            {
                _ = Native.FreeSid(appContainerSid);
            }
        }
    }

    private void GrantDirectoryAccess(string folder, SecurityIdentifier sid, FileSystemRights rights, bool cacheable)
    {
        var key = $"{sid.Value}|{folder}|{(int)rights}";
        if (cacheable && _aclCache.ContainsKey(key))
        {
            return;
        }

        var dirInfo = new DirectoryInfo(folder);
        var security = dirInfo.GetAccessControl();
        security.AddAccessRule(new FileSystemAccessRule(
            sid,
            rights,
            InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit,
            PropagationFlags.None,
            AccessControlType.Allow));
        dirInfo.SetAccessControl(security);

        if (cacheable)
        {
            _aclCache.TryAdd(key, 1);
        }
    }

    private void TryRevokeAccess(string folder, SecurityIdentifier sid)
    {
        try
        {
            if (!Directory.Exists(folder))
            {
                return;
            }

            var dirInfo = new DirectoryInfo(folder);
            var security = dirInfo.GetAccessControl();
            security.PurgeAccessRules(sid);
            dirInfo.SetAccessControl(security);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or SystemException)
        {
            if (_logger is not null)
            {
                _logger.LogDebug(ex, "Could not revoke temporary AppContainer ACL on {Folder}.", folder);
            }
            else
            {
                Trace.TraceWarning("Could not revoke temporary AppContainer ACL on '{0}': {1}", folder, ex.Message);
            }
        }
    }

    private static bool IsStandardWindowsPackageReadableFolder(string path)
    {
        var windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        var programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        var programFilesX86 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
        return (!string.IsNullOrEmpty(windows) && path.StartsWith(windows, StringComparison.OrdinalIgnoreCase))
            || (!string.IsNullOrEmpty(programFiles) && path.StartsWith(programFiles, StringComparison.OrdinalIgnoreCase))
            || (!string.IsNullOrEmpty(programFilesX86) && path.StartsWith(programFilesX86, StringComparison.OrdinalIgnoreCase));
    }

    internal static WindowsJobLimitSummary ComputeJobLimits(ConfinementSpec spec)
    {
        ArgumentNullException.ThrowIfNull(spec);
        var maxProcesses = spec.MaxProcesses > 0 ? (uint)spec.MaxProcesses : 16u;
        var maxMemoryBytes = spec.MaxMemoryBytes > 0 ? (ulong)spec.MaxMemoryBytes : 1024UL * 1024UL * 1024UL;
        var limitFlags =
            Native.JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE
            | Native.JOB_OBJECT_LIMIT_DIE_ON_UNHANDLED_EXCEPTION
            | Native.JOB_OBJECT_LIMIT_ACTIVE_PROCESS
            | Native.JOB_OBJECT_LIMIT_JOB_MEMORY;

        return new WindowsJobLimitSummary(
            LimitFlags: limitFlags,
            ActiveProcessLimit: maxProcesses,
            JobMemoryLimitBytes: maxMemoryBytes,
            UiRestrictionFlags: Native.JOB_OBJECT_UILIMIT_ALL);
    }

    internal static WindowsJobLimitSummary QueryJobLimits(IntPtr job)
    {
        var limitSize = Marshal.SizeOf<Native.JOBOBJECT_EXTENDED_LIMIT_INFORMATION>();
        var buffer = Marshal.AllocHGlobal(limitSize);
        try
        {
            CheckWin32(
                Native.QueryInformationJobObject(job, Native.JobObjectExtendedLimitInformation, buffer, limitSize, out _),
                "QueryInformationJobObject(ExtendedLimits)");
            var extended = Marshal.PtrToStructure<Native.JOBOBJECT_EXTENDED_LIMIT_INFORMATION>(buffer);

            CheckWin32(
                Native.QueryInformationJobObject(job, Native.JobObjectBasicUIRestrictions, buffer, sizeof(int), out _),
                "QueryInformationJobObject(BasicUIRestrictions)");
            var uiRestrictions = unchecked((uint)Marshal.ReadInt32(buffer));

            return new WindowsJobLimitSummary(
                LimitFlags: extended.BasicLimitInformation.LimitFlags,
                ActiveProcessLimit: extended.BasicLimitInformation.ActiveProcessLimit,
                JobMemoryLimitBytes: extended.JobMemoryLimit.ToUInt64(),
                UiRestrictionFlags: uiRestrictions);
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    private static IntPtr CreateConfiguredJobObject(ConfinementSpec spec, out IntPtr iocp)
    {
        var job = Native.CreateJobObjectW(IntPtr.Zero, null);
        if (job == IntPtr.Zero)
        {
            throw new Win32Exception(Marshal.GetLastWin32Error(), "CreateJobObjectW failed.");
        }

        iocp = IntPtr.Zero;
        var computed = ComputeJobLimits(spec);
        var limitSize = Marshal.SizeOf<Native.JOBOBJECT_EXTENDED_LIMIT_INFORMATION>();
        var buffer = Marshal.AllocHGlobal(limitSize);
        try
        {
            var limits = new Native.JOBOBJECT_EXTENDED_LIMIT_INFORMATION();
            limits.BasicLimitInformation.LimitFlags = computed.LimitFlags;
            limits.BasicLimitInformation.ActiveProcessLimit = computed.ActiveProcessLimit;
            limits.JobMemoryLimit = (UIntPtr)computed.JobMemoryLimitBytes;

            Marshal.StructureToPtr(limits, buffer, fDeleteOld: false);
            CheckWin32(
                Native.SetInformationJobObject(job, Native.JobObjectExtendedLimitInformation, buffer, limitSize),
                "SetInformationJobObject(ExtendedLimits)");

            // 0xFF = all JOB_OBJECT_UILIMIT_* flags (clipboard read/write, handles, desktop, display, system params, atoms, exit windows).
            Marshal.WriteInt32(buffer, unchecked((int)computed.UiRestrictionFlags));
            CheckWin32(
                Native.SetInformationJobObject(job, Native.JobObjectBasicUIRestrictions, buffer, sizeof(int)),
                "SetInformationJobObject(BasicUIRestrictions)");

            iocp = Native.CreateIoCompletionPort(new IntPtr(-1), IntPtr.Zero, UIntPtr.Zero, 1);
            if (iocp != IntPtr.Zero)
            {
                var assoc = new Native.JOBOBJECT_ASSOCIATE_COMPLETION_PORT
                {
                    CompletionKey = job,
                    CompletionPort = iocp,
                };
                var assocSize = Marshal.SizeOf<Native.JOBOBJECT_ASSOCIATE_COMPLETION_PORT>();
                Marshal.StructureToPtr(assoc, buffer, fDeleteOld: false);
                _ = Native.SetInformationJobObject(job, Native.JobObjectAssociateCompletionPortInformation, buffer, assocSize);
            }

            return job;
        }
        catch
        {
            if (iocp != IntPtr.Zero) { _ = Native.CloseHandle(iocp); }
            _ = Native.CloseHandle(job);
            throw;
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    private static void TerminateAndDrainJob(IntPtr job, IntPtr iocp)
    {
        if (job == IntPtr.Zero)
        {
            return;
        }

        _ = Native.TerminateJobObject(job, 1);

        if (iocp == IntPtr.Zero)
        {
            return;
        }

        var deadline = Environment.TickCount64 + 2_000;
        while (Environment.TickCount64 < deadline)
        {
            if (!Native.GetQueuedCompletionStatus(iocp, out var msgId, out var key, out _, 50))
            {
                break;
            }

            if (key == job && msgId == Native.JOB_OBJECT_MSG_ACTIVE_PROCESS_ZERO)
            {
                break;
            }
        }
    }

    private static void CreateInheritableChildPipe(
        ref Native.SECURITY_ATTRIBUTES sa,
        bool childReads,
        out IntPtr readHandle,
        out IntPtr writeHandle)
    {
        CheckWin32(Native.CreatePipe(out readHandle, out writeHandle, ref sa, 0), "CreatePipe");
        var parentOnlyHandle = childReads ? writeHandle : readHandle;
        CheckWin32(Native.SetHandleInformation(parentOnlyHandle, Native.HANDLE_FLAG_INHERIT, 0), "SetHandleInformation");
    }

    private static Task WaitForProcessHandleAsync(SafeProcessHandle handle, CancellationToken cancellationToken)
    {
        var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var waitObject = new ProcessWaitHandle(handle);

        RegisteredWaitHandle? registeredWait = null;
        CancellationTokenRegistration ctr = default;

        registeredWait = ThreadPool.RegisterWaitForSingleObject(
            waitObject,
            (state, _) =>
            {
                var source = (TaskCompletionSource<bool>)state!;
                source.TrySetResult(true);
            },
            tcs,
            Timeout.Infinite,
            executeOnlyOnce: true);

        if (cancellationToken.CanBeCanceled)
        {
            ctr = cancellationToken.Register(
                static state => ((TaskCompletionSource<bool>)state!).TrySetCanceled(),
                tcs);
        }

        return CleanupAfterAsync(tcs.Task, registeredWait, waitObject, ctr);
    }

    private static async Task CleanupAfterAsync(
        Task task,
        RegisteredWaitHandle registeredWait,
        WaitHandle waitHandle,
        CancellationTokenRegistration ctr)
    {
        try
        {
            await task.ConfigureAwait(false);
        }
        finally
        {
            _ = registeredWait.Unregister(null);
            await ctr.DisposeAsync().ConfigureAwait(false);
            waitHandle.Dispose();
        }
    }

    private static void AppendQuotedArgument(StringBuilder sb, string argument)
    {
        if (argument.Length > 0 && argument.IndexOfAny([' ', '\t', '\n', '\v', '"']) < 0)
        {
            sb.Append(argument);
            return;
        }

        sb.Append('"');
        var backslashes = 0;
        foreach (var ch in argument)
        {
            if (ch == '\\')
            {
                backslashes++;
            }
            else if (ch == '"')
            {
                sb.Append('\\', backslashes * 2 + 1);
                sb.Append('"');
                backslashes = 0;
            }
            else
            {
                if (backslashes > 0)
                {
                    sb.Append('\\', backslashes);
                    backslashes = 0;
                }

                sb.Append(ch);
            }
        }

        if (backslashes > 0)
        {
            sb.Append('\\', backslashes * 2);
        }

        sb.Append('"');
    }

    private void RevokeGrantedAccess(List<(string Folder, SecurityIdentifier Sid)> grantedWorkDirs)
    {
        foreach (var (folder, sid) in grantedWorkDirs)
        {
            TryRevokeAccess(folder, sid);
        }
    }

    internal static string CreateSessionTempDirectory(string sandboxId)
    {
        var sanitized = SanitizeSandboxId(sandboxId);
        var tempDir = Path.Combine(Path.GetTempPath(), "intochat-sandbox", $"{sanitized}-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        Directory.CreateDirectory(Path.Combine(tempDir, "appdata"));
        Directory.CreateDirectory(Path.Combine(tempDir, "localappdata"));
        return tempDir;
    }

    internal static void TryPurgeDirectory(string directory, ILogger? logger)
    {
        if (string.IsNullOrWhiteSpace(directory) || !Directory.Exists(directory))
        {
            return;
        }

        for (var attempt = 0; attempt < 5; attempt++)
        {
            try
            {
                if (!Directory.Exists(directory))
                {
                    return;
                }

                Directory.Delete(directory, recursive: true);
                return;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                if (attempt == 4)
                {
                    if (logger is not null)
                    {
                        logger.LogWarning(ex, "Could not recursively purge sandbox temp directory {TempDirectory}.", directory);
                    }
                    else
                    {
                        Trace.TraceWarning("Could not recursively purge sandbox temp directory '{0}': {1}", directory, ex.Message);
                    }

                    return;
                }

                Thread.Sleep(25 * (attempt + 1));
            }
        }
    }

    private static string SanitizeSandboxId(string sandboxId)
    {
        if (string.IsNullOrWhiteSpace(sandboxId))
        {
            return "sbx";
        }

        var sb = new StringBuilder(Math.Min(sandboxId.Length, 32));
        foreach (var ch in sandboxId)
        {
            if (sb.Length >= 32)
            {
                break;
            }

            sb.Append(char.IsLetterOrDigit(ch) || ch is '-' or '_' ? ch : '_');
        }

        return sb.Length > 0 ? sb.ToString() : "sbx";
    }

    private static void CheckWin32(bool ok, string operation)
    {
        if (!ok)
        {
            var err = Marshal.GetLastWin32Error();
            throw new Win32Exception(err, $"{operation} failed with Win32 error {err}.");
        }
    }

    internal sealed class WindowsConfinedProcessSession : ConfinedProcessSession
    {
        private readonly WindowsAppContainerRunner _owner;
        private readonly SafeProcessHandle _processHandle;
        private readonly List<(string Folder, SecurityIdentifier Sid)> _grantedWorkDirs;
        private IntPtr _job;
        private IntPtr _iocp;
        private int _disposed;

        public WindowsConfinedProcessSession(
            WindowsAppContainerRunner owner,
            int processId,
            SafeProcessHandle processHandle,
            IntPtr job,
            IntPtr iocp,
            ConfinementMode effectiveMode,
            string tempDirectory,
            StreamWriter standardInput,
            StreamReader standardOutput,
            StreamReader standardError,
            List<(string Folder, SecurityIdentifier Sid)> grantedWorkDirs)
        {
            _owner = owner;
            ProcessId = processId;
            _processHandle = processHandle;
            _job = job;
            _iocp = iocp;
            EffectiveMode = effectiveMode;
            TempDirectory = tempDirectory;
            StandardInput = standardInput;
            StandardOutput = standardOutput;
            StandardError = standardError;
            _grantedWorkDirs = grantedWorkDirs;
        }

        public override int ProcessId { get; }

        public override ConfinementMode EffectiveMode { get; }

        public override string TempDirectory { get; }

        public override StreamWriter StandardInput { get; }

        public override StreamReader StandardOutput { get; }

        public override StreamReader StandardError { get; }

        public override bool HasExited
        {
            get
            {
                if (Volatile.Read(ref _disposed) != 0 || _processHandle.IsClosed || _processHandle.IsInvalid)
                {
                    return true;
                }

                return Native.WaitForSingleObject(_processHandle.DangerousGetHandle(), 0) == Native.WAIT_OBJECT_0;
            }
        }

        public override int ExitCode
        {
            get
            {
                if (_processHandle.IsClosed || _processHandle.IsInvalid)
                {
                    return -1;
                }

                return Native.GetExitCodeProcess(_processHandle.DangerousGetHandle(), out var raw)
                    ? unchecked((int)raw)
                    : -1;
            }
        }

        internal WindowsJobLimitSummary QueryJobLimits()
        {
            ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0 || _job == IntPtr.Zero, this);
            return WindowsAppContainerRunner.QueryJobLimits(_job);
        }

        public override Task WaitForExitAsync(CancellationToken cancellationToken = default)
        {
            if (HasExited)
            {
                return Task.CompletedTask;
            }

            return WaitForProcessHandleAsync(_processHandle, cancellationToken);
        }

        public override void Kill()
        {
            if (Volatile.Read(ref _disposed) != 0)
            {
                return;
            }

            TerminateAndDrainJob(_job, _iocp);
        }

        public override ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0)
            {
                return ValueTask.CompletedTask;
            }

            try { StandardInput.Dispose(); } catch (ObjectDisposedException) { } catch (IOException) { }
            try { StandardOutput.Dispose(); } catch (ObjectDisposedException) { } catch (IOException) { }
            try { StandardError.Dispose(); } catch (ObjectDisposedException) { } catch (IOException) { }

            var job = Interlocked.Exchange(ref _job, IntPtr.Zero);
            var iocp = Interlocked.Exchange(ref _iocp, IntPtr.Zero);

            TerminateAndDrainJob(job, iocp);
            if (job != IntPtr.Zero) { _ = Native.CloseHandle(job); }
            if (iocp != IntPtr.Zero) { _ = Native.CloseHandle(iocp); }
            _processHandle.Dispose();

            _owner.RevokeGrantedAccess(_grantedWorkDirs);
            TryPurgeDirectory(TempDirectory, _owner._logger);
            return ValueTask.CompletedTask;
        }
    }
}
