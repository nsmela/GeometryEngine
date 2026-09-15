using System.Runtime.ExceptionServices;

namespace GeometryEngine.Internal.Csg;

/// <summary>
/// Runs a piece of work on a worker thread with a large reserved stack.
///
/// The BSP tree operations (<see cref="BspNode.Insert"/>, <see cref="BspNode.ClippedTo"/>,
/// <see cref="BspNode.Inverted"/> and their helpers) recurse to the depth of the tree.
/// The divider heuristic keeps that depth near logarithmic for well-distributed input,
/// but finely tessellated or adversarial meshes still build trees thousands of levels
/// deep — enough to exhaust the default 1 MB thread stack and bring the whole process
/// down with an uncatchable <see cref="StackOverflowException"/>. A library that models
/// every other failure as a value must not have a moderate sphere subtract crash the host.
///
/// Reserving a large stack (address space only; pages are committed lazily) raises the
/// ceiling by two orders of magnitude, which covers any realistic mesh. It does not make
/// the depth unbounded, so a truly degenerate input could still overflow; a fully
/// iterative kernel would remove the last of the risk and is left as follow-up work.
/// </summary>
internal static class DeepStack
{
    private const int StackBytes = 256 * 1024 * 1024;

    public static T Run<T>(Func<T> work)
    {
        T result = default!;
        ExceptionDispatchInfo? failure = null;

        var worker = new Thread(
            () =>
            {
                try
                {
                    result = work();
                }
                catch (Exception exception)
                {
                    failure = ExceptionDispatchInfo.Capture(exception);
                }
            },
            StackBytes);

        worker.Start();
        worker.Join();

        failure?.Throw();
        return result;
    }
}
