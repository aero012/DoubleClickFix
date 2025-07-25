﻿# 🖱️ Double-click Fix

A lightweight solution for mitigating double-click issues caused by malfunctioning mice.  

**New in release 1.4.x:** 🎉 **Robust Drag & Drop Support** – if you have problems with dragging, enable this feature in the UI! The tool will maintain a stable drag until you intentionally release.

This tool ensures smoother operation by filtering unintended double-click events and supporting reliable drag-and-drop gestures, allowing you to define the minimal delay between valid clicks directly from an intuitive user interface.

### 🛍️ Get it from the [Microsoft Store](https://apps.microsoft.com/detail/9PDGM7NL2FF2?hl=en-us&gl=CH&ocid=pdpshare)!

![logo](DoubleClickFix/app.ico)

---

## ✨ Features
- **Drag & Drop Fix (New!)**: You can enable this in the UI. Hold, drag, and drop reliably — even if your mouse switch chatters during the gesture. A short pause while dragging is treated as the true release, preventing accidental drops.
- **Customizable Delay**: Adjust the minimal delay between two clicks via a user-friendly interface. Default is 50 ms.
- **Customize for Specific Mouse Buttons**: Choose which mouse buttons to fix, including left, right, middle, X1, and X2. Default is left mouse button only.
- **Windows Tray Integration**: Double-click the tray icon to open the settings UI.
- **Startup Option**: Register the application to launch with Windows. The app tries to do this automatically when you launch it the first time.

---

## 🚀 Installation

The following options are supported for installing and running the application:

### Install from Microsoft Store (recommended)
1. Go to the [Store page](https://apps.microsoft.com/detail/9PDGM7NL2FF2?hl=en-us&gl=CH&ocid=pdpshare) and install it.

### Manual Setup
1. **Download**: Grab the latest release from the [Releases page](https://github.com/nenning/DoubleClickFix/releases).
2. **Unzip & Run**: Extract the files and execute the `.exe`.  
    - Note: You might need to install the [.NET Runtime](https://dotnet.microsoft.com/en-us/download/dotnet) first.  
    - Note: Settings are stored in the registry under `HKEY_CURRENT_USER\Software\DoubleClickFix`.  
    - Note: If you move the app to a different folder, you have to deregister & re-register the app to start with Windows.

### Advanced Setup
- **Build from Source**: Clone the repository and compile the application yourself using Visual Studio or your preferred .NET toolchain.

---

## ⚙️ Configuration

### Settings
- Settings can be adjusted in the UI, including:
  - **Per-button delay**: Minimal delay between clicks for each button.
  - **Ignored devices:** Specifies which device to ignore (e.g., touchpad or touchscreen). By default, device ID `0` is ignored, but this can be modified if needed (experimental).
  - **Fix dragging issues**: Enables this only if you have problems with dragging (& dropping).
  - **Drag start delay**: The time (in ms) a drag action must take to enter the drag lock state.
  - **Drag release delay**: The time (in ms) you must hold the button after stopping movement before the release is registered. Alternatively, you can manually click to exit the drag lock.
  
### 💡 Tips
- Check the logs in the UI for detailed information on the elapsed time between your mouse clicks and filterd out double-clicks.
- Experiment with different delay settings to optimize for your personal double-click speed and specific hardware issues.
- Use the test area on the right side of the UI to test your settings (try also triple-clicking to select a whole paragraph and selecting text).

### Handling Touch Devices
- All double-clicks from touchpads or touchscreens are allowed by default.  
- If you have trouble with this, enable the **Allow 0 ms Double-Click Duration** option in the UI.

---

## 🤝 Contributions
Contributions are welcome! Feel free to open issues, submit pull requests, or suggest improvements via the [Issues tab](https://github.com/nenning/DoubleClickFix/issues).

---

## 📜 License
This project is distributed under the [MIT License](LICENSE.txt).

---

## ℹ️ Valve Anti-Cheat (VAC) Compatibility

### What **DoubleClickFix** Does
This application uses a **low-level mouse hook** to intercept and process mouse input events. It adjusts the behavior of specific clicks based on a user-defined delay and maintains drag-lock for reliable drag-and-drop.

### What VAC Scans For
Valve Anti-Cheat is designed to detect:
- **Known cheating software**: Programs that directly modify game memory, inject code, or manipulate game data.
- **Unauthorized third-party processes**: Applications that interfere with or manipulate the game in unintended ways.

### Why This Shouldn't Be an Issue
DoubleClickFix operates independently of any game and does not interact with game files, memory, or processes. It only modifies mouse input at the system level to address hardware issues, which is outside the scope of VAC detection.  

**Disclaimer**: While DoubleClickFix is very unlikely to trigger VAC, always use third-party tools responsibly and at your own discretion. For official information, refer to Valve's [VAC documentation](https://help.steampowered.com/en/faqs/view/571A-97DA-70E9-FF74).

---

## 🛠️ Technical Notes
Some technical details mostly for development.

### 🖥️ Command-Line Arguments
- **`-nohook`** – Runs the app without registering the mouse hook. Useful for UI testing or debugging (automatically applied in debug mode).  
- **`-interactive`** or **`-i`** – Displays the UI on startup. Useful for testing (automatically applied in debug mode).

### 🌍 Language Override  
- The application language can be overridden by setting the **`languageOverride`** key in the `app.config` file (for testing purposes).  

### 📦 Creating a Release

#### Github
- To create a github release (zip), run the following commands:
    - `git tag -a v1.0.1.0`
    - `git push origin v1.0.1.0`
- This will trigger the GitHub Action that creates the release.
- Add the release notes on GitHub.

#### Microsoft Store
- If needed, adjust the version in `Package.appxmanifest`.
- To create a store package, use **Publish** → **Create App Packages** in Visual Studio.
- Publish it through the [Partner Portal](https://partner.microsoft.com/en-us/dashboard/apps-and-games/overview): upload the package (`.msixbundle`), fill in the details and submit it for certification.
