# WinUI 3 (Windows App SDK) Directives for LLMs

You are writing modern WinUI 3 (Windows App SDK / `Microsoft.UI.Xaml`) code for .NET 8+. 
Do NOT generate WPF (`System.Windows`) or UWP (`Windows.UI.Xaml`) patterns.

## 1. Window Rules (`Microsoft.UI.Xaml.Window`)
- `Microsoft.UI.Xaml.Window` inherits directly from `System.Object`. It is **NOT** a `FrameworkElement` or `Control`.
- **Displaying Windows:** ALWAYS use `window.Activate()`. NEVER write `window.Show()` or `window.ShowDialog()` (they do not exist).
- **Allowed XAML Attributes on `<Window>`:** Only `Title` and `<Window.SystemBackdrop>`.
- **Forbidden XAML Attributes on `<Window>`:** Do NOT set `Width`, `Height`, `Background`, `RequestedTheme`, `Resources`, `SizeToContent`, or `ExtendsContentIntoTitleBar` in XAML.
- **Window Initialization in C#:**
  - Enable extended titlebars in C#: `ExtendsContentIntoTitleBar = true;`
  - Sizing/positioning must use `this.AppWindow` or Win32 HWND (`WinRT.Interop.WindowNative.GetWindowHandle(this)`).

## 2. Layout, Themes, and Events
- **Events:** `Window` does not have `KeyDown`, `PointerWheelChanged`, or `Loaded` events. Wire input events to the root element inside `<Window>` (e.g., `<Grid x:Name="Root">`).
- **Themes:** Use `Microsoft.UI.Xaml.ElementTheme` (`Light`, `Dark`, `Default`). Apply it to the root element (`Root.RequestedTheme = ElementTheme.Dark;`), not `Window`.
- **Backdrops:** Set `this.SystemBackdrop = new Microsoft.UI.Xaml.Media.MicaBackdrop();` in C#.
- **Composition Namespace:** Low-level controllers (`MicaController`, `DesktopAcrylicController`) live in `Microsoft.UI.Composition.SystemBackdrops` (NOT `Microsoft.UI.Composition`).

## 3. XAML Resource Hierarchy
- Theme dictionaries must be structured strictly inside `Application.Resources`:
  ```xml
  <Application.Resources>
      <ResourceDictionary>
          <ResourceDictionary.ThemeDictionaries>
              <ResourceDictionary x:Key="Dark"> ... </ResourceDictionary>
              <ResourceDictionary x:Key="Light"> ... </ResourceDictionary>
          </ResourceDictionary.ThemeDictionaries>
      </ResourceDictionary>
  </Application.Resources>
