# Changelog

## 1.1.0

- **Pick what the taskbar shows**: temperature, usage, power and clock for the CPU and GPU, GPU memory temperature, RAM usage, network download/upload and disk read/write. Values are stacked two devices per block.
- **Longer history**: switch the details panel between the last 10 minutes, 1 hour and 24 hours. The 24-hour history is kept per minute (average, min and max) in `history.csv`, so it survives restarts. Charts show the average line with a min–max band, and hovering shows the peak.
- **Network and disk** readings in the details panel.
- **Update check**: once a day the app asks GitHub for the latest release and tells you when a new version is out (can be turned off in settings).
- **Turkish UI**, picked automatically when Windows is in Turkish, or chosen in settings.
- Releases are now built by GitHub Actions.

## 1.0.0

- First release: taskbar overlay with CPU/GPU temperature and usage, details panel with 10-minute charts, spike log, stability watcher (WHEA errors, blue screens, unexpected shutdowns), temperature notifications, settings window, start with Windows.
