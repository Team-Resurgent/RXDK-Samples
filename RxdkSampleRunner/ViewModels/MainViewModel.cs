using System.Collections.ObjectModel;
using System.IO;
using System.Text.RegularExpressions;
using Avalonia.Controls;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using RxdkSampleRunner.Models;
using RxdkSampleRunner.Mvvm;
using RxdkSampleRunner.Services;

namespace RxdkSampleRunner.ViewModels;

public sealed class MainViewModel : ObservableObject
{
    private readonly AppSettings _settings;
    private CancellationTokenSource? _cts;

    public MainViewModel()
    {
        _settings = SettingsService.Load();

        RefreshCommand = new AsyncRelayCommand(_ => RunExclusive(_ => Task.Run(RefreshSamples)), _ => !Busy);
        BuildAllCommand = new AsyncRelayCommand(_ => RunExclusive(BuildAllAsync), _ => !Busy && Samples.Count > 0);
        BuildCommand = new AsyncRelayCommand(o => RunExclusive(ct => BuildSampleAsync((SampleItem)o!, ct)), o => !Busy && o is SampleItem);
        LaunchCommand = new AsyncRelayCommand(o => RunExclusive(ct => LaunchXemuAsync((SampleItem)o!, ct)), o => !Busy && o is SampleItem);
        DeployRunCommand = new AsyncRelayCommand(o => RunExclusive(ct => DeployRunAsync((SampleItem)o!, ct)), o => !Busy && o is SampleItem);
        CancelCommand = new RelayCommand(_ => _cts?.Cancel(), _ => Busy);
        // Screenshot is independent of Busy — grab the kit's screen even mid-run.
        ScreenshotCommand = new AsyncRelayCommand(_ => CaptureXboxScreenshotAsync(),
                                                  _ => !string.IsNullOrWhiteSpace(ConsoleIp));
        BrowseXemuCommand = new AsyncRelayCommand(async _ => { var p = BrowseExeAsync is null ? null : await BrowseExeAsync(); if (p is not null) XemuPath = p; });
        BrowseSamplesCommand = new AsyncRelayCommand(async _ =>
        {
            var p = BrowseFolderAsync is null ? null : await BrowseFolderAsync();
            if (p is not null) { SamplesRoot = p; RefreshSamples(); }
        });

        // Drain buffered log output to the UI ~10x/second (see Append/FlushLog).
        new DispatcherTimer(TimeSpan.FromMilliseconds(100), DispatcherPriority.Background,
                            (_, _) => FlushLog()).Start();

        if (!string.IsNullOrWhiteSpace(_settings.SamplesRoot) && Directory.Exists(_settings.SamplesRoot))
            RefreshSamples();
    }

    // ---- settings-backed, persisted-on-change properties ----
    public string XemuPath { get => _settings.XemuPath; set { _settings.XemuPath = value; OnPropertyChanged(); Persist(); } }
    public string XemuParams { get => _settings.XemuParams; set { _settings.XemuParams = value; OnPropertyChanged(); Persist(); } }
    public string SamplesRoot { get => _settings.SamplesRoot; set { _settings.SamplesRoot = value; OnPropertyChanged(); Persist(); } }
    public string ConsoleIp { get => _settings.ConsoleIp; set { _settings.ConsoleIp = value; OnPropertyChanged(); Persist(); ScreenshotCommand.RaiseCanExecuteChanged(); } }
    public string CliPath { get => _settings.CliPath; set { _settings.CliPath = value; OnPropertyChanged(); Persist(); } }
    public string Configuration { get => _settings.Configuration; set { _settings.Configuration = value; OnPropertyChanged(); Persist(); } }

    public ObservableCollection<SampleItem> Samples { get; } = new();

    private string _log = "";
    public string Log { get => _log; set => SetProperty(ref _log, value); }

    private string _status = "Ready";
    public string Status { get => _status; set => SetProperty(ref _status, value); }

