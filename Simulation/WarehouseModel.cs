namespace Wms.Simulation;

internal enum PalletType
{
    A,
    B,
    C
}

internal static class PalletTypeExtensions
{
    public static int Height(this PalletType pallet)
    {
        return pallet switch
        {
            PalletType.A => 1,
            PalletType.B => 2,
            PalletType.C => 3,
            _ => throw new ArgumentOutOfRangeException(nameof(pallet), pallet, null)
        };
    }

    public static char Code(this PalletType pallet)
    {
        return pallet switch
        {
            PalletType.A => 'A',
            PalletType.B => 'B',
            PalletType.C => 'C',
            _ => '?'
        };
    }
}

internal sealed record StoredPallet(long Id, PalletType Type, TimeSpan LoadedAt)
{
    public int Height => Type.Height();

    public char Code => Type.Code();
}

internal readonly record struct Position(int X, int Y, int Depth)
{
    public int DisplayX => X + 1;

    public int DisplayY => Depth - Y;
}

internal sealed class PalletStack
{
    private readonly int _maxHeight;
    private readonly List<StoredPallet> _pallets;

    public PalletStack(int maxHeight)
        : this(maxHeight, [])
    {
    }

    private PalletStack(int maxHeight, IEnumerable<StoredPallet> pallets)
    {
        _maxHeight = maxHeight;
        _pallets = [.. pallets];
        Height = _pallets.Sum(pallet => pallet.Height);
    }

    public int Height { get; private set; }

    public bool IsEmpty => _pallets.Count == 0;

    public bool IsFull => Height == _maxHeight;

    public StoredPallet? Top => _pallets.Count == 0 ? null : _pallets[^1];

    public IReadOnlyList<StoredPallet> Pallets => _pallets;

    public TimeSpan? OldestLoadedAt => _pallets.Count == 0 ? null : _pallets.Min(pallet => pallet.LoadedAt);

    public double FifoScore(TimeSpan now)
    {
        return _pallets.Sum(pallet => Math.Max(0, (now - pallet.LoadedAt).TotalSeconds));
    }

    public double OldestWaitSeconds(TimeSpan now)
    {
        var oldest = OldestLoadedAt;
        return oldest is null ? 0 : Math.Max(0, (now - oldest.Value).TotalSeconds);
    }

    public int Count(PalletType pallet)
    {
        return _pallets.Count(item => item.Type == pallet);
    }

    public bool CanPush(PalletType pallet)
    {
        var palletHeight = pallet.Height();
        var top = Top;

        return Height + palletHeight <= _maxHeight
            && (top is null || palletHeight <= top.Height);
    }

    public void Push(StoredPallet pallet)
    {
        if (!CanPush(pallet.Type))
        {
            throw new InvalidOperationException($"Pallet {pallet.Code} cannot be stacked here.");
        }

        _pallets.Add(pallet);
        Height += pallet.Height;
    }

    public IReadOnlyList<StoredPallet> Clear()
    {
        var removed = _pallets.ToList();
        _pallets.Clear();
        Height = 0;
        return removed;
    }

    public PalletStack Clone()
    {
        return new PalletStack(_maxHeight, _pallets);
    }
}

internal sealed class Warehouse
{
    public const int MaxStackHeight = 7;
    public const int ReliefUnloadHeight = 6;

    private readonly PalletStack[,] _positions;

    public Warehouse(int width, int depth)
    {
        Width = width;
        Depth = depth;
        _positions = new PalletStack[width, depth];

        for (var x = 0; x < width; x++)
        {
            for (var y = 0; y < depth; y++)
            {
                _positions[x, y] = new PalletStack(MaxStackHeight);
            }
        }
    }

    private Warehouse(int width, int depth, PalletStack[,] positions)
    {
        Width = width;
        Depth = depth;
        _positions = positions;
    }

    public int Width { get; }

    public int Depth { get; }

    public PalletStack At(Position position)
    {
        return _positions[position.X, position.Y];
    }

    public IEnumerable<Position> Positions()
    {
        for (var y = 0; y < Depth; y++)
        {
            for (var x = 0; x < Width; x++)
            {
                yield return new Position(x, y, Depth);
            }
        }
    }

