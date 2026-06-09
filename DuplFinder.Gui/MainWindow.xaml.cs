using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;
using Forms = System.Windows.Forms;

namespace DuplFinder.Gui;

public partial class MainWindow : Window
{
    private sealed record CommandInvocation(string FileName, IReadOnlyList<string> Arguments);

    private sealed class CommandPlan
    {
        public bool IsReady { get; init; }
        public string NotReadyReason { get; init; } = "";
        public List<CommandInvocation> Invocations { get; init; } = [];
        public bool RequiresConfirmation { get; init; }
        public string ConfirmationText { get; init; } = "";
        public string Preview { get; init; } = "";
    }

    private sealed record FileTypeGroup(string Name, string[] Extensions, bool CheckedByDefault);

    private static readonly FileTypeGroup[] FileTypeGroups =
    [
        new("Documents", [".txt", ".pdf", ".doc", ".docx", ".xls", ".xlsx", ".ppt", ".pptx", ".odt", ".ods", ".rtf", ".csv", ".md"], true),
        new("Images", [".jpg", ".jpeg", ".png", ".gif", ".bmp", ".webp", ".tif", ".tiff", ".heic", ".raw", ".cr2", ".nef", ".arw"], true),
        new("Video", [".mp4", ".mkv", ".avi", ".mov", ".wmv", ".webm", ".flv", ".m4v", ".mpeg", ".mpg"], true),
        new("Audio", [".mp3", ".flac", ".wav", ".aac", ".m4a", ".ogg", ".opus", ".wma"], true),
        new("Archives / disk images", [".zip", ".7z", ".rar", ".tar", ".gz", ".iso"], false)
    ];

