using System.Globalization;

namespace Wms.Simulation;

internal sealed class SimulationEngine
{
    private const int Width = 9;
    private const int Depth = 3;
    private const int UnloadSeriesSize = 9;
    private readonly object _sync = new();
    private readonly ulong _initialSeed = CreateSeed();
    private readonly List<SimulationState> _history = [];
    private SimulationSettings _settings = new(
        TimeSpan.FromSeconds(5),
        TimeSpan.FromMinutes(5),
        TimeSpan.FromSeconds(5));
    private SimulationState _state;
    private DateTimeOffset _lastAutoUtc = DateTimeOffset.UtcNow;
    private int _cursor;
    private bool _isRunning = true;

    public SimulationEngine()
    {
        _state = CreateInitialState(_initialSeed);
        Log("info", "Start symulacji.");
        SaveHistory();
    }

    public void Tick()
    {
        lock (_sync)
        {
            AdvanceAutoClock();
        }
    }

    public SimulationSnapshotDto GetSnapshot()
    {
        lock (_sync)
        {
            AdvanceAutoClock();
            return ToDto();
        }
    }

    public SimulationSnapshotDto Play()
    {
        lock (_sync)
        {
            if (_state.IsBlocked)
            {
                _isRunning = false;
                return ToDto();
            }

            if (_cursor < _history.Count - 1)
            {
                TrimFuture();
            }

            _isRunning = true;
            _lastAutoUtc = DateTimeOffset.UtcNow;
            Log("control", "Wznowiono czas.");
            SaveHistory();
            return ToDto();
        }
    }

    public SimulationSnapshotDto Pause()
    {
        lock (_sync)
        {
            AdvanceAutoClock();

            if (_isRunning)
            {
                _isRunning = false;
                Log("control", "Zatrzymano czas.");
                SaveHistory();
            }

            return ToDto();
        }
    }

    public SimulationSnapshotDto StepPrevious()
    {
        lock (_sync)
        {
            AdvanceAutoClock();
            _isRunning = false;

            if (_cursor > 0)
            {
                _cursor--;
                _state = _history[_cursor].Clone();
            }

            _lastAutoUtc = DateTimeOffset.UtcNow;
            return ToDto();
        }
    }

    public SimulationSnapshotDto StepNext()
    {
        lock (_sync)
        {
            AdvanceAutoClock();
            _isRunning = false;

            if (_cursor < _history.Count - 1)
            {
                _cursor++;
                _state = _history[_cursor].Clone();
                _lastAutoUtc = DateTimeOffset.UtcNow;
                return ToDto();
            }

            AdvanceOneManualStep();
            SaveHistory();
            _lastAutoUtc = DateTimeOffset.UtcNow;
            return ToDto();
        }
    }

    public SimulationSnapshotDto Reset()
    {
        lock (_sync)
        {
            _state = CreateInitialState(CreateSeed());
            _history.Clear();
            _cursor = 0;
            _isRunning = false;
            _lastAutoUtc = DateTimeOffset.UtcNow;
            Log("control", "Zresetowano symulacje.");
            SaveHistory();
            return ToDto();
        }
    }

    public SimulationSnapshotDto UpdateSettings(
        TimeSpan loadInterval,
        TimeSpan unloadCycleInterval,
        TimeSpan unloadStackInterval)
    {
        lock (_sync)
        {
            AdvanceAutoClock();

            _settings = new SimulationSettings(loadInterval, unloadCycleInterval, unloadStackInterval);
            if (_state.IsUnloadCycleActive)
            {
                _state.NextLoadAt = _state.CurrentTime + _settings.LoadInterval;
                _state.NextUnloadStackAt = _state.CurrentTime + _settings.UnloadStackInterval;
            }
            else
            {
                _state.NextUnloadCycleAt = _state.CurrentTime + _settings.UnloadCycleInterval;
                _state.NextLoadAt = _state.IsLoadWaitingForUnload
                    ? _state.NextUnloadCycleAt
                    : _state.CurrentTime + _settings.LoadInterval;
            }

            _state.LastWaitingReadyCount = null;

            if (_cursor < _history.Count - 1)
            {
                TrimFuture();
            }

            Log(
                "settings",
                $"Ustawiono zaladunek co {FormatSeconds(loadInterval)} s, cykl rozladunku co {FormatSeconds(unloadCycleInterval)} s i stack w cyklu co {FormatSeconds(unloadStackInterval)} s.");
            SaveHistory();
            _lastAutoUtc = DateTimeOffset.UtcNow;
            return ToDto();
        }
    }

