using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Xunit;

namespace OnCourse.ABConnect.Tests;

/// <summary>
/// Compiles every C# sample in the package README against this build of the SDK.
/// </summary>
/// <remarks>
/// <para>
/// Version 2's README documented <c>services.AddABConnect(configuration)</c>, a method that did not
/// exist in the package. Anyone who followed the quickstart got a compile error on the first line they
/// pasted. A sample is documentation only if it is true, and the cheapest way to keep it true is to
/// compile it on every build, so a fence that drifts from the real API fails here rather than in
/// somebody's editor.
/// </para>
/// <para>
/// Each fence is a separate test case, so a failure names the markdown line that broke and prints the
/// compiler's own diagnostic against the README's line numbering rather than against a generated file
/// nobody can see.
/// </para>
/// </remarks>
public sealed class ReadmeSampleTests
{
    /// <summary>Diagnostics a documentation fence is allowed to produce.</summary>
    /// <remarks>
    /// CS5001 is "no static Main". Every fence compiles as its own top-level program so a fence may be
    /// a sequence of statements, a set of declarations, or both; a fence that declares types and
    /// contains no statements has no entry point, which is not a documentation defect.
    /// </remarks>
    private static readonly string[] AllowedErrorIds = ["CS5001"];

    /// <summary>
    /// Usings prepended to every fence so samples read as documentation rather than as compilable
    /// files. Namespaces a reader has to be told about, above all <c>OnCourse.ABConnect</c> itself, are
    /// deliberately absent: the README must show its own usings, and this test must fail if it shows
    /// the wrong ones.
    /// </summary>
    private static readonly string[] Prelude =
    [
        "using System;",
        "using System.Collections.Generic;",
        "using System.Linq;",
        "using System.Threading;",
        "using System.Threading.Tasks;",
    ];

    /// <summary>Every C# fence in the README, one test case each.</summary>
    public static TheoryData<int, int, string> Fences
    {
        get
        {
            TheoryData<int, int, string> data = new();

            foreach (var fence in ExtractFences(File.ReadAllLines(ReadmePath())))
            {
                data.Add(fence.Ordinal, fence.StartLine, fence.Code);
            }

            return data;
        }
    }

    [Theory]
    [MemberData(nameof(Fences))]
    public void EveryReadmeSampleCompilesAgainstTheBuiltAssembly(int ordinal, int startLine, string code)
    {
        var references = LoadReferences();

        Assert.NotEmpty(references);

        var source = new StringBuilder();

        foreach (var line in Prelude)
        {
            source.AppendLine(line);
        }

        source.Append(code);

        var syntaxTree = CSharpSyntaxTree.ParseText(
            source.ToString(),
            new CSharpParseOptions(LanguageVersion.Latest));

        var compilation = CSharpCompilation.Create(
            $"ReadmeFence{ordinal}",
            [syntaxTree],
            references,
            new CSharpCompilationOptions(
                OutputKind.ConsoleApplication,
                nullableContextOptions: NullableContextOptions.Enable));

        using var peStream = new MemoryStream();
        var emitResult = compilation.Emit(peStream);

        List<string> errors = [];

        foreach (var diagnostic in emitResult.Diagnostics)
        {
            if (diagnostic.Severity != DiagnosticSeverity.Error || AllowedErrorIds.Contains(diagnostic.Id))
            {
                continue;
            }

            // Map the reported line back to the line the reader sees in the markdown file.
            var position = diagnostic.Location.GetLineSpan().StartLinePosition;
            var markdownLine = startLine + (position.Line - Prelude.Length) + 1;
            var message = diagnostic.GetMessage(CultureInfo.InvariantCulture);

            errors.Add($"README.md({markdownLine},{position.Character + 1}): {diagnostic.Id}: {message}");
        }

        Assert.True(
            errors.Count == 0,
            $"README.md fence {ordinal}, opened at line {startLine}, does not compile:"
                + Environment.NewLine
                + string.Join(Environment.NewLine, errors)
                + Environment.NewLine
                + Indent(code));
    }

