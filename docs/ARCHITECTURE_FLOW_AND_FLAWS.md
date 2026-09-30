# MetroHub: Architecture Flow & Flaws Map

> **Purpose:** Visual reference for developers documenting how MetroHub connects end-to-end, how data and events flow, and where remaining architectural flaws/anti-patterns exist.

---

## 1. System Wiring & Flaw Diagram

```mermaid
flowchart TD
    %% --- USER & INPUT ---
    USER(["👤 User Input (Win+Space, Click, Drag)"])

    %% --- SHELL & UI LAYER ---
    subgraph UI ["🖥️ Shell & UI Layer"]
        MAIN["MainWindow.xaml.cs<br/>(Main UI Orchestrator)"]:::amber
        DRAG["MainWindow.DragDrop.cs<br/>(Pointer & Drag Handling)"]:::green
        TILE["TileControl.xaml.cs<br/>(Tile Card Rendering)"]:::green
        TILE_MGR["TileManager.cs<br/>(Collection Manager)"]:::green
    end

    %% --- STATE & METRONOME ---
    subgraph BRAIN ["🧠 State & Metronome (Heartbeat)"]
        HUBSTATE["HubState.cs<br/>(Canonical Visibility Authority)"]:::green
        BEAT["WidgetHeartbeatService<br/>(1-Second Timer Engine)"]:::green
        GUARD{"Host Guard<br/>(Safe.Try Firewall)"}:::green
        LOGGER["HiddenDiagnosticsLogger<br/>(Memory & Diagnostic Logging)"]:::green
    end

    %% --- WIDGET LAYER ---
    subgraph WIDGETS ["🧩 Widget System"]
        BASE["WidgetViewModelBase<br/>(Lifecycle & Base Hooks)"]:::green
        CATALOG["19 Modular Widgets<br/>(Clock, Weather, Audio, etc.)"]:::green
        NET_VM["NetworkWidgetViewModel<br/>⚠️ ANTI-PATTERN: Monolithic God-Class<br/>(Handles NICs, sockets, pings, charts all-in-one)"]:::red
        MSG["WeakReferenceMessenger<br/>⚠️ FLAW: Untyped Broadcasts<br/>(No guaranteed execution order)"]:::red
    end

    %% --- ENGINE & PERSISTENCE ---
    subgraph CORE ["💾 Engine & Persistence Layer"]
        PHYSICS["GridPlacementService<br/>(Snapping & Grid Math)"]:::green
        UNDO["LayoutHistoryService<br/>(Undo/Redo Stacks)"]:::green
        STORE["StorageService & WidgetStateStore<br/>(Atomic .tmp ➔ File.Replace ➔ .bak)"]:::green
        WINDOWS["Windows OS APIs<br/>(Audio, WiFi, Displays)"]:::green
    end

    %% --- DATA FLOWS ---
    USER ==>|"1. Open / Close"| MAIN
    USER ==>|"2. Drag Tile"| DRAG

    MAIN -->|"Updates State"| HUBSTATE
    MAIN -.->|"Logs Transitions"| LOGGER

    HUBSTATE -->|"Start / Stop"| BEAT
    HUBSTATE -->|"Broadcast Visibility"| MSG
    MSG -.->|"Pause / Resume"| BASE

    BEAT -->|"Paces 1s Tick"| GUARD
    GUARD -->|"Safe Tick"| CATALOG
    GUARD -->|"Safe Tick"| NET_VM
    GUARD -.->|"On Crash: Render Error Card"| TILE

    BASE --> CATALOG
    BASE --> NET_VM

    CATALOG -->|"Read Hardware"| WINDOWS
    NET_VM -->|"Direct Network Queries"| WINDOWS

    DRAG -->|"Calculate Slots"| PHYSICS
    TILE_MGR -->|"Arrange"| PHYSICS
    PHYSICS -->|"Record State"| UNDO
    PHYSICS -->|"Save Layout"| STORE
    CATALOG -->|"Save Widget Data"| STORE

    %% --- STYLING ---
    classDef green fill:#dcfce7,stroke:#16a34a,stroke-width:2px,color:#14532d;
    classDef amber fill:#fef3c7,stroke:#d97706,stroke-width:2px,color:#78350f;
    classDef red fill:#fee2e2,stroke:#dc2626,stroke-width:3px,stroke-dasharray: 5 5,color:#7f1d1d;
```

