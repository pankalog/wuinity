# Code Change Architecture Notes

This document summarizes the current uncommitted changes in the repository and explains how they fit together architecturally.

## Scope of Changed Files

### Modified files
- `PREACT/PREACTcore/Source/Engine/Engine.cs`
- `PREACT/PREACTcore/Source/Evacuation/Drones/DroneModule.cs`
- `PREACT/PREACTcore/Source/Input/PREACTInput.cs`
- `PREACT/PREACTcore/Source/Simulation/EvacuationManager.cs`
- `WUInity/Assets/WUInity/Core/WUInityManager.cs`
- `WUInity/Assets/WUInity/GUI/LegacyGUI/MainGUI.cs`
- `WUInity/Assets/WUInity/GUI/LegacyGUI/OutputGUI.cs`
- `WUInity/Assets/WUInity/Visualization/SimulationDomainVisualizerUnity.cs`

### New files
- `Examples/NFDRS4_Behave/Roxborough/Roxborough_global_smoke_drones.wui`
- `PREACT/PREACTcore/Source/Evacuation/Drones/SwarmDroneModule.cs`
- `PREACT/PREACTcore/Source/Evacuation/Drones/Scaffold/DroneSwarmRuntime.cs`
- `PREACT/PREACTcore/Source/Evacuation/Drones/Scaffold/Policies/IDroneRoutingPolicy.cs`
- `PREACT/PREACTcore/Source/Evacuation/Drones/Scaffold/Policies/RasterRoutingPolicy.cs`
- `PREACT/PREACTcore/Source/Evacuation/Drones/Scaffold/Policies/AcsRoutingPolicy.cs`
- `PREACT/PREACTcore/Source/Input/Categories/DroneInput/DroneModuleInput.cs`
- `PREACT/PREACTcore/Source/Input/Categories/DroneInput/DroneFleetInput.cs`
- `PREACT/PREACTcore/Source/Input/Categories/DroneInput/DronePolicyInput.cs`
- `PREACT/PREACTcore/Source/Input/Categories/DroneInput/AcsPolicyInput.cs`
- `PREACT/PREACTcore/Source/Input/Categories/DroneInput/RasterPolicyInput.cs`
- `WUInity/Assets/WUInity/Editor/WUInityDevSettingsWindow.cs`
- `WUInity/Assets/WUInity/Editor/WUInityDevSettingsWindow.cs.meta`

## High-Level Architecture Change

The main feature added is a **drone swarm simulation scaffold** integrated end-to-end through:

1. Input parsing (`.wui` -> strongly typed drone input classes)
2. Evacuation module creation (new `SwarmDroneModule` lifecycle)
3. Runtime simulation logic (raster scanning, battery, diagnostics)
4. Unity visualization (drone markers, labels, responsibility grid, SUMO road overlay)
5. Legacy GUI support (quick WUI picker + drone status panel)
6. Development tooling (editor window for startup/env variables)

## Key Design Additions

### 1) Drone input model and parsing pipeline

New input category classes define configurable behavior for drones:
- Fleet settings: count, speed, battery, recharge, charging station (`DroneFleetInput`)
- Routing settings: policy selection and weights (`DronePolicyInput`)
- ACS parameters (`AcsPolicyInput`)
- Raster settings (`RasterPolicyInput`)
- Top-level module toggle and nested sections (`DroneModuleInput`)

`PREACTInput` now owns `DroneModule` input and parses `[DroneModule]` after traffic and before wildfire.

Architectural impact:
- Drone behavior is now data-driven from scenario files.
- New section headers in `.wui` are first-class citizens in the parser.

### 2) Evacuation module lifecycle extension

`EvacuationManager` now:
- Exposes `DroneModule` as a public property.
- Creates drone module conditionally based on input.
- Enforces dependency: drone module currently requires SUMO traffic module.
- Instantiates `SwarmDroneModule` and bubbles creation failure via `success`.

Architectural impact:
- Drone simulation is treated as a peer evacuation module with explicit lifecycle gating.
- Startup validation now includes cross-module compatibility checks.

### 3) Drone module abstraction contract upgrade

`DroneModule` base class now includes optional query APIs:
- `TryGetStatusText`
- `TryGetDronePositions`
- `TryGetDroneStates`
- `TryGetRasterGrid`
- `TryGetRasterActiveMask`

Architectural impact:
- Consumers (GUI/visualization) can read drone state without downcasting to concrete implementations.
- Module-to-UI communication follows a pull-based capability contract.

### 4) New SwarmDroneModule runtime