    private void AdvanceAutoClock()
    {
        var now = DateTimeOffset.UtcNow;

        if (!_isRunning || _cursor < _history.Count - 1)
        {
            _lastAutoUtc = now;
            return;
        }

        var delta = now - _lastAutoUtc;
        _lastAutoUtc = now;

        if (delta <= TimeSpan.Zero)
        {
            return;
        }

        _state.CurrentTime += delta;

        if (ProcessDueEvents())
        {
            SaveHistory();
        }
    }

    private void AdvanceOneManualStep()
    {
        if (_state.IsBlocked)
        {
            return;
        }

        if (ProcessDueEvents())
        {
            return;
        }

        var nextTime = NextManualEventTime();
        if (nextTime > _state.CurrentTime)
        {
            _state.CurrentTime = nextTime;
        }

        if (!ProcessDueEvents())
        {
            Log("control", "Krok bez zmiany stanu magazynu.");
        }
    }

    private TimeSpan NextManualEventTime()
    {
        if (_state.IsUnloadCycleActive)
        {
            return Min(_state.NextLoadAt, _state.NextUnloadStackAt);
        }

        if (_state.NextUnloadCycleAt <= _state.CurrentTime)
        {
            return _state.NextLoadAt;
        }

        return Min(_state.NextLoadAt, _state.NextUnloadCycleAt);
    }

    private bool ProcessDueEvents()
    {
        if (_state.IsBlocked)
        {
            return false;
        }

        var changed = false;

        for (var guard = 0; guard < 2000; guard++)
        {
            if (_state.IsUnloadCycleActive)
            {
                var loadDue = _state.NextLoadAt <= _state.CurrentTime;
                var stackDue = _state.NextUnloadStackAt <= _state.CurrentTime;

                if (!loadDue && !stackDue)
                {
                    break;
                }

                if (stackDue && (!loadDue || _state.NextUnloadStackAt <= _state.NextLoadAt))
                {
                    changed |= TryUnloadCycleStack(_state.NextUnloadStackAt) != CycleStackAttempt.NoChange;
                    continue;
                }

                if (loadDue)
                {
                    _state.NextLoadAt = _state.CurrentTime + _settings.LoadInterval;
                    changed = true;
                    break;
                }

                break;
            }

            var inactiveLoadDue = _state.NextLoadAt <= _state.CurrentTime;
            var cycleDue = _state.NextUnloadCycleAt <= _state.CurrentTime;

            if (!inactiveLoadDue && !cycleDue)
            {
                break;
            }

            if (inactiveLoadDue && (!cycleDue || _state.NextLoadAt <= _state.NextUnloadCycleAt))
            {
                var eventTime = _state.NextLoadAt;
                if (!LoadAtScheduledTime())
                {
                    changed = true;
                    break;
                }

                changed = true;

                if (_state.NextUnloadCycleAt <= eventTime)
                {
                    changed |= TryStartUnloadCycle(eventTime) != CycleStartAttempt.NoChange;
                }

                continue;
            }

            if (cycleDue)
            {
                var attempt = TryStartUnloadCycle(_state.NextUnloadCycleAt);
                changed |= attempt != CycleStartAttempt.NoChange;

                if (attempt == CycleStartAttempt.Started)
                {
                    continue;
                }

                if (inactiveLoadDue)
                {
                    var eventTime = _state.NextLoadAt;
                    if (!LoadAtScheduledTime())
                    {
                        changed = true;
                        break;
                    }

                    changed = true;

                    if (_state.NextUnloadCycleAt <= eventTime)
                    {
                        changed |= TryStartUnloadCycle(eventTime) != CycleStartAttempt.NoChange;
                    }

                    continue;
                }

                break;
            }
        }

        return changed;
    }

    private bool LoadAtScheduledTime()
    {
        var eventTime = _state.NextLoadAt;
        if (!LoadOnePallet(eventTime))
        {
            return false;
        }

        _state.NextLoadAt += _settings.LoadInterval;
        return true;
    }

