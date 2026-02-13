# Migration To Submodule Consumption

This guide migrates a Godot project from copied-in `AgenticCore/` files to a pinned git submodule.

## 1. Back Up Existing State

- Commit or stash current changes in your host project.

## 2. Remove Copied Folder

- Remove the tracked in-project `AgenticCore/` directory.

## 3. Add Submodule

```bash
git submodule add <agenticcore-repo-url> AgenticCore
cd AgenticCore
git checkout v0.1.0
```

## 4. Validate Path Contracts

Confirm host references still resolve:

- `res://AgenticCore/Interaction/TargetResolution.cs`
- `res://AgenticCore/Telemetry/TelemetryClient.cs` (if used)
- `res://AgenticCore/Telemetry/Economics.cs` (if used)
- scene/script references to runtime files under `res://AgenticCore/*`

## 5. Move Debug Bundle Ownership To Host

The old `AgenticCore/Debug/` bundle is not shipped in this runtime-first package.

If your project referenced those debug scenes:

- move/replace them under host-owned paths (for example `res://UI/AgenticDebug/`)
- update `.tscn` references accordingly

## 6. Rebuild + Smoke Test

- Rebuild C# solution.
- Open scenes that instantiate agentic entities.
- Verify target resolution/autoload startup and a full think/tool cycle.

## 7. Pin And Track

- Keep submodule pinned to tags (`v0.1.x`).
- Upgrade intentionally and test before committing tag bumps.
