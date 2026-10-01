import Foundation
import HealthKit

/// A heart-rate sample as supplied by Apple Health, regardless of its device/app source.
public struct WorkoutHeartRatePoint: Codable, Sendable {
    public let healthKitID: UUID
    public let timestamp: Date
    public let beatsPerMinute: Double
    public let sourceName: String
    public let deviceName: String?
    /// `HKMetadataKeyWasUserEntered`: typed into the Health app rather than measured. The API labels it "self-reported".
    public let wasUserEntered: Bool
}

public struct WorkoutHeartRateReport: Codable, Sendable {
    public let workoutID: UUID
    public let startedAt: Date
    /// `nil` while the workout runs. When set, the report is authoritative: the API marks points missing from it as
    /// removed by the source.
    public let endedAt: Date?
    public let averageBeatsPerMinute: Double?
    public let maximumBeatsPerMinute: Double?
    public let points: [WorkoutHeartRatePoint]

    public init(workoutID: UUID, startedAt: Date, endedAt: Date?, points: [WorkoutHeartRatePoint]) {
        self.workoutID = workoutID
        self.startedAt = startedAt
        self.endedAt = endedAt
        self.points = points.sorted { $0.timestamp < $1.timestamp }
        // Informational only: the API recalculates both an arithmetic and a time-weighted mean from accepted points.
        self.averageBeatsPerMinute = points.isEmpty ? nil : points.map(\.beatsPerMinute).reduce(0, +) / Double(points.count)
        self.maximumBeatsPerMinute = points.map(\.beatsPerMinute).max()
    }
}

public protocol WorkoutHeartRateUploading {
    func upload(_ workout: WorkoutHeartRateReport) async throws
}

/// API JSON encoding: ISO 8601 UTC **with fractional seconds**. Plain `.iso8601` truncates to whole seconds, which can
/// push a sample taken in the first second of the workout before the session start and get it rejected.
enum APIJSON {
    static let encoder: JSONEncoder = {
        let encoder = JSONEncoder()
        let style = Date.ISO8601FormatStyle(includingFractionalSeconds: true)
        encoder.dateEncodingStrategy = .custom { date, encoder in
            var container = encoder.singleValueContainer()
            try container.encode(style.format(date))
        }
        return encoder
    }()
}

/// Supply an authenticated endpoint from the host app:
/// `PUT {api}/api/v1/workout-sessions/{workoutID}/heart-rate`.
/// The backend upserts by workoutID and deduplicates points by (client, healthKitID), so a retry cannot duplicate readings.
public struct HTTPWorkoutHeartRateUploader: WorkoutHeartRateUploading {
    public let endpoint: URL
    public let bearerToken: String
    public let session: URLSession

    public init(endpoint: URL, bearerToken: String, session: URLSession = .shared) {
        self.endpoint = endpoint
        self.bearerToken = bearerToken
        self.session = session
    }

    public func upload(_ workout: WorkoutHeartRateReport) async throws {
        var request = URLRequest(url: endpoint)
        request.httpMethod = "PUT"
        request.setValue("application/json", forHTTPHeaderField: "Content-Type")
        request.setValue("Bearer \(bearerToken)", forHTTPHeaderField: "Authorization")
        request.setValue(workout.workoutID.uuidString, forHTTPHeaderField: "Idempotency-Key")
        request.httpBody = try APIJSON.encoder.encode(workout)
        try await send(request, using: session)
    }
}

/// Reports the HKWorkout saved for a session: `PUT {api}/api/v1/workout-sessions/{workoutID}/apple-health-workout`.
/// The API keeps the first id; a different id answers 409, meaning a duplicate exists and should be deleted.
public struct HTTPSavedWorkoutReporter {
    public let endpoint: URL
    public let bearerToken: String
    public let session: URLSession

    public init(endpoint: URL, bearerToken: String, session: URLSession = .shared) {
        self.endpoint = endpoint
        self.bearerToken = bearerToken
        self.session = session
    }

    public func report(_ workout: HKWorkout) async throws {
        struct Body: Encodable {
            let healthKitWorkoutId: UUID
            let savedAt: Date
        }

        var request = URLRequest(url: endpoint)
        request.httpMethod = "PUT"
        request.setValue("application/json", forHTTPHeaderField: "Content-Type")
        request.setValue("Bearer \(bearerToken)", forHTTPHeaderField: "Authorization")
        request.httpBody = try APIJSON.encoder.encode(Body(healthKitWorkoutId: workout.uuid, savedAt: workout.endDate))
        try await send(request, using: session)
    }
}

/// 2xx succeeds; 408, 409 (concurrent write), 429 and 5xx are retryable; other 4xx are permanent and must not be
/// retried forever (e.g. 403 when Apple Health import is paused, 404 when the session does not exist).
private func send(_ request: URLRequest, using session: URLSession) async throws {
    let (_, response) = try await session.data(for: request)
    guard let httpResponse = response as? HTTPURLResponse else { throw HealthKitWorkoutError.uploadFailed }
    switch httpResponse.statusCode {
    case 200..<300:
        return
    case 408, 409, 429, 500...:
        throw HealthKitWorkoutError.uploadFailed
    default:
        throw HealthKitWorkoutError.uploadRejected(statusCode: httpResponse.statusCode)
    }
}