    private bool LoadOnePallet(TimeSpan eventTime)
    {
        var pallet = _state.PendingLoadPallet ?? RandomPallet();
        var decision = LoadingOptimizer.ChoosePosition(
            _state.Warehouse,
            pallet,
            eventTime,
            () => NextRandomInt(int.MaxValue));

        if (decision is null)
        {
            if (CanWaitForUnloadCycle())
            {
                EnterLoadWaitBlock(pallet, eventTime);
                return false;
            }

            EnterFinalBlock(pallet, eventTime);
            return false;
        }

        CompleteLoadWaitBlock(eventTime);

        var selected = decision.Position;
        var storedPallet = new StoredPallet(++_state.NextPalletId, pallet, eventTime);
        _state.Warehouse.At(selected).Push(storedPallet);
        _state.LoadedPallets++;
        _state.LastWaitingReadyCount = null;
        Log(
            "load",
            $"Zaladunek {pallet.Code()} #{storedPallet.Id} -> X={selected.DisplayX}, Y={selected.DisplayY}. Regula: {decision.Reason}.",
            eventTime);
        return true;
    }

    private bool CanWaitForUnloadCycle()
    {
        if (_state.IsUnloadCycleActive)
        {
            return _state.ActiveUnloadRemaining > 0;
        }

        var plan = _state.Warehouse.PlanUnloadSeries(UnloadSeriesSize);
        if (plan.Count == UnloadSeriesSize)
        {
            return true;
        }

        return _state.Warehouse.PlanUnloadSeries(UnloadSeriesSize, allowRelief: true).Count == UnloadSeriesSize;
    }

    private void EnterLoadWaitBlock(PalletType pallet, TimeSpan eventTime)
    {
        _state.PendingLoadPallet = pallet;
        _state.BlockMessage = $"blokada - oczekiwanie na cykl rozladunku dla palety {pallet.Code()}";

        if (!_state.IsLoadWaitingForUnload)
        {
            _state.IsLoadWaitingForUnload = true;
            _state.LoadWaitBlockStartedAt = eventTime;
            Log(
                "wait",
                $"{_state.BlockMessage}. Symulacja pracuje dalej i zlicza czas blokady.",
                eventTime);
        }

        if (_state.IsUnloadCycleActive)
        {
            _state.NextLoadAt = eventTime + _settings.LoadInterval;
            return;
        }

        _state.NextLoadAt = _state.NextUnloadCycleAt > eventTime
            ? _state.NextUnloadCycleAt
            : eventTime + _settings.LoadInterval;
    }

    private void CompleteLoadWaitBlock(TimeSpan eventTime)
    {
        if (!_state.IsLoadWaitingForUnload)
        {
            _state.PendingLoadPallet = null;
            return;
        }

        var startedAt = _state.LoadWaitBlockStartedAt ?? eventTime;
        var duration = eventTime > startedAt ? eventTime - startedAt : TimeSpan.Zero;
        _state.TotalLoadWaitBlockTime += duration;
        _state.IsLoadWaitingForUnload = false;
        _state.LoadWaitBlockStartedAt = null;
        _state.PendingLoadPallet = null;
        _state.BlockMessage = null;

        Log(
            "wait",
            $"Koniec blokady oczekiwania: trwala {FormatTime(duration)}, lacznie {FormatTime(_state.TotalLoadWaitBlockTime)}.",
            eventTime);
    }

    private void EnterFinalBlock(PalletType pallet, TimeSpan eventTime)
    {
        if (_state.IsLoadWaitingForUnload)
        {
            var startedAt = _state.LoadWaitBlockStartedAt ?? eventTime;
            if (eventTime > startedAt)
            {
                _state.TotalLoadWaitBlockTime += eventTime - startedAt;
            }
        }

        _state.IsLoadWaitingForUnload = false;
        _state.LoadWaitBlockStartedAt = null;
        _state.PendingLoadPallet = pallet;
        _state.IsBlocked = true;
        _state.BlockMessage = $"blokada ostateczna - brak miejsca na magazynie dla palety {pallet.Code()} i brak cyklu rozladunku, ktory moze odblokowac magazyn";
        _isRunning = false;
        Log("block", _state.BlockMessage, eventTime);
    }

