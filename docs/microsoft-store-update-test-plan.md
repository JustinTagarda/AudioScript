# Microsoft Store update test plan

AudioScript now uses one Microsoft Store/MSIX update path:

1. App startup completes normally.
2. A hidden background update check runs only after first render.
3. If no update is available, no UI is shown.
4. If an update is available, silent Store download support is checked.
5. If silent download succeeds, the update is deferred until app exit.
6. If silent download is unavailable or fails, the app uses the OS-provided Store update UI.
7. If deferred install exists when the user closes the app, the app shows a modal install-progress window, attempts Store-supported install, clears deferred state, and then continues shutdown.
8. The app never restarts itself after an update operation.

Validated project decisions:

- Startup host: `App.xaml.cs`, because this is the app-level bootstrap location that constructs services and shows `MainWindow`.
- Update owner: `AppUpdateService` implements `IAppUpdateCoordinator` and owns the end-to-end flow.
- Store API owner: `MicrosoftStoreUpdateProvider` owns `StoreContext` update API calls and Store result translation.
- Deferred state: `%LocalAppData%` package/local state settings path, stored as `Settings\update-state.json`.
- Logging: existing `ProcessLogService`.
- User-visible behavior: no custom UI during hidden checking, no visible error on startup update failure, OS Store UI only after an update is confirmed and silent download is unavailable or fails, modal progress UI only during exit-time install.
- Non-Store context: skip update flow, log, and continue startup.
- Restart behavior: no automatic restart, shutdown, or replacement process launch for updates.

Manual verification:

- Local debug run: start `bin\Debug\net10.0-windows10.0.17763.0\AudioScript.exe`; update logic should log a supported-context skip and show no update UI.
- Packaged sideload run: launch the MSIX package; Store API failures or missing Store association should be logged and should not crash startup.
- Store installed, no update: app should open normally, hidden check should log no updates, and no checking UI should appear.
- Store installed, update available, Store automatic app updates off: hidden check should find the update, silent capability should be false or unavailable, and Windows/Store update UI should be used.
- Store installed, silent download succeeds: app should defer install until exit, then show the exit-time progress window and attempt Store-supported install on close.
- Cancel fallback UI: cancellation should be logged, app usage should continue, and there should be no immediate retry loop.
- Completed update: update completion should be logged, and AudioScript should not call shutdown, exit, or start a replacement process.