public enum HealthKitWorkoutError: Error {
    case unavailable
    case alreadyMonitoring
    case noActiveWorkout
    case invalidEndDate
    case workoutWriteNotAuthorized
    /// Transient: keep the report persisted and retry with the same workout id.
    case uploadFailed
    /// Permanent: do not retry; surface the state (e.g. import paused) to the user.
    case uploadRejected(statusCode: Int)
}

/// Call `start` from a user-initiated action in the iPhone app. HealthKit may deliver samples
/// after the device syncs; this is not a guaranteed real-time heart-rate stream.
@MainActor
public final class HealthKitWorkoutIntegration {
    private let store: HKHealthStore
    private let heartRateType = HKQuantityType.quantityType(forIdentifier: .heartRate)!
    private let beatsPerMinuteUnit = HKUnit.count().unitDivided(by: .minute())
    private var query: HKAnchoredObjectQuery?
    private var workoutID: UUID?
    private var startedAt: Date?
    private var pointsByID: [UUID: WorkoutHeartRatePoint] = [:]
    private var onPointsChanged: (([WorkoutHeartRatePoint]) -> Void)?

    public init(store: HKHealthStore = HKHealthStore()) {
        self.store = store
    }

    /// The user must approve HealthKit's permission sheet. Apple does not expose whether
    /// read permission was denied: a query with no results must be shown as unavailable data.
    /// `workoutID` must be the id already registered with `POST /api/v1/workout-sessions`.
    public func start(
        workoutID: UUID,
        at startDate: Date = Date(),
        onPointsChanged: @escaping ([WorkoutHeartRatePoint]) -> Void
    ) async throws {
        guard HKHealthStore.isHealthDataAvailable() else { throw HealthKitWorkoutError.unavailable }
        guard query == nil, self.workoutID == nil else { throw HealthKitWorkoutError.alreadyMonitoring }

        try await store.requestAuthorization(toShare: [], read: [heartRateType])

        self.workoutID = workoutID
        self.startedAt = startDate
        self.pointsByID = [:]
        self.onPointsChanged = onPointsChanged

        // No source predicate: any source recorded in Apple Health is eligible.
        // Keep source metadata for provenance and deduplication.
        let predicate = HKQuery.predicateForSamples(
            withStart: startDate, end: nil, options: .strictStartDate
        )
        let anchoredQuery = HKAnchoredObjectQuery(
            type: heartRateType,
            predicate: predicate,
            anchor: nil,
            limit: HKObjectQueryNoLimit
        ) { [weak self] _, samples, deleted, _, _ in
            Task { @MainActor in self?.apply(samples: samples, deleted: deleted, for: workoutID) }
        }
        anchoredQuery.updateHandler = { [weak self] _, samples, deleted, _, _ in
            Task { @MainActor in self?.apply(samples: samples, deleted: deleted, for: workoutID) }
        }
        query = anchoredQuery
        store.execute(anchoredQuery)
    }

    /// Stops monitoring and performs a final range query so late-delivered points already in
    /// HealthKit are included. Later device syncs are handled by `reconcile`.
    public func finish(at endDate: Date = Date()) async throws -> WorkoutHeartRateReport {
        guard let workoutID, let startedAt, let query else {
            throw HealthKitWorkoutError.noActiveWorkout
        }
        guard endDate >= startedAt else { throw HealthKitWorkoutError.invalidEndDate }

        store.stop(query)
        self.query = nil
        let finalSamples = try await samples(from: startedAt, to: endDate)
        apply(samples: finalSamples, deleted: nil, for: workoutID)
        let result = WorkoutHeartRateReport(
            workoutID: workoutID,
            startedAt: startedAt,
            endedAt: endDate,
            points: Array(pointsByID.values).filter { $0.timestamp >= startedAt && $0.timestamp <= endDate }
        )
        self.workoutID = nil
        self.startedAt = nil
        self.onPointsChanged = nil
        pointsByID = [:]
        return result
    }

    /// HK-08: rebuilds the authoritative report for a finished workout from what Apple Health holds now, picking up
    /// samples that sources synced late and dropping samples they deleted. Call it for each session returned by
    /// `GET /api/v1/me/health-connections/apple-health/reconciliation` (e.g. on app launch and when returning to the
    /// foreground) and upload the result. Uploading an unchanged report is harmless.
    public func reconcile(workoutID: UUID, startedAt: Date, endedAt: Date) async throws -> WorkoutHeartRateReport {
        guard HKHealthStore.isHealthDataAvailable() else { throw HealthKitWorkoutError.unavailable }
        guard endedAt >= startedAt else { throw HealthKitWorkoutError.invalidEndDate }
        let points = try await samples(from: startedAt, to: endedAt).compactMap(point(from:))
        return WorkoutHeartRateReport(workoutID: workoutID, startedAt: startedAt, endedAt: endedAt, points: points)
    }