    /// <summary>The README carries samples, and a README with none of them is itself a defect.</summary>
    [Fact]
    public void TheReadmeCarriesSamples()
    {
        var fences = ExtractFences(File.ReadAllLines(ReadmePath()));

        Assert.NotEmpty(fences);
    }

    /// <summary>
    /// Locates the repository README.
    /// </summary>
    /// <param name="thisFile">
    /// Supplied by the compiler. Do not pass it. The test output directory is not reliably inside the
    /// repository, because <c>--artifacts-path</c> can put it anywhere, so the walk starts from this
    /// source file's own location and only falls back to the output directory.
    /// </param>
    /// <returns>The absolute path of the README.</returns>
    private static string ReadmePath([CallerFilePath] string thisFile = "")
    {
        return FindAbove(Path.GetDirectoryName(thisFile))
            ?? FindAbove(AppContext.BaseDirectory)
            ?? throw new InvalidOperationException(
                $"No README.md found above either '{thisFile}' or '{AppContext.BaseDirectory}'.");

        static string? FindAbove(string? start)
        {
            if (string.IsNullOrEmpty(start) || !Directory.Exists(start))
            {
                return null;
            }

            var directory = new DirectoryInfo(start);

            while (directory is not null)
            {
                var candidate = Path.Combine(directory.FullName, "README.md");

                if (File.Exists(candidate))
                {
                    return candidate;
                }

                directory = directory.Parent;
            }

            return null;
        }
    }

    /// <summary>
    /// Every managed assembly this test process was launched with, which is the SDK plus its transitive
    /// dependencies plus the shared framework. Using the running set rather than a hand-maintained list
    /// is what makes this compile against the real assembly rather than a stale copy of its API.
    /// </summary>
    private static List<MetadataReference> LoadReferences()
    {
        var trustedPlatformAssemblies = AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES") as string ?? string.Empty;
        List<MetadataReference> references = [];
        HashSet<string> seen = new(StringComparer.OrdinalIgnoreCase);

        foreach (var path in trustedPlatformAssemblies.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            if (!path.EndsWith(".dll", StringComparison.OrdinalIgnoreCase) || !File.Exists(path))
            {
                continue;
            }

            if (!seen.Add(Path.GetFileNameWithoutExtension(path)))
            {
                continue;
            }

            references.Add(MetadataReference.CreateFromFile(path));
        }

        return references;
    }

    /// <summary>Pulls every <c>csharp</c> or <c>cs</c> fenced block out of a markdown file.</summary>
    private static List<Fence> ExtractFences(string[] lines)
    {
        List<Fence> fences = [];
        var ordinal = 0;

        for (var i = 0; i < lines.Length; i++)
        {
            var opening = lines[i].TrimEnd();

            if (!opening.StartsWith("```", StringComparison.Ordinal))
            {
                continue;
            }

            var language = opening[3..].Trim();
            var isCSharp = language.Equals("csharp", StringComparison.OrdinalIgnoreCase)
                || language.Equals("cs", StringComparison.OrdinalIgnoreCase);

            var body = new StringBuilder();
            var startLine = i + 1;
            var closed = false;

            for (i++; i < lines.Length; i++)
            {
                if (lines[i].TrimEnd() == "```")
                {
                    closed = true;
                    break;
                }

                body.AppendLine(lines[i]);
            }

            Assert.True(closed, $"README.md has an unterminated fence opened at line {startLine}.");

            if (isCSharp)
            {
                fences.Add(new Fence(++ordinal, startLine, body.ToString()));
            }
        }

        return fences;
    }

    private static string Indent(string code)
        => string.Join(
            Environment.NewLine,
            code.Replace("\r\n", "\n", StringComparison.Ordinal)
                .Split('\n')
                .Select(static line => "    | " + line));

    /// <summary>One fenced C# block, with the markdown line its opening fence sits on.</summary>
    private sealed record Fence(int Ordinal, int StartLine, string Code);
}
