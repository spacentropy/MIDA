using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;

namespace Mida.Tests;

/// <summary>
/// What a stored graph cannot follow must keep a graph from being stored: a predictor that reads the disk for itself,
/// and a project file that does.
/// </summary>
public sealed class GraphEvaluationTests : IDisposable
{
    private readonly string temp = Directory.CreateTempSubdirectory("mida-tests-").FullName;

    public void Dispose() => Directory.Delete(temp, recursive: true);

    // Read from the package's metadata, not loaded: its types need MSBuild, which only mida's own process has.
    private static List<string> PackagePredictors()
    {
        using var file = File.OpenRead(Path.Combine(AppContext.BaseDirectory, "Microsoft.Build.Prediction.dll"));
        using var assembly = new PEReader(file);
        var metadata = assembly.GetMetadataReader();
        return
        [
            .. metadata.TypeDefinitions
                .Select(metadata.GetTypeDefinition)
                .Where(type => !type.IsNested && metadata.GetString(type.Namespace).StartsWith("Microsoft.Build.Prediction.Predictors", StringComparison.Ordinal))
                .Select(type => metadata.GetString(type.Name))
                .Where(name => name.EndsWith("Predictor", StringComparison.Ordinal)),
        ];
    }

    // A new predictor may read the disk. Until someone has read its source and put it in one of the two lists,
    // a graph it took part in is not stored, and this test fails.
    [Test]
    public async Task EveryPredictorOfThePackageHasBeenRead()
    {
        var predictors = PackagePredictors();

        await Assert.That(predictors.Count).IsGreaterThan(40);
        await Assert.That(predictors.Where(p => !GraphEvaluation.FollowedPredictors.Contains(p) && !GraphEvaluation.UnfollowedPredictors.Contains(p))).IsEmpty();
        await Assert.That(GraphEvaluation.FollowedPredictors.Intersect(GraphEvaluation.UnfollowedPredictors)).IsEmpty();
    }

    private string Project(string name, string content)
    {
        var path = Path.Combine(temp, name);
        File.WriteAllText(path, $"""<Project xmlns="http://schemas.microsoft.com/developer/msbuild/2003">{content}</Project>""");
        return path;
    }

    [Test]
    [Arguments("""<PropertyGroup><Stamp>$([System.IO.File]::ReadAllText('$(MSBuildThisFileDirectory)stamp.txt'))</Stamp></PropertyGroup>""")]
    [Arguments("""<PropertyGroup Condition="$([System.IO.Directory]::GetFiles('$(MSBuildProjectDirectory)', '*.sln').Length) != 0"><A>b</A></PropertyGroup>""")]
    [Arguments("""<PropertyGroup><Ci>$([System.Environment]::GetEnvironmentVariable('CI'))</Ci></PropertyGroup>""")]
    [Arguments("""<ItemGroup><Found Include="@(Candidate->Exists())" /></ItemGroup>""")]
    [Arguments("""<Import Project="more.props" Condition="$([System.IO.File]::Exists('more.props'))" />""")]
    [Arguments("""<Choose><When Condition="'$(A)' == ''"><PropertyGroup><B>$([system.io.file]::ReadAllText('b'))</B></PropertyGroup></When></Choose>""")]
    public async Task ReadOutsideATargetThatNoQuestionRecordsIsNamed(string content) =>
        await Assert.That(GraphEvaluation.Scan(Project("Directory.Build.props", content)).Unrecorded).Contains("Directory.Build.props");

    [Test]
    // A target's body runs in the build, whose inputs are the key's business.
    [Arguments("""<Target Name="Stamp"><PropertyGroup><Stamp>$([System.IO.File]::ReadAllText('stamp.txt'))</Stamp></PropertyGroup></Target>""")]
    [Arguments("""<Target Name="Find" Condition="$([System.IO.File]::Exists('x'))"><ItemGroup><Found Include="@(Candidate->Exists())" /></ItemGroup></Target>""")]
    // These ask MSBuild's file system, which writes the question down.
    [Arguments("""<Import Project="more.props" Condition="Exists('more.props')" />""")]
    [Arguments("""<PropertyGroup><Above>$([MSBuild]::GetPathOfFileAbove('Directory.Build.props', '$(MSBuildThisFileDirectory)../'))</Above></PropertyGroup>""")]
    [Arguments("""<ItemGroup><Compile Include="**/*.cs" /></ItemGroup><PropertyGroup><Name>$([System.IO.Path]::GetFileName('a/b'))</Name></PropertyGroup>""")]
    public async Task ReadThatIsRecordedOrLeftToTheBuildIsNotNamed(string content) =>
        await Assert.That(GraphEvaluation.Scan(Project("Directory.Build.props", content)).Unrecorded).IsNull();

    // The two the SDK makes, each of which mida asks again for itself.
    [Test]
    [Arguments("Microsoft.Managed.Core.targets", """<ItemGroup><EditorConfigFiles Include="@(PotentialEditorConfigFiles->Exists())" /></ItemGroup>""", true)]
    [Arguments("Microsoft.Managed.Core.targets", """<ItemGroup><Other Include="@(PotentialEditorConfigFiles->Exists())" /></ItemGroup>""", false)]
    [Arguments("Microsoft.NET.Sdk.DefaultItems.targets", """<PropertyGroup><EnableDefaultItems Condition="'$([System.IO.Directory]::GetFiles($(MSBuildProjectDirectory), '*.sln').Length)' != '0'">false</EnableDefaultItems></PropertyGroup>""", true)]
    [Arguments("Other.targets", """<PropertyGroup><EnableDefaultItems Condition="'$([System.IO.Directory]::GetFiles($(MSBuildProjectDirectory), '*.sln').Length)' != '0'">false</EnableDefaultItems></PropertyGroup>""", false)]
    public async Task OnlyTheSdksOwnReadsAreKnown(string file, string content, bool known) =>
        await Assert.That(GraphEvaluation.Scan(Project(file, content)).Unrecorded is null).IsEqualTo(known);

    [Test]
    public async Task FileThatIsNoXmlIsNamed() =>
        await Assert.That(GraphEvaluation.Scan(Project("Broken.props", "<Unclosed>")).Unrecorded).Contains("Broken.props");

    // Each name is a question while no variable has it, so a variable set later shows: MSBuild reports none it did not find.
    [Test]
    public async Task PropertiesReadOutsideTargetsAreNamed()
    {
        var scan = GraphEvaluation.Scan(Project("Directory.Build.props", """
            <PropertyGroup Condition="'$(Feature)' == 'on'"><Name>$(Prefix.ToUpper())-$( Spaced )</Name></PropertyGroup>
            <ItemGroup><Compile Include="$(SourceRoot)**/*.cs" /></ItemGroup>
            <Import Project="$(ImportedFrom)/x.props" Condition="$([MSBuild]::IsOSPlatform('Linux'))" />
            <Target Name="Late" AfterTargets="$(HookAfter)" Condition="'$(RunTime)' == ''">
              <PropertyGroup><InBody>$(BodyOnly)</InBody></PropertyGroup>
            </Target>
            """));

        await Assert.That(scan.Unrecorded).IsNull();
        await Assert.That(scan.Properties).IsEquivalentTo(["Feature", "Prefix", "Spaced", "SourceRoot", "ImportedFrom", "HookAfter"]);
    }
}