    /// Upload the latest in-progress snapshot. The host app can call this after each UI update
    /// or on a timer and should persist/retry failed reports when connectivity returns.
    public func syncCurrent(using uploader: WorkoutHeartRateUploading) async throws {
        guard let workoutID, let startedAt else { throw HealthKitWorkoutError.noActiveWorkout }
        let report = WorkoutHeartRateReport(
            workoutID: workoutID, startedAt: startedAt, endedAt: nil,
            points: Array(pointsByID.values)
        )
        try await uploader.upload(report)
    }

    public func upload(_ workout: WorkoutHeartRateReport, using uploader: WorkoutHeartRateUploading) async throws {
        try await uploader.upload(workout)
    }

    /// Invoke only after the user explicitly enables "Save completed workouts to Apple Health"
    /// (`PUT /api/v1/me/health-connections/apple-health/workout-saving`). A separate write authorization is
    /// requested; no heart-rate samples are rewritten. If a workout carrying this session id already exists (retry,
    /// reinstall, second device) it is returned instead of saving a duplicate. Report the result with
    /// `HTTPSavedWorkoutReporter`.
    @discardableResult
    public func saveToAppleHealth(
        _ workout: WorkoutHeartRateReport,
        activityType: HKWorkoutActivityType
    ) async throws -> HKWorkout? {
        guard let endedAt = workout.endedAt else { throw HealthKitWorkoutError.noActiveWorkout }
        guard HKHealthStore.isHealthDataAvailable() else { throw HealthKitWorkoutError.unavailable }
        let workoutType = HKObjectType.workoutType()
        try await store.requestAuthorization(toShare: [workoutType], read: [])
        guard store.authorizationStatus(for: workoutType) == .sharingAuthorized else {
            throw HealthKitWorkoutError.workoutWriteNotAuthorized
        }

        if let existing = try await savedWorkout(externalID: workout.workoutID) {
            return existing
        }

        let configuration = HKWorkoutConfiguration()
        configuration.activityType = activityType
        let builder = HKWorkoutBuilder(healthStore: store, configuration: configuration, device: nil)
        try await builder.beginCollection(at: workout.startedAt)
        try await builder.addMetadata([HKMetadataKeyExternalUUID: workout.workoutID.uuidString])
        try await builder.endCollection(at: endedAt)
        return try await builder.finishWorkout()
    }

    /// Finds a workout this app saved earlier for the session. HealthKit always lets an app read objects it wrote.
    private func savedWorkout(externalID: UUID) async throws -> HKWorkout? {
        try await withCheckedThrowingContinuation { continuation in
            let predicate = NSCompoundPredicate(andPredicateWithSubpredicates: [
                HKQuery.predicateForObjects(from: HKSource.default()),
                HKQuery.predicateForObjects(withMetadataKey: HKMetadataKeyExternalUUID, allowedValues: [externalID.uuidString]),
            ])
            let query = HKSampleQuery(
                sampleType: HKObjectType.workoutType(),
                predicate: predicate,
                limit: 1,
                sortDescriptors: nil
            ) { _, results, error in
                if let error { continuation.resume(throwing: error) }
                else { continuation.resume(returning: results?.first as? HKWorkout) }
            }
            store.execute(query)
        }
    }

    private func apply(samples: [HKSample]?, deleted: [HKDeletedObject]?, for expectedWorkoutID: UUID) {
        guard workoutID == expectedWorkoutID else { return }
        for item in deleted ?? [] { pointsByID.removeValue(forKey: item.uuid) }
        for sample in samples ?? [] {
            guard let point = point(from: sample) else { continue }
            pointsByID[point.healthKitID] = point
        }
        onPointsChanged?(pointsByID.values.sorted { $0.timestamp < $1.timestamp })
    }

    private func point(from sample: HKSample) -> WorkoutHeartRatePoint? {
        guard let sample = sample as? HKQuantitySample else { return nil }
        let bpm = sample.quantity.doubleValue(for: beatsPerMinuteUnit)
        guard bpm > 0 else { return nil }
        return WorkoutHeartRatePoint(
            healthKitID: sample.uuid,
            timestamp: sample.startDate,
            beatsPerMinute: bpm,
            sourceName: sample.sourceRevision.source.name,
            deviceName: sample.device?.name,
            wasUserEntered: (sample.metadata?[HKMetadataKeyWasUserEntered] as? Bool) ?? false
        )
    }

    private func samples(from start: Date, to end: Date) async throws -> [HKSample] {
        try await withCheckedThrowingContinuation { continuation in
            let predicate = HKQuery.predicateForSamples(withStart: start, end: end, options: .strictStartDate)
            let sampleQuery = HKSampleQuery(
                sampleType: heartRateType,
                predicate: predicate,
                limit: HKObjectQueryNoLimit,
                sortDescriptors: nil
            ) { _, results, error in
                if let error { continuation.resume(throwing: error) }
                else { continuation.resume(returning: results ?? []) }
            }
            store.execute(sampleQuery)
        }
    }
}
