import Foundation
import HealthKit

/// A heart-rate sample as supplied by Apple Health, regardless of its device/app source.
public struct WorkoutHeartRatePoint: Codable, Sendable {
    public let healthKitID: UUID
    public let timestamp: Date
    public let beatsPerMinute: Double
    public let sourceName: String
    public let deviceName: String?
}

public struct WorkoutHeartRateReport: Codable, Sendable {
    public let workoutID: UUID
    public let startedAt: Date
    public let endedAt: Date?
    public let averageBeatsPerMinute: Double?
    public let maximumBeatsPerMinute: Double?
    public let points: [WorkoutHeartRatePoint]

    public init(workoutID: UUID, startedAt: Date, endedAt: Date?, points: [WorkoutHeartRatePoint]) {
        self.workoutID = workoutID
        self.startedAt = startedAt
        self.endedAt = endedAt
        self.points = points.sorted { $0.timestamp < $1.timestamp }
        self.averageBeatsPerMinute = points.isEmpty ? nil : points.map(\.beatsPerMinute).reduce(0, +) / Double(points.count)
        self.maximumBeatsPerMinute = points.map(\.beatsPerMinute).max()
    }
}

public protocol WorkoutHeartRateUploading {
    func upload(_ workout: WorkoutHeartRateReport) async throws
}

/// Supply an authenticated endpoint from the host app. The backend should upsert by workoutID
/// and heart-rate points by healthKitID so a retry cannot duplicate readings.
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
        let encoder = JSONEncoder()
        encoder.dateEncodingStrategy = .iso8601
        request.httpBody = try encoder.encode(workout)
        let (_, response) = try await session.data(for: request)
        guard let httpResponse = response as? HTTPURLResponse,
              (200..<300).contains(httpResponse.statusCode) else {
            throw HealthKitWorkoutError.uploadFailed
        }
    }
}

public enum HealthKitWorkoutError: Error {
    case unavailable
    case alreadyMonitoring
    case noActiveWorkout
    case invalidEndDate
    case workoutWriteNotAuthorized
    case uploadFailed
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
    /// HealthKit are included. Later device syncs require a separate reconciliation job.
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

    /// Invoke only after the user explicitly enables "Save completed workouts to Apple Health".
    /// A separate write authorization is requested; no heart-rate samples are rewritten.
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

        let configuration = HKWorkoutConfiguration()
        configuration.activityType = activityType
        let builder = HKWorkoutBuilder(healthStore: store, configuration: configuration, device: nil)
        try await builder.beginCollection(at: workout.startedAt)
        try await builder.addMetadata([HKMetadataKeyExternalUUID: workout.workoutID.uuidString])
        try await builder.endCollection(at: endedAt)
        return try await builder.finishWorkout()
    }

    private func apply(samples: [HKSample]?, deleted: [HKDeletedObject]?, for expectedWorkoutID: UUID) {
        guard workoutID == expectedWorkoutID else { return }
        for item in deleted ?? [] { pointsByID.removeValue(forKey: item.uuid) }
        for sample in samples ?? [] {
            guard let sample = sample as? HKQuantitySample, sample.quantity.doubleValue(for: beatsPerMinuteUnit) > 0 else {
                continue
            }
            pointsByID[sample.uuid] = WorkoutHeartRatePoint(
                healthKitID: sample.uuid,
                timestamp: sample.startDate,
                beatsPerMinute: sample.quantity.doubleValue(for: beatsPerMinuteUnit),
                sourceName: sample.sourceRevision.source.name,
                deviceName: sample.device?.name
            )
        }
        onPointsChanged?(pointsByID.values.sorted { $0.timestamp < $1.timestamp })
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