    private CycleStartAttempt TryStartUnloadCycle(TimeSpan eventTime)
    {
        var plan = _state.Warehouse.PlanUnloadSeries(UnloadSeriesSize);
        var usedReliefStacks = false;

        if (plan.Count < UnloadSeriesSize)
        {
            plan = _state.Warehouse.PlanUnloadSeries(UnloadSeriesSize, allowRelief: true);
            usedReliefStacks = plan.Count == UnloadSeriesSize
                && plan.Any(position => !_state.Warehouse.At(position).IsFull);
        }

        if (plan.Count < UnloadSeriesSize)
        {
            if (_state.LastWaitingReadyCount == plan.Count)
            {
                return CycleStartAttempt.NoChange;
            }

            _state.LastWaitingReadyCount = plan.Count;
            Log(
                "wait",
                $"Cykl rozladunku czeka: plan serii {plan.Count}/{UnloadSeriesSize}. Cykl ruszy dopiero, gdy mozna pobrac dokladnie 9 stackow.",
                eventTime);
            return CycleStartAttempt.WaitingLogged;
        }

        _state.IsUnloadCycleActive = true;
        _state.ActiveUnloadCycleStartedAt = eventTime;
        _state.ActiveUnloadCompleted = 0;
        _state.ActiveUnloadRemaining = UnloadSeriesSize;
        _state.ActiveUnloadPlan.Clear();
        _state.ActiveUnloadPlan.AddRange(plan);
        _state.NextUnloadStackAt = eventTime;
        _state.NextUnloadCycleAt = eventTime + _settings.UnloadCycleInterval;
        _state.LastWaitingReadyCount = null;

        var reliefText = usedReliefStacks ? " W planie sa awaryjne stacki 6/7 odblokowujace kolumny." : string.Empty;
        Log("cycle", $"Start cyklu rozladunku: zaplanowano dokladnie {UnloadSeriesSize} stackow.{reliefText}", eventTime);
        return CycleStartAttempt.Started;
    }

    private CycleStackAttempt TryUnloadCycleStack(TimeSpan eventTime)
    {
        if (!_state.IsUnloadCycleActive)
        {
            return CycleStackAttempt.NoChange;
        }

        var candidate = SelectUnloadCandidate(eventTime, _state.ActiveUnloadPlan);

        if (candidate is null)
        {
            Log(
                "wait",
                "Aktywny cykl rozladunku czeka: brak dostepnego pelnego stacka.",
                eventTime);
            _state.NextUnloadStackAt = eventTime + _settings.UnloadStackInterval;
            return CycleStackAttempt.WaitingLogged;
        }

        var stack = _state.Warehouse.At(candidate.Position);
        var removed = stack.Clear();
        _state.ActiveUnloadPlan.Remove(candidate.Position);
        _state.UnloadedStacks++;
        _state.ActiveUnloadCompleted++;
        _state.ActiveUnloadRemaining--;
        _state.LastWaitingReadyCount = null;

        var oldest = removed.OrderBy(pallet => pallet.LoadedAt).First();
        Log(
            "unload",
            $"Cykl {_state.UnloadBatches + 1}: stack {_state.ActiveUnloadCompleted}/{UnloadSeriesSize}, X={candidate.Position.DisplayX}, Y={candidate.Position.DisplayY}. FIFO najstarsza #{oldest.Id} czekala {FormatTime(TimeSpan.FromSeconds(candidate.OldestWaitSeconds))}.",
            eventTime);

        if (_state.ActiveUnloadRemaining == 0)
        {
            _state.IsUnloadCycleActive = false;
            _state.ActiveUnloadPlan.Clear();
            _state.UnloadBatches++;
            if (_state.NextLoadAt <= eventTime)
            {
                _state.NextLoadAt = eventTime + _settings.LoadInterval;
            }

            var duration = eventTime - _state.ActiveUnloadCycleStartedAt;
            Log(
                "cycle",
                $"Zakonczono cykl #{_state.UnloadBatches}: rozladowano 9 stackow w {FormatTime(duration)}.",
                eventTime);
        }
        else
        {
            _state.NextUnloadStackAt = eventTime + _settings.UnloadStackInterval;
        }

        return CycleStackAttempt.Unloaded;
    }

    private UnloadCandidate? SelectUnloadCandidate(TimeSpan now, IReadOnlyCollection<Position>? plannedPositions = null)
    {
        if (plannedPositions is { Count: > 0 })
        {
            var nextPlanned = plannedPositions.First();
            if (!_state.Warehouse.IsUnloadable(nextPlanned, treatedAsEmpty: null, allowRelief: true))
            {
                return null;
            }

            return CreateUnloadCandidate(nextPlanned, now);
        }

        return _state.Warehouse.GetAccessibleUnloadableStacks(allowRelief: true)
            .Where(position => _state.Warehouse.IsUnloadable(position, treatedAsEmpty: null, allowRelief: true))
            .Select(position => CreateUnloadCandidate(position, now))
            .OrderBy(candidate => candidate.OldestLoadedAt)
            .ThenByDescending(candidate => candidate.FifoScore)
            .ThenBy(candidate => candidate.Position.Y)
            .ThenBy(candidate => candidate.Position.X)
            .FirstOrDefault();
    }

