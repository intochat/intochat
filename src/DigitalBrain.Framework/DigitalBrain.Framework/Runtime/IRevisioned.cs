namespace DigitalBrain.Framework;

public interface IRevisioned<TSelf> where TSelf : IRevisioned<TSelf>
{
    long Revision { get; }

    // Applied operation ids, newest last, at most 64.
    string[] Operations { get; }

    // Revision + 1 with the id recorded.
    TSelf Advanced(string operationId);
}
