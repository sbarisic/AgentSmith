namespace LarchQueue;

// Source-only acceptance fixture, not a compiled project.
public static class QueuePolicy
{
    public const int Capacity = 23;
    public const string Ordering = "FIFO";
    public static bool CanEnqueue(int count) => count < Capacity;
}