    private UnloadCandidate CreateUnloadCandidate(Position position, TimeSpan now)
    {
        var stack = _state.Warehouse.At(position);
        return new UnloadCandidate(
            position,
            stack.OldestLoadedAt ?? TimeSpan.MaxValue,
            stack.OldestWaitSeconds(now),
            stack.FifoScore(now),
            !stack.IsFull);
    }

    private SimulationSnapshotDto ToDto()
    {
        var unloadPlan = _state.Warehouse.PlanUnloadSeries(UnloadSeriesSize);
        if (unloadPlan.Count < UnloadSeriesSize)
        {
            unloadPlan = _state.Warehouse.PlanUnloadSeries(UnloadSeriesSize, allowRelief: true);
        }
        var unloadPlanSet = unloadPlan.ToHashSet();
        var nextUnload = _state.IsUnloadCycleActive ? SelectUnloadCandidate(_state.CurrentTime, _state.ActiveUnloadPlan) : null;
        var accessibleFull = _state.Warehouse.GetAccessibleFullStacks().Count();
        var cells = _state.Warehouse.Positions()
            .Select(position =>
            {
                var stack = _state.Warehouse.At(position);
                var pallets = stack.Pallets
                    .Select(pallet => new PalletDto(
                        pallet.Id,
                        pallet.Code.ToString(),
                        pallet.Height,
                        FormatTime(pallet.LoadedAt),
                        Math.Max(0, (_state.CurrentTime - pallet.LoadedAt).TotalSeconds)))
                    .ToList();

                var order = stack.Pallets.Count == 0
                    ? "pusto"
                    : string.Join(" -> ", stack.Pallets.Select(pallet => $"{pallet.Code}#{pallet.Id}"));

                return new CellDto(
                    position.X,
                    position.Y,
                    position.DisplayX,
                    position.DisplayY,
                    stack.Height,
                    _state.Warehouse.IsAccessible(position),
                    stack.IsFull,
                    _state.Warehouse.IsUnloadable(position, treatedAsEmpty: null, allowRelief: true) && !stack.IsFull,
                    unloadPlanSet.Contains(position),
                    nextUnload is not null && nextUnload.Position == position,
                    stack.Count(PalletType.A),
                    stack.Count(PalletType.B),
                    stack.Count(PalletType.C),
                    stack.OldestWaitSeconds(_state.CurrentTime),
                    stack.FifoScore(_state.CurrentTime),
                    order,
                    pallets);
            })
            .ToList();

        var waitingForUnload = !_state.IsUnloadCycleActive
            && _state.CurrentTime >= _state.NextUnloadCycleAt
            && unloadPlan.Count < UnloadSeriesSize;
        var currentLoadBlockDuration = CurrentLoadWaitBlockDuration();
        var totalLoadBlockDuration = _state.TotalLoadWaitBlockTime + currentLoadBlockDuration;
        var nextUnloadIn = _state.IsUnloadCycleActive
            ? FormatCountdown(_state.NextUnloadStackAt - _state.CurrentTime)
            : waitingForUnload
                ? "czeka"
                : FormatCountdown(_state.NextUnloadCycleAt - _state.CurrentTime);
        var nextLoadIn = _state.IsLoadWaitingForUnload
            ? "czeka"
            : FormatCountdown(_state.NextLoadAt - _state.CurrentTime);
        var mode = _state.IsBlocked
            ? "blocked"
            : _state.IsLoadWaitingForUnload
                ? "waiting"
                : _isRunning
                    ? "running"
                    : "paused";

        return new SimulationSnapshotDto(
            _state.Warehouse.Width,
            _state.Warehouse.Depth,
            mode,
            _isRunning && !_state.IsBlocked,
            _state.IsBlocked,
            _state.BlockMessage,
            _state.IsLoadWaitingForUnload,
            _state.IsBlocked,
            FormatTime(totalLoadBlockDuration),
            totalLoadBlockDuration.TotalSeconds,
            FormatTime(currentLoadBlockDuration),
            currentLoadBlockDuration.TotalSeconds,
            _state.PendingLoadPallet?.Code().ToString(),
            FormatTime(_state.CurrentTime),
            _state.CurrentTime.TotalSeconds,
            _settings.LoadInterval.TotalSeconds,
            _settings.UnloadCycleInterval.TotalSeconds,
            _settings.UnloadCycleInterval.TotalSeconds,
            _settings.UnloadStackInterval.TotalSeconds,
            nextLoadIn,
            nextUnloadIn,
            waitingForUnload ? "czeka" : FormatCountdown(_state.NextUnloadCycleAt - _state.CurrentTime),
            _state.IsUnloadCycleActive
                ? FormatCountdown(_state.NextUnloadStackAt - _state.CurrentTime)
                : "-",
            _state.IsUnloadCycleActive,
            _state.ActiveUnloadCompleted,
            _state.ActiveUnloadRemaining,
            waitingForUnload,
            _state.LoadedPallets,
            _state.UnloadedStacks,
            _state.UnloadBatches,
            _state.Warehouse.CountFullStacks(),
            accessibleFull,
            unloadPlan.Count,
            UnloadSeriesSize,
            _cursor + 1,
            _history.Count,
            _cursor > 0,
            true,
            cells,
            _state.Events
                .OrderByDescending(item => item.Number)
                .Take(30)
                .Select(item => new SimulationEventDto(item.Number, FormatTime(item.Time), item.Kind, item.Text))
                .ToList());
    }

