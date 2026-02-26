using System;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Linq;
using System.ServiceProcess;
using System.Text;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using System.Security.Principal;
using System.IO;
using System.Text.Json;
using System.Collections.Generic;
using Forms = System.Windows.Forms;

namespace UKiller;

public enum TargetType
{
    Process,
    Service,
    ScheduledTask
}

public class TargetItem
{
    public bool IsSelected { get; set; }
    public TargetType Type { get; set; }
    public string Name { get; set; } = string.Empty;
    public string DisplayName { get; set; } = string.Empty;
    public string Extra { get; set; } = string.Empty;
    public bool IsStoppedService { get; set; }
    public bool IsActive { get; set; } = true;
    public bool IsDisabledLike { get; set; }
}

public partial class MainWindow : Window
{
    private readonly ObservableCollection<TargetItem> _targets = new();
    private readonly DispatcherTimer _autoScanTimer = new();
    private readonly Forms.NotifyIcon _notifyIcon = new();
    private readonly string _selfProcessName = Process.GetCurrentProcess().ProcessName;
    private readonly Dictionary<string, string> _i18n = new(StringComparer.OrdinalIgnoreCase);
    private readonly Forms.ToolStripMenuItem _trayShowItem = new();
    private readonly Forms.ToolStripMenuItem _trayRefreshItem = new();
    private readonly Forms.ToolStripMenuItem _trayExitItem = new();
    private string _language = "en";
    private bool _uiReady;

    private sealed class UpKillerSettings
    {
        public string? Filter { get; set; }
        public bool IncludeWindowsUpdate { get; set; }
        public bool ShowOnlyActive { get; set; } = true;
        public int AutoScanMinutes { get; set; } = 5;
        public bool AutoScanEnabled { get; set; }
        public string Language { get; set; } = "en";
    }