    private bool _busy;
    public bool Busy { get => _busy; private set { if (SetProperty(ref _busy, value)) RaiseAllCanExec(); } }

    // File/folder pickers are supplied by the View (which owns the window/StorageProvider).
    public Func<Task<string?>>? BrowseExeAsync;
    public Func<Task<string?>>? BrowseFolderAsync;

    public AsyncRelayCommand RefreshCommand { get; }
    public AsyncRelayCommand BuildAllCommand { get; }
    public AsyncRelayCommand BuildCommand { get; }
    public AsyncRelayCommand LaunchCommand { get; }
    public AsyncRelayCommand DeployRunCommand { get; }
    public RelayCommand CancelCommand { get; }
    public AsyncRelayCommand ScreenshotCommand { get; }
    public AsyncRelayCommand BrowseXemuCommand { get; }
    public AsyncRelayCommand BrowseSamplesCommand { get; }

    private void RaiseAllCanExec()
    {
        RefreshCommand.RaiseCanExecuteChanged();
        BuildAllCommand.RaiseCanExecuteChanged();
        BuildCommand.RaiseCanExecuteChanged();
        LaunchCommand.RaiseCanExecuteChanged();
        DeployRunCommand.RaiseCanExecuteChanged();
        CancelCommand.RaiseCanExecuteChanged();
    }

    private void Persist() => SettingsService.Save(_settings);

    // ---- log helpers (batched, constant-cost display) ----
    // Build output arrives as hundreds of lines/second. Two problems to avoid:
    //  1) Updating the bound Log per line re-renders the whole SelectableTextBlock each
    //     time and freezes the UI — so we buffer lines off-thread and flush on a 100ms
    //     timer (a burst becomes one update; Background priority lets input win).
    //  2) Binding the ENTIRE growing log makes every append/render O(size) — it gets
    //     slower and slower as the log grows. So the bound Log is only a fixed-size TAIL;
    //     the full text lives in a StringBuilder (used by Copy Log). Render cost is then
    //     constant regardless of how long the build runs.
    private const int DisplayTail = 64_000;    // chars rendered on screen
    private const int FullCap     = 2_000_000; // chars retained for Copy Log
    private readonly object _logSync = new();
    private readonly System.Text.StringBuilder _logPending = new();
    private readonly System.Text.StringBuilder _logFull = new();

    /// <summary>The complete (bounded) log, for the Copy Log button.</summary>
    public string FullLog { get { lock (_logSync) return _logFull.ToString(); } }

    private void Append(string line)
    {
        lock (_logSync) _logPending.Append(line).Append('\n');
    }

    private void FlushLog()
    {
        string tail;
        lock (_logSync)
        {
            if (_logPending.Length == 0) return;
            _logFull.Append(_logPending);
            _logPending.Clear();
            // Trim in big chunks (drop to 75% when over cap) so this is O(n) rarely, not
            // every flush.
            if (_logFull.Length > FullCap)
                _logFull.Remove(0, _logFull.Length - (FullCap * 3 / 4));
            var start = Math.Max(0, _logFull.Length - DisplayTail);
            tail = _logFull.ToString(start, _logFull.Length - start);
        }
        Log = tail;   // constant-size render, one PropertyChanged per flush
    }

    private void ClearLog()
    {
        lock (_logSync) { _logPending.Clear(); _logFull.Clear(); }
        Dispatcher.UIThread.Post(() => Log = "");
    }

    // ---- capture-card preview (Elgato etc. via FlashCap) ----
    private readonly CaptureService _capture = new();

    public ObservableCollection<CaptureDeviceItem> CaptureDeviceList { get; } = new();

    private bool _captureEnabled;
    public bool CaptureEnabled
    {
        get => _captureEnabled;
        set { if (SetProperty(ref _captureEnabled, value)) { OnPropertyChanged(nameof(CaptureRowHeight)); _ = ToggleCaptureAsync(value); } }
    }