    private TimeSpan CurrentLoadWaitBlockDuration()
    {
        if (!_state.IsLoadWaitingForUnload || _state.LoadWaitBlockStartedAt is null)
        {
            return TimeSpan.Zero;
        }

        return _state.CurrentTime > _state.LoadWaitBlockStartedAt.Value
            ? _state.CurrentTime - _state.LoadWaitBlockStartedAt.Value
            : TimeSpan.Zero;
    }

    private void SaveHistory()
    {
        if (_cursor < _history.Count - 1)
        {
            TrimFuture();
        }

        _history.Add(_state.Clone());
        _cursor = _history.Count - 1;

        const int maxHistory = 500;
        if (_history.Count <= maxHistory)
        {
            return;
        }

        var removeCount = _history.Count - maxHistory;
        _history.RemoveRange(0, removeCount);
        _cursor -= removeCount;
    }

    private void TrimFuture()
    {
        if (_cursor < _history.Count - 1)
        {
            _history.RemoveRange(_cursor + 1, _history.Count - _cursor - 1);
        }
    }

    private void Log(string kind, string text)
    {
        Log(kind, text, _state.CurrentTime);
    }

    private void Log(string kind, string text, TimeSpan eventTime)
    {
        _state.EventSerial++;
        _state.Events.Add(new SimulationEvent(_state.EventSerial, eventTime, kind, text));

        const int maxEvents = 80;
        if (_state.Events.Count > maxEvents)
        {
            _state.Events.RemoveRange(0, _state.Events.Count - maxEvents);
        }
    }

    private PalletType RandomPallet()
    {
        var values = Enum.GetValues<PalletType>();
        return values[NextRandomInt(values.Length)];
    }

    private int NextRandomInt(int maxExclusive)
    {
        _state.RandomState = NextRandomState(_state.RandomState);
        return (int)(_state.RandomState % (ulong)maxExclusive);
    }

    private SimulationState CreateInitialState(ulong seed)
    {
        return new SimulationState
        {
            Warehouse = new Warehouse(Width, Depth),
            CurrentTime = TimeSpan.Zero,
            NextLoadAt = _settings.LoadInterval,
            NextUnloadCycleAt = _settings.UnloadCycleInterval,
            NextUnloadStackAt = TimeSpan.Zero,
            RandomState = seed == 0 ? _initialSeed : seed
        };
    }

    private static ulong CreateSeed()
    {
        var seed = unchecked((ulong)DateTimeOffset.UtcNow.Ticks ^ 0x9E3779B97F4A7C15UL);
        return seed == 0 ? 0xA0761D6478BD642FUL : seed;
    }

    private static ulong NextRandomState(ulong state)
    {
        state ^= state << 13;
        state ^= state >> 7;
        state ^= state << 17;
        return state == 0 ? 0xA0761D6478BD642FUL : state;
    }

    private static TimeSpan Min(TimeSpan first, TimeSpan second)
    {
        return first <= second ? first : second;
    }