    private static string SettingsFilePath =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "UpKiller", "settings.json");

    public MainWindow()
    {
        InitializeComponent();
        TargetsGrid.ItemsSource = _targets;
        SetLanguage("en", applyOnly: true);

        _autoScanTimer.Interval = TimeSpan.FromMinutes(5);
        _autoScanTimer.Tick += (_, _) => PerformScan(false);

        // Notify icon (system tray)
        _notifyIcon.Icon = System.Drawing.SystemIcons.Application;
        _notifyIcon.Visible = true;
        _notifyIcon.Text = "UpKiller";
        var menu = new Forms.ContextMenuStrip();
        _trayShowItem.Click += (_, _) => RestoreFromTray();
        _trayRefreshItem.Click += (_, _) => Dispatcher.Invoke(() => PerformScan(true));
        _trayExitItem.Click += (_, _) =>
        {
            _notifyIcon.Visible = false;
            Application.Current.Shutdown();
        };
        menu.Items.Add(_trayShowItem);
        menu.Items.Add(_trayRefreshItem);
        menu.Items.Add("-");
        menu.Items.Add(_trayExitItem);
        _notifyIcon.ContextMenuStrip = menu;

        // Ayarları yükle
        LoadSettings();
        _uiReady = true;

        // Admin uyarısı
        if (IsRunningAsAdmin())
        {
            AdminWarningTextBlock.Text = T("admin.running");
            AdminWarningTextBlock.Foreground = System.Windows.Media.Brushes.Green;
        }
        else
        {
            AdminWarningTextBlock.Text = T("admin.need");
            AdminWarningTextBlock.Foreground = System.Windows.Media.Brushes.Red;
            MessageBox.Show(T("msg.admin.body"), T("msg.admin.title"), MessageBoxButton.OK, MessageBoxImage.Warning);
        }

        // İlk açılışta bir kere tara
        PerformScan(false);
    }

    private void ScanButton_Click(object sender, RoutedEventArgs e)
    {
        PerformScan(true);
    }

    private void ScanProcesses(Func<string, bool> matcher)
    {
        foreach (var proc in Process.GetProcesses().OrderBy(p => p.ProcessName))
        {
            string name = proc.ProcessName;
            if (string.Equals(name, _selfProcessName, StringComparison.OrdinalIgnoreCase))
                continue;
            string display = proc.MainWindowTitle;
            string combined = $"{name} {display}";
            if (!matcher(combined))
                continue;

            double ramMb = 0;
            double cpuSeconds = 0;

            try
            {
                ramMb = proc.WorkingSet64 / 1024d / 1024d;
            }
            catch { }

            try
            {
                cpuSeconds = proc.TotalProcessorTime.TotalSeconds;
            }
            catch { }

            _targets.Add(new TargetItem
            {
                Type = TargetType.Process,
                Name = name,
                DisplayName = string.IsNullOrWhiteSpace(display) ? name : display,
                Extra = $"{T("detail.pid")}: {SafeGetPid(proc)}, RAM: {ramMb:F1} MB, {T("detail.cpuTime")}: {cpuSeconds:F1} {T("detail.seconds")}",
                IsActive = true,
                IsDisabledLike = false
            });
        }
    }

    private bool IsRunningAsAdmin()
    {
        try
        {
            using var identity = WindowsIdentity.GetCurrent();
            var principal = new WindowsPrincipal(identity);
            return principal.IsInRole(WindowsBuiltInRole.Administrator);
        }
        catch
        {
            return false;
        }
    }

    private Func<string, bool>? BuildMatcher()
    {
        var mainFilter = FilterTextBox.Text?.Trim();
        var patterns = new System.Collections.Generic.List<string>();

        if (!string.IsNullOrWhiteSpace(mainFilter))
        {
            var tokens = ParseSearchTokens(mainFilter);

            foreach (var token in tokens)
            {
                if (!patterns.Contains(token, StringComparer.OrdinalIgnoreCase))
                {
                    patterns.Add(token);
                }
            }
        }

        if (patterns.Count == 0)
        {
            patterns.Add("update");
            FilterTextBox.Text = "update";
        }

        // 1) Ana filtreyi regex gibi dene (tırnaklı ifade varsa contains modunda kal)
        if (!string.IsNullOrWhiteSpace(mainFilter) && !mainFilter.Contains('\"'))
        {
            try
            {
                var regex = new Regex(mainFilter, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
                return s => regex.IsMatch(s) || patterns
                    .Skip(1)
                    .Any(p => s.Contains(p, StringComparison.OrdinalIgnoreCase));
            }
            catch
            {
                // Geçersiz regex ise sessizce normal contains'e düş
            }
        }

        // 2) Hepsi için normal contains
        return s => patterns.Any(p => s.Contains(p, StringComparison.OrdinalIgnoreCase));
    }

    private void LanguageComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_uiReady)
            return;

        if (LanguageComboBox.SelectedItem is System.Windows.Controls.ComboBoxItem item &&
            item.Tag is string tag)
        {
            SetLanguage(tag, applyOnly: false);
        }
    }

    private void SetLanguage(string languageCode, bool applyOnly)
    {
        _language = string.IsNullOrWhiteSpace(languageCode) ? "en" : languageCode.ToLowerInvariant();
        LoadLanguageJson(_language);
        ApplyLocalization();
        if (!applyOnly)
        {
            SaveSettings();
        }
    }

    private void LoadLanguageJson(string languageCode)
    {
        _i18n.Clear();

        var baseDir = AppContext.BaseDirectory;
        var file = Path.Combine(baseDir, "lang", $"{languageCode}.json");
        var fallback = Path.Combine(baseDir, "lang", "en.json");
        var toRead = File.Exists(file) ? file : fallback;

        if (!File.Exists(toRead))
            return;

        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(toRead));
            foreach (var prop in doc.RootElement.EnumerateObject())
            {
                if (prop.Value.ValueKind == JsonValueKind.String)
                {
                    _i18n[prop.Name] = prop.Value.GetString() ?? string.Empty;
                }
            }
        }
        catch
        {
            // ignore localization parse errors
        }
    }

    private string T(string key)
    {
        return _i18n.TryGetValue(key, out var value) && !string.IsNullOrWhiteSpace(value)
            ? value
            : key;
    }

    private void ApplyLocalization()
    {
        Title = T("window.title");
        AppTitleTextBlock.Text = T("app.title");
        FilterLabelTextBlock.Text = T("filter.label");
        IncludeWindowsUpdateCheckBox.Content = T("include.windowsupdate");
        ShowOnlyActiveCheckBox.Content = T("show.only.active");
        FilterHintTextBlock.Text = T("filter.hint");
        LanguageLabelTextBlock.Text = T("language.label");
        AutoScanLabelTextBlock.Text = T("autoscan.label");
        AutoScanCheckBox.Content = T("autoscan.toggle");
        ScanButton.Content = T("button.search");
        KillButton.Content = "☠";
        ReportLabelTextBlock.Text = T("report.label");

        if (TargetsGrid.Columns.Count >= 5)
        {
            TargetsGrid.Columns[0].Header = T("column.select");
            TargetsGrid.Columns[1].Header = T("column.type");
            TargetsGrid.Columns[2].Header = T("column.name");
            TargetsGrid.Columns[3].Header = T("column.displayName");
            TargetsGrid.Columns[4].Header = T("column.details");
        }

        _trayShowItem.Text = T("tray.show");
        _trayRefreshItem.Text = T("tray.refresh");
        _trayExitItem.Text = T("tray.exit");

        foreach (System.Windows.Controls.ComboBoxItem item in LanguageComboBox.Items)
        {
            if ((item.Tag as string)?.Equals(_language, StringComparison.OrdinalIgnoreCase) == true)
            {
                LanguageComboBox.SelectedItem = item;
                break;
            }
        }
    }

    private static System.Collections.Generic.List<string> ParseSearchTokens(string input)
    {
        var result = new System.Collections.Generic.List<string>();

        // "Hesap Makinesi" gibi tırnaklı kalıpları tek parça alır, diğerlerini boşlukla böler.
        foreach (Match m in Regex.Matches(input, "\"([^\"]+)\"|(\\S+)"))
        {
            var token = m.Groups[1].Success ? m.Groups[1].Value : m.Groups[2].Value;
            if (!string.IsNullOrWhiteSpace(token))
            {
                result.Add(token.Trim());
            }
        }

        return result;
    }

    private void PerformScan(bool showMessageBox)
    {
        _targets.Clear();
        ReportTextBox.Text = string.Empty;

        var matcher = BuildMatcher();
        if (matcher is null)
            return;

        ScanProcesses(matcher);
        ScanServices(matcher, IncludeWindowsUpdateCheckBox.IsChecked == true);
        ScanScheduledTasks(matcher);

        if (ShowOnlyActiveCheckBox.IsChecked == true)
        {
            var activeOnly = _targets.Where(t => t.IsActive).ToList();
            _targets.Clear();
            foreach (var item in activeOnly)
            {
                _targets.Add(item);
            }
        }

        // Aktif olanları (çalışan process + Running servisler) üstte gelecek şekilde sırala
        var ordered = _targets
            .OrderByDescending(t =>
                t.Type == TargetType.Process ||
                (t.Type == TargetType.Service && !t.IsStoppedService))
            .ThenBy(t => t.Type)
            .ThenBy(t => t.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();

        _targets.Clear();
        foreach (var item in ordered)
        {
            _targets.Add(item);
        }

        // Varsayılan: tüm satırlar seçili gelsin, kullanıcı istemediklerini kaldırsın
        foreach (var t in _targets)
        {
            t.IsSelected = !t.IsDisabledLike;
        }
        TargetsGrid.Items.Refresh();

        if (showMessageBox)
        {
            MessageBox.Show(string.Format(T("msg.scan.body"), _targets.Count), T("msg.scan.title"), MessageBoxButton.OK, MessageBoxImage.Information);
        }
    }

    private static int SafeGetPid(Process p)
    {
        try
        {
            return p.Id;
        }
        catch
        {
            return -1;
        }
    }

    private void ScanServices(Func<string, bool> matcher, bool includeWindowsUpdate)
    {
        var knownUpdateServiceNames = new[]
        {
            "wuauserv",   // Windows Update
            "UsoSvc",     // Update Orchestrator Service
            "BITS",       // Background Intelligent Transfer Service
            "WaaSMedicSvc" // Windows Update Medic Service
        };

        foreach (var sc in ServiceController.GetServices().OrderBy(s => s.DisplayName))
        {
            string name = sc.ServiceName;
            string display = sc.DisplayName;
            bool isWindowsUpdateRelated =
                display.Contains("Windows Update", StringComparison.OrdinalIgnoreCase) ||
                knownUpdateServiceNames.Contains(name, StringComparer.OrdinalIgnoreCase);

            if (!matcher($"{name} {display}") && !(includeWindowsUpdate && isWindowsUpdateRelated))
                continue;

            _targets.Add(new TargetItem
            {
                Type = TargetType.Service,
                Name = name,
                DisplayName = display,
                Extra = sc.Status == ServiceControllerStatus.Stopped ? $"{T("detail.status")}: {T("detail.disabled")}" : $"{T("detail.status")}: {sc.Status}",
                IsStoppedService = sc.Status == ServiceControllerStatus.Stopped,
                IsActive = sc.Status != ServiceControllerStatus.Stopped,
                IsDisabledLike = sc.Status == ServiceControllerStatus.Stopped
            });
        }
    }

    private void ScanScheduledTasks(Func<string, bool> matcher)
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = "schtasks.exe",
                Arguments = "/Query /FO CSV /V /NH",
                RedirectStandardOutput = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };

            using var proc = Process.Start(psi);
            if (proc == null)
                return;

            string? line;
            while ((line = proc.StandardOutput.ReadLine()) != null)
            {
                if (string.IsNullOrWhiteSpace(line))
                    continue;

                // CSV: "TaskName","Next Run Time",...,"Status",...
                string[] cols = ParseCsvLine(line);
                if (cols.Length < 3)
                    continue;

                string taskName = cols[0].Trim('"');
                string nextRun = cols.Length > 1 ? cols[1].Trim('"') : string.Empty;
                string status = cols.Length > 3 ? cols[3].Trim('"') : string.Empty;
                bool isDisabledTask =
                    status.Contains("disabled", StringComparison.OrdinalIgnoreCase) ||
                    status.Contains("devre", StringComparison.OrdinalIgnoreCase);

                var combined = $"{taskName} {status}";
                if (!matcher(combined))
                    continue;

                _targets.Add(new TargetItem
                {
                    Type = TargetType.ScheduledTask,
                    Name = taskName,
                    DisplayName = taskName,
                    Extra = $"{T("detail.nextRun")}: {nextRun}, {T("detail.status")}: {status}",
                    IsActive = !isDisabledTask,
                    IsDisabledLike = isDisabledTask
                });
            }
        }
        catch (Exception ex)
        {
            MessageBox.Show(string.Format(T("msg.taskReadError.body"), ex.Message), T("msg.error.title"), MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private static string[] ParseCsvLine(string line)
    {
        var result = new System.Collections.Generic.List<string>();
        var sb = new StringBuilder();
        bool inQuotes = false;

        foreach (char c in line)
        {
            if (c == '\"')
            {
                inQuotes = !inQuotes;
                sb.Append(c);
            }
            else if (c == ',' && !inQuotes)
            {
                result.Add(sb.ToString());
                sb.Clear();
            }
            else
            {
                sb.Append(c);
            }
        }
        result.Add(sb.ToString());
        return result.ToArray();
    }

    private void KillButton_Click(object sender, RoutedEventArgs e)
    {
        var selected = _targets.Where(t => t.IsSelected).ToList();
        if (!selected.Any())
        {
            MessageBox.Show(T("msg.noSelection.body"), T("msg.noSelection.title"), MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        if (MessageBox.Show(string.Format(T("msg.confirm.body"), selected.Count),
                T("msg.confirm.title"), MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes)
        {
            return;
        }

        var sb = new StringBuilder();
        sb.AppendLine($"{T("report.timestamp")}: {DateTime.Now}");
        sb.AppendLine();

        foreach (var item in selected)
        {
            switch (item.Type)
            {
                case TargetType.Process:
                    KillProcess(item, sb);
                    break;
                case TargetType.Service:
                    StopAndDisableService(item, sb);
                    break;
                case TargetType.ScheduledTask:
                    DisableScheduledTask(item, sb);
                    break;
            }
        }

        ReportTextBox.Text = sb.ToString();
        MessageBox.Show(T("msg.done.body"), T("msg.done.title"), MessageBoxButton.OK, MessageBoxImage.Information);
    }

    private void KillProcess(TargetItem item, StringBuilder sb)
    {
        try
        {
            var processes = Process.GetProcessesByName(item.Name);
            if (processes.Length == 0)
            {
                sb.AppendLine($"[PROCESS] {item.Name}: zaten çalışmıyor.");
                return;
            }

            foreach (var p in processes)
            {
                try
                {
                    sb.AppendLine($"[PROCESS] {item.Name} (PID: {p.Id}) öldürülüyor...");
                    p.Kill(true);
                    p.WaitForExit(5000);
                    sb.AppendLine($"[PROCESS] {item.Name} (PID: {p.Id}) kapatıldı.");
                }
                catch (Exception ex)
                {
                    sb.AppendLine($"[PROCESS] {item.Name} (PID: {p.Id}) kapatılamadı: {ex.Message}");
                }
            }
        }
        catch (Exception ex)
        {
            sb.AppendLine($"[PROCESS] {item.Name} genel hata: {ex.Message}");
        }
    }

    private void StopAndDisableService(TargetItem item, StringBuilder sb)
    {
        try
        {
            using var sc = new ServiceController(item.Name);
            sb.AppendLine($"[SERVICE] {item.DisplayName} ({item.Name}) durum: {sc.Status}");

            if (sc.Status is ServiceControllerStatus.Running or ServiceControllerStatus.Paused or ServiceControllerStatus.StartPending)
            {
                try
                {
                    sb.AppendLine($"[SERVICE] {item.Name} durduruluyor...");
                    sc.Stop();
                    sc.WaitForStatus(ServiceControllerStatus.Stopped, TimeSpan.FromSeconds(15));
                    sb.AppendLine($"[SERVICE] {item.Name} durduruldu.");
                }
                catch (Exception ex)
                {
                    sb.AppendLine($"[SERVICE] {item.Name} durdurulamadı: {ex.Message}");
                }
            }

            try
            {
                var psi = new ProcessStartInfo
                {
                    FileName = "sc.exe",
                    Arguments = $"config \"{item.Name}\" start= disabled",
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true
                };
                using var proc = Process.Start(psi);
                if (proc != null)
                {
                    string output = proc.StandardOutput.ReadToEnd();
                    string error = proc.StandardError.ReadToEnd();
                    proc.WaitForExit(5000);
                    sb.AppendLine($"[SERVICE] {item.Name} disabled yapıldı. Çıkış kodu: {proc.ExitCode}");
                    if (!string.IsNullOrWhiteSpace(output))
                        sb.AppendLine($"    OUT: {output.Trim()}");
                    if (!string.IsNullOrWhiteSpace(error))
                        sb.AppendLine($"    ERR: {error.Trim()}");
                }
            }
            catch (Exception ex)
            {
                sb.AppendLine($"[SERVICE] {item.Name} disable yapılamadı: {ex.Message}");
            }
        }
        catch (Exception ex)
        {
            sb.AppendLine($"[SERVICE] {item.Name} genel hata: {ex.Message}");
        }
    }

    private void DisableScheduledTask(TargetItem item, StringBuilder sb)
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = "schtasks.exe",
                Arguments = $"/Change /TN \"{item.Name}\" /Disable",
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            };

            using var proc = Process.Start(psi);
            if (proc == null)
            {
                sb.AppendLine($"[TASK] {item.Name} için schtasks başlatılamadı.");
                return;
            }

            string output = proc.StandardOutput.ReadToEnd();
            string error = proc.StandardError.ReadToEnd();
            proc.WaitForExit(5000);

            sb.AppendLine($"[TASK] {item.Name} disable edildi. Çıkış kodu: {proc.ExitCode}");
            if (!string.IsNullOrWhiteSpace(output))
                sb.AppendLine($"    OUT: {output.Trim()}");
            if (!string.IsNullOrWhiteSpace(error))
                sb.AppendLine($"    ERR: {error.Trim()}");
        }
        catch (Exception ex)
        {
            sb.AppendLine($"[TASK] {item.Name} disable edilemedi: {ex.Message}");
        }
    }

    private void FilterTextBox_KeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key == System.Windows.Input.Key.Enter)
        {
            e.Handled = true;
            PerformScan(true);
        }
    }

    private void AutoScanCheckBox_Checked(object? sender, RoutedEventArgs e)
    {
        if (AutoScanCheckBox.IsChecked == true)
        {
            if (!int.TryParse(AutoScanMinutesTextBox.Text, out var minutes) || minutes <= 0)
            {
                minutes = 5;
                AutoScanMinutesTextBox.Text = "5";
            }

            _autoScanTimer.Interval = TimeSpan.FromMinutes(minutes);
            _autoScanTimer.Start();
        }
        else
        {
            _autoScanTimer.Stop();
        }
    }

    private void AutoScanMinutesTextBox_LostFocus(object sender, RoutedEventArgs e)
    {
        if (_autoScanTimer.IsEnabled)
        {
            AutoScanCheckBox_Checked(null, null!);
        }
    }

    private void ShowOnlyActiveCheckBox_Changed(object sender, RoutedEventArgs e)
    {
        if (!_uiReady)
            return;

        PerformScan(false);
    }

    private void Window_StateChanged(object sender, EventArgs e)
    {
        if (WindowState == WindowState.Minimized)
        {
            Hide();
            _notifyIcon.BalloonTipTitle = T("tray.balloon.title");
            _notifyIcon.BalloonTipText = T("tray.balloon.text");
            _notifyIcon.ShowBalloonTip(2000);
        }
    }

    private void RestoreFromTray()
    {
        Show();
        WindowState = WindowState.Normal;
        Activate();
    }

    private void Window_Closing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        SaveSettings();
        var result = MessageBox.Show(T("msg.close.body"), T("msg.close.title"), MessageBoxButton.YesNo, MessageBoxImage.Question);
        if (result == MessageBoxResult.No)
        {
            e.Cancel = true;
            Hide();
            _notifyIcon.BalloonTipTitle = T("tray.balloon.title");
            _notifyIcon.BalloonTipText = T("tray.balloon.text");
            _notifyIcon.ShowBalloonTip(2000);
        }
        else
        {
            _notifyIcon.Visible = false;
        }
    }

    private void LoadSettings()
    {
        try
        {
            if (File.Exists(SettingsFilePath))
            {
                var json = File.ReadAllText(SettingsFilePath);
                var settings = JsonSerializer.Deserialize<UpKillerSettings>(json);
                if (settings != null)
                {
                    SetLanguage(string.IsNullOrWhiteSpace(settings.Language) ? "en" : settings.Language, applyOnly: true);

                    if (!string.IsNullOrWhiteSpace(settings.Filter))
                        FilterTextBox.Text = settings.Filter;

                    IncludeWindowsUpdateCheckBox.IsChecked = settings.IncludeWindowsUpdate;
                    ShowOnlyActiveCheckBox.IsChecked = settings.ShowOnlyActive;

                    AutoScanMinutesTextBox.Text = settings.AutoScanMinutes > 0
                        ? settings.AutoScanMinutes.ToString()
                        : "5";

                    AutoScanCheckBox.IsChecked = settings.AutoScanEnabled;

                    if (settings.AutoScanEnabled)
                    {
                        AutoScanCheckBox_Checked(null, null!);
                    }
                }
            }
        }
        catch
        {
            // Ayar okunamazsa sessizce devam et
        }
    }

    private void SaveSettings()
    {
        try
        {
            var settings = new UpKillerSettings
            {
                Filter = FilterTextBox.Text,
                IncludeWindowsUpdate = IncludeWindowsUpdateCheckBox.IsChecked == true,
                ShowOnlyActive = ShowOnlyActiveCheckBox.IsChecked == true,
                AutoScanMinutes = int.TryParse(AutoScanMinutesTextBox.Text, out var mins) && mins > 0 ? mins : 5,
                AutoScanEnabled = AutoScanCheckBox.IsChecked == true,
                Language = _language
            };

            var dir = Path.GetDirectoryName(SettingsFilePath);
            if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
            {
                Directory.CreateDirectory(dir);
            }

            var json = JsonSerializer.Serialize(settings, new JsonSerializerOptions
            {
                WriteIndented = true
            });

            File.WriteAllText(SettingsFilePath, json);
        }
        catch
        {
            // Yazılamazsa sessizce bırak
        }
    }
}

