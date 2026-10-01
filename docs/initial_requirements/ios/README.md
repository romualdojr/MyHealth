# HealthKit Integration Module (iPhone)

This directory contains a standalone native implementation of the iPhone side of the Apple Health integration, and the
contract it has with the MyHealth API (`src/MyHealth.ApiService`, implemented and covered by integration tests). Add the
Swift file to the iOS target and compile and test it in Xcode on a physical iPhone with real Apple Health data
(deployment target iOS 15+ for `Date.ISO8601FormatStyle`).

## iOS Target Setup

1. Enable the **HealthKit** capability in Xcode (`com.apple.developer.healthkit` entitlement).
2. Add `NSHealthShareUsageDescription`: “Read your heart rate from Apple Health during workouts to show your progress and sync it with your account.”
3. Add `NSHealthUpdateUsageDescription`: “Save completed workouts to Apple Health when you authorize it.”
4. Explain data use and API sharing before connecting (use `GET …/apple-health` for the list of receiving professionals); call `start` only after an explicit client action. iOS controls the read-permission dialog.
5. Provide a separate control for saving workouts to Apple Health; call `saveToAppleHealth` only after that choice. Write permission is independent of read permission.

## App Flow

```swift
let api = URL(string: "https://api.example.com/api/v1")!
let healthKit = HealthKitWorkoutIntegration()

// 1. The phone generates the session id and registers it BEFORE starting HealthKit (works offline: retry later,
//    the call is idempotent on sessionId).
let workoutID = UUID()
// POST {api}/workout-sessions { sessionId: workoutID, planId, plannedSessionId, startedAt }

let uploader = HTTPWorkoutHeartRateUploader(
    endpoint: api.appending(path: "workout-sessions/\(workoutID)/heart-rate"),
    bearerToken: currentAccessToken
)

// 2. User action: start workout (only when the client connected Apple Health:
//    POST {api}/me/health-connections/apple-health/connect).
try await healthKit.start(workoutID: workoutID, at: startedAt) { points in
    // Update the chart, latest sample time and latest sync time. Data may arrive late.
}

// 3. While the session is active, upload sequentially when new points arrive or on a timer.
try await healthKit.syncCurrent(using: uploader)

// 4. User action: finish. Persist `result` locally BEFORE uploading; retry on .uploadFailed with the same id.
let result = try await healthKit.finish(at: completedAt)
// POST {api}/workout-sessions/{workoutID}/complete { completedAt, perceivedExertion, ... }
try await healthKit.upload(result, using: uploader)

// 5. Only if the client enabled saving (PUT {api}/me/health-connections/apple-health/workout-saving { enabled: true }):
if let saved = try await healthKit.saveToAppleHealth(result, activityType: .traditionalStrengthTraining) {
    try await HTTPSavedWorkoutReporter(
        endpoint: api.appending(path: "workout-sessions/\(workoutID)/apple-health-workout"),
        bearerToken: currentAccessToken
    ).report(saved)
}

// 6. On launch / foreground: reconcile late samples (HK-08).
// GET {api}/me/health-connections/apple-health/reconciliation → sessions[] { sessionId, startedAt, endedAt, ... }
for s in candidates {
    let report = try await healthKit.reconcile(workoutID: s.sessionId, startedAt: s.startedAt, endedAt: s.endedAt)
    try await healthKit.upload(report, using: uploaderFor(s.sessionId))
}
```

Retry rules (`HealthKitWorkoutError`): `.uploadFailed` (network, 408, 409, 429, 5xx) → keep the report and retry with
the same id; `.uploadRejected(statusCode)` (400, 403, 404) → permanent, do not retry (e.g. 403 means import is paused or
revoked — the workout itself is unaffected).

## API Contract

All routes are under `/api/v1`, need `Authorization: Bearer <token>`, and use ISO 8601 UTC dates (fractional seconds
accepted and recommended).

### `PUT /workout-sessions/{workoutID}/heart-rate`

