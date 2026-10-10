using System.Text.Json.Serialization;

namespace SparkEventAnalyzer.Models;

// Subset of the Spark History Server REST API v1 schemas.
// Reference: https://spark.apache.org/docs/latest/monitoring.html#rest-api

public sealed class SparkApplication
{
    [JsonPropertyName("id")] public string Id { get; set; } = string.Empty;
    [JsonPropertyName("name")] public string Name { get; set; } = string.Empty;
    [JsonPropertyName("attempts")] public List<SparkApplicationAttempt> Attempts { get; set; } = new();
}

public sealed class SparkApplicationAttempt
{
    [JsonPropertyName("attemptId")] public string? AttemptId { get; set; }
    [JsonPropertyName("startTime")][JsonConverter(typeof(SparkDateTimeOffsetConverter))] public DateTimeOffset? StartTime { get; set; }
    [JsonPropertyName("endTime")][JsonConverter(typeof(SparkDateTimeOffsetConverter))] public DateTimeOffset? EndTime { get; set; }
    [JsonPropertyName("sparkUser")] public string? SparkUser { get; set; }
    [JsonPropertyName("completed")] public bool Completed { get; set; }
}

public sealed class SparkJob
{
    [JsonPropertyName("jobId")] public int JobId { get; set; }
    [JsonPropertyName("name")] public string Name { get; set; } = string.Empty;
    [JsonPropertyName("status")] public string Status { get; set; } = string.Empty;
    [JsonPropertyName("numTasks")] public int NumTasks { get; set; }
    [JsonPropertyName("numCompletedTasks")] public int NumCompletedTasks { get; set; }
    [JsonPropertyName("numFailedTasks")] public int NumFailedTasks { get; set; }
    [JsonPropertyName("numActiveTasks")] public int NumActiveTasks { get; set; }
    [JsonPropertyName("stageIds")] public List<int> StageIds { get; set; } = new();
}

public sealed class SparkStage
{
    [JsonPropertyName("status")] public string Status { get; set; } = string.Empty;
    [JsonPropertyName("stageId")] public int StageId { get; set; }
    [JsonPropertyName("attemptId")] public int AttemptId { get; set; }
    [JsonPropertyName("name")] public string Name { get; set; } = string.Empty;
    [JsonPropertyName("numTasks")] public int NumTasks { get; set; }
    [JsonPropertyName("numFailedTasks")] public int NumFailedTasks { get; set; }
    [JsonPropertyName("numCompleteTasks")] public int NumCompleteTasks { get; set; }
    [JsonPropertyName("executorRunTime")] public long ExecutorRunTime { get; set; }
    [JsonPropertyName("shuffleReadBytes")] public long ShuffleReadBytes { get; set; }
    [JsonPropertyName("shuffleWriteBytes")] public long ShuffleWriteBytes { get; set; }
    [JsonPropertyName("memoryBytesSpilled")] public long MemoryBytesSpilled { get; set; }
    [JsonPropertyName("diskBytesSpilled")] public long DiskBytesSpilled { get; set; }
    [JsonPropertyName("failureReason")] public string? FailureReason { get; set; }
    [JsonPropertyName("details")] public string? Details { get; set; }
}

public sealed class SparkExecutor
{
    [JsonPropertyName("id")] public string Id { get; set; } = string.Empty;
    [JsonPropertyName("hostPort")] public string? HostPort { get; set; }
    [JsonPropertyName("isActive")] public bool IsActive { get; set; }
    [JsonPropertyName("totalCores")] public int TotalCores { get; set; }
    [JsonPropertyName("totalTasks")] public int TotalTasks { get; set; }
    [JsonPropertyName("failedTasks")] public int FailedTasks { get; set; }
    [JsonPropertyName("completedTasks")] public int CompletedTasks { get; set; }
    [JsonPropertyName("totalDuration")] public long TotalDuration { get; set; }
    [JsonPropertyName("totalGCTime")] public long TotalGCTime { get; set; }
    [JsonPropertyName("totalInputBytes")] public long TotalInputBytes { get; set; }
    [JsonPropertyName("totalShuffleRead")] public long TotalShuffleRead { get; set; }
    [JsonPropertyName("totalShuffleWrite")] public long TotalShuffleWrite { get; set; }
    [JsonPropertyName("maxMemory")] public long MaxMemory { get; set; }
    [JsonPropertyName("memoryUsed")] public long MemoryUsed { get; set; }
}
