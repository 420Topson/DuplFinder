# DuplFinder v1.1.0 Draft Release Notes

## Highlights

- First Windows GUI MVP wrapper for the existing CLI.
- Read-only console output pane for stdout/stderr and status messages.
- Drive/disk multi-select and `Choose folder...` scan target selection.
- Selected scan targets list with duplicate normalization.
- Real-time command preview before execution.
- Minimum file size slider for duplicate reporting.
- File type checkbox groups backed by real CLI/core filtering.
- Files with no extension are skipped by default.
- Optional checkbox adds the real `--include-no-extension` CLI option.
- Windows/system folders are skipped by default.
- Workflow gating keeps commands disabled until required artifacts exist.
- Dry-run-first quarantine workflow is retained.

## Safety Model

- Exact duplicate identity is unchanged: `same file size + same full SHA-256`.
- Extension filtering only controls which files are scanned.
- The GUI does not implement fuzzy, perceptual, or DNA matching.
- The GUI does not directly delete original duplicate files.
- Destructive quarantine cleanup remains restricted to validated quarantine-session files through the CLI.