    private static readonly HashSet<string> CliDefaultExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".pdf", ".txt", ".md", ".rtf", ".doc", ".docx", ".xls", ".xlsx", ".ppt", ".pptx", ".odt", ".ods", ".csv",
        ".jpg", ".jpeg", ".png", ".webp", ".gif", ".bmp", ".tif", ".tiff", ".heic", ".raw", ".cr2", ".nef", ".arw",
        ".mp4", ".mkv", ".avi", ".mov", ".wmv", ".flv", ".webm", ".mpeg", ".mpg", ".m4v",
        ".mp3", ".flac", ".wav", ".aac", ".ogg", ".m4a", ".opus", ".wma"
    };

    private static readonly string[] MinSizeLabels = ["0 B", "1 KB", "10 KB", "100 KB", "1 MB", "10 MB", "100 MB", "1 GB", "10 GB"];
    private static readonly string[] MinSizeArgs = ["", "1KB", "10KB", "100KB", "1MB", "10MB", "100MB", "1GB", "10GB"];

    private readonly ObservableCollection<string> _scanTargets = [];
    private readonly Dictionary<string, System.Windows.Controls.CheckBox> _extensionBoxes = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, System.Windows.Controls.CheckBox> _driveBoxes = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<(System.Windows.Controls.CheckBox GroupBox, List<System.Windows.Controls.CheckBox> ChildBoxes)> _fileTypeGroupBoxes = [];
    private System.Windows.Controls.CheckBox? _extensionlessBox;
    private bool _updatingFileTypes;
    private bool _isRunning;
    private Process? _runningProcess;
    private CancellationTokenSource? _runCts;

    public MainWindow()
    {
        InitializeComponent();
        SelectedTargetsListBox.ItemsSource = _scanTargets;
        CliPathTextBox.Text = FindDefaultCliPath();
        DbPathTextBox.Text = Path.GetFullPath("duplicates.db");
        ReportPathTextBox.Text = Path.GetFullPath("prestage-report.html");
        QuarantineFolderTextBox.Text = Path.GetFullPath("DuplFinder-Quarantine");
        BuildFileTypeCheckboxes();
        RefreshDrives();
        AppendOutput("DuplFinder GUI MVP loaded. No command runs until you click Run.");
        UpdateCommandPreviewAndState();
    }

    private void BuildFileTypeCheckboxes()
    {
        FileTypeGroupsPanel.Children.Clear();
        _extensionBoxes.Clear();
        _fileTypeGroupBoxes.Clear();

        foreach (var group in FileTypeGroups)
        {
            var groupBox = new System.Windows.Controls.GroupBox { Header = group.Name, Margin = new Thickness(0, 0, 0, 8) };
            var panel = new StackPanel { Margin = new Thickness(8) };
            var groupCheckBox = new System.Windows.Controls.CheckBox
            {
                Content = group.Name,
                FontWeight = FontWeights.SemiBold,
                IsThreeState = true,
                Margin = new Thickness(0, 0, 0, 4)
            };
            panel.Children.Add(groupCheckBox);

            var wrap = new WrapPanel();
            var childBoxes = new List<System.Windows.Controls.CheckBox>();
            foreach (var extension in group.Extensions)
            {
                var checkBox = new System.Windows.Controls.CheckBox
                {
                    Content = extension,
                    Tag = extension,
                    IsChecked = CliDefaultExtensions.Contains(extension),
                    Margin = new Thickness(0, 0, 12, 6)
                };
                checkBox.Checked += OnExtensionChanged;
                checkBox.Unchecked += OnExtensionChanged;
                wrap.Children.Add(checkBox);
                childBoxes.Add(checkBox);
                _extensionBoxes[extension] = checkBox;
            }

            groupCheckBox.IsChecked = childBoxes.All(static box => box.IsChecked == true);
            groupCheckBox.Checked += (_, _) => SetGroupExtensions(childBoxes, true);
            groupCheckBox.Unchecked += (_, _) => SetGroupExtensions(childBoxes, false);
            _fileTypeGroupBoxes.Add((groupCheckBox, childBoxes));

            panel.Children.Add(wrap);
            groupBox.Content = panel;
            FileTypeGroupsPanel.Children.Add(groupBox);
        }

        var extensionless = new System.Windows.Controls.CheckBox
        {
            Content = "Files with no extension",
            IsChecked = false,
            Margin = new Thickness(0, 6, 0, 4),
            ToolTip = "Usually disabled. Some system/config/cache files have no extension. Enable only if you intentionally want to scan extensionless files."
        };
        extensionless.Checked += OnExtensionChanged;
        extensionless.Unchecked += OnExtensionChanged;
        _extensionlessBox = extensionless;
        FileTypeGroupsPanel.Children.Add(extensionless);
    }

    private void SetGroupExtensions(IEnumerable<System.Windows.Controls.CheckBox> boxes, bool isChecked)
    {
        if (_updatingFileTypes)
            return;

        _updatingFileTypes = true;
        foreach (var box in boxes)
            box.IsChecked = isChecked;
        RefreshFileTypeGroupCheckboxes();
        _updatingFileTypes = false;
        UpdateCommandPreviewAndState();
    }

    private void OnExtensionChanged(object sender, RoutedEventArgs e)
    {
        if (_updatingFileTypes)
            return;

        RefreshFileTypeGroupCheckboxes();
        UpdateCommandPreviewAndState();
    }

    private void RefreshFileTypeGroupCheckboxes()
    {
        _updatingFileTypes = true;
        foreach (var (groupBox, childBoxes) in _fileTypeGroupBoxes)
        {
            var checkedCount = childBoxes.Count(static box => box.IsChecked == true);
            groupBox.IsChecked = checkedCount == 0
                ? false
                : checkedCount == childBoxes.Count
                    ? true
                    : null;
        }
        _updatingFileTypes = false;
    }

    private void RefreshDrives()
    {
        DriveListBox.Items.Clear();
        _driveBoxes.Clear();

        foreach (var drive in DriveInfo.GetDrives().OrderBy(static d => d.Name, StringComparer.OrdinalIgnoreCase))
        {
            var root = drive.Name;
            var label = "";
            var details = drive.DriveType.ToString();
            var available = false;
            try
            {
                available = drive.IsReady;
                if (available)
                {
                    label = drive.VolumeLabel;
                    details = $"{drive.DriveType}, {FormatBytes(drive.AvailableFreeSpace)} free of {FormatBytes(drive.TotalSize)}, {drive.DriveFormat}";
                }
                else
                {
                    details = $"{drive.DriveType}, unavailable";
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                details = $"{drive.DriveType}, unavailable: {ex.Message}";
            }

            var checkBox = new System.Windows.Controls.CheckBox
            {
                Content = $"{root} {label} ({details})",
                Tag = root,
                IsEnabled = available,
                Margin = new Thickness(0, 0, 0, 4)
            };
            checkBox.Checked += OnDriveChecked;
            checkBox.Unchecked += OnDriveUnchecked;
            DriveListBox.Items.Add(checkBox);
            _driveBoxes[root] = checkBox;
        }
    }

    private void OnDriveChecked(object sender, RoutedEventArgs e)
    {
        if (sender is System.Windows.Controls.CheckBox { Tag: string path })
            AddScanTarget(path);
    }

    private void OnDriveUnchecked(object sender, RoutedEventArgs e)
    {
        if (sender is System.Windows.Controls.CheckBox { Tag: string path })
            RemoveScanTarget(path);
    }

    private void AddScanTarget(string path)
    {
        if (!TryNormalizeTarget(path, out var normalized, out var error))
        {
            AppendOutput($"Target rejected: {error}");
            return;
        }

        if (_scanTargets.Any(existing => string.Equals(existing, normalized, StringComparison.OrdinalIgnoreCase)))
        {
            AppendOutput($"Target already selected: {normalized}");
            return;
        }

        _scanTargets.Add(normalized);
        UpdateTargetWarnings();
        UpdateCommandPreviewAndState();
    }

    private void RemoveScanTarget(string path)
    {
        if (!TryNormalizeTarget(path, out var normalized, out _))
            normalized = path;

        var existing = _scanTargets.FirstOrDefault(target => string.Equals(target, normalized, StringComparison.OrdinalIgnoreCase));
        if (existing is not null)
            _scanTargets.Remove(existing);

        UpdateTargetWarnings();
        UpdateCommandPreviewAndState();
    }

    private void UpdateTargetWarnings()
    {
        var warning = new StringBuilder();
        for (var i = 0; i < _scanTargets.Count; i++)
        {
            for (var j = 0; j < _scanTargets.Count; j++)
            {
                if (i == j)
                    continue;

                if (IsParentPath(_scanTargets[i], _scanTargets[j]))
                    warning.AppendLine($"Warning: {_scanTargets[j]} is inside {_scanTargets[i]} and may be scanned twice.");
            }
        }

        TargetWarningTextBlock.Text = warning.ToString().Trim();
    }

    private static bool IsParentPath(string parent, string child)
    {
        if (string.Equals(parent, child, StringComparison.OrdinalIgnoreCase))
            return false;

        var normalizedParent = EnsureTrailingSeparator(parent);
        return child.StartsWith(normalizedParent, StringComparison.OrdinalIgnoreCase);
    }

    private static bool TryNormalizeTarget(string path, out string normalized, out string error)
    {
        normalized = "";
        error = "";
        if (string.IsNullOrWhiteSpace(path))
        {
            error = "path is empty";
            return false;
        }

        try
        {
            var full = Path.GetFullPath(path);
            var root = Path.GetPathRoot(full);
            normalized = string.Equals(full.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar), root?.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar), StringComparison.OrdinalIgnoreCase)
                ? root!
                : full.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            return true;
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or NotSupportedException)
        {
            error = ex.Message;
            return false;
        }
    }

    private CommandPlan BuildCurrentCommandPlan()
    {
        var cliPath = CliPathTextBox.Text.Trim();
        if (string.IsNullOrWhiteSpace(cliPath))
            return NotReady("Not ready: select CLI executable path");

        return MainTabs.SelectedIndex switch
        {
            0 => BuildScanPlan(cliPath),
            1 => BuildDuplicatesPlan(cliPath),
            2 => BuildPrestagePlan(cliPath),
            3 => BuildApplyPlan(cliPath),
            4 => BuildUndoPlan(cliPath),
            5 => BuildPurgePlan(cliPath),
            6 => BuildStatsCleanPlan(cliPath),
            _ => NotReady("Not ready: select a command tab")
        };
    }

    private CommandPlan BuildScanPlan(string cliPath)
    {
        var dbPath = DbPathTextBox.Text.Trim();
        var selectedExtensions = GetSelectedExtensions();
        var includeNoExtension = _extensionlessBox?.IsChecked == true;

        if (_scanTargets.Count == 0)
            return NotReady("Not ready: select one or more drives or folders first");
        if (string.IsNullOrWhiteSpace(dbPath))
            return NotReady("Not ready: select database path");
        if (selectedExtensions.Count == 0 && !includeNoExtension)
            return NotReady("Not ready: select at least one file type to scan");

        var profile = GetSelectedProfile();
        var invocations = new List<CommandInvocation>();
        foreach (var target in _scanTargets)
        {
            var args = new List<string> { "scan", target, "--db", dbPath, "--profile", profile };
            if (selectedExtensions.Count > 0)
                args.AddRange(["--include-ext", string.Join(",", selectedExtensions)]);
            if (includeNoExtension)
                args.Add("--include-no-extension");
            if (RecordSkippedCheckBox.IsChecked == true)
                args.Add("--record-skipped");
            if (FollowReparseCheckBox.IsChecked == true)
                args.Add("--follow-reparse-points");
            invocations.Add(new CommandInvocation(cliPath, args));
        }

        var driveRoots = _scanTargets.Where(IsDriveRoot).ToArray();
        var preview = new StringBuilder();
        if (invocations.Count > 1)
            preview.AppendLine($"# Scan batch: {invocations.Count} targets").AppendLine();
        foreach (var invocation in invocations)
            preview.AppendLine(FormatPreview(invocation));

        return new CommandPlan
        {
            IsReady = true,
            Invocations = invocations,
            Preview = preview.ToString().TrimEnd(),
            RequiresConfirmation = driveRoots.Length > 0,
            ConfirmationText = driveRoots.Length == 0
                ? ""
                : "You selected one or more full drive roots.\n\nThis can scan a very large number of files and may take a long time.\nSystem or protected folders may be skipped due to permissions.\n\nSelected drive roots:\n" + string.Join(Environment.NewLine, driveRoots) + "\n\nContinue?"
        };
    }

    private CommandPlan BuildDuplicatesPlan(string cliPath)
    {
        var dbPath = DbPathTextBox.Text.Trim();
        if (!ExistingFile(dbPath))
            return NotReady("Not ready: run scan first or select an existing DuplFinder database.");

        var args = new List<string> { "duplicates", "--db", dbPath };
        var minSize = GetMinSizeArg();
        if (!string.IsNullOrWhiteSpace(minSize))
            args.AddRange(["--min-size", minSize]);
        if (ExportCsvCheckBox.IsChecked == true && !string.IsNullOrWhiteSpace(CsvPathTextBox.Text))
            args.AddRange(["--export", CsvPathTextBox.Text.Trim()]);
        return Ready(cliPath, args);
    }

    private CommandPlan BuildPrestagePlan(string cliPath)
    {
        var dbPath = DbPathTextBox.Text.Trim();
        var reportPath = ReportPathTextBox.Text.Trim();
        if (!ExistingFile(dbPath))
            return NotReady("Not ready: run scan first or select an existing DuplFinder database.");
        if (string.IsNullOrWhiteSpace(reportPath))
            return NotReady("Not ready: select HTML report output path");

        var args = new List<string> { "prestage-report", "--db", dbPath, "--out", reportPath };
        if (ReportForceCheckBox.IsChecked == true)
            args.Add("--force");
        return Ready(cliPath, args);
    }

    private CommandPlan BuildApplyPlan(string cliPath)
    {
        var planPath = StagePlanPathTextBox.Text.Trim();
        if (!ExistingFile(planPath))
            return NotReady("Not ready: select an existing stage-plan.json");

        if (ApplyQuarantineRadio.IsChecked == true)
        {
            var quarantineFolder = QuarantineFolderTextBox.Text.Trim();
            if (string.IsNullOrWhiteSpace(quarantineFolder))
                return NotReady("Not ready: select quarantine folder");

            return Ready(cliPath, ["apply-stage-plan", "--plan", planPath, "--quarantine", quarantineFolder], true,
                "Quarantine mode moves selected STAGE files into a DuplFinder quarantine session.\nKEEP files are never moved or modified.\n\nContinue?");
        }

        return Ready(cliPath, ["apply-stage-plan", "--plan", planPath, "--dry-run"]);
    }

    private CommandPlan BuildUndoPlan(string cliPath)
    {
        var manifestPath = ManifestPathTextBox.Text.Trim();
        if (!ExistingFile(manifestPath))
            return NotReady("Not ready: select an existing quarantine manifest");

        if (UndoRestoreRadio.IsChecked == true)
            return Ready(cliPath, ["undo-quarantine", "--manifest", manifestPath, "--restore"], true,
                "Restore mode moves quarantined files back using the manifest and never overwrites existing originals.\n\nContinue?");

        return Ready(cliPath, ["undo-quarantine", "--manifest", manifestPath, "--dry-run"]);
    }

    private CommandPlan BuildPurgePlan(string cliPath)
    {
        var manifestPath = ManifestPathTextBox.Text.Trim();
        if (!ExistingFile(manifestPath))
            return NotReady("Not ready: select an existing quarantine manifest");

        if (PurgeConfirmRadio.IsChecked == true)
            return Ready(cliPath, ["purge-quarantine", "--manifest", manifestPath, "--confirm-purge"], true,
                "This permanently deletes validated files already inside the DuplFinder quarantine session.\nIt does not delete original duplicate paths or KEEP paths.\n\nContinue?");

        return Ready(cliPath, ["purge-quarantine", "--manifest", manifestPath, "--dry-run"]);
    }

    private CommandPlan BuildStatsCleanPlan(string cliPath)
    {
        var dbPath = DbPathTextBox.Text.Trim();
        if (!ExistingFile(dbPath))
            return NotReady("Not ready: run scan first or select an existing DuplFinder database.");

        if (CleanDbRadio.IsChecked == true)
            return Ready(cliPath, ["clean-db", "--db", dbPath], true, "clean-db removes stale database records only. It does not delete files from disk.\n\nContinue?");

        return Ready(cliPath, ["stats", "--db", dbPath]);
    }

    private static CommandPlan Ready(string cliPath, IReadOnlyList<string> args, bool confirmation = false, string confirmationText = "")
    {
        var invocation = new CommandInvocation(cliPath, args);
        return new CommandPlan
        {
            IsReady = true,
            Invocations = [invocation],
            Preview = FormatPreview(invocation),
            RequiresConfirmation = confirmation,
            ConfirmationText = confirmationText
        };
    }

    private static CommandPlan NotReady(string reason) => new()
    {
        IsReady = false,
        NotReadyReason = reason,
        Preview = reason
    };

    private List<string> GetSelectedExtensions() => _extensionBoxes
        .Where(static pair => pair.Value.IsChecked == true)
        .Select(static pair => pair.Key)
        .OrderBy(static ext => ext, StringComparer.OrdinalIgnoreCase)
        .ToList();

    private string GetSelectedProfile()
    {
        if (ProfileComboBox.SelectedItem is ComboBoxItem { Tag: string profile })
            return profile;
        return "sata-ssd";
    }

    private string GetMinSizeArg()
    {
        var index = Math.Clamp((int)Math.Round(MinSizeSlider.Value), 0, MinSizeArgs.Length - 1);
        MinSizeLabel.Text = $"Minimum duplicate file size: {MinSizeLabels[index]}";
        return MinSizeArgs[index];
    }

    private void UpdateCommandPreviewAndState()
    {
        if (!IsLoaded)
            return;

        var selectedExtensions = GetSelectedExtensions();
        var includeNoExtension = _extensionlessBox?.IsChecked == true;
        FileTypeCountTextBlock.Text = $"{selectedExtensions.Count} extensions enabled" + (includeNoExtension ? " + files with no extension" : "");
        var plan = BuildCurrentCommandPlan();
        CommandPreviewTextBox.Text = plan.Preview;
        ReadyTextBlock.Text = plan.IsReady ? "Ready" : plan.NotReadyReason;
        RunCommandButton.IsEnabled = plan.IsReady && !_isRunning;
        OpenReportButton.IsEnabled = ExistingFile(ReportPathTextBox.Text.Trim());
        CancelButton.IsEnabled = _isRunning;
    }

    private async void OnRunCommand(object sender, RoutedEventArgs e)
    {
        var plan = BuildCurrentCommandPlan();
        if (!plan.IsReady)
        {
            AppendOutput(plan.NotReadyReason);
            return;
        }

        if (plan.RequiresConfirmation)
        {
            var result = System.Windows.MessageBox.Show(this, plan.ConfirmationText, "Confirm command", MessageBoxButton.YesNo, MessageBoxImage.Warning);
            if (result != MessageBoxResult.Yes)
            {
                AppendOutput("Command cancelled by user before execution.");
                return;
            }
        }

        _isRunning = true;
        _runCts = new CancellationTokenSource();
        UpdateCommandPreviewAndState();

        try
        {
            foreach (var invocation in plan.Invocations)
            {
                AppendOutput($"> {FormatPreview(invocation)}");
                var exitCode = await RunProcessAsync(invocation, _runCts.Token);
                AppendOutput($"Exit code: {exitCode}");
                if (exitCode != 0)
                {
                    AppendOutput("Stopping command batch after failure.");
                    break;
                }

                ApplyPostRunState(invocation);
            }
        }
        catch (OperationCanceledException)
        {
            AppendOutput("Command cancelled.");
        }
        finally
        {
            _runningProcess = null;
            _runCts?.Dispose();
            _runCts = null;
            _isRunning = false;
            UpdateCommandPreviewAndState();
        }
    }

    private async Task<int> RunProcessAsync(CommandInvocation invocation, CancellationToken cancellationToken)
    {
        var psi = new ProcessStartInfo
        {
            FileName = invocation.FileName,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };

        foreach (var arg in invocation.Arguments)
            psi.ArgumentList.Add(arg);

        using var process = new Process { StartInfo = psi, EnableRaisingEvents = true };
        process.OutputDataReceived += (_, e) =>
        {
            if (e.Data is not null)
                Dispatcher.Invoke(() => AppendOutput(e.Data));
        };
        process.ErrorDataReceived += (_, e) =>
        {
            if (e.Data is not null)
                Dispatcher.Invoke(() => AppendOutput(e.Data));
        };

        if (!process.Start())
            throw new InvalidOperationException("Could not start CLI process.");

        _runningProcess = process;
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        await using var registration = cancellationToken.Register(() =>
        {
            try
            {
                if (!process.HasExited)
                    process.Kill(entireProcessTree: true);
            }
            catch (InvalidOperationException)
            {
            }
        });

        await process.WaitForExitAsync(cancellationToken);
        return process.ExitCode;
    }

    private void ApplyPostRunState(CommandInvocation invocation)
    {
        if (invocation.Arguments.Count == 0)
            return;

        switch (invocation.Arguments[0])
        {
            case "prestage-report":
                if (OpenReportCheckBox.IsChecked == true)
                    OpenPath(ReportPathTextBox.Text.Trim());
                break;
            case "apply-stage-plan":
                var output = OutputTextBox.Text;
                var match = System.Text.RegularExpressions.Regex.Match(output, @"(?m)^Manifest:\s+(.+duplfinder-quarantine-manifest\.json)\s*$");
                if (match.Success)
                    ManifestPathTextBox.Text = match.Groups[1].Value.Trim();
                break;
        }
    }

    private void AppendOutput(string text)
    {
        OutputTextBox.AppendText(text + Environment.NewLine);
        OutputTextBox.ScrollToEnd();
    }

    private void OnCancelCommand(object sender, RoutedEventArgs e) => _runCts?.Cancel();
    private void OnClearOutput(object sender, RoutedEventArgs e) => OutputTextBox.Clear();
    private void OnCopyCommand(object sender, RoutedEventArgs e) => System.Windows.Clipboard.SetText(CommandPreviewTextBox.Text);
    private void OnRunButtonMouseEnter(object sender, System.Windows.Input.MouseEventArgs e) => UpdateCommandPreviewAndState();

    private void OnControlsChanged(object sender, RoutedEventArgs e) => UpdateCommandPreviewAndState();
    private void OnControlsChanged(object sender, TextChangedEventArgs e) => UpdateCommandPreviewAndState();

    private void OnRefreshDrives(object sender, RoutedEventArgs e) => RefreshDrives();

    private void OnChooseFolder(object sender, RoutedEventArgs e)
    {
        using var dialog = new Forms.FolderBrowserDialog { Description = "Choose scan folder", UseDescriptionForTitle = true };
        if (dialog.ShowDialog() == Forms.DialogResult.OK)
            AddScanTarget(dialog.SelectedPath);
    }

    private void OnRemoveSelectedTarget(object sender, RoutedEventArgs e)
    {
        if (SelectedTargetsListBox.SelectedItem is string path)
            RemoveScanTarget(path);
    }

    private void OnClearTargets(object sender, RoutedEventArgs e)
    {
        _scanTargets.Clear();
        foreach (var box in _driveBoxes.Values)
            box.IsChecked = false;
        UpdateTargetWarnings();
        UpdateCommandPreviewAndState();
    }

    private void OnBrowseDb(object sender, RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.SaveFileDialog { Filter = "SQLite database (*.db)|*.db|All files (*.*)|*.*", FileName = "duplicates.db" };
        if (dialog.ShowDialog(this) == true)
            DbPathTextBox.Text = dialog.FileName;
    }

    private void OnBrowseCsv(object sender, RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.SaveFileDialog { Filter = "CSV files (*.csv)|*.csv|All files (*.*)|*.*", FileName = "duplicates.csv" };
        if (dialog.ShowDialog(this) == true)
            CsvPathTextBox.Text = dialog.FileName;
    }

    private void OnBrowseReport(object sender, RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.SaveFileDialog { Filter = "HTML report (*.html)|*.html|All files (*.*)|*.*", FileName = "prestage-report.html" };
        if (dialog.ShowDialog(this) == true)
            ReportPathTextBox.Text = dialog.FileName;
    }

    private void OnBrowseStagePlan(object sender, RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.OpenFileDialog { Filter = "Stage plan (*.json)|*.json|All files (*.*)|*.*" };
        if (dialog.ShowDialog(this) == true)
            StagePlanPathTextBox.Text = dialog.FileName;
    }

    private void OnBrowseManifest(object sender, RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.OpenFileDialog { Filter = "Quarantine manifest (*.json)|*.json|All files (*.*)|*.*" };
        if (dialog.ShowDialog(this) == true)
            ManifestPathTextBox.Text = dialog.FileName;
    }

    private void OnBrowseQuarantineFolder(object sender, RoutedEventArgs e)
    {
        using var dialog = new Forms.FolderBrowserDialog { Description = "Choose quarantine root folder", UseDescriptionForTitle = true };
        if (dialog.ShowDialog() == Forms.DialogResult.OK)
            QuarantineFolderTextBox.Text = dialog.SelectedPath;
    }

    private void OnBrowseCli(object sender, RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.OpenFileDialog { Filter = "DuplicateFinder executable (*.exe)|*.exe|All files (*.*)|*.*" };
        if (dialog.ShowDialog(this) == true)
            CliPathTextBox.Text = dialog.FileName;
    }

    private void OnOpenReport(object sender, RoutedEventArgs e) => OpenPath(ReportPathTextBox.Text.Trim());

    private void OnOpenOutputFolder(object sender, RoutedEventArgs e)
    {
        var candidates = new[]
        {
            ReportPathTextBox.Text.Trim(),
            CsvPathTextBox.Text.Trim(),
            StagePlanPathTextBox.Text.Trim(),
            DbPathTextBox.Text.Trim()
        };

        var path = candidates.FirstOrDefault(p => !string.IsNullOrWhiteSpace(p));
        if (path is null)
            return;

        var folder = Directory.Exists(path) ? path : Path.GetDirectoryName(path);
        if (!string.IsNullOrWhiteSpace(folder))
            OpenPath(folder);
    }

    private static void OpenPath(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return;

        Process.Start(new ProcessStartInfo
        {
            FileName = path,
            UseShellExecute = true
        });
    }

    private static string FindDefaultCliPath()
    {
        var baseDir = AppContext.BaseDirectory;
        var sameFolder = Path.Combine(baseDir, "DuplicateFinder.exe");
        if (File.Exists(sameFolder))
            return sameFolder;

        var repoRoot = Path.GetFullPath(Path.Combine(baseDir, "..", "..", "..", ".."));
        var releaseCli = Path.Combine(repoRoot, "bin", "Release", "net8.0", "DuplicateFinder.exe");
        if (File.Exists(releaseCli))
            return releaseCli;

        var debugCli = Path.Combine(repoRoot, "bin", "Debug", "net8.0", "DuplicateFinder.exe");
        return debugCli;
    }

    private static bool ExistingFile(string path) => !string.IsNullOrWhiteSpace(path) && File.Exists(path);

    private static bool IsDriveRoot(string path)
    {
        var root = Path.GetPathRoot(path);
        return !string.IsNullOrWhiteSpace(root) &&
               string.Equals(EnsureTrailingSeparator(path), EnsureTrailingSeparator(root), StringComparison.OrdinalIgnoreCase);
    }

    private static string EnsureTrailingSeparator(string path)
    {
        if (path.EndsWith(Path.DirectorySeparatorChar))
            return path;
        return path + Path.DirectorySeparatorChar;
    }

    private static string FormatPreview(CommandInvocation invocation)
    {
        var exe = Path.GetFileName(invocation.FileName);
        return string.Join(" ", new[] { exe }.Concat(invocation.Arguments).Select(QuoteArgument));
    }

    private static string QuoteArgument(string arg)
    {
        if (arg.Length == 0)
            return "\"\"";

        return arg.Any(char.IsWhiteSpace) || arg.Contains('"')
            ? "\"" + arg.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("\"", "\\\"", StringComparison.Ordinal) + "\""
            : arg;
    }

    private static string FormatBytes(long bytes)
    {
        string[] units = ["B", "KB", "MB", "GB", "TB"];
        double value = bytes;
        var unit = 0;
        while (value >= 1024 && unit < units.Length - 1)
        {
            value /= 1024;
            unit++;
        }

        return $"{value:0.#} {units[unit]}";
    }
}