    private CaptureDeviceItem? _selectedCaptureDevice;
    public CaptureDeviceItem? SelectedCaptureDevice
    {
        get => _selectedCaptureDevice;
        set { if (SetProperty(ref _selectedCaptureDevice, value) && _captureEnabled && value is not null) _ = RestartCaptureAsync(); }
    }

    private Bitmap? _captureFrame;
    public Bitmap? CaptureFrame
    {
        get => _captureFrame;
        private set { var old = _captureFrame; if (SetProperty(ref _captureFrame, value)) old?.Dispose(); }
    }

    private string _captureStatus = "";
    public string CaptureStatus { get => _captureStatus; set => SetProperty(ref _captureStatus, value); }

    /// <summary>Row height for the capture pane: a 2* share when on, collapsed to 0 when off.</summary>
    public GridLength CaptureRowHeight => _captureEnabled ? new GridLength(2, GridUnitType.Star) : new GridLength(0);

    private async Task ToggleCaptureAsync(bool on)
    {
        if (!on)
        {
            await _capture.StopAsync();
            CaptureFrame = null;
            CaptureStatus = "";
            return;
        }

        CaptureStatus = "Looking for capture devices…";
        var devices = await Task.Run(CaptureService.ListDevices);
        CaptureDeviceList.Clear();
        foreach (var d in devices) CaptureDeviceList.Add(d);
        if (CaptureDeviceList.Count == 0) { CaptureStatus = "No capture devices found"; return; }

        // Auto-select an Elgato / game-capture device when present.
        _selectedCaptureDevice = CaptureDeviceList.FirstOrDefault(
                d => d.Name.Contains("Elgato", StringComparison.OrdinalIgnoreCase)
                  || d.Name.Contains("Game Capture", StringComparison.OrdinalIgnoreCase))
            ?? CaptureDeviceList[0];
        OnPropertyChanged(nameof(SelectedCaptureDevice));
        await RestartCaptureAsync();
    }

    private async Task RestartCaptureAsync()
    {
        if (SelectedCaptureDevice is null) return;
        try { await _capture.StartAsync(SelectedCaptureDevice.Descriptor, bmp => CaptureFrame = bmp, s => CaptureStatus = s); }
        catch (Exception ex) { CaptureStatus = "Capture failed: " + ex.Message; }
    }

    /// <summary>Release the capture device on window close.</summary>
    public async Task ShutdownAsync() => await _capture.DisposeAsync();

    // ---- xbdm screenshot -> clipboard ----
    private async Task CaptureXboxScreenshotAsync()
    {
        var ip = ConsoleIp;
        if (string.IsNullOrWhiteSpace(ip)) { Status = "Set the Xbox IP first"; return; }

        Status = "Grabbing screenshot…";
        var tmp = Path.Combine(Path.GetTempPath(), $"rxdk-shot-{Guid.NewGuid():N}.bmp");
        try
        {
            await Task.Run(() =>
            {
                using var conn = Rxdk.Xbdm.Managed.XbdmSession.Connect(ip);
                conn.CaptureScreenshot(tmp);
            });
            var bytes = await File.ReadAllBytesAsync(tmp);
            var ok = ClipboardImage.SetBmp(bytes);   // on the UI thread (continuation)
            Status = ok
                ? "Screenshot copied to clipboard"
                : "Screenshot captured, but clipboard image isn't supported on this OS";
        }
        catch (Exception ex) { Status = "Screenshot failed: " + ex.Message; }
        finally { try { if (File.Exists(tmp)) File.Delete(tmp); } catch { /* temp cleanup */ } }
    }