`SwarmDroneModule` implements simulation behavior:
- SUMO graph ingestion and simulation-space conversion
- Drone agent creation and per-agent finite state behavior (idle/transit/scan/return/charging)
- Battery drain/charge dynamics
- Raster coverage grid generation over domain
- Optional filtering to road-intersecting cells
- Ownership assignment of raster cells to drones
- Periodic diagnostics with vehicle-scan coverage metrics from active traffic vehicles
- Thread-safe snapshots for UI renderers

`DroneSwarmRuntime.cs` provides runtime data structures:
- `DroneAgentRuntime`, `RasterCellRuntime`, `DroneEdgeRuntime`, `DroneRoadGraphRuntime`
- `DroneAgentState` enum

Policy layer:
- `IDroneRoutingPolicy` interface added for pluggable strategy.
- `RasterRoutingPolicy` implemented as the active strategy.
- `AcsRoutingPolicy` scaffolded but currently not selected in runtime (module logs fallback to raster).

Architectural impact:
- The system now has a policy abstraction seam for future routing algorithms.
- Current behavior is intentionally scaffold-first, with ACS introduced as a future extension point.

## Unity / Presentation Layer Changes

### 5) Domain visualization enhancements

`SimulationDomainVisualizerUnity` now supports:
- Dynamic drone marker spawning and updates
- Drone text labels with state
- Drone responsibility grid rendering using line renderers
- SUMO road-network polyline overlay rendering
- Cleanup methods for drone markers/grid and SUMO roads

Architectural impact:
- Visualization now has dedicated transient scene objects for drone and transport overlays.
- Rendering API is expanded so manager-level logic can push simulation snapshots each frame.

### 6) WUInity manager orchestration changes

`WUInityManager` adds:
- Startup `.wui` resolution via `WUINITY_STARTUP_WUI` env var, with fallback default
- Drone marker texture loading from resources (`maki/airfield-15`)
- Runtime per-frame drone rendering update path
- SUMO road network overlay generation and cleanup
- State cleanup hooks when visuals are disabled/reset

It also improves logging for PROJ environment setup and selected PROJ data directory.

Architectural impact:
- Manager now orchestrates both simulation status and optional geospatial/debug overlays.
- Startup behavior becomes environment-configurable for dev workflows.

### 7) Legacy GUI improvements

`MainGUI`:
- Adds quick list-based `.wui` selector from `Examples` tree (prev/next/load).

`OutputGUI`:
- Adds a drone statistics panel when drone module status is available.

Architectural impact:
- Non-programmatic users can load drone scenarios faster.
- Diagnostics from simulation core are visible directly in UI.

### 8) Unity editor dev tooling

`WUInityDevSettingsWindow` adds a new editor window under `WUInity/Dev/Configuration` that stores and applies:
- `WUINITY_STARTUP_WUI`
- `PROJ_LIB`
- `PROJ_DATA`
- `SUMO_HOME`

Bootstrapping runs on editor load to reapply saved values to the Unity process.

Architectural impact:
- Reduces machine-specific startup friction.
- Formalizes environment setup as editor-configurable state.

## Scenario / Example Configuration

`Roxborough_global_smoke_drones.wui` introduces a drone-enabled scenario with:
- `[DroneModule]` enabled
- Fleet/policy/ACS/raster sections populated
- SUMO traffic + wildfire + smoke combined

Architectural role:
- Serves as integration test scenario for parsing, module startup, runtime behavior, and visualization.

## End-to-End Runtime Flow

1. `.wui` is loaded and drone sections are parsed into `PREACTInput.DroneModule`.
2. `EvacuationManager` validates prerequisites and creates `SwarmDroneModule`.
3. On each simulation step, drones update battery/state/targets and raster freshness.
4. Module emits status and snapshots through `TryGet...` APIs.
5. `WUInityManager` pulls snapshots and drives `SimulationDomainVisualizerUnity` updates.
6. `OutputGUI` displays textual drone diagnostics.

## Notable Constraints and Current Limitations

- Drone module currently requires SUMO traffic module enabled.
- Runtime currently falls back to raster routing even if ACS is selected.
- Drone grid ownership is column-based and intended as scaffold behavior.

## Practical Entry Points for Further Work

- Core drone logic: `PREACT/PREACTcore/Source/Evacuation/Drones/SwarmDroneModule.cs`
- Policy extension seam: `PREACT/PREACTcore/Source/Evacuation/Drones/Scaffold/Policies/IDroneRoutingPolicy.cs`
- Input schema: `PREACT/PREACTcore/Source/Input/Categories/DroneInput/DroneModuleInput.cs`
- Module orchestration: `PREACT/PREACTcore/Source/Simulation/EvacuationManager.cs`
- Unity runtime orchestration: `WUInity/Assets/WUInity/Core/WUInityManager.cs`
- Visualization implementation: `WUInity/Assets/WUInity/Visualization/SimulationDomainVisualizerUnity.cs`
