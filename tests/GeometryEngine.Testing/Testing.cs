using System.Diagnostics;
using System.Reflection;
using System.Runtime.CompilerServices;

namespace GeometryEngine.Testing;

/// <summary>Marks a parameterless method as a test case.</summary>
[AttributeUsage(AttributeTargets.Method)]
public sealed class FactAttribute : Attribute
{
    /// <summary>When set, the test is reported as skipped and not executed.</summary>
    public string? Skip { get; init; }
}

/// <summary>Groups the tests of one class under a heading in the report.</summary>
[AttributeUsage(AttributeTargets.Class)]
public sealed class SuiteAttribute(string name) : Attribute
{
    public string Name { get; } = name;
}

/// <summary>Raised by <see cref="Check"/> when an expectation is not met.</summary>
public sealed class ExpectationFailed(string message) : Exception(message);

/// <summary>
/// Assertions. Every failure carries the source expression of the actual value,
/// so the report reads like the test that produced it.
/// </summary>
public static class Check
{
    public static void True(bool condition, [CallerArgumentExpression(nameof(condition))] string expression = "")
    {
        if (!condition)
        {
            throw new ExpectationFailed($"expected {expression} to be true");
        }
    }

    public static void False(bool condition, [CallerArgumentExpression(nameof(condition))] string expression = "")
    {
        if (condition)
        {
            throw new ExpectationFailed($"expected {expression} to be false");
        }
    }

    public static void Equal<T>(T expected, T actual, [CallerArgumentExpression(nameof(actual))] string expression = "")
    {
        if (!EqualityComparer<T>.Default.Equals(expected, actual))
        {
            throw new ExpectationFailed($"expected {expression} to be {expected}, but it was {actual}");
        }
    }

    public static void NotEqual<T>(T unexpected, T actual, [CallerArgumentExpression(nameof(actual))] string expression = "")
    {
        if (EqualityComparer<T>.Default.Equals(unexpected, actual))
        {
            throw new ExpectationFailed($"expected {expression} to differ from {unexpected}");
        }
    }

    /// <summary>Absolute-tolerance comparison of two doubles.</summary>
    public static void Close(double expected, double actual, double tolerance, [CallerArgumentExpression(nameof(actual))] string expression = "")
    {
        if (double.IsNaN(actual) || Math.Abs(expected - actual) > tolerance)
        {
            throw new ExpectationFailed(
                $"expected {expression} to be {expected:G10} +/- {tolerance:G3}, but it was {actual:G10} (off by {Math.Abs(expected - actual):G4})");
        }
    }

    /// <summary>Relative-tolerance comparison, for quantities whose scale varies.</summary>
    public static void RelativelyClose(double expected, double actual, double relativeTolerance, [CallerArgumentExpression(nameof(actual))] string expression = "")
    {
        var allowed = Math.Abs(expected) * relativeTolerance;
        if (double.IsNaN(actual) || Math.Abs(expected - actual) > allowed)
        {
            var relative = Math.Abs(expected) < double.Epsilon ? double.NaN : Math.Abs(expected - actual) / Math.Abs(expected);
            throw new ExpectationFailed(
                $"expected {expression} to be within {relativeTolerance:P3} of {expected:G10}, but it was {actual:G10} (off by {relative:P4})");
        }
    }

    public static void Greater(double actual, double bound, [CallerArgumentExpression(nameof(actual))] string expression = "")
    {
        if (!(actual > bound))
        {
            throw new ExpectationFailed($"expected {expression} to be greater than {bound:G10}, but it was {actual:G10}");
        }
    }

    public static void GreaterOrEqual(double actual, double bound, [CallerArgumentExpression(nameof(actual))] string expression = "")
    {
        if (!(actual >= bound))
        {
            throw new ExpectationFailed($"expected {expression} to be at least {bound:G10}, but it was {actual:G10}");
        }
    }

    public static void Less(double actual, double bound, [CallerArgumentExpression(nameof(actual))] string expression = "")
    {
        if (!(actual < bound))
        {
            throw new ExpectationFailed($"expected {expression} to be less than {bound:G10}, but it was {actual:G10}");
        }
    }

    public static void LessOrEqual(double actual, double bound, [CallerArgumentExpression(nameof(actual))] string expression = "")
    {
        if (!(actual <= bound))
        {
            throw new ExpectationFailed($"expected {expression} to be at most {bound:G10}, but it was {actual:G10}");
        }
    }

    public static TException Throws<TException>(Action action)
        where TException : Exception
    {
        try
        {
            action();
        }
        catch (TException expected)
        {
            return expected;
        }
        catch (Exception unexpected)
        {
            throw new ExpectationFailed($"expected {typeof(TException).Name}, but got {unexpected.GetType().Name}: {unexpected.Message}");
        }

        throw new ExpectationFailed($"expected {typeof(TException).Name}, but nothing was thrown");
    }
}

/// <summary>One executed test and what happened to it.</summary>
public sealed record TestOutcome(string Suite, string Name, TestStatus Status, TimeSpan Duration, string Detail)
{
    public static TestOutcome Passed(string suite, string name, TimeSpan duration) =>
        new(suite, name, TestStatus.Passed, duration, string.Empty);

    public static TestOutcome Failed(string suite, string name, TimeSpan duration, string detail) =>
        new(suite, name, TestStatus.Failed, duration, detail);

    public static TestOutcome Skipped(string suite, string name, string reason) =>
        new(suite, name, TestStatus.Skipped, TimeSpan.Zero, reason);
}

public enum TestStatus
{
    Passed,
    Failed,
    Skipped,
}

