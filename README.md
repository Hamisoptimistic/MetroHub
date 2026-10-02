# MetroHub

MetroHub is a Windows desktop launcher: a full-screen, Windows 8/10-style live-tile canvas that appears on a hotkey and disappears when you leave it.

<!-- SCREENSHOT_PLACEHOLDER: Add application screenshot or GIF demonstration here (owner supplies) -->

## Tech Stack
* **Language & Framework:** C#, .NET 10 (`net10.0-windows10.0.19041.0`), WPF
* **UI Components:** [WPF-UI](https://github.com/lepoco/wpfui)
* **Architecture:** MVVM using [CommunityToolkit.Mvvm](https://learn.microsoft.com/dotnet/communitytoolkit/mvvm/)

## Widgets
MetroHub includes 18 catalog widgets located in `src/MetroHub/Widgets/Catalog/`:
* `AudioControls`
* `BrightnessControls`
* `CaffeineSleep`
* `Calendar`
* `Clock`
* `Dino`
* `Habit`
* `Media`
* `Network`
* `Photos`
* `Pomodoro`
* `Power`
* `QuickControls`
* `Quotes`
* `Radio`
* `Rover`
* `Stub`
* `Weather`

## Building and Testing

### Prerequisites
* Windows 10 (build 19041+) or Windows 11
* [.NET 10 SDK](https://dotnet.microsoft.com/download) (verified on SDK 10.0.401)

### Build Commands

1. **Restore dependencies:**
   ```powershell
   dotnet restore
   ```

2. **Build the solution:**
   ```powershell
   dotnet build
   ```

3. **Run the test suite:**
   ```powershell
   dotnet test
   ```

## License
MetroHub is licensed under the [GNU General Public License v3.0](LICENSE).