    // ---- sample discovery ----
    private void RefreshSamples()
    {
        var root = _settings.SamplesRoot;
        var found = new List<SampleItem>();
        if (!string.IsNullOrWhiteSpace(root) && Directory.Exists(root))
        {
            foreach (var vcx in Directory.EnumerateFiles(root, "*.vcxproj", SearchOption.AllDirectories)
                         .Where(p => !p.Replace('\\', '/').Contains("/out/"))
                         .OrderBy(p => p, StringComparer.OrdinalIgnoreCase))
            {
                var dir = Path.GetDirectoryName(vcx)!;
                var item = new SampleItem
                {
                    Name = Path.GetFileNameWithoutExtension(vcx),
                    Category = Path.GetRelativePath(root, dir),
                    Directory = dir,
                    VcxprojPath = vcx,
                };
                item.Rescan();   // detect existing ISO (off the UI thread)
                found.Add(item);
            }
        }
        Dispatcher.UIThread.Post(() =>
        {
            Samples.Clear();
            foreach (var s in found) Samples.Add(s);
            Status = string.IsNullOrWhiteSpace(root) || !Directory.Exists(root)
                ? "Set a valid samples root."
                : $"{Samples.Count} samples  ({Samples.Count(s => s.IsoExists)} with ISO)";
            BuildAllCommand.RaiseCanExecuteChanged();
        });
    }

    // ---- operations ----
    private async Task RunExclusive(Func<CancellationToken, Task> op)
    {
        if (Busy) return;
        Busy = true;
        _cts = new CancellationTokenSource();
        try { await op(_cts.Token); }
        catch (OperationCanceledException) { Append("— cancelled —"); Status = "Cancelled"; }
        catch (Exception ex) { Append("ERROR: " + ex.Message); Status = "Error"; }
        finally { _cts?.Dispose(); _cts = null; Busy = false; }
    }

    private async Task BuildAllAsync(CancellationToken ct)
    {
        ClearLog();
        var msbuild = ToolLocator.ResolveMsbuild(_settings.MsbuildPath);
        if (msbuild is null) { Append("ERROR: MSBuild not found — install VS Build Tools or set the MSBuild path."); Status = "MSBuild not found"; return; }

        int ok = 0, fail = 0, i = 0;
        foreach (var s in Samples.ToList())
        {
            ct.ThrowIfCancellationRequested();
            i++;
            Status = $"Building {i}/{Samples.Count}: {s.Name}  (ok {ok}, fail {fail})";
            var exit = await BuildOneAsync(msbuild, s, ct);
            if (exit == 0) ok++; else fail++;
        }
        Status = $"Build all done — {ok} ok, {fail} failed";
    }

    private Task BuildSampleAsync(SampleItem s, CancellationToken ct)
    {
        ClearLog();
        var msbuild = ToolLocator.ResolveMsbuild(_settings.MsbuildPath);
        if (msbuild is null) { Append("ERROR: MSBuild not found — install VS Build Tools or set the MSBuild path."); Status = "MSBuild not found"; return Task.CompletedTask; }
        return BuildOneAsync(msbuild, s, ct);
    }

    private async Task<int> BuildOneAsync(string msbuild, SampleItem s, CancellationToken ct)
    {
        s.State = BuildState.Building;
        s.IsBusy = true;
        Append($"=== Building {s.Name} ===");
        var args = new[] { s.VcxprojPath, $"/p:Configuration={_settings.Configuration};Platform=Xbox", "/nologo", "/v:minimal" };
        var exit = await ProcessRunner.RunAsync(msbuild, args, s.Directory, Append, ct);
        s.IsBusy = false;
        Dispatcher.UIThread.Post(() => s.Rescan());
        if (exit == ProcessRunner.Cancelled)
            ct.ThrowIfCancellationRequested(); // clean OCE → top-level "— cancelled —"; don't mark Failed
        s.State = exit == 0 ? BuildState.Built : BuildState.Failed;
        return exit;
    }

    private async Task LaunchXemuAsync(SampleItem s, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(_settings.XemuPath) || !File.Exists(_settings.XemuPath))
        { Status = "Set a valid xemu path first."; Append("ERROR: xemu path not set / not found."); return; }
        var iso = s.IsoFor(_settings.Configuration);
        if (iso is null) { Status = $"{s.Name}: no ISO — build it first."; Append($"ERROR: no ISO for {s.Name} — build it first."); return; }