/// <summary>Discovers and runs every <see cref="FactAttribute"/> in an assembly.</summary>
public static class TestRunner
{
    private const string Reset = "\u001b[0m";
    private const string Green = "\u001b[32m";
    private const string Red = "\u001b[31m";
    private const string Yellow = "\u001b[33m";
    private const string Grey = "\u001b[90m";
    private const string Bold = "\u001b[1m";
    private const string Cyan = "\u001b[36m";

    /// <summary>Runs every discovered test and returns a process exit code.</summary>
    public static int Run(Assembly assembly, string? filter = null)
    {
        var suites = assembly.GetTypes()
            .Where(type => type is { IsClass: true, IsAbstract: false })
            .Select(type => (Type: type, Tests: DiscoverTests(type)))
            .Where(candidate => candidate.Tests.Count > 0)
            .OrderBy(candidate => SuiteNameOf(candidate.Type), StringComparer.Ordinal)
            .ToList();

        var outcomes = new List<TestOutcome>();
        var stopwatch = Stopwatch.StartNew();

        Console.WriteLine();
        Console.WriteLine($"{Bold}GeometryEngine test run{Reset} {Grey}({assembly.GetName().Name}, .NET {Environment.Version}){Reset}");
        Console.WriteLine();

        foreach (var (type, tests) in suites)
        {
            var suiteName = SuiteNameOf(type);
            Console.WriteLine($"{Cyan}{suiteName}{Reset}");

            foreach (var test in tests)
            {
                if (filter is not null && !test.Name.Contains(filter, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                var outcome = Execute(type, test, suiteName);
                outcomes.Add(outcome);
                Console.WriteLine(Describe(outcome));
            }

            Console.WriteLine();
        }

        stopwatch.Stop();
        return Report(outcomes, stopwatch.Elapsed);
    }

    private static List<MethodInfo> DiscoverTests(Type type) =>
        type.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static)
            .Where(method => method.GetCustomAttribute<FactAttribute>() is not null)
            .Where(method => method.GetParameters().Length == 0)
            .OrderBy(method => method.Name, StringComparer.Ordinal)
            .ToList();

    private static string SuiteNameOf(Type type) =>
        type.GetCustomAttribute<SuiteAttribute>()?.Name ?? type.Name;

    private static TestOutcome Execute(Type type, MethodInfo method, string suiteName)
    {
        var name = Humanise(method.Name);
        var fact = method.GetCustomAttribute<FactAttribute>()!;
        if (!string.IsNullOrWhiteSpace(fact.Skip))
        {
            return TestOutcome.Skipped(suiteName, name, fact.Skip!);
        }

        var stopwatch = Stopwatch.StartNew();
        try
        {
            var instance = method.IsStatic ? null : Activator.CreateInstance(type);
            method.Invoke(instance, null);
            stopwatch.Stop();
            return TestOutcome.Passed(suiteName, name, stopwatch.Elapsed);
        }
        catch (TargetInvocationException invocation) when (invocation.InnerException is not null)
        {
            stopwatch.Stop();
            var inner = invocation.InnerException;
            var detail = inner is ExpectationFailed ? inner.Message : $"{inner.GetType().Name}: {inner.Message}";
            return TestOutcome.Failed(suiteName, name, stopwatch.Elapsed, detail);
        }
        catch (Exception unexpected)
        {
            stopwatch.Stop();
            return TestOutcome.Failed(suiteName, name, stopwatch.Elapsed, $"{unexpected.GetType().Name}: {unexpected.Message}");
        }
    }

    private static string Describe(TestOutcome outcome) => outcome.Status switch
    {
        TestStatus.Passed => $"  {Green}PASS{Reset}  {outcome.Name} {Grey}{outcome.Duration.TotalMilliseconds,7:0.0} ms{Reset}",
        TestStatus.Skipped => $"  {Yellow}SKIP{Reset}  {outcome.Name} {Grey}({outcome.Detail}){Reset}",
        _ => $"  {Red}FAIL{Reset}  {outcome.Name} {Grey}{outcome.Duration.TotalMilliseconds,7:0.0} ms{Reset}\n        {Red}{outcome.Detail}{Reset}",
    };

    private static int Report(IReadOnlyList<TestOutcome> outcomes, TimeSpan elapsed)
    {
        var passed = outcomes.Count(outcome => outcome.Status == TestStatus.Passed);
        var failed = outcomes.Count(outcome => outcome.Status == TestStatus.Failed);
        var skipped = outcomes.Count(outcome => outcome.Status == TestStatus.Skipped);

        if (failed > 0)
        {
            Console.WriteLine($"{Bold}{Red}Failures{Reset}");
            foreach (var outcome in outcomes.Where(outcome => outcome.Status == TestStatus.Failed))
            {
                Console.WriteLine($"  {Red}x{Reset} {outcome.Suite} / {outcome.Name}");
                Console.WriteLine($"      {outcome.Detail}");
            }

            Console.WriteLine();
        }

        var banner = failed == 0 ? $"{Green}{Bold}ALL GREEN{Reset}" : $"{Red}{Bold}RED{Reset}";
        Console.WriteLine($"{banner}  {passed} passed, {failed} failed, {skipped} skipped in {elapsed.TotalSeconds:0.00} s");
        Console.WriteLine();

        return failed == 0 ? 0 : 1;
    }

    /// <summary>Turns <c>Union_OfDisjointCubes_SumsVolumes</c> into readable prose.</summary>
    private static string Humanise(string methodName) => methodName.Replace('_', ' ');
}
