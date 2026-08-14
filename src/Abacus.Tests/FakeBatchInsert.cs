using Abacus;

namespace Abacus.Tests;

/// <summary>Test double for IBatchInsert: records staged rows instead of hitting a real database.</summary>
public sealed class FakeBatchInsert : IBatchInsert
{
    public List<Dictionary<int, object?>> Rows { get; } = new();
    private Dictionary<int, object?> current = new();

    public void SetString(int index, string? value) => current[index] = value;
    public void SetInt(int index, int value) => current[index] = value;
    public void SetDouble(int index, double value) => current[index] = value;

    public void AddBatch()
    {
        Rows.Add(current);
        current = new Dictionary<int, object?>();
    }
}