        ClearLog();  // in-app log is cleared on every new launch
        Status = $"xemu: {s.Name}";

        // xemu.exe is a Windows GUI-subsystem binary: launched with redirected pipes it
        // never wires stdout/stderr, so "-serial stdio" (and xemu's own startup log) never
        // reach us. Give it a real console instead — run it inside a cmd window, where the
        // title's serial console AND xemu's diagnostics show up and stay readable.
        var inner = $"\"{_settings.XemuPath}\" {_settings.XemuParams} -dvd_path \"{iso}\"";
        var psi = new System.Diagnostics.ProcessStartInfo
        {
            FileName = "cmd.exe",
            // /k keeps the console open after xemu exits so the log stays readable.
            Arguments = $"/k title xemu - {s.Name} & {inner}",
            UseShellExecute = false,
            CreateNoWindow = false,   // allocate a visible console window
            WorkingDirectory = Path.GetDirectoryName(_settings.XemuPath) ?? "",
        };
        try
        {
            System.Diagnostics.Process.Start(psi);
            Append($"Launched {s.Name} in a console window — xemu's serial console and startup log appear there.");
            Append("(xemu is a GUI app that can't stream into this pane; the console window shows everything.)");
            Status = $"xemu (console): {s.Name}";
        }
        catch (Exception ex)
        {
            Append($"ERROR: cannot launch xemu console: {ex.Message}");
            Status = "xemu launch failed";
        }
        await Task.CompletedTask;
    }

    private async Task DeployRunAsync(SampleItem s, CancellationToken ct)
    {
        var cli = ToolLocator.ResolveCli(_settings.CliPath);
        if (!File.Exists(cli)) { Append($"ERROR: rxdk CLI not found at {cli}"); Status = "CLI not found"; return; }
        if (!File.Exists(s.ManifestPath)) { Append($"ERROR: no built manifest ({s.ManifestPath}) — build first."); Status = "Not built"; return; }

        ClearLog();
        var deploy = new List<string> { "deploy", "--project-root", s.Directory, "--manifest", s.ManifestPath };
        // --go: launch-and-run without halting at the initial break for a debugger. The
        // runner is for test runs, not F5 debugging, so the title would otherwise sit
        // "waiting for debugger connection" (DM_CREATETHREAD stop) and never start.
        var run = new List<string> { "run", "--project-root", s.Directory, "--manifest", s.ManifestPath, "--go" };
        if (!string.IsNullOrWhiteSpace(_settings.ConsoleIp))
        {
            deploy.Add("--console"); deploy.Add(_settings.ConsoleIp);
            run.Add("--console"); run.Add(_settings.ConsoleIp);
        }

        Status = $"Deploying {s.Name} to hardware…";
        var deployRc = await ProcessRunner.RunAsync(cli, deploy, s.Directory, Append, ct);
        if (deployRc == ProcessRunner.Cancelled) { Append("— deploy cancelled —"); Status = "Cancelled"; return; }
        if (deployRc != 0) { Status = "Deploy failed"; return; }

        Status = $"Running {s.Name} on hardware…";
        // With --go the title runs free on the kit; this call just streams its debug output
        // for the launch window. Cancelling it only stops the stream — the title keeps
        // running on the console (it's already magic-booted), so that's a normal outcome.
        var runRc = await ProcessRunner.RunAsync(cli, run, s.Directory, Append, ct);
        Status = runRc == ProcessRunner.Cancelled
            ? $"{s.Name} running on kit (stopped streaming)"
            : $"Launched {s.Name} on hardware";
    }

    /// <summary>Split a parameter string into argv, honoring simple double-quoted groups.</summary>
    private static List<string> SplitParams(string s)
    {
        var outv = new List<string>();
        foreach (Match m in Regex.Matches(s ?? "", "\"([^\"]*)\"|(\\S+)"))
            outv.Add(m.Groups[1].Success ? m.Groups[1].Value : m.Groups[2].Value);
        return outv;
    }
}
