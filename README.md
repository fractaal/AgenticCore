# AgenticCore

Portable Godot-first runtime for agentic LLM entities, tool routing, telemetry, and shared interaction primitives.

## Scope (v0.1.x)

This repository intentionally ships the runtime/core systems only:

- `Core/`
- `Interaction/`
- `LLM/`
- `Common/`
- `Schema/`
- `Exceptions/`
- `Telemetry/`
- `Vision/`
- `tools/telemetry-server/` (viewer tooling)

Not included in this repo:

- The old `Debug/` scene bundle that depended on host-specific UI and font assets.

## Install in a Godot C# Project

Mount at `res://AgenticCore` so script paths stay stable.

```bash
git submodule add <agenticcore-repo-url> AgenticCore
cd AgenticCore
git checkout v0.1.0
```

Then in the host project:

- Ensure your autoloads reference:
  - `res://AgenticCore/Interaction/TargetResolution.cs`
  - `res://AgenticCore/Telemetry/TelemetryClient.cs` (optional)
  - `res://AgenticCore/Telemetry/Economics.cs` (optional)
- Rebuild C# solutions after introducing the submodule.

## Update Workflow (Pinned Tags)

Recommended for game stability: pin exact tags.

```bash
cd AgenticCore
git fetch --tags
git checkout v0.1.1
cd ..
git add AgenticCore
git commit -m "chore: bump AgenticCore to v0.1.1"
```

## Telemetry Viewer

Viewer/server tooling is included at:

- `tools/telemetry-server/`

See `tools/telemetry-server/README.md` for usage.

## Versioning Policy

- `v0.1.x`: no required path contract changes.
- Minor/major bumps (`v0.2.0+`): may include migration-required contract/path changes.

## Notes

- This is currently Godot-coupled runtime code (not NuGet-targeted engine-agnostic core yet).
- Host projects own their own debug UI composition.
