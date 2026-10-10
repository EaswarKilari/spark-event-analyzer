namespace SparkEventAnalyzer.Models;

// Compact, LLM-ready summary of a Spark application run.
// This is the seed of the `preprocess_events` step: it reduces the run to the
// signals that matter for failure / performance root-cause analysis.
public sealed class SparkRunSummary
{
    public string ApplicationId { get; set; } = string.Empty;
    public string ApplicationName { get; set; } = string.Empty;
    public string? AttemptId { get; set; }
    public string? SparkUser { get; set; }
    public bool Completed { get; set; }

    public int TotalJobs { get; set; }
    public int FailedJobs { get; set; }
    public int TotalStages { get; set; }
    public int FailedStages { get; set; }
    public int TotalFailedTasks { get; set; }

    public List<StageFinding> HotStages { get; set; } = new();
    public List<StageFinding> FailedStageDetails { get; set; } = new();
    public List<StageFinding> SkewSuspects { get; set; } = new();
    public List<StageFinding> SpillSuspects { get; set; } = new();
    public List<ExecutorFinding> ExecutorIssues { get; set; } = new();

    // Human-readable one-line signals the LLM can reason over quickly.
    public List<string> Signals { get; set; } = new();
}

public sealed class StageFinding
{
    public int StageId { get; set; }
    public int AttemptId { get; set; }
    public string Name { get; set; } = string.Empty;
    public string Status { get; set; } = string.Empty;
    public int NumTasks { get; set; }
    public int NumFailedTasks { get; set; }
    public long ExecutorRunTimeMs { get; set; }
    public long ShuffleReadBytes { get; set; }
    public long ShuffleWriteBytes { get; set; }
    public long MemoryBytesSpilled { get; set; }
    public long DiskBytesSpilled { get; set; }
    public string? FailureReason { get; set; }
}

public sealed class ExecutorFinding
{
    public string Id { get; set; } = string.Empty;
    public bool IsActive { get; set; }
    public int FailedTasks { get; set; }
    public long TotalGCTimeMs { get; set; }
    public long TotalDurationMs { get; set; }
    public double GcRatio { get; set; }
}