    private static string FormatTime(TimeSpan value)
    {
        var rounded = TimeSpan.FromMilliseconds(Math.Max(0, Math.Round(value.TotalMilliseconds)));
        var hasMilliseconds = rounded.Milliseconds != 0;

        if (rounded.TotalHours >= 1)
        {
            return rounded.ToString(
                hasMilliseconds ? @"hh\:mm\:ss\.fff" : @"hh\:mm\:ss",
                CultureInfo.InvariantCulture);
        }

        return rounded.ToString(
            hasMilliseconds ? @"mm\:ss\.fff" : @"mm\:ss",
            CultureInfo.InvariantCulture);
    }

    private static string FormatCountdown(TimeSpan value)
    {
        return value <= TimeSpan.Zero ? "00:00" : FormatTime(value);
    }

    private static string FormatSeconds(TimeSpan value)
    {
        return value.TotalSeconds.ToString("0.###", CultureInfo.InvariantCulture);
    }
}

internal sealed record SimulationSettings(
    TimeSpan LoadInterval,
    TimeSpan UnloadCycleInterval,
    TimeSpan UnloadStackInterval);

internal sealed record SimulationEvent(int Number, TimeSpan Time, string Kind, string Text);

internal sealed record UnloadCandidate(
    Position Position,
    TimeSpan OldestLoadedAt,
    double OldestWaitSeconds,
    double FifoScore,
    bool IsRelief);

internal enum CycleStartAttempt
{
    NoChange,
    WaitingLogged,
    Started
}

internal enum CycleStackAttempt
{
    NoChange,
    WaitingLogged,
    Unloaded
}

internal sealed class SimulationState
{
    public required Warehouse Warehouse { get; init; }

    public TimeSpan CurrentTime { get; set; }

    public TimeSpan NextLoadAt { get; set; }

    public TimeSpan NextUnloadCycleAt { get; set; }

    public TimeSpan NextUnloadStackAt { get; set; }

    public bool IsUnloadCycleActive { get; set; }

    public TimeSpan ActiveUnloadCycleStartedAt { get; set; }

    public int ActiveUnloadCompleted { get; set; }

    public int ActiveUnloadRemaining { get; set; }

    public List<Position> ActiveUnloadPlan { get; } = [];

    public bool IsBlocked { get; set; }

    public string? BlockMessage { get; set; }

    public bool IsLoadWaitingForUnload { get; set; }

    public TimeSpan? LoadWaitBlockStartedAt { get; set; }

    public TimeSpan TotalLoadWaitBlockTime { get; set; }

    public PalletType? PendingLoadPallet { get; set; }

    public int LoadedPallets { get; set; }

    public int UnloadedStacks { get; set; }

    public int UnloadBatches { get; set; }

    public int? LastWaitingReadyCount { get; set; }

    public int EventSerial { get; set; }

    public long NextPalletId { get; set; }

    public ulong RandomState { get; set; }

    public List<SimulationEvent> Events { get; } = [];

    public SimulationState Clone()
    {
        var clone = new SimulationState
        {
            Warehouse = Warehouse.Clone(),
            CurrentTime = CurrentTime,
            NextLoadAt = NextLoadAt,
            NextUnloadCycleAt = NextUnloadCycleAt,
            NextUnloadStackAt = NextUnloadStackAt,
            IsUnloadCycleActive = IsUnloadCycleActive,
            ActiveUnloadCycleStartedAt = ActiveUnloadCycleStartedAt,
            ActiveUnloadCompleted = ActiveUnloadCompleted,
            ActiveUnloadRemaining = ActiveUnloadRemaining,
            IsBlocked = IsBlocked,
            BlockMessage = BlockMessage,
            IsLoadWaitingForUnload = IsLoadWaitingForUnload,
            LoadWaitBlockStartedAt = LoadWaitBlockStartedAt,
            TotalLoadWaitBlockTime = TotalLoadWaitBlockTime,
            PendingLoadPallet = PendingLoadPallet,
            LoadedPallets = LoadedPallets,
            UnloadedStacks = UnloadedStacks,
            UnloadBatches = UnloadBatches,
            LastWaitingReadyCount = LastWaitingReadyCount,
            EventSerial = EventSerial,
            NextPalletId = NextPalletId,
            RandomState = RandomState
        };

        clone.ActiveUnloadPlan.AddRange(ActiveUnloadPlan);
        clone.Events.AddRange(Events);
        return clone;
    }
}
