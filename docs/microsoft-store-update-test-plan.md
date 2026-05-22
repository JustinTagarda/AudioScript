# Microsoft Store update test plan

AudioScript now uses one Microsoft Store/MSIX update path:

1. App startup completes normally.
2. A hidden background update check runs only after first render.
3. If the user clicks the version label or the footer update button, the app runs the same user-initiated update coordinator path.
4. If no update is available, the user-initiated update flow completes without starting download or install UI.
5. The status control also exposes a restore-purchase action for rechecking entitlement.
6. Startup background update checks remain silent and can still defer install until exit.
7. Hidden startup checks may be throttled by `MinimumCheckInterval`, but a deferred install must still be revalidated on close.
8. If silent download succeeds during startup checking, the update is deferred until app exit.
9. If silent download is unavailable or fails during startup checking, the app uses the OS-provided Store update UI.
10. If deferred install exists when the user closes the app, the app shows a modal install-progress window, attempts Store-supported install, clears deferred state, and then continues shutdown.
11. The app never restarts itself after an update operation.

Validated project decisions:

- Startup host: `App.xaml.cs`, because this is the app-level bootstrap location that constructs services and shows `MainWindow`.
- Update owner: `AppUpdateService` implements `IAppUpdateCoordinator` and owns the end-to-end flow.
- Store API owner: `MicrosoftStoreUpdateProvider` owns `StoreContext` update API calls and Store result translation.
- Deferred state: `%LocalAppData%` package/local state settings path, stored as `Settings\update-state.json`.
- Logging: existing `ProcessLogService`.
- User-visible behavior: no custom UI during hidden startup checking, no visible error on startup update failure, version-label and footer update actions use the same coordinator path, OS Store UI only after an update is confirmed and silent download is unavailable or fails during startup checking, modal progress UI only during exit-time install.
- User-visible behavior: restore purchase is available from the bottom-right status control and reports clear purchase state.
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
