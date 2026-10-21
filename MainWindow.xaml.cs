using System.Collections.ObjectModel;
using System.Security.Cryptography;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using Microsoft.Win32;
using NordicP3.Diagnostics;
using NordicP3.J2534;

namespace NordicP3;

public partial class MainWindow : Window
{
    private readonly ObservableCollection<KeyValuePair<string, string>> _info = new();
    private readonly ObservableCollection<KeyValuePair<string, string>> _dtcs = new();
    private readonly SemaphoreSlim _io = new(1, 1);          // one J2534 conversation at a time
    private readonly DispatcherTimer _voltTimer = new() { Interval = TimeSpan.FromSeconds(1) };

    private J2534Device? _dev;
    private DiagClient? _diag;
    private ModuleProfile? _module;
    private CancellationTokenSource? _opCts;
    private bool _busy;
    private double _lastVolts = double.NaN;

    public MainWindow()
    {
        InitializeComponent();
        InfoList.ItemsSource = _info;
        DtcList.ItemsSource = _dtcs;
        _voltTimer.Tick += async (_, _) => await PollVoltageAsync();
        Log.Line += (line, raw) => Dispatcher.InvokeAsync(() =>
        {
            if (raw && RawTraffic.IsChecked != true) return;
            LogBox.AppendText(line + Environment.NewLine);
            LogBox.ScrollToEnd();
        });
    }

    // ---------------------------------------------------------------- startup

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        Log.Write($"NordicP3 {GetType().Assembly.GetName().Version} ({(Environment.Is64BitProcess ? "64" : "32")}-bit). Log file: {Log.FilePath}");

        var (set, source) = ProfileSet.Load();
        ModuleBox.ItemsSource = set.Modules;
        ModuleBox.SelectedIndex = set.Modules.Count > 0 ? 0 : -1;
        Log.Write($"Loaded {set.Modules.Count} module profile(s) from {source}.");

