using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using DuplicateFinder.Models;
using DuplicateFinder.Utils;

namespace DuplicateFinder.Services;

public sealed class PrestageReportService
{
    private const string StagePlanSchema = "duplfinder.stage-plan.v1";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = false
    };

    public async Task<int> GenerateAsync(PrestageReportOptions options, CancellationToken ct)
    {
        var outputPath = Path.GetFullPath(options.OutputPath);
        var outputDirectory = Path.GetDirectoryName(outputPath);
        if (!string.IsNullOrWhiteSpace(outputDirectory))
            Directory.CreateDirectory(outputDirectory);

        if (File.Exists(outputPath) && !options.Force)
            throw new IOException($"Output file already exists: {outputPath}. Pass --force to overwrite it.");

        var groups = await LoadGroupsAsync(options.DbPath, ct);
        var payload = new ReportPayload(
            schema: StagePlanSchema,
            generated_utc: DateTimeOffset.UtcNow.ToString("O"),
            source_db: Path.GetFullPath(options.DbPath),
            source_report: outputPath,
            groups: groups);

        var json = JsonSerializer.Serialize(payload, JsonOptions)
            .Replace("</", "<\\/", StringComparison.OrdinalIgnoreCase);
        var html = BuildHtml(json);

        var mode = options.Force ? FileMode.Create : FileMode.CreateNew;
        await using var stream = new FileStream(outputPath, mode, FileAccess.Write, FileShare.Read, 64 * 1024, useAsync: true);
        await using var writer = new StreamWriter(stream, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        await writer.WriteAsync(html.AsMemory(), ct);

        return groups.Count;
    }

    private async Task<List<ReportGroup>> LoadGroupsAsync(string dbPath, CancellationToken ct)
    {
        await using var connection = OpenReadOnlyConnection(dbPath);
        await connection.OpenAsync(ct);

        await using var groupCmd = connection.CreateCommand();
        groupCmd.CommandText = @"
SELECT size, hash, COUNT(*) AS copies
FROM files
WHERE hash IS NOT NULL AND status = 'ok' AND is_skipped = 0
GROUP BY size, hash
HAVING COUNT(*) > 1
ORDER BY size DESC, copies DESC, hash;";

        var groupRows = new List<(long Size, string Hash)>();
        await using (var groupReader = await groupCmd.ExecuteReaderAsync(ct))
        {
            while (await groupReader.ReadAsync(ct))
                groupRows.Add((groupReader.GetInt64(0), groupReader.GetString(1)));
        }

        var groups = new List<ReportGroup>(groupRows.Count);
        var groupNumber = 1;
        foreach (var row in groupRows)
        {
            var files = await LoadFilesForGroupAsync(connection, row.Size, row.Hash, ct);
            if (files.Count < 2)
                continue;

            groups.Add(new ReportGroup(
                group_number: groupNumber++,
                size: row.Size,
                size_display: SizeParser.FormatBytes(row.Size),
                hash: row.Hash,
                hash_preview: row.Hash.Length <= 12 ? row.Hash : row.Hash[..12],
                files: files));
        }

        return groups;
    }

    private static SqliteConnection OpenReadOnlyConnection(string dbPath)
    {
        var builder = new SqliteConnectionStringBuilder
        {
            DataSource = dbPath,
            Mode = SqliteOpenMode.ReadOnly,
            Cache = SqliteCacheMode.Shared
        };

        return new SqliteConnection(builder.ToString());
    }

    private static async Task<List<ReportFile>> LoadFilesForGroupAsync(
        SqliteConnection connection,
        long size,
        string hash,
        CancellationToken ct)
    {
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = @"
SELECT path, file_name, size, last_write_time_utc
FROM files
WHERE size = $size AND hash = $hash AND status = 'ok' AND is_skipped = 0
ORDER BY path;";
        cmd.Parameters.AddWithValue("$size", size);
        cmd.Parameters.AddWithValue("$hash", hash);

        var files = new List<ReportFile>();
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            var path = reader.GetString(0);
            var fileName = reader.IsDBNull(1) ? Path.GetFileName(path) : reader.GetString(1);
            var fileSize = reader.GetInt64(2);
            var lastWrite = reader.IsDBNull(3) ? "" : reader.GetString(3);

            files.Add(new ReportFile(
                path: path,
                file_name: fileName,
                size: fileSize,
                size_display: SizeParser.FormatBytes(fileSize),
                last_write_time_utc: lastWrite));
        }

        return files;
    }

    private static string BuildHtml(string reportDataJson)
    {
        const string template = """
<!doctype html>
<html lang="en">
<head>
  <meta charset="utf-8">
  <meta name="viewport" content="width=device-width, initial-scale=1">
  <title>DuplFinder Prestage Report</title>
  <style>
    :root {
      color-scheme: dark;
      --bg: #0f1115;
      --card: #171a21;
      --row: #1d212b;
      --row-alt: #222733;
      --border: #303644;
      --text: #d7dde8;
      --muted: #9aa4b2;
      --accent: #7aa2f7;
      --warning-bg: #2a2415;
      --warning-border: #5c4a1e;
    }

    * {
      box-sizing: border-box;
    }

    body {
      margin: 0;
      background: var(--bg);
      color: var(--text);
      font: 15px/1.45 "Segoe UI", system-ui, -apple-system, BlinkMacSystemFont, sans-serif;
    }

    main {
      max-width: 1400px;
      margin: 0 auto;
      padding: 28px 20px 44px;
    }

    h1 {
      margin: 0 0 10px;
      font-size: 28px;
      font-weight: 700;
      letter-spacing: 0;
    }

    button,
    input {
      font: inherit;
    }

    button {
      border: 1px solid var(--border);
      border-radius: 6px;
      background: #202634;
      color: var(--text);
      padding: 8px 12px;
      cursor: pointer;
    }

    button:hover,
    button:focus {
      border-color: var(--accent);
      outline: none;
    }

    input[type="radio"],
    input[type="checkbox"] {
      width: 18px;
      height: 18px;
      accent-color: var(--accent);
    }

    input:disabled {
      cursor: not-allowed;
      opacity: 0.55;
    }

    .muted {
      color: var(--muted);
    }

    .warning {
      margin: 18px 0;
      padding: 14px 16px;
      border: 1px solid var(--warning-border);
      border-radius: 8px;
      background: var(--warning-bg);
      color: var(--text);
    }

    .summary {
      display: grid;
      grid-template-columns: repeat(5, minmax(150px, 1fr));
      gap: 12px;
      margin: 18px 0;
    }

    .summary-item {
      border: 1px solid var(--border);
      border-radius: 8px;
      background: var(--card);
      padding: 12px;
    }

    .summary-label {
      color: var(--muted);
      font-size: 12px;
      text-transform: uppercase;
    }

    .summary-value {
      margin-top: 5px;
      font-size: 21px;
      font-weight: 700;
    }

    .controls {
      display: flex;
      flex-wrap: wrap;
      gap: 10px;
      margin: 18px 0 24px;
    }

    .primary {
      border-color: #4267b2;
      background: #233a61;
      color: #eef4ff;
    }

    .group-card {
      margin: 16px 0;
      border: 1px solid var(--border);
      border-radius: 8px;
      background: var(--card);
      overflow: hidden;
    }

    .group-header {
      display: grid;
      grid-template-columns: 1fr auto;
      gap: 14px;
      align-items: start;
      padding: 14px 16px;
      border-bottom: 1px solid var(--border);
    }

    .group-title {
      margin: 0 0 8px;
      font-size: 18px;
      font-weight: 700;
      overflow-wrap: anywhere;
    }

    .group-meta {
      display: flex;
      flex-wrap: wrap;
      gap: 8px 14px;
      color: var(--muted);
      font-size: 13px;
    }

    .hash-line {
      margin-top: 8px;
      color: var(--muted);
      font-family: Consolas, "Cascadia Mono", monospace;
      font-size: 13px;
      overflow-wrap: anywhere;
    }

    .table-wrap {
      overflow-x: auto;
    }

    table {
      width: 100%;
      border-collapse: collapse;
      min-width: 900px;
    }

    th,
    td {
      padding: 10px 12px;
      border-bottom: 1px solid var(--border);
      text-align: left;
      vertical-align: top;
    }

    th {
      color: var(--muted);
      font-size: 12px;
      font-weight: 600;
      text-transform: uppercase;
      background: #151922;
    }

    tbody tr {
      background: var(--row);
    }

    tbody tr:nth-child(even) {
      background: var(--row-alt);
    }

    tbody tr.keep-row {
      outline: 1px solid rgba(122, 162, 247, 0.45);
      outline-offset: -1px;
    }

    .choice-cell {
      width: 70px;
      text-align: center;
    }

    .size-cell {
      white-space: nowrap;
    }

    .path-cell {
      min-width: 380px;
      max-width: 760px;
      font-family: Consolas, "Cascadia Mono", monospace;
      font-size: 13px;
      overflow-wrap: anywhere;
    }

    .file-cell {
      min-width: 190px;
      overflow-wrap: anywhere;
    }

    .empty {
      border: 1px solid var(--border);
      border-radius: 8px;
      background: var(--card);
      padding: 18px;
      color: var(--muted);
    }

    @media (max-width: 780px) {
      main {
        padding: 20px 12px 34px;
      }

      .summary {
        grid-template-columns: repeat(2, minmax(140px, 1fr));
      }

      .group-header {
        grid-template-columns: 1fr;
      }
    }
  </style>
</head>
<body>
  <main>
    <h1>DuplFinder Prestage Report</h1>
    <p class="muted">Review exact duplicate groups and export a future staging plan.</p>

    <section class="warning">
      This report does not move or delete files. It only exports a stage plan. No data is sent anywhere.
    </section>

    <section class="summary" aria-label="Summary">
      <div class="summary-item">
        <div class="summary-label">Duplicate groups</div>
        <div class="summary-value" id="summary-groups">0</div>
      </div>
      <div class="summary-item">
        <div class="summary-label">Files in duplicate groups</div>
        <div class="summary-value" id="summary-files">0</div>
      </div>
      <div class="summary-item">
        <div class="summary-label">Redundant files</div>
        <div class="summary-value" id="summary-redundant">0</div>
      </div>
      <div class="summary-item">
        <div class="summary-label">Selected for staging</div>
        <div class="summary-value" id="summary-selected">0</div>
      </div>
      <div class="summary-item">
        <div class="summary-label">Selected bytes</div>
        <div class="summary-value" id="summary-bytes">0 B</div>
      </div>
    </section>

    <section class="controls" aria-label="Report controls">
      <button class="primary" type="button" id="export-button">Export stage-plan.json</button>
      <button type="button" id="expand-all">Expand all</button>
      <button type="button" id="collapse-all">Collapse all</button>
      <button type="button" id="select-all">Select all non-keep files</button>
      <button type="button" id="clear-all">Clear all staging selections</button>
    </section>

    <section id="groups" aria-label="Duplicate groups"></section>
  </main>

  <script id="report-data" type="application/json">__REPORT_DATA_JSON__</script>
  <script>
    const reportData = JSON.parse(document.getElementById('report-data').textContent);
    const state = new Map();

    function formatBytes(bytes) {
      const units = ['B', 'KB', 'MB', 'GB', 'TB'];
      let size = Number(bytes);
      let unit = 0;
      while (size >= 1024 && unit < units.length - 1) {
        size /= 1024;
        unit++;
      }
      return `${size.toFixed(unit === 0 ? 0 : 2)} ${units[unit]}`;
    }

    function initializeState() {
      for (const group of reportData.groups) {
        const staged = new Set();
        const groupState = { keepIndex: 0, staged, expanded: true };
        stageAllExceptKeep(group, groupState, 0);
        state.set(group.group_number, groupState);
      }
    }

    function stageAllExceptKeep(group, groupState, keepIndex) {
      groupState.staged.clear();
      group.files.forEach((_, candidateIndex) => {
        if (candidateIndex !== keepIndex) groupState.staged.add(candidateIndex);
      });
    }

    function updateSummary() {
      let selectedCount = 0;
      let selectedBytes = 0;
      let fileCount = 0;
      let redundantCount = 0;

      for (const group of reportData.groups) {
        fileCount += group.files.length;
        redundantCount += Math.max(0, group.files.length - 1);
        const groupState = state.get(group.group_number);
        for (const index of groupState.staged) {
          const file = group.files[index];
          if (!file || index === groupState.keepIndex) continue;
          selectedCount++;
          selectedBytes += Number(file.size);
        }
      }

      document.getElementById('summary-groups').textContent = reportData.groups.length.toString();
      document.getElementById('summary-files').textContent = fileCount.toString();
      document.getElementById('summary-redundant').textContent = redundantCount.toString();
      document.getElementById('summary-selected').textContent = selectedCount.toString();
      document.getElementById('summary-bytes').textContent = formatBytes(selectedBytes);
    }

    function appendText(parent, text, className) {
      const element = document.createElement('span');
      if (className) element.className = className;
      element.textContent = text;
      parent.appendChild(element);
      return element;
    }

    function renderGroups() {
      const root = document.getElementById('groups');
      root.textContent = '';

      if (reportData.groups.length === 0) {
        const empty = document.createElement('div');
        empty.className = 'empty';
        empty.textContent = 'No exact duplicate groups were found in this database.';
        root.appendChild(empty);
        updateSummary();
        return;
      }

      for (const group of reportData.groups) {
        root.appendChild(renderGroup(group));
      }

      updateSummary();
    }

    function renderGroup(group) {
      const groupState = state.get(group.group_number);
      const suggested = group.files[0] || { file_name: '(unknown)' };
      const card = document.createElement('article');
      card.className = 'group-card';

      const header = document.createElement('header');
      header.className = 'group-header';

      const headerText = document.createElement('div');
      const title = document.createElement('h2');
      title.className = 'group-title';
      title.textContent = suggested.file_name || '(unknown)';
      headerText.appendChild(title);

      const meta = document.createElement('div');
      meta.className = 'group-meta';
      appendText(meta, `Group ${group.group_number}`);
      appendText(meta, `${group.files.length} files`);
      appendText(meta, group.size_display);
      appendText(meta, `${group.size} bytes`);
      appendText(meta, `SHA-256 ${group.hash_preview}`);
      headerText.appendChild(meta);

      const hash = document.createElement('div');
      hash.className = 'hash-line';
      hash.textContent = `Full SHA-256: ${group.hash}`;
      headerText.appendChild(hash);

      const toggle = document.createElement('button');
      toggle.type = 'button';
      toggle.textContent = groupState.expanded ? 'Collapse' : 'Expand';
      toggle.setAttribute('aria-expanded', groupState.expanded ? 'true' : 'false');
      toggle.addEventListener('click', () => {
        groupState.expanded = !groupState.expanded;
        renderGroups();
      });

      header.appendChild(headerText);
      header.appendChild(toggle);
      card.appendChild(header);

      if (groupState.expanded) {
        const wrap = document.createElement('div');
        wrap.className = 'table-wrap';
        const table = document.createElement('table');
        const thead = document.createElement('thead');
        const headerRow = document.createElement('tr');
        ['Keep', 'Stage', 'File name', 'Full path', 'Size', 'Last write time'].forEach((heading) => {
          const th = document.createElement('th');
          th.textContent = heading;
          headerRow.appendChild(th);
        });
        thead.appendChild(headerRow);
        table.appendChild(thead);

        const tbody = document.createElement('tbody');
        group.files.forEach((file, index) => {
          tbody.appendChild(renderFileRow(group, file, index));
        });
        table.appendChild(tbody);
        wrap.appendChild(table);
        card.appendChild(wrap);
      }

      return card;
    }

    function renderFileRow(group, file, index) {
      const groupState = state.get(group.group_number);
      const row = document.createElement('tr');
      if (index === groupState.keepIndex) row.className = 'keep-row';

      const keepCell = document.createElement('td');
      keepCell.className = 'choice-cell';
      const keep = document.createElement('input');
      keep.type = 'radio';
      keep.name = `keep-${group.group_number}`;
      keep.checked = index === groupState.keepIndex;
      keep.addEventListener('change', () => {
        groupState.keepIndex = index;
        stageAllExceptKeep(group, groupState, index);
        renderGroups();
      });
      keepCell.appendChild(keep);
      row.appendChild(keepCell);

      const stageCell = document.createElement('td');
      stageCell.className = 'choice-cell';
      const stage = document.createElement('input');
      stage.type = 'checkbox';
      stage.disabled = index === groupState.keepIndex;
      stage.checked = index !== groupState.keepIndex && groupState.staged.has(index);
      stage.addEventListener('change', () => {
        if (stage.checked) {
          groupState.staged.add(index);
        } else {
          groupState.staged.delete(index);
        }
        updateSummary();
      });
      stageCell.appendChild(stage);
      row.appendChild(stageCell);

      const nameCell = document.createElement('td');
      nameCell.className = 'file-cell';
      nameCell.textContent = file.file_name;
      row.appendChild(nameCell);

      const pathCell = document.createElement('td');
      pathCell.className = 'path-cell';
      pathCell.textContent = file.path;
      row.appendChild(pathCell);

      const sizeCell = document.createElement('td');
      sizeCell.className = 'size-cell';
      sizeCell.textContent = file.size_display;
      row.appendChild(sizeCell);

      const timeCell = document.createElement('td');
      timeCell.textContent = file.last_write_time_utc || '';
      row.appendChild(timeCell);

      return row;
    }

    function selectAllNonKeep() {
      for (const group of reportData.groups) {
        const groupState = state.get(group.group_number);
        stageAllExceptKeep(group, groupState, groupState.keepIndex);
      }
      renderGroups();
    }

    function clearAllStaging() {
      for (const groupState of state.values()) {
        groupState.staged.clear();
      }
      renderGroups();
    }

    function setExpanded(expanded) {
      for (const groupState of state.values()) {
        groupState.expanded = expanded;
      }
      renderGroups();
    }

    function buildStagePlan() {
      const groups = [];
      for (const group of reportData.groups) {
        const groupState = state.get(group.group_number);
        const keepFile = group.files[groupState.keepIndex] || group.files[0];
        const keepPath = keepFile ? keepFile.path : '';
        const stagePaths = [];

        for (const index of groupState.staged) {
          const file = group.files[index];
          if (!file || file.path === keepPath) continue;
          stagePaths.push(file.path);
        }

        if (stagePaths.length > 0) {
          groups.push({
            group_number: group.group_number,
            size: group.size,
            hash: group.hash,
            keep_path: keepPath,
            stage_paths: stagePaths
          });
        }
      }

      return {
        schema: 'duplfinder.stage-plan.v1',
        created_utc: new Date().toISOString(),
        source_db: reportData.source_db,
        source_report: reportData.source_report,
        generator: 'DuplFinder',
        groups
      };
    }

    function exportStagePlan() {
      const plan = buildStagePlan();
      const json = JSON.stringify(plan, null, 2);
      const blob = new Blob([json], { type: 'application/json;charset=utf-8' });
      const url = URL.createObjectURL(blob);
      const link = document.createElement('a');
      link.href = url;
      link.download = 'stage-plan.json';
      document.body.appendChild(link);
      link.click();
      link.remove();
      URL.revokeObjectURL(url);
    }

    initializeState();
    renderGroups();

    document.getElementById('export-button').addEventListener('click', exportStagePlan);
    document.getElementById('expand-all').addEventListener('click', () => setExpanded(true));
    document.getElementById('collapse-all').addEventListener('click', () => setExpanded(false));
    document.getElementById('select-all').addEventListener('click', selectAllNonKeep);
    document.getElementById('clear-all').addEventListener('click', clearAllStaging);
  </script>
</body>
</html>
""";

        return template.Replace("__REPORT_DATA_JSON__", reportDataJson, StringComparison.Ordinal);
    }

    private sealed record ReportPayload(
        string schema,
        string generated_utc,
        string source_db,
        string source_report,
        List<ReportGroup> groups);

    private sealed record ReportGroup(
        int group_number,
        long size,
        string size_display,
        string hash,
        string hash_preview,
        List<ReportFile> files);

    private sealed record ReportFile(
        string path,
        string file_name,
        long size,
        string size_display,
        string last_write_time_utc);
}
