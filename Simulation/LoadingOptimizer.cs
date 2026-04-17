namespace Wms.Simulation;

internal static class LoadingOptimizer
{
    public static LoadDecision? ChoosePosition(
        Warehouse warehouse,
        PalletType pallet,
        TimeSpan now,
        Func<int> tieBreaker)
    {
        return warehouse.GetLoadCandidates(pallet)
            .Select(position => Evaluate(warehouse, position, pallet, now, tieBreaker()))
            .OrderBy(decision => decision.Priority)
            .ThenByDescending(decision => decision.OldestWaitSeconds)
            .ThenByDescending(decision => decision.FifoScore)
            .ThenByDescending(decision => decision.AfterHeight)
            .ThenBy(decision => decision.Sequence)
            .ThenBy(decision => decision.TieBreaker)
            .FirstOrDefault();
    }

    private static LoadDecision Evaluate(
        Warehouse warehouse,
        Position position,
        PalletType pallet,
        TimeSpan now,
        int tieBreaker)
    {
        var stack = warehouse.At(position);
        var afterHeight = stack.Height + pallet.Height();
        var exactFill = afterHeight == Warehouse.MaxStackHeight;
        var closesReliefStack = pallet == PalletType.A && stack.Height == Warehouse.ReliefUnloadHeight;
        var priority = closesReliefStack
            ? 0
            : exactFill
                ? 1
                : stack.IsEmpty
                    ? 3
                    : 2;
        var sequence = ColumnFillSequence(position);
        var oldestWaitSeconds = stack.OldestWaitSeconds(now);
        var fifoScore = stack.FifoScore(now);
        var reason = Reason(
            position,
            stack,
            priority,
            oldestWaitSeconds,
            fifoScore);

        return new LoadDecision(
            position,
            priority,
            sequence,
            oldestWaitSeconds,
            fifoScore,
            afterHeight,
            reason,
            tieBreaker);
    }

    private static string Reason(
        Position position,
        PalletStack stack,
        int priority,
        double oldestWaitSeconds,
        double fifoScore)
    {
        var coordinate = $"X{position.DisplayX}Y{position.DisplayY}";
        return priority switch
        {
            0 => $"{coordinate}: priorytet A dopelnia stack 6/7 do 7/7",
            1 => $"{coordinate}: domyka stack do 7/7, najstarsza paleta czeka {FormatAge(oldestWaitSeconds)}",
            2 => $"{coordinate}: aging FIFO uzupelnia niepelny stack, najstarsza paleta czeka {FormatAge(oldestWaitSeconds)}, suma wieku {FormatAge(fifoScore)}",
            _ when stack.IsEmpty => $"{coordinate}: otwiera nowa pozycje w kolejnosci kolumnowej",
            _ => $"{coordinate}: uzupelnia stack"
        };
    }

    private static string FormatAge(double totalSeconds)
    {
        var safeSeconds = Math.Max(0, (int)Math.Floor(totalSeconds));
        var minutes = safeSeconds / 60;
        var seconds = safeSeconds % 60;

        return minutes > 0 ? $"{minutes}m {seconds}s" : $"{seconds}s";
    }

    private static int ColumnFillSequence(Position position)
    {
        return position.X * position.Depth + position.DisplayY - 1;
    }
}

internal sealed record LoadDecision(
    Position Position,
    int Priority,
    int Sequence,
    double OldestWaitSeconds,
    double FifoScore,
    int AfterHeight,
    string Reason,
    int TieBreaker);