Body = `WorkoutHeartRateReport`: `workoutID`, `startedAt`, `endedAt` (null while in progress), `averageBeatsPerMinute`,
`maximumBeatsPerMinute`, `points[]` (`healthKitID`, `timestamp`, `beatsPerMinute`, `sourceName`, `deviceName`,
`wasUserEntered` — optional, default false). The body `workoutID` must match the URL. The server:

- Verifies the authenticated client owns the session and that Apple Health import is **connected** (403 when paused or revoked).
- Accepts only samples of 20–300 bpm inside the **server's** session interval and outside periods in which the client had paused or revoked import (the authorized period).
- Deduplicates points by `(client, healthKitID)`: a sample already stored for another session of the same client is counted in `claimedByOtherSession` and not stored again (also enforced by a unique index).
- Treats each report as a snapshot: in-progress reports are additive; the first report with `endedAt` is **final** (points missing from it are marked *removed by source*, never hard-deleted); later reports with `endedAt` are **reconciliation**; an in-progress report arriving after the final one is **ignored as stale** and can never revert the completed state.
- Accepts reconciliation for up to **30 days** after the workout ended (409 `HealthData.ReconciliationWindowClosed` afterwards).
- Ignores the phone's mean and maximum and recalculates from accepted points (see *Mean* below); stores provenance, every upload in a sync history, a revision number and `lastRevisedAt`.
- Is safe to retry: identical reports produce only duplicates. The `Idempotency-Key` header is accepted but not required, because the operation is idempotent by content.

Response: `{ kind, accepted, duplicates, corrected, rejected, removed, claimedByOtherSession, revision, isFinalized, summary, syncedAt }`.

### Other endpoints used by the iPhone

| Method & path | Purpose |
|---|---|
| `POST /workout-sessions` | Register the phone-generated session id (idempotent; 201 created, 200 already exists). |
| `POST /workout-sessions/{id}/complete` | Completion and feedback. Independent of HealthKit: works with access denied. |
| `GET /workout-sessions/{id}/heart-rate` | Points (each with `kind`: measured / self-reported), gaps > 60 s, sources, summary, `lastSyncedAt`, `latestSampleAt`, `lastRevisedAt`. |
| `GET /me/health-connections/apple-health` | Status (`NotConnected`/`Connected`/`Paused`/`Revoked`), history, excluded periods, `saveWorkoutsToAppleHealth`, receiving professionals. |
| `POST /me/health-connections/apple-health/connect` · `/pause` · `/revoke` | Separate client actions. Revoke also stops sharing with professionals and turns off workout saving. |
| `PUT /me/health-connections/apple-health/workout-saving` | `{ enabled }` — opt-in to saving workouts (independent of import). |
| `PUT /workout-sessions/{id}/apple-health-workout` | `{ healthKitWorkoutId, savedAt }` — first id wins; a different id → 409 (duplicate saved; delete it). Requires the opt-in. |
| `GET /me/health-connections/apple-health/reconciliation` | Finished sessions within the 30-day window to re-query; empty unless import is connected. |

### Mean

The module and `averageBeatsPerMinute` use the arithmetic mean of samples. The API stores **both**:
`averageBeatsPerMinute` (`arithmetic-mean-of-samples`, comparable with the phone) and
`timeWeightedAverageBeatsPerMinute` (`time-weighted-trapezoidal-excluding-gaps-over-60s`, null when no two samples are
within 60 s). Neither is a clinical measure.

## Pending Validation (device)

- Compile in Xcode within the actual target.
- Test on iPhone: denied or partial read access, denied write access, samples from different Apple Health sources, user-entered samples, missing and delayed samples, source deletion, no network connection, reinstall after saving a workout.
- Local persistence of pending reports and of the reconciliation loop belongs to the host app.

References: [HealthKit authorization](https://developer.apple.com/documentation/HealthKit/authorizing-access-to-health-data), [anchored query](https://developer.apple.com/documentation/healthkit/hkanchoredobjectquery), [HKWorkoutBuilder](https://developer.apple.com/documentation/healthkit/hkworkoutbuilder).