        ScanDrivers();
    }

    private void ScanDrivers()
    {
        var drivers = DriverInfo.Discover();
        DriverBox.ItemsSource = drivers;
        DriverBox.SelectedItem =
            drivers.FirstOrDefault(d => d.Loadable && (d.Name.Contains("DiCE", StringComparison.OrdinalIgnoreCase)
                                                       || d.LibraryPath.Contains("DiCE", StringComparison.OrdinalIgnoreCase)))
            ?? drivers.FirstOrDefault(d => d.Loadable)
            ?? drivers.FirstOrDefault();

        if (drivers.Count == 0)
            Log.Write("No J2534 drivers found in the registry. Install the DiCE driver, or use 'Choose DLL...'.");
        else
            foreach (var d in drivers)
                Log.Write($"Found J2534 driver: {d.Name} [{d.Vendor}] {d.LibraryPath} - {PeInfo.Describe(d.Arch)}");
    }

    private void OnRescan(object sender, RoutedEventArgs e) => ScanDrivers();

    private void OnDriverChanged(object sender, SelectionChangedEventArgs e)
    {
        if (DriverBox.SelectedItem is not DriverInfo d) { DriverDetail.Text = ""; return; }
        DriverDetail.Text = $"{d.LibraryPath}\n{PeInfo.Describe(d.Arch)}" +
                            (d.SupportsIso15765 ? "" : " - driver does not list ISO15765") +
                            (d.Loadable ? "" : " - cannot be used by this 32-bit program");
    }

    private void OnBrowseDll(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFileDialog { Filter = "J2534 driver (*.dll)|*.dll", Title = "Choose J2534 driver DLL" };
        if (dlg.ShowDialog(this) != true) return;
        var d = DriverInfo.FromFile(dlg.FileName);
        Log.Write($"{dlg.FileName}: {PeInfo.Describe(d.Arch)}");
        var list = (DriverBox.ItemsSource as List<DriverInfo>) ?? new();
        list.Insert(0, d);
        DriverBox.ItemsSource = null;
        DriverBox.ItemsSource = list;
        DriverBox.SelectedItem = d;
    }

    // ---------------------------------------------------------------- connection

    private async void OnConnect(object sender, RoutedEventArgs e)
    {
        if (DriverBox.SelectedItem is not DriverInfo driver) { Warn("Choose a J2534 interface first."); return; }
        if (!driver.Loadable) { Warn($"{driver.LibraryPath} is {PeInfo.Describe(driver.Arch)} and can't be loaded by this program."); return; }
        if (ModuleBox.SelectedItem is not ModuleProfile module) { Warn("Choose a module profile."); return; }

        await RunAsync("Connecting", async _ =>
        {
            var dev = J2534Device.Open(driver);
            try
            {
                dev.Trace = (dir, bytes) => Log.Write($"{dir}  {Log.Hex(bytes)}", raw: true);
                var (fw, dll, api) = dev.ReadVersion();
                Log.Write($"Interface open. Firmware {fw}, DLL {dll}, API {api}");
                Ui(() =>
                {
                    _info.Clear();
                    _info.Add(new("Interface", driver.Name));
                    _info.Add(new("Interface firmware", fw));
                    _info.Add(new("Driver DLL version", dll));
                    _info.Add(new("J2534 API", api));
                });

                dev.OpenChannel(module.TxId, module.RxId, module.Baud, module.ExtendedIds);
                Log.Write($"ISO 15765 channel open to {module.Name}: request {module.RequestId}, response {module.ResponseId}, {module.Baud} bit/s");
                _dev = dev; _diag = new DiagClient(dev); _module = module;
            }
            catch
            {
                dev.Dispose();
                throw;
            }
            await Task.CompletedTask;
        });

        SetConnected(_dev != null);
        if (_dev != null) { await PollVoltageAsync(); _voltTimer.Start(); }
    }

    private async void OnDisconnect(object sender, RoutedEventArgs e) => await DisconnectAsync();

    private async Task DisconnectAsync()
    {
        _voltTimer.Stop();
        await _io.WaitAsync();
        try { _dev?.Dispose(); }
        finally { _dev = null; _diag = null; _io.Release(); }
        Log.Write("Disconnected.");
        VoltText.Text = "-- . -- V";
        VoltText.Foreground = Brushes.Black;
        SetConnected(false);
    }

    private async Task PollVoltageAsync()
    {
        if (_dev == null || !await _io.WaitAsync(0)) return;   // skip while another operation owns the interface
        try
        {
            var v = await Task.Run(() => _dev!.ReadBatteryVolts());
            _lastVolts = v;
            VoltText.Text = $"{v:0.00} V";
            VoltText.Foreground = v < 12.0 ? Brushes.Firebrick : v < 12.5 ? Brushes.DarkOrange : Brushes.ForestGreen;
        }
        catch (Exception ex)
        {
            VoltText.Text = "n/a";
            Log.Write($"Voltage read failed: {ex.Message}");
            _voltTimer.Stop();
        }
        finally { _io.Release(); }
    }

    // ---------------------------------------------------------------- read-only diagnostics

    private async void OnReadInfo(object sender, RoutedEventArgs e) => await RunAsync("Reading ECU info", async _ =>
    {
        var diag = _diag!; var m = _module!;
        var rows = new List<KeyValuePair<string, string>>();

        if (m.ObdInfo)
        {
            void Obd(byte pid, string label, bool ascii)
            {
                var d = diag.ObdVehicleInfo(pid);
                if (d != null) rows.Add(new(label, ascii ? DiagClient.Ascii(d) : Log.Hex(d)));
            }
            Obd(0x02, "VIN (OBD)", true);
            Obd(0x04, "Calibration ID", true);
            Obd(0x06, "Calibration verification no.", false);
            Obd(0x0A, "ECU name", true);
        }

        foreach (var did in m.Dids)
        {
            var d = diag.UdsReadDid(did.Value);
            rows.Add(new($"{did.Label} [{did.Did}]", d == null ? "(not supported)" : did.Ascii ? DiagClient.Ascii(d) : Log.Hex(d)));
        }

        Ui(() =>
        {
            foreach (var key in rows.Select(r => r.Key).ToList())
                for (int i = _info.Count - 1; i >= 0; i--) if (_info[i].Key == key) _info.RemoveAt(i);
            foreach (var r in rows) _info.Add(r);
        });
        Log.Write($"Read {rows.Count} info item(s).");
        await Task.CompletedTask;
    });

    private async void OnReadDtcs(object sender, RoutedEventArgs e) => await RunAsync("Reading DTCs", async _ =>
    {
        var stored = _diag!.ObdDtcs(pending: false);
        var pending = _diag.ObdDtcs(pending: true);
        Ui(() =>
        {
            _dtcs.Clear();
            foreach (var c in stored) _dtcs.Add(new(c, "Stored"));
            foreach (var c in pending.Except(stored)) _dtcs.Add(new(c, "Pending"));
            if (_dtcs.Count == 0) _dtcs.Add(new("-", "No codes reported"));
        });
        Log.Write($"DTCs: {stored.Count} stored, {pending.Count} pending.");
        await Task.CompletedTask;
    });

    private async void OnClearDtcs(object sender, RoutedEventArgs e)
    {
        if (MessageBox.Show(this, "Clear all engine trouble codes and freeze-frame data?", "Clear DTCs",
                MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
        await RunAsync("Clearing DTCs", async _ =>
        {
            _diag!.ObdClearDtcs();
            Log.Write("DTCs cleared.");
            Ui(() => _dtcs.Clear());
            await Task.CompletedTask;
        });
    }

    // ---------------------------------------------------------------- dump / write

    private async void OnDump(object sender, RoutedEventArgs e)
    {
        var m = _module!;
        var dlg = new SaveFileDialog
        {
            Filter = "ECU image (*.bin)|*.bin",
            FileName = $"{Safe(InfoValue("VIN") ?? "ECU")}_{Safe(InfoValue("software") ?? "image")}_{DateTime.Now:yyyyMMdd_HHmm}.bin",
            InitialDirectory = Directory.CreateDirectory(Path.Combine(Log.Folder, "dumps")).FullName,
        };
        if (dlg.ShowDialog(this) != true) return;

        var progress = ProgressReporter();   // created on the UI thread so updates marshal back to it
        await RunAsync("Reading ECU image", async ct =>
        {
            var proc = FlashProcedures.For(m, _dev!, _diag!);
            var image = await proc.ReadImageAsync(progress, ct);
            await File.WriteAllBytesAsync(dlg.FileName, image, ct);

            var sha = Convert.ToHexString(SHA256.HashData(image));
            var meta = new
            {
                module = m.Name,
                read = DateTime.Now,
                sizeBytes = image.Length,
                sha256 = sha,
                info = Ui(() => _info.ToDictionary(k => k.Key, k => k.Value)),
            };
            await File.WriteAllTextAsync(Path.ChangeExtension(dlg.FileName, ".json"),
                JsonSerializer.Serialize(meta, new JsonSerializerOptions { WriteIndented = true }), ct);
            Log.Write($"Saved {image.Length} bytes to {dlg.FileName} (SHA-256 {sha}).");
        });
    }

    private async void OnFlash(object sender, RoutedEventArgs e)
    {
        var m = _module!;
        var dlg = new OpenFileDialog { Filter = "ECU image (*.bin)|*.bin|All files (*.*)|*.*", Title = "Choose image to write" };
        if (dlg.ShowDialog(this) != true) return;

        var image = await File.ReadAllBytesAsync(dlg.FileName);

        // Guardrails before anything is sent to the module.
        if (m.Flash.ImageSizeBytes > 0 && image.Length != m.Flash.ImageSizeBytes)
        {
            Warn($"File is {image.Length:N0} bytes; {m.Name} expects {m.Flash.ImageSizeBytes:N0}. Not writing.");
            return;
        }
        await PollVoltageAsync();
        if (double.IsNaN(_lastVolts) || _lastVolts < m.Flash.MinVoltage)
        {
            Warn($"Supply is {(double.IsNaN(_lastVolts) ? "unknown" : _lastVolts.ToString("0.00") + " V")}; " +
                 $"at least {m.Flash.MinVoltage:0.0} V is required. Connect a charger or bench supply.");
            return;
        }
        var confirm = ConfirmDialog.Ask(this,
            $"Write {Path.GetFileName(dlg.FileName)} ({image.Length:N0} bytes) to {m.Name}?\n\n" +
            "Do not disconnect the interface or switch off power until it finishes. " +
            "An interrupted write can leave the ECU unable to start.\n\nType FLASH to continue.", "FLASH");
        if (!confirm) { Log.Write("Write cancelled by user."); return; }

        Log.Write($"Writing {dlg.FileName} (SHA-256 {Convert.ToHexString(SHA256.HashData(image))}).");
        var progress = ProgressReporter();
        await RunAsync("Writing ECU image", async ct =>
        {
            var proc = FlashProcedures.For(m, _dev!, _diag!);
            await proc.WriteImageAsync(image, progress, ct);
            Log.Write("Write finished and verified.");
        });
    }

    private void OnCancel(object sender, RoutedEventArgs e)
    {
        _opCts?.Cancel();
        Log.Write("Cancel requested.");
    }

    // ---------------------------------------------------------------- plumbing

    /// <summary>Runs an operation on a worker thread with the interface locked and the UI disabled.</summary>
    private async Task RunAsync(string what, Func<CancellationToken, Task> work)
    {
        if (_busy) return;
        _busy = true;
        _opCts = new CancellationTokenSource();
        SetBusy(true, what);
        await _io.WaitAsync();
        try
        {
            await Task.Run(() => work(_opCts.Token));
            StatusText.Text = $"{what}: done";
        }
        catch (OperationCanceledException)
        {
            StatusText.Text = $"{what}: cancelled";
            Log.Write($"{what}: cancelled.");
        }
        catch (Exception ex)
        {
            StatusText.Text = $"{what}: failed";
            Log.Write($"{what} failed: {ex.Message}");
            MessageBox.Show(this, ex.Message, what, MessageBoxButton.OK,
                ex is NotSupportedException ? MessageBoxImage.Information : MessageBoxImage.Error);
        }
        finally
        {
            _io.Release();
            _busy = false;
            SetBusy(false, null);
            Progress.Value = 0;
        }
    }

    private IProgress<double> ProgressReporter() => new Progress<double>(p => Progress.Value = Math.Clamp(p * 100, 0, 100));

    private void SetConnected(bool on)
    {
        ConnectBtn.IsEnabled = !on;
        DriverBox.IsEnabled = ModuleBox.IsEnabled = BrowseDllBtn.IsEnabled = RefreshBtn.IsEnabled = !on;
        DisconnectBtn.IsEnabled = ReadInfoBtn.IsEnabled = ReadDtcBtn.IsEnabled = ClearDtcBtn.IsEnabled =
            DumpBtn.IsEnabled = FlashBtn.IsEnabled = on;
        StatusText.Text = on ? $"Connected: {_module?.Name}" : "Not connected";
    }

    private void SetBusy(bool busy, string? what)
    {
        var connected = _dev != null;
        foreach (var b in new[] { ReadInfoBtn, ReadDtcBtn, ClearDtcBtn, DumpBtn, FlashBtn, DisconnectBtn })
            b.IsEnabled = !busy && connected;
        ConnectBtn.IsEnabled = !busy && !connected;
        CancelBtn.IsEnabled = busy;
        if (busy) StatusText.Text = what + "...";
    }

    private void OnClosing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        if (_busy)
        {
            Warn("An operation is running. Wait for it to finish (or cancel it) before closing.");
            e.Cancel = true;
            return;
        }
        _voltTimer.Stop();
        _dev?.Dispose();
    }

    private string? InfoValue(string keyContains) =>
        _info.FirstOrDefault(k => k.Key.Contains(keyContains, StringComparison.OrdinalIgnoreCase)
                                  && !string.IsNullOrWhiteSpace(k.Value) && !k.Value.StartsWith("(")).Value;

    private static string Safe(string s) => string.Concat(s.Where(c => !Path.GetInvalidFileNameChars().Contains(c) && c != ' '));

    private void Warn(string msg)
    {
        Log.Write(msg);
        MessageBox.Show(this, msg, "NordicP3", MessageBoxButton.OK, MessageBoxImage.Warning);
    }

    private void Ui(Action a) => Dispatcher.Invoke(a);
    private T Ui<T>(Func<T> f) => Dispatcher.Invoke(f);
}