    public IEnumerable<Position> GetLoadCandidates(PalletType pallet)
    {
        return Positions().Where(position =>
            IsAccessible(position)
            && At(position).CanPush(pallet)
            && DoesNotCoverUnreadyDepth(position));
    }

    public IEnumerable<Position> GetAccessibleFullStacks()
    {
        return Positions().Where(position => At(position).IsFull && IsAccessible(position));
    }

    public IEnumerable<Position> GetAccessibleUnloadableStacks(bool allowRelief)
    {
        return Positions().Where(position => IsUnloadable(position, treatedAsEmpty: null, allowRelief));
    }

    public IReadOnlyList<Position> PlanUnloadSeries(int seriesSize, bool allowRelief = false)
    {
        var plan = new List<Position>(seriesSize);
        var treatedAsEmpty = new HashSet<Position>();

        while (plan.Count < seriesSize)
        {
            var next = Positions()
                .Where(position => !treatedAsEmpty.Contains(position))
                .Where(position => IsUnloadable(position, treatedAsEmpty, allowRelief))
                .OrderBy(position => At(position).OldestLoadedAt ?? TimeSpan.MaxValue)
                .ThenByDescending(position => At(position).Height)
                .ThenBy(position => position.Y)
                .ThenBy(position => position.X)
                .ThenBy(position => At(position).IsFull ? 0 : 1)
                .Cast<Position?>()
                .FirstOrDefault();

            if (next is null)
            {
                break;
            }

            plan.Add(next.Value);
            treatedAsEmpty.Add(next.Value);
        }

        return plan;
    }

    public bool IsUnloadable(Position position, IReadOnlySet<Position>? treatedAsEmpty, bool allowRelief)
    {
        if (!IsAccessible(position, treatedAsEmpty))
        {
            return false;
        }

        var stack = At(position);

        return stack.IsFull
            || allowRelief && stack.Height == ReliefUnloadHeight && BlocksSomethingBehind(position, treatedAsEmpty);
    }

    public int CountFullStacks()
    {
        return Positions().Count(position => At(position).IsFull);
    }

    public bool IsAccessible(Position target)
    {
        return IsAccessible(target, treatedAsEmpty: null);
    }

    public bool IsAccessible(Position target, IReadOnlySet<Position>? treatedAsEmpty)
    {
        if (!IsInside(target))
        {
            return false;
        }

        for (var y = target.Y - 1; y >= 0; y--)
        {
            var inFront = new Position(target.X, y, Depth);
            if (treatedAsEmpty?.Contains(inFront) == true)
            {
                continue;
            }

            if (!At(inFront).IsEmpty)
            {
                return false;
            }
        }

        return true;
    }

    public bool IsReliefUnloadCandidate(Position position)
    {
        return At(position).Height == ReliefUnloadHeight && BlocksSomethingBehind(position, treatedAsEmpty: null);
    }

    public Warehouse Clone()
    {
        var clone = new PalletStack[Width, Depth];

        for (var x = 0; x < Width; x++)
        {
            for (var y = 0; y < Depth; y++)
            {
                clone[x, y] = _positions[x, y].Clone();
            }
        }

        return new Warehouse(Width, Depth, clone);
    }

    private bool DoesNotCoverUnreadyDepth(Position position)
    {
        if (!At(position).IsEmpty)
        {
            return true;
        }

        for (var y = position.Y + 1; y < Depth; y++)
        {
            var deeper = At(new Position(position.X, y, Depth));
            if (deeper.Height < ReliefUnloadHeight)
            {
                return false;
            }
        }

        return true;
    }

    private bool BlocksSomethingBehind(Position position, IReadOnlySet<Position>? treatedAsEmpty)
    {
        for (var y = position.Y + 1; y < Depth; y++)
        {
            var behind = new Position(position.X, y, Depth);
            if (treatedAsEmpty?.Contains(behind) == true)
            {
                continue;
            }

            if (!At(behind).IsEmpty)
            {
                return true;
            }
        }

        return false;
    }

    private bool IsInside(Position position)
    {
        return position.X >= 0 && position.X < Width && position.Y >= 0 && position.Y < Depth;
    }
}
