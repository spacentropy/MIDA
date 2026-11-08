using static Mida.Tests.TestRepo;

namespace Mida.Tests;

/// <summary>
/// A seeded layered DAG of projects. Every project's Name() nests its dependencies' names, so a body edit is
/// observable from every test above it, and the generator can say which keys an edit must change.
/// </summary>
internal sealed class GeneratedRepo
{
    public sealed record Project(string Name, int Layer, string[] References, bool IsTest, string Frameworks);

    public string Root { get; }

    public IReadOnlyList<Project> Projects { get; }

    private GeneratedRepo(string root, List<Project> projects)
    {
        Root = root;
        Projects = projects;
    }

    public IEnumerable<Project> Libraries => Projects.Where(p => !p.IsTest);

    /// <summary>Every fourth project above the bottom layer is a test over one library below it. multiTargetEvery = 0 means none.</summary>
#pragma warning disable CA5394 // The graph must be the same on every machine, so it is seeded, not secure.
    public static GeneratedRepo Generate(string root, int count, int seed = 1, int multiTargetEvery = 0, int testEvery = 4)
    {
        var random = new Random(seed);
        var layers = Math.Max(1, (int)Math.Log2(count));
        var projects = new List<Project>();
        for (var i = 0; i < count; i++)
        {
            var layer = i * layers / count;
            var below = projects.Where(p => p.Layer < layer && !p.IsTest).ToArray();
            var isTest = below.Length > 0 && i % testEvery == testEvery - 1;
            var references = below.Length == 0
                ? []
                : below.OrderBy(_ => random.Next()).Take(isTest ? 1 : random.Next(1, 3)).Select(p => p.Name).ToArray();
            var multi = !isTest && multiTargetEvery > 0 && i % multiTargetEvery == 0;
            projects.Add(new($"{(isTest ? 'T' : 'P')}{i}", layer, references, isTest, multi ? "net10.0;net11.0" : "net10.0"));
        }

        var repo = new GeneratedRepo(root, projects);
        repo.Checkout();
        return repo;
    }
#pragma warning restore CA5394

    private void Checkout()
    {
        Write(Root, "Repo.slnx", $"<Solution>{string.Concat(Projects.Select(p => $"""<Project Path="{p.Name}/{p.Name}.csproj" />"""))}</Solution>");
        Write(Root, "Directory.Build.props", "<Project><PropertyGroup><ManagePackageVersionsCentrally>true</ManagePackageVersionsCentrally></PropertyGroup></Project>");
        Write(Root, "Directory.Packages.props", """<Project><ItemGroup><PackageVersion Include="TUnit" Version="1.81.5" /></ItemGroup></Project>""");
        Write(Root, "global.json", GlobalJson);
        foreach (var p in Projects)
        {
            var references = string.Concat(p.References.Select(Reference));
            var frameworks = p.Frameworks.Contains(';', StringComparison.Ordinal)
                ? $"<TargetFrameworks>{p.Frameworks}</TargetFrameworks>"
                : $"<TargetFramework>{p.Frameworks}</TargetFramework>";
            if (p.IsTest)
            {
                Write(Root, $"{p.Name}/{p.Name}.csproj", $"""
                    <Project Sdk="Microsoft.NET.Sdk">
                      <PropertyGroup>{frameworks}<OutputType>Exe</OutputType><ImplicitUsings>enable</ImplicitUsings></PropertyGroup>
                      <ItemGroup>{references}<PackageReference Include="TUnit" /></ItemGroup>
                    </Project>
                    """);
                Write(Root, $"{p.Name}/Cover.cs", $$"""
                    public class Cover
                    {
                        [Test]
                        public async Task Name() => await Assert.That({{p.References[0]}}.Name()).IsEqualTo("{{Expected(p.References[0])}}");
                    }
                    """);
            }
            else
            {
                Write(Root, $"{p.Name}/{p.Name}.csproj", $"""
                    <Project Sdk="Microsoft.NET.Sdk">
                      <PropertyGroup>{frameworks}</PropertyGroup>
                      <ItemGroup>{references}</ItemGroup>
                    </Project>
                    """);
                var nested = string.Join(" + \",\" + ", p.References.Select(r => $"{r}.Name()"));
                Write(Root, $"{p.Name}/{p.Name}.cs", $$"""
                    public static class {{p.Name}} { public static string Name() => "{{p.Name}}" + "[" + {{(nested.Length == 0 ? "\"\"" : nested)}} + "]"; }
                    """);
            }
        }
    }

    private string Expected(string name)
    {
        var p = Projects.Single(p => p.Name == name);
        return $"{name}[{string.Join(",", p.References.Select(Expected))}]";
    }

    /// <summary>The project and everything that transitively references it.</summary>
    public HashSet<Project> Dependents(Project project)
    {
        var set = new HashSet<Project> { project };
        // References only point at earlier projects, so one pass in order is the closure.
        foreach (var p in Projects)
        {
            if (p.References.Any(r => set.Any(d => d.Name == r)))
            {
                set.Add(p);
            }
        }

        return set;
    }

    /// <summary>Changes the value Name() returns, not the API: only this project's key changes, every test above it fails.</summary>
    public void BodyEdit(Project project) => Edit(project, $"\"{project.Name}\"", $"\"{project.Name}x\"");

    /// <summary>Adds a public member: the reference assembly changes, so every dependent's key changes.</summary>
    public void ApiEdit(Project project) => Edit(project, "{ public static", "{ public static string Added() => \"\"; public static");

    private void Edit(Project project, string find, string replace)
    {
        var path = Path.Combine(Root, project.Name, $"{project.Name}.cs");
        File.WriteAllText(path, File.ReadAllText(path).Replace(find, replace, StringComparison.Ordinal));
    }
}