---

## 2. Color Legend & Architectural Health

| Color | Status | Meaning |
|---|---|---|
| 🟩 **Green** | **Healthy / Decoupled** | Follows single-responsibility, is guarded against failure, and has deterministic flow. |
| 🟨 **Amber** | **Cohesive but Dense** | Works reliably, but has high coordination responsibility. Treat with care. |
| 🟥 **Red** | **Anti-Pattern / Flaw** | Technical debt or structural anti-pattern that should be monitored or refactored in the future. |

---

## 3. Deep Dive into the 3 Red Flaws & Anti-Patterns

### 🟥 Flaw 1: `NetworkWidgetViewModel` (Monolithic God-Class)
- **Location:** `src/MetroHub/Widgets/Catalog/Network/NetworkWidgetViewModel.cs`
- **The Problem:** 
  The network widget is over 1,000 lines long and handles 5 disparate domains simultaneously:
  1. Windows Network Interface (NIC) discovery and IP status polling.
  2. Byte-rate differential throughput arithmetic.
  3. Continuous internet latency ICMP ping socket requests.
  4. Real-time ring-buffer chart history rendering.
  5. UI property transformation and formatting.
- **Why It Remains:** 
  It is proven and working in production. Per owner directive, it was intentionally left untouched during Phase 3/4 to protect user stability.
- **Future Solution:** 
  Split into a pure domain worker (`NetworkMonitorService`), a chart model helper, and a lightweight ViewModel containing only UI bindings.

---

### 🟥 Flaw 2: `WeakReferenceMessenger` (Implicit Broadcasts)
- **Location:** `CommunityToolkit.Mvvm.Messaging.WeakReferenceMessenger`
- **The Problem:** 
  When `HubState` changes visibility, the messenger broadcasts `HubVisibilityChangedMessage` to all subscribers simultaneously. 
  Because it is an untyped publish/subscribe bus:
  - There is **no guaranteed execution order** between widgets.
  - If Widget A depends on Widget B waking up first, subtle timing or race bugs can happen.
  - Code search cannot easily show who responds to what message without manual grepping.
- **Current Mitigation:** 
  `WidgetHeartbeatService` and `HubState` keep timers decoupled from the message payload.
- **Future Solution:** 
  Keep `WeakReferenceMessenger` strictly for cross-boundary UI events, and use direct lifecycle callbacks for core services.

---

### 🟩 Flaw 3 (Resolved): `TEMP-SHIM(T-22)` Legacy Forwarder Removed
- **Status:** **REMOVED**
- `HiddenDiagnosticsLogger.IsHubHidden` was verified to have 0 callers across the solution and has been deleted. `HiddenDiagnosticsLogger` is now 100% state-free.

---

## 4. The Amber Component: `MainWindow.xaml.cs`
- **Location:** `src/MetroHub/MainWindow*.cs`
- **Description:** 
  `MainWindow` is partitioned into partial classes (`.DragDrop.cs`, `.Tiles.cs`), which makes file sizes manageable. However, it still acts as an **orchestrator** that directly manages:
  - Win32 keyboard/mouse hooks and OS window messages (`WndProc`).
  - Tile drag-and-drop gesture states.
  - Backdrop window composition (Mica, Acrylic, Desktop Wallpaper).
- **Guidance:** 
  Do not add business logic to `MainWindow`. Route state changes through `HubState` and math through `GridPlacementService`.

---

## 5. The Green Core: Why the Rest is Solid

1. **`HubState.cs`:**
   Canonical, thread-safe authority on visibility (`IsVisible`, `IsHidden`, `VisibilityChanged`).
2. **`WidgetHeartbeatService` + Host Guard:**
   Paces a 1-second pulse to visible widgets. If any widget throws an exception during its tick, the guard catches it, logs it, renders a red error card on that tile, and keeps the other 19 widgets alive.
3. **`StorageService` & `WidgetStateStore`:**
   Atomic save pattern (`.tmp` $\rightarrow$ `File.Replace` $\rightarrow$ `.bak`) wrapped in `Safe.Try`. Guaranteed zero silent data loss.
4. **`GridPlacementService`:**
   Pure, deterministic 2D grid placement math completely decoupled from WPF controls.
