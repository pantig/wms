namespace Wms.Simulation;

internal sealed record SimulationSnapshotDto(
    int Width,
    int Depth,
    string Mode,
    bool IsRunning,
    bool IsBlocked,
    string? BlockMessage,
    string CurrentTime,
    double CurrentSeconds,
    double LoadIntervalSeconds,
    double UnloadIntervalSeconds,
    double UnloadCycleIntervalSeconds,
    double UnloadStackIntervalSeconds,
    string NextLoadIn,
    string NextUnloadIn,
    string NextUnloadCycleIn,
    string NextUnloadStackIn,
    bool IsUnloadCycleActive,
    int ActiveUnloadCompleted,
    int ActiveUnloadRemaining,
    bool IsWaitingForUnloadBatch,
    int LoadedPallets,
    int UnloadedStacks,
    int UnloadBatches,
    int FullStacks,
    int AccessibleFullStacks,
    int ReadyUnloadStacks,
    int UnloadBatchSize,
    int HistoryIndex,
    int HistoryCount,
    bool CanStepBackward,
    bool CanStepForward,
    IReadOnlyList<CellDto> Cells,
    IReadOnlyList<SimulationEventDto> Events);

internal sealed record CellDto(
    int X,
    int Y,
    int DisplayX,
    int DisplayY,
    int Height,
    bool IsAccessible,
    bool IsFull,
    bool IsReliefUnloadReady,
    bool IsInUnloadPlan,
    bool IsNextUnload,
    int CountA,
    int CountB,
    int CountC,
    double OldestWaitSeconds,
    double FifoScore,
    string OrderBottomToTop,
    IReadOnlyList<PalletDto> Pallets);

internal sealed record PalletDto(long Id, string Type, int Height, string LoadedAt, double WaitSeconds);

internal sealed record SimulationEventDto(int Number, string Time, string Kind, string Text);
